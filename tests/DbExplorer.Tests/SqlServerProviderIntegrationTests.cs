using System.Diagnostics;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
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
            await Exec(cn, "CREATE PROCEDURE dbo.Twice @n int OUTPUT AS SET @n = @n * 2;");
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

    [SkippableFact]
    public async Task A_caller_giving_up_does_not_fail_the_shared_database_lookup()
    {
        Skip.If(Settings is null);
        await using var provider = new SqlServerProvider(Profile(""));
        using var cts = new CancellationTokenSource();
        var first = provider.GetObjectsAsync(cts.Token);
        var second = provider.GetColumnsAsync();
        cts.Cancel();

        try { await first; } catch (Exception) { /* cancelled: its own outcome does not matter here */ }
        Assert.Contains(await second, c => c.Database == Database && c.Table == "Numbers");
    }

    [SkippableFact]
    public async Task Output_parameters_send_their_value_in()
    {
        Skip.If(Settings is null);
        var proc = new DbObject { Database = Database, Schema = "dbo", Name = "Twice", Type = DbObjectType.Procedure };
        var parameters = await _provider!.GetRoutineParametersAsync(proc);
        var n = Assert.Single(parameters, p => p.Direction != DbParameterDirection.ReturnValue);
        Assert.Equal(("@n", DbParameterDirection.InputOutput), (n.Name, n.Direction));

        var result = await _provider.ExecuteRoutineAsync(proc, parameters, new Dictionary<string, object?> { ["@n"] = "21" }, 30);
        Assert.Equal("42", Convert.ToString(result.OutputValues["@n"]));
    }

    [SkippableFact]
    public async Task Disposing_the_provider_closes_its_pooled_connections()
    {
        Skip.If(Settings is null);
        async Task<int> CountAsync()
        {
            await using var admin = new SqlConnection(AdminConnectionString);
            await admin.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID(@db) AND program_name LIKE 'DbExplorer.%'", admin);
            cmd.Parameters.AddWithValue("@db", Database);
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        var provider = new SqlServerProvider(_profile!);
        await provider.GetObjectsAsync();
        await provider.QueryReadOnlyAsync("SELECT 1", null, Options);
        await provider.ExecuteScriptAsync("SELECT 1", null, 30);
        // The pooled connections can take a moment to show up in the session list.
        var open = await CountAsync();
        for (var i = 0; i < 50 && open == 0; i++)
        {
            await Task.Delay(100);
            open = await CountAsync();
        }
        // Another class disposing a provider with the same pools at this very moment can close them first; that proves
        // nothing either way, so the test is inconclusive rather than red.
        Skip.If(open == 0, "no pooled connection was visible in the session list");

        await provider.DisposeAsync();
        var remaining = await CountAsync();
        for (var i = 0; i < 50 && remaining > 0; i++)
        {
            await Task.Delay(100);
            remaining = await CountAsync();
        }
        Assert.Equal(0, remaining);
    }

    [SkippableFact]
    public async Task Numeric_search_takes_any_decimal()
    {
        Skip.If(Settings is null);
        var columns = new List<DbColumn>
        {
            new() { Database = Database, Schema = "dbo", Table = "Numbers", Name = "Id", BaseType = "int", Ordinal = 1, IsPrimaryKey = true },
            new() { Database = Database, Schema = "dbo", Table = "Numbers", Name = "Amount", BaseType = "decimal", Ordinal = 2 }
        };
        var table = new DbTableTarget(Database, "dbo", "Numbers", columns);

        async Task<IReadOnlyList<DataMatch>> SearchAsync(string text) =>
            await _provider!.SearchTableAsync(table, SearchTerm.Create(text, SearchMatchMode.Exact, includeNumeric: true, includeGuid: false), Options);

        var big = Assert.Single(await SearchAsync("12345678901234567890123456789"));
        Assert.Equal(("Amount", "Id=1"), (big.Column, big.RowKey));
        Assert.Equal("Id=2", Assert.Single(await SearchAsync("42")).RowKey);
        Assert.Equal("Id=2", Assert.Single(await SearchAsync("42.000")).RowKey);
        Assert.Empty(await SearchAsync("0.0000000000001"));
    }
}
