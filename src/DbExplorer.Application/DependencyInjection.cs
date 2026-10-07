using DbExplorer.Application.Api;
using DbExplorer.Application.Compare;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Query;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbExplorer.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddDbExplorerApplication(this IServiceCollection services)
    {
        services.AddSingleton<AppPaths>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<ISecretProtector>(_ => OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new NoSecretProtector());
        services.AddSingleton<ConnectionStore>();
        services.AddSingleton<KnownHostsStore>();
        services.TryAddSingleton<ISshHostKeyPrompt, RejectUnknownHostKeys>();
        services.AddSingleton<SshTunnelService>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<MetadataCache>();
        services.AddSingleton<SchemaHistoryStore>();
        services.AddSingleton<VirtualForeignKeyStore>();
        services.AddSingleton<ChangeRecorder>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton<DefinitionService>();
        services.AddSingleton<MetadataSearchService>();
        services.AddSingleton<DataSearchService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<ScriptStore>();
        services.AddSingleton<SnippetLibrary>();
        services.AddSingleton<QueryExecutionService>();
        services.AddSingleton<MultiDatabaseQueryService>();
        services.AddSingleton<ObjectComparisonService>();
        services.AddSingleton<ObjectCopyService>();
        services.AddSingleton<ApiCollectionImporter>();
        services.AddSingleton<ApiCollectionStore>();
        services.AddSingleton(sp => new ApiRequestRunner(
            new HttpClient { Timeout = TimeSpan.FromSeconds(100) },
            sp.GetRequiredService<ApiCollectionStore>()));
        return services;
    }
}
