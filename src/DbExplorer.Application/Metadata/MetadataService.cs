using DbExplorer.Application.Lab;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Metadata;

public sealed class MetadataService(MetadataCache cache, SchemaHistoryStore history, VirtualForeignKeyStore virtualKeys)
{
    public async Task<MetadataSnapshot> LoadAsync(
        ConnectionProfile profile, IDatabaseProvider provider, bool forceRefresh, CancellationToken ct = default,
        IProgress<string>? progress = null)
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

        // Five catalog reads in parallel, each on its own connection(s); the provider caps how many run at once.
        progress?.Report("Reading catalog: objects, columns, routines, keys, indexes…");
        var objectsTask = provider.GetObjectsAsync(ct);
        var columnsTask = provider.GetColumnsAsync(ct);
        var modulesTask = provider.GetModulesAsync(ct);
        var foreignKeysTask = provider.GetForeignKeysAsync(ct);
        var indexesTask = provider.GetIndexesAsync(includePhysicalStats: false, ct, includeUsageStats: false);
        if (progress is not null)
            ReportAsPartsFinish([("objects", objectsTask), ("columns", columnsTask), ("routines", modulesTask),
                ("keys", foreignKeysTask), ("indexes", indexesTask)], progress);
        await Task.WhenAll(objectsTask, columnsTask, modulesTask, foreignKeysTask, indexesTask);
        // Providers skip a database whose read fails, which includes one cut short by a cancel: never cache that.
        ct.ThrowIfCancellationRequested();

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

    /// <summary>"Reading catalog (2 of 5 done) · waiting for columns, keys…" each time one of the reads finishes.</summary>
    private static void ReportAsPartsFinish((string Name, Task Task)[] parts, IProgress<string> progress)
    {
        var done = 0;
        foreach (var part in parts)
        {
            _ = part.Task.ContinueWith(_ =>
            {
                var finished = Interlocked.Increment(ref done);
                var pending = parts.Where(p => !p.Task.IsCompleted).Select(p => p.Name).ToList();
                progress.Report(pending.Count == 0
                    ? "Reading catalog: done, preparing…"
                    : $"Reading catalog ({finished} of {parts.Length} done) · waiting for {string.Join(", ", pending)}…");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
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
