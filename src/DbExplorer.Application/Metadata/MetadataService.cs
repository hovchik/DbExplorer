using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Metadata;

public sealed class MetadataService(MetadataCache cache)
{
    public async Task<MetadataSnapshot> LoadAsync(
        ConnectionProfile profile, IDatabaseProvider provider, bool forceRefresh, CancellationToken ct = default)
    {
        var key = MetadataCache.CacheKey(profile);

        if (!forceRefresh)
        {
            var cached = await cache.TryLoadAsync(key, ct);
            if (cached is not null) return cached;
        }

        // Three short catalog reads in parallel, each on its own connection.
        var objectsTask = provider.GetObjectsAsync(ct);
        var columnsTask = provider.GetColumnsAsync(ct);
        var modulesTask = provider.GetModulesAsync(ct);
        await Task.WhenAll(objectsTask, columnsTask, modulesTask);

        var snapshot = new MetadataSnapshot
        {
            Objects = objectsTask.Result,
            Columns = columnsTask.Result,
            Modules = modulesTask.Result,
            RefreshedAt = DateTimeOffset.Now
        };

        await cache.SaveAsync(key, snapshot, ct);
        return snapshot;
    }
}
