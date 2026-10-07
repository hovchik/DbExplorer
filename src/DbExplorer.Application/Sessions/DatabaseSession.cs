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
