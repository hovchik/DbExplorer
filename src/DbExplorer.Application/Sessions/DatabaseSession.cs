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

    public event EventHandler? SnapshotChanged;

    internal void ReplaceSnapshot(MetadataSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}
