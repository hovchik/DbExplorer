using Dapper;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using MySqlConnector;

namespace DbExplorer.Providers.MySql;

/// <summary>MySQL 8+ and MariaDB 10.6+, one provider: the two share the protocol, the driver and nearly all of the catalog.</summary>
public sealed class MySqlProviderFactory : IDatabaseProviderFactory
{
    public const string ProviderKey = "MySql";

    public string Key => ProviderKey;
    public string DisplayName => "MySQL / MariaDB";
    public int DefaultPort => 3306;
    public bool SupportsIntegratedSecurity => false;
    public bool SupportsReadOnlyIntent => false;

    public IDatabaseProvider Create(ConnectionProfile profile) => new MySqlProvider(profile);

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        await using var cn = new MySqlConnection(MySqlSql.BuildConnectionString(profile, ""));
        await cn.OpenAsync(ct);
        var rows = await cn.QueryAsync<string>(new CommandDefinition(
            MySqlQueries.Databases, commandTimeout: 30, cancellationToken: ct));
        return rows.AsList();
    }
}
