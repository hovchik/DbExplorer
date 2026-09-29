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

    public async Task RefreshMetadataAsync(DatabaseSession session, CancellationToken ct = default)
    {
        var snapshot = await metadata.LoadAsync(session.Profile, session.Provider, forceRefresh: true, ct);
        session.ReplaceSnapshot(snapshot);
    }
}
