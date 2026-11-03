using Dapper;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using Npgsql;

namespace DbExplorer.Providers.Postgres;

public sealed class PostgresProviderFactory : IDatabaseProviderFactory
{
    public const string ProviderKey = "Postgres";

    public string Key => ProviderKey;
    public string DisplayName => "PostgreSQL";
    public int DefaultPort => 5432;
    public bool SupportsIntegratedSecurity => true;
    public bool SupportsReadOnlyIntent => false;

    public IDatabaseProvider Create(ConnectionProfile profile) => new PostgresProvider(profile);

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        await using var dataSource = PostgresSql.CreateDataSource(profile);
        await using var cn = await dataSource.OpenConnectionAsync(ct);
        var rows = await cn.QueryAsync<string>(new CommandDefinition(
            PostgresQueries.Databases, commandTimeout: 30, cancellationToken: ct));
        return rows.AsList();
    }
}
