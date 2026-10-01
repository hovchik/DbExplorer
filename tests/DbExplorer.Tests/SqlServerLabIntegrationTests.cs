using DbExplorer.Application;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Search;
using DbExplorer.Providers.SqlServer;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Tests;

/// <summary>
/// The Lab features against a real SQL Server. Skipped unless DBEXPLORER_TEST_MSSQL is set to
/// "host;port;user;password" of a server where the login may create the database dbx_lab_test.
/// </summary>
public sealed class SqlServerLabIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_lab_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_MSSQL");
    private static readonly DataSearchOptions Options = new(1000, 30, 2000);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-ss-" + Guid.NewGuid().ToString("N"));
    private DatabaseSession? _session;
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

    private string ConnectionString => SqlServerSql.BuildConnectionString(_profile!, "tests", forceReadWrite: true);

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await using (var admin = new SqlConnection(SqlServerSql.BuildConnectionString(Profile("master"), "tests", forceReadWrite: true)))
        {
            await admin.OpenAsync();
            await Exec(admin, $"IF DB_ID('{Database}') IS NOT NULL BEGIN ALTER DATABASE {Database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Database}; END");
            await Exec(admin, $"CREATE DATABASE {Database}");
        }

        _profile = Profile(Database);
        await using (var cn = new SqlConnection(ConnectionString))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE TABLE dbo.Customers (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL, Status nvarchar(20), Region nvarchar(10));
                CREATE TABLE dbo.Orders (Id int PRIMARY KEY, CustomerId int, Total decimal(10,2), State nvarchar(20), Version rowversion);
                CREATE TABLE dbo.Settings ([Key] nvarchar(50) PRIMARY KEY, Value nvarchar(100));
                INSERT INTO dbo.Customers VALUES (1, N'Acme', N'active', N'EU'), (2, N'Globex', N'active', NULL), (3, N'Initech', N'closed', N'US');
                INSERT INTO dbo.Orders (Id, CustomerId, Total, State) VALUES (10, 1, 100, N'open'), (11, 1, 50, N'paid'), (12, 2, 75, N'open'), (13, 99, 10, N'open');
                INSERT INTO dbo.Settings VALUES (N'theme', N'dark'), (N'lang', N'en');
                """);
        }

        var paths = new AppPaths(_root);
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        var provider = new SqlServerProvider(_profile);
        var snapshot = await metadata.LoadAsync(_profile, provider, forceRefresh: true);
        _session = new DatabaseSession(_profile, new SqlServerProviderFactory(), provider, await provider.GetServerVersionAsync(), snapshot);
    }

    public async Task DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
        SqlConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static async Task Exec(SqlConnection cn, string sql, SqlTransaction? tx = null)
    {
        await using var cmd = new SqlCommand(sql, cn, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WriteAsync(string sql)
    {
        await using var cn = new SqlConnection(ConnectionString);
        await cn.OpenAsync();
        await Exec(cn, sql);
    }

    [SkippableFact]
    public async Task Change_recorder_finds_the_tables_and_rows_an_action_wrote()
    {
        Skip.If(Settings is null);
        var recorder = new ChangeRecorder();
        // Catalog row counts come from partition stats; all three tables are tiny, so all are snapshotted.
        var options = new RecorderOptions { SnapshotMaxRows = 100, Query = Options };
        var start = await recorder.StartAsync(_session!, null, options);
        Assert.NotNull(start.Marker);

        await WriteAsync("""
            UPDATE dbo.Customers SET Status = N'vip' WHERE Id = 1;
            INSERT INTO dbo.Orders (Id, CustomerId, Total, State) VALUES (14, 3, 20, N'open');
            DELETE FROM dbo.Settings WHERE [Key] = N'lang';
            """);
        var result = await recorder.StopAsync(_session!, start, options);

        Assert.Equal(["Customers", "Orders", "Settings"], result.Tables.Select(t => t.Table).Order());
        var vip = Assert.Single(result.Rows, r => r.Table == "Customers");
        Assert.Equal(RowChangeKind.Updated, vip.Kind);
        Assert.Equal("Status", vip.ChangedColumns);
        Assert.Contains(result.Rows, r => r.Table == "Orders" && r.Key == "Id=14");
        Assert.Contains(result.Rows, r => r.Table == "Settings" && r.Kind == RowChangeKind.Deleted);
    }

    [SkippableFact]
    public async Task Change_recorder_uses_rowversion_without_snapshots()
    {
        Skip.If(Settings is null);
        var recorder = new ChangeRecorder();
        var options = new RecorderOptions { SnapshotMaxRows = 0, Query = Options };
        var start = await recorder.StartAsync(_session!, null, options);
        await WriteAsync("UPDATE dbo.Orders SET State = N'shipped' WHERE Id = 12; UPDATE dbo.Customers SET Region = N'X' WHERE Id = 2;");
        var result = await recorder.StopAsync(_session!, start, options);

        var row = Assert.Single(result.Rows);
        Assert.Equal((RowChangeKind.Written, "Orders", "Id=12"), (row.Kind, row.Table, row.Key));
        Assert.StartsWith("counts only (no rowversion", result.Tables.Single(t => t.Table == "Customers").Detail);
    }

    [SkippableFact]
    public async Task Why_not_names_the_join_and_the_where_condition()
    {
        Skip.If(Settings is null);
        var debugger = new WhyNotDebugger(_session!, null, Options);
        const string sql = "SELECT TOP (10) o.Id, c.Name FROM dbo.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId WHERE o.State = N'open' AND o.Total > 60 ORDER BY o.Id";

        var orphan = await debugger.AnalyzeAsync(sql, "o.Id = 13");
        Assert.Contains("JOIN dbo.Customers c", orphan.Verdict);

        var cheap = await debugger.AnalyzeAsync(sql, "o.Id = 11");
        Assert.Contains(cheap.Steps, s => s.Outcome == ProbeOutcome.Fail && s.Stage == "WHERE o.State = N'open'");

        var limited = await debugger.AnalyzeAsync("SELECT TOP (1) * FROM dbo.Customers ORDER BY Id", "Id = 3");
        Assert.Contains("TOP / LIMIT", limited.Verdict);
        Assert.Contains(limited.Steps, s => s.Message.Contains("row 3"));

        var having = await debugger.AnalyzeAsync(
            "SELECT o.CustomerId, COUNT(*) AS n FROM dbo.Orders o GROUP BY o.CustomerId HAVING COUNT(*) > 1", "o.CustomerId = 2");
        Assert.Contains("HAVING", having.Verdict);
    }

    [SkippableFact]
    public async Task Dry_run_shows_changes_and_rolls_them_back()
    {
        Skip.If(Settings is null);
        var result = await new DryRunService().RunAsync(_session!, _session!.Snapshot, null, """
            UPDATE c SET Status = N'gone', Region = N'XX' FROM dbo.Customers c WHERE c.Status = N'active';
            DELETE FROM dbo.Orders WHERE CustomerId = 99;
            INSERT INTO dbo.Settings ([Key], Value) VALUES (N'tz', N'UTC');
            """, 30);

        Assert.All(result.Statements, s => Assert.Null(s.Error));
        Assert.Equal(2, result.Statements[0].RowsAffected);
        Assert.Equal(2, result.Statements[0].Changes.Count);
        Assert.Equal("Id=13", Assert.Single(result.Statements[1].Changes).Key);
        Assert.Equal("Key=tz", Assert.Single(result.Statements[2].Changes).Key);

        var check = await _session.Provider.QueryReadOnlyAsync("SELECT COUNT(*) FROM dbo.Customers WHERE Status = N'active'", null, Options);
        Assert.Equal(2, Convert.ToInt32(check.Rows[0][0]));
        check = await _session.Provider.QueryReadOnlyAsync("SELECT COUNT(*) FROM dbo.Settings WHERE [Key] = N'tz'", null, Options);
        Assert.Equal(0, Convert.ToInt32(check.Rows[0][0]));
    }

    [SkippableFact]
    public async Task Dry_run_with_a_trigger_falls_back_from_output()
    {
        Skip.If(Settings is null);
        await WriteAsync("CREATE TRIGGER dbo.trSettings ON dbo.Settings AFTER INSERT AS BEGIN SET NOCOUNT ON; END");
        var result = await new DryRunService().RunAsync(_session!, _session!.Snapshot, null,
            "INSERT INTO dbo.Settings ([Key], Value) VALUES (N'a', N'1'); UPDATE dbo.Settings SET Value = N'light' WHERE [Key] = N'theme';", 30);

        Assert.All(result.Statements, s => Assert.Null(s.Error));
        Assert.Equal(1, result.Statements[0].RowsAffected);
        Assert.Equal("inserted rows could not be listed", result.Statements[0].Note);
        Assert.Equal("Value", Assert.Single(result.Statements[1].Changes).ChangedColumns);
    }

    [SkippableFact]
    public async Task Lock_impact_sees_estimates_and_the_session_in_the_way()
    {
        Skip.If(Settings is null);
        await using var other = new SqlConnection(ConnectionString);
        await other.OpenAsync();
        await using var tx = (SqlTransaction)await other.BeginTransactionAsync();
        await Exec(other, "UPDATE dbo.Orders SET Total = Total WHERE Id = 10", tx);
        int spid;
        await using (var cmd = new SqlCommand("SELECT @@SPID", other, tx)) spid = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        var report = await new LockImpactAnalyzer().AnalyzeAsync(_session!, null, "UPDATE dbo.Orders SET Total = 0; ALTER TABLE dbo.Orders ADD Note int;");

        Assert.Contains(report.Targets, t => t.Operation == "UPDATE" && t.Table == "dbo.Orders" && t.EstimatedRows > 0);
        Assert.Contains(report.Targets, t => t.LockKind == "Sch-M");
        Assert.Equal(ImpactSeverity.Danger, report.Worst);
        Assert.Contains(report.Findings, f => f.Message.Contains($"Session {spid}"));
        await tx.RollbackAsync();
        Assert.DoesNotContain(await _session!.Provider.GetColumnsAsync(), c => c.Name == "Note");
    }

    [SkippableFact]
    public async Task Relationship_inference_and_containment()
    {
        Skip.If(Settings is null);
        var r = Assert.Single(RelationshipInference.Infer(_session!.Snapshot), x => x.ChildTable == "Orders" && x.ChildColumn == "CustomerId");
        Assert.Equal(("Customers", "Id"), (r.ParentTable, r.ParentColumn));
        var containment = await RelationshipInference.VerifyAsync(_session, r, 1000, Options);
        Assert.Equal((4L, 3L), (containment.Sampled, containment.Matched));
    }

    [SkippableFact]
    public async Task Value_cache_returns_frequent_values()
    {
        Skip.If(Settings is null);
        var cache = new ColumnValueCache(_session!);
        var table = _session!.Snapshot.Objects.Single(o => o.Name == "Orders");
        var column = _session.Snapshot.Columns.Single(c => c.Table == "Orders" && c.Name == "State");
        var loaded = new TaskCompletionSource();
        cache.ValuesLoaded += () => loaded.TrySetResult();
        Assert.Null(cache.TryGet(table, column));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var values = cache.TryGet(table, column)!;
        Assert.Equal(("open", 3L), (values[0].Value, values[0].Count));
    }
}
