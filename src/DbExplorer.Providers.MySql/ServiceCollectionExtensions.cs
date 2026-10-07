using DbExplorer.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Providers.MySql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMySqlProvider(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseProviderFactory, MySqlProviderFactory>();
        return services;
    }
}
