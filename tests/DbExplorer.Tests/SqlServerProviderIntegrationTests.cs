using System.Diagnostics;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Search;
using DbExplorer.Providers.SqlServer;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Tests;

/// <summary>
/// The SQL Server provider against a real server. Skipped unless DBEXPLORER_TEST_MSSQL is set to
/// "host;port;user;password" of a server where the login may create the database dbx_provider_test.
/// </summary>
public sealed class SqlServerProviderIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_provider_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_MSSQL");
    private static readonly DataSearchOptions Options = new(1000, 30, 2000);

    private SqlServerProvider? _provider;
    private ConnectionProfile? _profile;

    private static ConnectionProfile Profile(string database)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = SqlServerProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test", TrustServerCertificate = true
        };
    }

    private static string AdminConnectionString =>
        SqlServerSql.BuildConnectionString(Profile("master"), "tests", forceReadWrite: true);

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await using (var admin = new SqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await Exec(admin, $"IF DB_ID('{Database}') IS NOT NULL BEGIN ALTER DATABASE {Database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Database}; END");
            await Exec(admin, $"CREATE DATABASE {Database}");
        }

        _profile = Profile(Database);
        await using (var cn = new SqlConnection(SqlServerSql.BuildConnectionString(_profile, "tests", forceReadWrite: true)))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE TABLE dbo.Numbers (Id int PRIMARY KEY, Amount decimal(38, 0), Name nvarchar(50));
                INSERT INTO dbo.Numbers VALUES (1, 12345678901234567890123456789, N'big'), (2, 42, N'small');
                """);
        }
        _provider = new SqlServerProvider(_profile);
    }

    public async Task DisposeAsync()
    {
        if (Settings is null) return;
        if (_provider is not null) await _provider.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var admin = new SqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await Exec(admin, $"IF DB_ID('{Database}') IS NOT NULL BEGIN ALTER DATABASE {Database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Database}; END");
    }

    private static async Task Exec(SqlConnection cn, string sql)
    {
        await using var cmd = new SqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Read_only_query_stops_on_the_server_at_the_row_limit()
    {
        Skip.If(Settings is null);
        // Billions of rows: reading them all would take far longer than the test allows.
        const string sql = "SELECT a.object_id FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c";
        var sw = Stopwatch.StartNew();
        var result = await _provider!.QueryReadOnlyAsync(sql, null, Options, maxRows: 10);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal(10, result.Rows.Count);
        Assert.True(result.IsTruncated);

        // The pooled connections that were cancelled still work.
        for (var i = 0; i < 3; i++)
        {
            var again = await _provider.QueryReadOnlyAsync("SELECT COUNT(*) FROM dbo.Numbers", null, Options);
            Assert.Equal(2, again.Rows[0][0]);
        }
    }
}
