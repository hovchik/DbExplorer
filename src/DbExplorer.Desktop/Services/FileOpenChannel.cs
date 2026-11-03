using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DbExplorer.Desktop.Services;

/// <summary>
/// Hands files opened from Explorer to the DB Explorer that is already running, instead of starting a second
/// window per file (Explorer starts one process for each file selected). The first instance owns a named mutex and
/// listens on a named pipe; a later start with files sends their paths there and exits. A start without files is a
/// normal, separate window, as before. The pipe only accepts the same user (<see cref="PipeOptions.CurrentUserOnly"/>).
/// </summary>
public sealed class FileOpenChannel : IDisposable
{
    private const int MaxMessageBytes = 256 * 1024;

    private readonly string _pipeName;
    private readonly string _mutexName;
    private Mutex? _mutex;
    private CancellationTokenSource? _listening;

    public FileOpenChannel(string name)
    {
        _pipeName = name;
        _mutexName = name + "-primary";
    }

    /// <summary>One channel per user and data folder, so a portable copy with its own folder stays separate.</summary>
    public static string DefaultName(string dataRoot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|" + dataRoot.ToUpperInvariant()));
        return "DbExplorer-open-" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>True when this process is the one that receives files (it keeps the mutex until disposed).</summary>
    public bool IsPrimary => _mutex is not null;

    public bool TryBecomePrimary()
    {
        if (_mutex is not null) return true;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, _mutexName);
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; } // the last owner crashed; the mutex is ours now
            if (!owned)
            {
                mutex.Dispose();
                return false;
            }
            _mutex = mutex;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            // Without the mutex every start is its own window, as before this existed.
            mutex?.Dispose();
            return false;
        }
    }

    /// <summary>Starts receiving paths on a background task; <paramref name="received"/> runs on that task.</summary>
    public void Listen(Action<IReadOnlyList<string>> received)
    {
        if (_mutex is null || _listening is not null) return;
        _listening = new CancellationTokenSource();
        var ct = _listening.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    // Our process id first, so the sender can let this window come to the front (Windows only lets
                    // the foreground process hand that right on).
                    await server.WriteAsync(Encoding.UTF8.GetBytes(Environment.ProcessId + "\n"), ct);
                    await server.FlushAsync(ct);
                    var paths = await ReadPathsAsync(server, ct);
                    if (paths.Count > 0) received(paths);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("file open channel", ex);
                    try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }, ct);
    }

    /// <summary>Sends <paramref name="paths"/> to the running instance; false when none answered in time.</summary>
    public async Task<bool> TrySendAsync(IReadOnlyList<string> paths, TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(cts.Token);
            var pid = await ReadLineAsync(client, cts.Token);
            if (OperatingSystem.IsWindows() && int.TryParse(pid, out var id)) AllowSetForegroundWindow(id);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", paths) + "\n");
            await client.WriteAsync(bytes, cts.Token);
            await client.FlushAsync(cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadPathsAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxMessageBytes) break;
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)Math.Min(buffer.Length, MaxMessageBytes))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified)
            .ToList();
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 32 && await stream.ReadAsync(one, ct) == 1 && one[0] != (byte)'\n') bytes.Add(one[0]);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        _listening?.Cancel();
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _mutex = null;
    }
}
