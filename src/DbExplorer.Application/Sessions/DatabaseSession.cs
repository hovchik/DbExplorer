using System.Collections.Concurrent;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Sessions;

/// <summary>An open connection: the provider plus the current metadata snapshot.</summary>
public sealed class DatabaseSession(
    ConnectionProfile profile,
    IDatabaseProviderFactory factory,
    IDatabaseProvider provider,
    string serverVersion,
    MetadataSnapshot snapshot) : IAsyncDisposable
{
    public ConnectionProfile Profile { get; } = profile;
    public IDatabaseProviderFactory Factory { get; } = factory;
    public IDatabaseProvider Provider { get; } = provider;
    public string ServerVersion { get; } = serverVersion;
    public MetadataSnapshot Snapshot { get; private set; } = snapshot;

    /// <summary>The SSH tunnel the session's connections go through (closed with the session), or null.</summary>
    public SshTunnel? Tunnel { get; init; }

    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Cancelled when the session is disposed, so background work for it (a catalog refresh) stops.</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    public event EventHandler? SnapshotChanged;

    /// <summary>Per-database catalogs handed out by <see cref="SessionService.GetDatabaseSnapshotAsync"/>.</summary>
    internal ConcurrentDictionary<string, Lazy<Task<MetadataSnapshot>>> DatabaseSnapshots { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal void ReplaceSnapshot(MetadataSnapshot snapshot)
    {
        Snapshot = snapshot;
        DatabaseSnapshots.Clear();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised (on the caller's thread) when the server's database list may have changed: after Refresh metadata, or a
    /// statement that creates, drops or renames a database. Database pickers read <see cref="GetServerDatabasesAsync"/> again.
    /// </summary>
    public event EventHandler? DatabasesChanged;

    private readonly object _databasesLock = new();
    private Task<IReadOnlyList<string>>? _serverDatabases;

    /// <summary>
    /// Every database on the server the login can open, read once and shared by all the session's pickers until
    /// <see cref="InvalidateDatabases"/>. The catalog snapshot only knows databases that hold objects (or only the
    /// connection's own database), so a new, empty database is listed here and nowhere else. A failed read is not kept.
    /// </summary>
    public Task<IReadOnlyList<string>> GetServerDatabasesAsync()
    {
        lock (_databasesLock)
        {
            if (_serverDatabases is null || _serverDatabases.IsFaulted || _serverDatabases.IsCanceled)
                _serverDatabases = Task.Run(() => Factory.ListDatabasesAsync(Profile, Lifetime));
            return _serverDatabases;
        }
    }

    /// <summary>Forgets the server's database list and tells the pickers to read it again.</summary>
    public void InvalidateDatabases()
    {
        lock (_databasesLock) _serverDatabases = null;
        DatabasesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>False for a view made by <see cref="WithSnapshot"/>: it borrows the provider and must not dispose it.</summary>
    public bool OwnsProvider { get; private init; } = true;

    /// <summary>
    /// The same connection seen through another catalog, e.g. one database of the server that the session's own
    /// snapshot does not cover. The view shares the provider (disposing it does nothing) and never refreshes itself.
    /// </summary>
    public DatabaseSession WithSnapshot(MetadataSnapshot snapshot) =>
        new(Profile, Factory, Provider, ServerVersion, snapshot) { OwnsProvider = false, Tunnel = Tunnel };

    public async ValueTask DisposeAsync()
    {
        if (!OwnsProvider) return;
        _lifetime.Cancel();
        try
        {
            await Provider.DisposeAsync();
        }
        finally
        {
            // After the provider: its pooled connections close through the tunnel first.
            if (Tunnel is not null) await Tunnel.DisposeAsync();
        }
    }
}
