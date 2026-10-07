using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Sessions;

public sealed class SessionService(ProviderRegistry registry, MetadataService metadata, SshTunnelService tunnels)
{
    /// <summary>
    /// Opens the connection and loads the catalog entirely on the thread pool: driver connects (SqlClient's login
    /// and TLS handshake are partly synchronous), row materialization and lookup building never touch the caller's
    /// (UI) thread. <paramref name="progress"/> reports on the context it was created on.
    /// </summary>
    public async Task<DatabaseSession> ConnectAsync(ConnectionProfile profile, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var context = SynchronizationContext.Current;
        var factory = registry.Get(profile.ProviderKey);
        SshTunnel? tunnel = null;
        if (profile.Ssh.Enabled)
        {
            progress?.Report($"Opening SSH tunnel to {profile.Ssh}…");
            tunnel = await tunnels.OpenAsync(profile, factory.DefaultPort, ct);
            factory = new TunnelledProviderFactory(factory, tunnel);
        }

        IDatabaseProvider provider;
        try
        {
            provider = factory.Create(profile);
        }
        catch
        {
            if (tunnel is not null) await tunnel.DisposeAsync();
            throw;
        }

        try
        {
            var (version, snapshot) = await Task.Run(async () =>
            {
                // The server round-trip (which also validates the credentials) and the local cache read overlap.
                var versionTask = provider.GetServerVersionAsync(ct);
                var snapshotTask = metadata.LoadAsync(profile, provider, forceRefresh: false, ct, progress);
                await Task.WhenAll(versionTask, snapshotTask).ConfigureAwait(false);
                return (versionTask.Result, snapshotTask.Result.Warm());
            }, ct).WaitAsync(ct); // a cancel returns at once, even while a driver call is still unwinding
            var session = new DatabaseSession(profile, factory, provider, version, snapshot) { Tunnel = tunnel };
            if (snapshot.IsStale) RefreshInBackground(session, context);
            return session;
        }
        catch (Exception ex)
        {
            await provider.DisposeAsync();
            if (tunnel is null) throw;
            await tunnel.DisposeAsync();
            var explained = tunnel.Explain(ex);
            if (ReferenceEquals(explained, ex)) throw;
            throw explained;
        }
    }

    /// <summary>
    /// Re-reads the catalog without holding up the caller, then swaps the snapshot in on the caller's
    /// synchronization context (the UI thread), where <see cref="DatabaseSession.SnapshotChanged"/> listeners run.
    /// Disconnecting cancels it.
    /// </summary>
    private void RefreshInBackground(DatabaseSession session, SynchronizationContext? context)
    {
        var ct = session.Lifetime;
        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = (await metadata.LoadAsync(session.Profile, session.Provider, forceRefresh: true, ct).ConfigureAwait(false)).Warm();
                if (ct.IsCancellationRequested) return;
                if (context is null) session.ReplaceSnapshot(fresh);
                else context.Post(_ => { if (!ct.IsCancellationRequested) session.ReplaceSnapshot(fresh); }, null);
            }
            catch
            {
                // The stale snapshot keeps working; the user can still refresh by hand.
            }
        }, ct);
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
        return await Task.Run(async () => (await metadata.LoadAsync(profile, provider, forceRefresh: false).ConfigureAwait(false)).Warm());
    }

    /// <summary>
    /// Re-reads the catalog of one database of the session's server and returns it: the session's own snapshot when
    /// it covers that database, otherwise a fresh read over a short-lived connection, which later
    /// <see cref="GetDatabaseSnapshotAsync"/> calls then return.
    /// </summary>
    public async Task<MetadataSnapshot> RefreshDatabaseSnapshotAsync(DatabaseSession session, string? database, CancellationToken ct = default)
    {
        var own = string.IsNullOrEmpty(database) || string.Equals(session.Profile.Database, database, StringComparison.OrdinalIgnoreCase);
        if (own || session.Snapshot.ContainsDatabase(database!))
        {
            await RefreshMetadataAsync(session, ct);
            if (own) return session.Snapshot;
            if (session.Snapshot.ContainsDatabase(database!)) return session.Snapshot.ForDatabase(database!);
        }

        var profile = session.Profile.Clone();
        profile.Database = database!;
        await using var provider = session.Factory.Create(profile);
        var fresh = await Task.Run(async () => (await metadata.LoadAsync(profile, provider, forceRefresh: true, ct).ConfigureAwait(false)).Warm(), ct);
        session.DatabaseSnapshots[database!] = new Lazy<Task<MetadataSnapshot>>(() => Task.FromResult(fresh));
        return fresh;
    }

    /// <summary>Re-applies the relationships accepted for the session's connection (after the user accepted or removed one).</summary>
    public void ReloadVirtualForeignKeys(DatabaseSession session) =>
        session.ReplaceSnapshot(metadata.WithVirtualKeys(session.Profile, session.Snapshot));

    /// <summary>Reads the catalog on the thread pool, then swaps it in on the caller's context.</summary>
    public async Task RefreshMetadataAsync(DatabaseSession session, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var snapshot = await Task.Run(async () =>
            (await metadata.LoadAsync(session.Profile, session.Provider, forceRefresh: true, ct, progress).ConfigureAwait(false)).Warm(), ct)
            .WaitAsync(ct);
        session.ReplaceSnapshot(snapshot);
    }
}
