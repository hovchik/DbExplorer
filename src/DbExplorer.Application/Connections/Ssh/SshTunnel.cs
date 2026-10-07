using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DbExplorer.Core.Connections;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DbExplorer.Application.Connections.Ssh;

/// <summary>An SSH problem explained for the user (which step failed and what to check).</summary>
public sealed class SshTunnelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A local port on 127.0.0.1 forwarded through an SSH server to the database. When the SSH connection drops (a laptop
/// sleeping, a network change) it is re-established in the background, and the local port stays the same, so the
/// driver's next connection works again without reconnecting in the app.
/// </summary>
/// <remarks>
/// The driver connects to our own listener, which hands connections to SSH.NET's forwarded port one at a time.
/// SSH.NET's listener serves a connection that is already waiting when it re-arms its accept on the same thread as the
/// one before, so two connections opened together (a connection pool warming up, the catalog read in parallel) would
/// leave the first one stalled until the second closed.
/// </remarks>
public sealed class SshTunnel : IAsyncDisposable
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandOffTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<SshClient>> _reconnect;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _handOff = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _waiting = new();
    private readonly Task _accept;
    private readonly Task _watch;
    private volatile SshClient _client;
    private volatile ForwardedPortLocal _forward;
    private volatile string? _forwardError;

    private SshTunnel(SshClient client, string server, string remoteHost, int remotePort, Func<CancellationToken, Task<SshClient>> reconnect)
    {
        _client = client;
        _reconnect = reconnect;
        Server = server;
        RemoteHost = remoteHost;
        RemotePort = remotePort;
        _forward = StartForward(client);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            _listener.Start();
        }
        catch
        {
            Close(client, _forward);
            throw;
        }
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptAsync);
        _watch = Task.Run(WatchAsync);
    }

    /// <summary>"user@host" of the SSH server.</summary>
    public string Server { get; }

    /// <summary>The database as the SSH server reaches it.</summary>
    public string RemoteHost { get; }
    public int RemotePort { get; }

    /// <summary>The port on 127.0.0.1 the driver connects to.</summary>
    public int LocalPort { get; }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Why the SSH server last refused to open a connection to the database (null when it never did).</summary>
    public string? ForwardError => _forwardError;

    internal static SshTunnel Start(SshClient client, string server, string remoteHost, int remotePort, Func<CancellationToken, Task<SshClient>> reconnect) =>
        new(client, server, remoteHost, remotePort, reconnect);

    /// <summary>The profile the driver uses: the same database, reached on the local end of the tunnel.</summary>
    public ConnectionProfile Local(ConnectionProfile profile)
    {
        var local = profile.Clone();
        local.CertificateHostName ??= RemoteHost;
        local.Host = "127.0.0.1";
        local.Port = LocalPort;
        local.Ssh.Enabled = false;
        return local;
    }

    /// <summary>
    /// A database error seen through the tunnel, explained when its cause is that the SSH server could not reach the
    /// database (the driver only sees the connection close); otherwise the error itself.
    /// </summary>
    public Exception Explain(Exception databaseError) =>
        _forwardError is { } reason && databaseError is not SshTunnelException
            ? new SshTunnelException($"Connected to the SSH server {Server}, but it could not reach the database at {RemoteHost}:{RemotePort}: {reason}", databaseError)
            : databaseError;

    private ForwardedPortLocal StartForward(SshClient client)
    {
        var forward = new ForwardedPortLocal("127.0.0.1", 0, RemoteHost, (uint)RemotePort);
        forward.Exception += (_, e) => _forwardError = e.Exception.Message;
        forward.RequestReceived += OnRequestReceived;
        client.AddForwardedPort(forward);
        forward.Start();
        return forward;
    }

    /// <summary>SSH.NET took a connection and has already re-armed its listener: the next one may go.</summary>
    private void OnRequestReceived(object? sender, PortForwardEventArgs e)
    {
        if (_waiting.TryRemove((int)e.OriginatorPort, out var handedOff)) handedOff.TrySetResult();
    }

    private async Task AcceptAsync()
    {
        var ct = _lifetime.Token;
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => RelayAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task RelayAsync(Socket client, CancellationToken ct)
    {
        using (client)
        using (var inner = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true })
        {
            client.NoDelay = true;
            try
            {
                await _handOff.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var forward = _forward;
                    if (!forward.IsStarted) return; // reconnecting: the driver sees the connection close and reports it
                    inner.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    var handedOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var key = ((IPEndPoint)inner.LocalEndPoint!).Port;
                    _waiting[key] = handedOff;
                    try
                    {
                        await inner.ConnectAsync(IPAddress.Loopback, (int)forward.BoundPort, ct).ConfigureAwait(false);
                        await handedOff.Task.WaitAsync(HandOffTimeout, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _waiting.TryRemove(key, out _);
                    }
                }
                finally
                {
                    _handOff.Release();
                }

                await using var a = new NetworkStream(client, ownsSocket: false);
                await using var b = new NetworkStream(inner, ownsSocket: false);
                var toServer = Pump(a, b, inner, ct);
                var toClient = Pump(b, a, client, ct);
                // Either side closing ends the pair.
                await Task.WhenAny(toServer, toClient).ConfigureAwait(false);

                // SSH.NET closes the connection without an error when the SSH server cannot open it (wrong database host
                // or port, a firewall behind the SSH server). Both engines always answer the driver's first message.
                if (toClient is { IsCompletedSuccessfully: true, Result: 0 } && _client.IsConnected && !ct.IsCancellationRequested)
                    _forwardError ??= "the connection was closed before the database answered. Check the database host and port as the SSH server sees them";
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ObjectDisposedException)
            {
                // The driver sees the connection close; ForwardError explains why when the SSH side refused it.
            }
        }
    }

    /// <summary>Copies until <paramref name="from"/> closes; the number of bytes copied.</summary>
    private static async Task<long> Pump(Stream from, Stream to, Socket toSocket, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }
            toSocket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
        return total;
    }

    private async Task WatchAsync()
    {
        using var timer = new PeriodicTimer(WatchInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (_client.IsConnected && _forward.IsStarted) continue;
                try
                {
                    await ReconnectAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!_lifetime.IsCancellationRequested)
                {
                    // The server is still unreachable: try again on the next tick.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        var client = await _reconnect(ct).ConfigureAwait(false);
        try
        {
            var forward = StartForward(client);
            var (oldClient, oldForward) = (_client, _forward);
            (_client, _forward) = (client, forward);
            Close(oldClient, oldForward);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static void Close(SshClient client, ForwardedPortLocal forward)
    {
        try { if (forward.IsStarted) forward.Stop(); } catch (Exception) { /* already broken */ }
        forward.Dispose();
        try { if (client.IsConnected) client.Disconnect(); } catch (Exception) { /* already broken */ }
        client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _listener.Stop();
        await Task.WhenAll(_accept, _watch).ConfigureAwait(false);
        // Stopping waits for open channels to finish; the driver connections are already closed by now.
        await Task.Run(() => Close(_client, _forward)).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
