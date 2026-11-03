using DbExplorer.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Providers.Sqlite;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteProvider(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseProviderFactory, SqliteProviderFactory>();
        return services;
    }
}
