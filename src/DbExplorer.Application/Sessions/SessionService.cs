using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Sessions;

public sealed class SessionService(ProviderRegistry registry, MetadataService metadata)
{
    public async Task<DatabaseSession> ConnectAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var factory = registry.Get(profile.ProviderKey);
        var provider = factory.Create(profile);
        try
        {
            // The server round-trip (which also validates the credentials) and the local cache read overlap.
            var versionTask = provider.GetServerVersionAsync(ct);
            var snapshotTask = metadata.LoadAsync(profile, provider, forceRefresh: false, ct);
            await Task.WhenAll(versionTask, snapshotTask);
            var session = new DatabaseSession(profile, factory, provider, versionTask.Result, snapshotTask.Result);
            if (snapshotTask.Result.IsStale) RefreshInBackground(session);
            return session;
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Re-reads the catalog without holding up the caller, then swaps the snapshot in on the caller's
    /// synchronization context (the UI thread), where <see cref="DatabaseSession.SnapshotChanged"/> listeners run.
    /// </summary>
    private void RefreshInBackground(DatabaseSession session)
    {
        var context = SynchronizationContext.Current;
        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = await metadata.LoadAsync(session.Profile, session.Provider, forceRefresh: true);
                if (context is null) session.ReplaceSnapshot(fresh);
                else context.Post(_ => session.ReplaceSnapshot(fresh), null);
            }
            catch
            {
                // The stale snapshot keeps working; the user can still refresh by hand.
            }
        });
    }

    /// <summary>
    /// The catalog of one database on the session's server, for editor suggestions scoped to it. A server-level
    /// session already holds every database, so this is a filtered view; any other database is read with its own
    /// short-lived connection (and the local cache), once per session.
    /// </summary>
    public Task<MetadataSnapshot> GetDatabaseSnapshotAsync(DatabaseSession session, string database)
    {
        var lazy = session.DatabaseSnapshots.GetOrAdd(database, db => new Lazy<Task<MetadataSnapshot>>(() => LoadDatabaseSnapshotAsync(session, db)));
        var task = lazy.Value;
        // A failed read must not stick: the next request tries again.
        _ = task.ContinueWith(t => session.DatabaseSnapshots.TryRemove(new KeyValuePair<string, Lazy<Task<MetadataSnapshot>>>(database, lazy)),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return task;
    }

    private async Task<MetadataSnapshot> LoadDatabaseSnapshotAsync(DatabaseSession session, string database)
    {
        var snapshot = session.Snapshot;
        if (snapshot.ContainsDatabase(database)) return await Task.Run(() => snapshot.ForDatabase(database));
        if (string.Equals(session.Profile.Database, database, StringComparison.OrdinalIgnoreCase)) return snapshot;

        var profile = session.Profile.Clone();
        profile.Database = database;
        await using var provider = session.Factory.Create(profile);
        return await metadata.LoadAsync(profile, provider, forceRefresh: false);
    }

    public async Task RefreshMetadataAsync(DatabaseSession session, CancellationToken ct = default)
    {
        var snapshot = await metadata.LoadAsync(session.Profile, session.Provider, forceRefresh: true, ct);
        session.ReplaceSnapshot(snapshot);
    }
}
