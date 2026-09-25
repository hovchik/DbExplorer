using DbExplorer.Application.Connections;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Query;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddDbExplorerApplication(this IServiceCollection services)
    {
        services.AddSingleton<AppPaths>();
        services.AddSingleton<ISecretProtector>(_ => OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new NoSecretProtector());
        services.AddSingleton<ConnectionStore>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<MetadataCache>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton<DefinitionService>();
        services.AddSingleton<MetadataSearchService>();
        services.AddSingleton<DataSearchService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<ScriptStore>();
        services.AddSingleton<QueryExecutionService>();
        return services;
    }
}
