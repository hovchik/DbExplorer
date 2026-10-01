using DbExplorer.Application.Lab;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Metadata;

public sealed class MetadataService(MetadataCache cache, SchemaHistoryStore history, VirtualForeignKeyStore virtualKeys)
{
    public async Task<MetadataSnapshot> LoadAsync(
        ConnectionProfile profile, IDatabaseProvider provider, bool forceRefresh, CancellationToken ct = default)
    {
        var key = MetadataCache.CacheKey(profile);

        if (!forceRefresh)
        {
            var cached = await cache.TryLoadAsync(key, ct);
            if (cached is not null)
            {
                // The first connect after an update has no history yet: the cached catalog becomes its baseline.
                if (!cached.IsStale) _ = RecordHistoryInBackgroundAsync(key, cached);
                return WithVirtualKeys(key, cached);
            }
        }

        // Five short catalog reads in parallel, each on its own connection.
        var objectsTask = provider.GetObjectsAsync(ct);
        var columnsTask = provider.GetColumnsAsync(ct);
        var modulesTask = provider.GetModulesAsync(ct);
        var foreignKeysTask = provider.GetForeignKeysAsync(ct);
        var indexesTask = provider.GetIndexesAsync(includePhysicalStats: false, ct, includeUsageStats: false);
        await Task.WhenAll(objectsTask, columnsTask, modulesTask, foreignKeysTask, indexesTask);

        var snapshot = new MetadataSnapshot
        {
            Objects = objectsTask.Result,
            Columns = columnsTask.Result,
            Modules = modulesTask.Result,
            ForeignKeys = foreignKeysTask.Result,
            Indexes = indexesTask.Result,
            RefreshedAt = DateTimeOffset.Now
        };

        // Persist to the local cache in the background: the caller only needs the in-memory
        // snapshot to proceed, and blocking the UI on a disk write adds needless latency.
        _ = PersistInBackgroundAsync(key, snapshot);
        _ = RecordHistoryInBackgroundAsync(key, snapshot);

        return WithVirtualKeys(key, snapshot);
    }

    /// <summary>The snapshot with the relationships the user accepted for this connection (never written to the cache).</summary>
    public MetadataSnapshot WithVirtualKeys(ConnectionProfile profile, MetadataSnapshot snapshot) =>
        WithVirtualKeys(MetadataCache.CacheKey(profile), snapshot);

    private MetadataSnapshot WithVirtualKeys(string key, MetadataSnapshot snapshot)
    {
        var keys = virtualKeys.Load(key);
        return keys.Count == 0 && !snapshot.ForeignKeys.Any(f => f.IsVirtual) ? snapshot : snapshot.WithVirtualForeignKeys(keys);
    }

    private async Task PersistInBackgroundAsync(string key, MetadataSnapshot snapshot)
    {
        try
        {
            await cache.SaveAsync(key, snapshot, CancellationToken.None);
        }
        catch
        {
            // Best-effort cache write; failures here should not affect the already-returned snapshot.
        }
    }

    private async Task RecordHistoryInBackgroundAsync(string key, MetadataSnapshot snapshot)
    {
        try
        {
            await history.RecordAsync(key, snapshot);
        }
        catch
        {
            // History is a convenience: a locked or full disk must not break connecting.
        }
    }
}
