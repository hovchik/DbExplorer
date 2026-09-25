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
            var version = await provider.GetServerVersionAsync(ct);
            var snapshot = await metadata.LoadAsync(profile, provider, forceRefresh: false, ct);
            return new DatabaseSession(profile, factory, provider, version, snapshot);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    public async Task RefreshMetadataAsync(DatabaseSession session, CancellationToken ct = default)
    {
        var snapshot = await metadata.LoadAsync(session.Profile, session.Provider, forceRefresh: true, ct);
        session.ReplaceSnapshot(snapshot);
    }
}
