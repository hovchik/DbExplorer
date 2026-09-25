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
        var db = string.IsNullOrWhiteSpace(profile.Database) ? "postgres" : profile.Database;
        await using var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(profile, db));
        await cn.OpenAsync(ct);
        var rows = await cn.QueryAsync<string>(new CommandDefinition(
            PostgresQueries.Databases, commandTimeout: 30, cancellationToken: ct));
        return rows.AsList();
    }
}
