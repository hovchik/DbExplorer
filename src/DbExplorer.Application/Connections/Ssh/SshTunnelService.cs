using System.Net.Sockets;
using DbExplorer.Core.Connections;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DbExplorer.Application.Connections.Ssh;

/// <summary>Opens SSH tunnels for connections that use one, checking host keys against the ones the user accepted.</summary>
public sealed class SshTunnelService(KnownHostsStore knownHosts, ISshHostKeyPrompt prompt)
{
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Connects to the profile's SSH server and forwards a local port to its database. A host key seen for the first
    /// time (or changed) is shown to the user to accept; the prompt runs on the caller's synchronization context.
    /// </summary>
    /// <exception cref="SshTunnelException">The SSH step failed; the message says why.</exception>
    public async Task<SshTunnel> OpenAsync(ConnectionProfile profile, int defaultDatabasePort, CancellationToken ct = default)
    {
        var ssh = profile.Ssh.Clone();
        Validate(ssh);
        EnsureThreads();
        var (host, port) = Destination(profile, defaultDatabasePort);

        var client = await ConnectAsync(ssh, interactive: true, ct);
        try
        {
            return SshTunnel.Start(client, ssh.ToString(), host, port, c => ConnectAsync(ssh, interactive: false, c));
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new SshTunnelException($"Connected to the SSH server {ssh}, but could not open a local port for the tunnel: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// SSH.NET relays each tunnelled connection with blocking reads on a pool thread for as long as it stays open (pooled
    /// driver connections stay open). Without spare threads, a burst of connections waits on the pool's slow growth.
    /// </summary>
    private static void EnsureThreads()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        if (workers < 64 || io < 64) ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
    }

    /// <summary>
    /// Where the SSH server should forward to: the profile's host and port. A SQL Server named instance
    /// ("server\instance") is found through the UDP browser service, which a tunnel cannot carry, so it needs its port.
    /// </summary>
    public static (string Host, int Port) Destination(ConnectionProfile profile, int defaultPort)
    {
        var host = profile.Host.Trim();
        var port = profile.Port;

        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        var comma = host.LastIndexOf(',');
        if (comma >= 0)
        {
            if (port is null && int.TryParse(host[(comma + 1)..].Trim(), out var inline)) port = inline;
            host = host[..comma].Trim();
        }
        var slash = host.IndexOf('\\');
        if (slash >= 0)
        {
            if (port is null)
                throw new SshTunnelException(
                    $"The named instance {host} cannot be reached through SSH without its port. Enter the instance's TCP port on the General tab.");
            host = host[..slash];
        }
        if (host is "" or "." or "(local)") host = "localhost";
        return (host, port ?? defaultPort);
    }

    private static void Validate(SshTunnelSettings ssh)
    {
        if (string.IsNullOrWhiteSpace(ssh.Host)) throw new SshTunnelException("Enter the SSH server on the SSH tab.");
        if (ssh.Port is <= 0 or > 65535) throw new SshTunnelException("The SSH port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(ssh.UserName)) throw new SshTunnelException("Enter the SSH user on the SSH tab.");
        if (ssh.AuthMethod == SshAuthMethod.PrivateKey && string.IsNullOrWhiteSpace(ssh.PrivateKeyPath))
            throw new SshTunnelException("Choose the private key file on the SSH tab.");
    }

    private async Task<SshClient> ConnectAsync(SshTunnelSettings ssh, bool interactive, CancellationToken ct)
    {
        var host = ssh.Host.Trim();
        for (var attempt = 0; ; attempt++)
        {
            SshHostKey? untrusted = null;
            var client = new SshClient(ConnectionInfo(ssh)) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            client.HostKeyReceived += (_, e) =>
            {
                var fingerprint = "SHA256:" + e.FingerPrintSHA256;
                var known = knownHosts.Find(host, ssh.Port);
                e.CanTrust = known == fingerprint;
                if (!e.CanTrust) untrusted = new SshHostKey(host, ssh.Port, e.HostKeyName, fingerprint, known);
            };

            try
            {
                // Off the caller's thread: key exchange and sign-in are partly synchronous inside SSH.NET.
                await Task.Run(() => client.ConnectAsync(ct), ct);
                return client;
            }
            catch (Exception ex)
            {
                client.Dispose();
                ct.ThrowIfCancellationRequested();
                if (untrusted is not { } key) throw Explain(ex, ssh);

                if (interactive && attempt == 0 && await prompt.TrustAsync(key, ct))
                {
                    knownHosts.Trust(key);
                    continue;
                }
                throw new SshTunnelException(key.Changed
                    ? $"The host key of the SSH server {key.Server} has changed since you accepted it, and the new key was not accepted. Check with the server's administrator before trusting it."
                    : $"The host key of the SSH server {key.Server} was not accepted.", ex);
            }
        }
    }

    private ConnectionInfo ConnectionInfo(SshTunnelSettings ssh)
    {
        var user = ssh.UserName.Trim();
        AuthenticationMethod[] methods;
        if (ssh.AuthMethod == SshAuthMethod.PrivateKey)
        {
            methods = [new PrivateKeyAuthenticationMethod(user, LoadKey(ssh))];
        }
        else
        {
            // Many servers ask for the password through keyboard-interactive rather than the password method.
            var password = ssh.Password ?? "";
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = password;
            };
            methods = [new PasswordAuthenticationMethod(user, password), interactive];
        }
        return new ConnectionInfo(ssh.Host.Trim(), ssh.Port, user, methods) { Timeout = ConnectTimeout };
    }

    private static PrivateKeyFile LoadKey(SshTunnelSettings ssh)
    {
        var path = Environment.ExpandEnvironmentVariables(ssh.PrivateKeyPath.Trim());
        if (path.StartsWith('~')) path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'));
        if (!File.Exists(path)) throw new SshTunnelException($"The SSH key file {path} was not found.");
        try
        {
            return string.IsNullOrEmpty(ssh.Passphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, ssh.Passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new SshTunnelException("The SSH key file is protected by a passphrase. Enter it on the SSH tab.", ex);
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new SshTunnelException(
                string.IsNullOrEmpty(ssh.Passphrase)
                    ? $"Could not read the SSH key file {Path.GetFileName(path)}: {ex.Message}"
                    : $"Could not read the SSH key file {Path.GetFileName(path)}. Is the passphrase right? ({ex.Message})", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SshTunnelException($"Could not open the SSH key file {path}: {ex.Message}", ex);
        }
    }

    private static Exception Explain(Exception ex, SshTunnelSettings ssh) => ex switch
    {
        SshTunnelException or OperationCanceledException => ex,
        SocketException s => new SshTunnelException($"Could not reach the SSH server {ssh.Host.Trim()}:{ssh.Port}: {s.Message}", ex),
        SshOperationTimeoutException => new SshTunnelException($"The SSH server {ssh.Host.Trim()}:{ssh.Port} did not answer in time.", ex),
        SshAuthenticationException => new SshTunnelException(ssh.AuthMethod == SshAuthMethod.PrivateKey
            ? $"The SSH server refused the key for {ssh.UserName.Trim()}. Is the public key in the server's authorized_keys? ({ex.Message})"
            : $"The SSH server refused the password for {ssh.UserName.Trim()}. ({ex.Message})", ex),
        SshConnectionException or SshException => new SshTunnelException($"SSH connection to {ssh.Host.Trim()}:{ssh.Port} failed: {ex.Message}", ex),
        _ => new SshTunnelException($"SSH connection to {ssh.Host.Trim()}:{ssh.Port} failed: {ex.Message}", ex)
    };
}
