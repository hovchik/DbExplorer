namespace DbExplorer.Application.Team;

/// <summary>
/// Raises <see cref="Changed"/> soon after something in the team folder changes. File system events are coalesced
/// (a sync tool writes a file in several steps), and the folder is also polled, because network shares and some sync
/// tools do not raise events reliably.
/// </summary>
public sealed class TeamFolderWatcher : IDisposable
{
    private readonly string _root;
    private readonly Timer _debounce;
    private readonly Timer _poll;
    private readonly TimeSpan _delay;
    private FileSystemWatcher? _watcher;
    private string _signature;
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread.</summary>
    public event Action? Changed;

    public TeamFolderWatcher(string root, TimeSpan? pollInterval = null, TimeSpan? debounce = null)
    {
        _root = root;
        _delay = debounce ?? TimeSpan.FromMilliseconds(800);
        _signature = Signature();
        _debounce = new Timer(_ => Raise(), null, Timeout.Infinite, Timeout.Infinite);
        var every = pollInterval ?? TimeSpan.FromSeconds(30);
        _poll = new Timer(_ => Poll(), null, every, every);
        StartWatcher();
    }

    private void StartWatcher()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
            // Too many changes at once, or the share went away: polling still catches up.
            _watcher.Error += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Schedule();

    private void Schedule()
    {
        if (_disposed) return;
        try { _debounce.Change(_delay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Poll()
    {
        if (_disposed) return;
        if (_watcher is null) StartWatcher();
        if (Signature() != _signature) Raise();
    }

    private void Raise()
    {
        if (_disposed) return;
        _signature = Signature();
        Changed?.Invoke();
    }

    /// <summary>Names, sizes and times of the shared files: cheap to compare without reading contents. Only the three
    /// shared folders count, so a team folder inside a git checkout does not walk the whole repository.</summary>
    private string Signature()
    {
        try
        {
            if (!Directory.Exists(_root)) return "missing";
            return string.Join("|", new[] { TeamFolder.ConnectionsDirectory, TeamFolder.QueriesDirectory, TeamFolder.SnippetsDirectory }
                .Select(d => Path.Combine(_root, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                .Select(f => new FileInfo(f))
                .Select(f => $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}")
                .Order(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _debounce.Dispose();
        _poll.Dispose();
    }
}
