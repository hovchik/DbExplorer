using DbExplorer.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Providers.Postgres;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresProvider(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseProviderFactory, PostgresProviderFactory>();
        return services;
    }
}
