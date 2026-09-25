using Dapper;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Providers.SqlServer;

public sealed class SqlServerProviderFactory : IDatabaseProviderFactory
{
    public const string ProviderKey = "SqlServer";

    public string Key => ProviderKey;
    public string DisplayName => "Microsoft SQL Server";
    public int DefaultPort => 1433;
    public bool SupportsIntegratedSecurity => true;
    public bool SupportsReadOnlyIntent => true;

    public IDatabaseProvider Create(ConnectionProfile profile) => new SqlServerProvider(profile);

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var cs = SqlServerSql.BuildConnectionString(profile, SqlServerSql.MetaAppName, "master");
        await using var cn = new SqlConnection(cs);
        await cn.OpenAsync(ct);
        var rows = await cn.QueryAsync<string>(new CommandDefinition(
            SqlServerQueries.Databases, commandTimeout: 30, cancellationToken: ct));
        return rows.AsList();
    }
}
