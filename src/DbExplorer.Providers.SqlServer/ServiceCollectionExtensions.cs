using DbExplorer.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Providers.SqlServer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlServerProvider(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseProviderFactory, SqlServerProviderFactory>();
        return services;
    }
}
