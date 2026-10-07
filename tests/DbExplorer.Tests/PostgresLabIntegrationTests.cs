using DbExplorer.Application;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Query.Plans;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// The Lab features against a real PostgreSQL server. Skipped unless DBEXPLORER_TEST_PG is set to
/// "host;port;user;password" of a server where the user may create the database dbx_lab_test.
/// </summary>
public sealed class PostgresLabIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_lab_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");
    private static readonly DataSearchOptions Options = new(1000, 30, 2000);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-pg-" + Guid.NewGuid().ToString("N"));
    private DatabaseSession? _session;
    private ConnectionProfile? _profile;

    private static ConnectionProfile Profile(string database)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = PostgresProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test"
        };
    }

    private string ConnectionString => PostgresSql.BuildConnectionString(_profile!);

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await using (var admin = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile("postgres"))))
        {
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
            await Exec(admin, $"CREATE DATABASE {Database}");
        }

        _profile = Profile(Database);
        await using (var cn = new NpgsqlConnection(ConnectionString))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE TABLE customers (id int PRIMARY KEY, name text NOT NULL, status text, region text);
                CREATE TABLE orders (id int PRIMARY KEY, customer_id int, total numeric(10,2), state text);
                CREATE TABLE settings (key text PRIMARY KEY, value text);
                INSERT INTO customers VALUES (1, 'Acme', 'active', 'EU'), (2, 'Globex', 'active', NULL), (3, 'Initech', 'closed', 'US');
                INSERT INTO orders VALUES (10, 1, 100, 'open'), (11, 1, 50, 'paid'), (12, 2, 75, 'open'), (13, 99, 10, 'open');
                INSERT INTO settings VALUES ('theme', 'dark'), ('lang', 'en');
                ANALYZE;
                """);
            // Publish the setup's statistics now, or they arrive during a test and look like its changes.
            await Exec(cn, "SELECT pg_stat_force_next_flush()");
            await Exec(cn, "SELECT 1");
        }

        var paths = new AppPaths(_root);
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        var provider = new PostgresProvider(_profile);
        var snapshot = await metadata.LoadAsync(_profile, provider, forceRefresh: true);
        _session = new DatabaseSession(_profile, new PostgresProviderFactory(), provider, await provider.GetServerVersionAsync(), snapshot);
    }

    public async Task DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static async Task Exec(NpgsqlConnection cn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Writes from another connection, then makes it publish its statistics right away.</summary>
    private async Task WriteAsync(string sql)
    {
        await using var cn = new NpgsqlConnection(ConnectionString);
        await cn.OpenAsync();
        await Exec(cn, sql);
        await Exec(cn, "SELECT pg_stat_force_next_flush()");
        await Exec(cn, "SELECT 1");
    }

    [SkippableFact]
    public async Task Cost_lens_reads_the_estimated_plan_without_running_the_query()
    {
        Skip.If(Settings is null);
        // pg_sleep would take 30 s if the statement ran; an estimated plan returns at once.
        const string sql = "SELECT o.*, pg_sleep(30) FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.total > 20";
        var statement = CostLens.Estimable(sql)!;
        var started = DateTime.UtcNow;

        var estimate = await CostLens.EstimateAsync(_session!, null, statement, CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.NotNull(estimate);
        Assert.True(estimate!.Rows >= 1);
        Assert.NotNull(estimate.Cost);
    }

    [SkippableFact]
    public async Task Graphical_plan_reads_the_estimated_plan_of_each_statement()
    {
        Skip.If(Settings is null);
        const string sql = "SELECT o.* FROM orders o JOIN customers c ON c.id = o.customer_id;\nSELECT count(*) FROM settings";
        var result = await _session!.Provider.ExecuteScriptAsync(PlanReader.BuildScript(sql, "PostgreSQL", analyze: false), null, 30, CancellationToken.None);

        var plans = PlanReader.Read(result.ResultSets, "PostgreSQL", sql);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, p => Assert.False(p.IsActual));
        Assert.Contains(plans[0].Nodes, n => n.Object?.StartsWith("public.orders", StringComparison.Ordinal) == true);
        Assert.Equal("SELECT count(*) FROM settings", plans[1].Statement);
    }

    [SkippableFact]
    public async Task Graphical_plan_measures_a_write_and_rolls_it_back()
    {
        Skip.If(Settings is null);
        const string sql = "DELETE FROM orders WHERE state = 'open'";
        var result = await _session!.Provider.ExecuteScriptAsync(PlanReader.BuildScript(sql, "PostgreSQL", analyze: true), null, 30, CancellationToken.None);

        var plan = Assert.Single(PlanReader.Read(result.ResultSets, "PostgreSQL", sql));
        Assert.True(plan.IsActual);
        Assert.NotNull(plan.ExecutionTimeMs);
        Assert.Contains(plan.Nodes, n => n.ActualRows == 3);
        var count = await _session.Provider.ExecuteScriptAsync("SELECT count(*) FROM orders", null, 30, CancellationToken.None);
        Assert.Equal(4L, Convert.ToInt64(count.ResultSets[0].Rows[0][0]));
    }

    [SkippableFact]
    public async Task Change_recorder_finds_the_tables_and_rows_an_action_wrote()
    {
        Skip.If(Settings is null);
        var recorder = new ChangeRecorder();
        var options = new RecorderOptions { SnapshotMaxRows = 100, Query = Options };

        var start = await recorder.StartAsync(_session!, null, options);
        Assert.NotNull(start.Marker);
        Assert.True(start.Snapshots.Count >= 1); // the planner estimates are tiny after ANALYZE

        await WriteAsync("""
            UPDATE customers SET status = 'vip' WHERE id = 1;
            INSERT INTO orders VALUES (14, 3, 20, 'open');
            DELETE FROM settings WHERE key = 'lang';
            """);

        var result = await recorder.StopAsync(_session!, start, options);

        Assert.Equal(["customers", "orders", "settings"], result.Tables.Select(t => t.Table).Order());
        var vip = Assert.Single(result.Rows, r => r.Table == "customers");
        Assert.Contains(vip.Kind, new[] { RowChangeKind.Updated, RowChangeKind.Written });
        Assert.Contains(vip.Columns, c => c.Column == "status" && Equals(c.After, "vip"));
        Assert.Contains(result.Rows, r => r.Table == "orders" && r.Key == "id=14");
        Assert.Contains(result.Rows, r => r.Table == "settings" && r.Kind == RowChangeKind.Deleted && r.Key == "key=lang");
    }

    [SkippableFact]
    public async Task Change_recorder_uses_xmin_without_snapshots()
    {
        Skip.If(Settings is null);
        var recorder = new ChangeRecorder();
        var options = new RecorderOptions { SnapshotMaxRows = 0, Query = Options };
        var start = await recorder.StartAsync(_session!, null, options);
        await WriteAsync("UPDATE orders SET state = 'shipped' WHERE id = 12;");
        var result = await recorder.StopAsync(_session!, start, options);

        var row = Assert.Single(result.Rows);
        Assert.Equal((RowChangeKind.Written, "id=12"), (row.Kind, row.Key));
        Assert.StartsWith("rows written since start", Assert.Single(result.Tables).Detail);
    }

    [SkippableFact]
    public async Task Why_not_names_the_join_and_the_where_condition()
    {
        Skip.If(Settings is null);
        var debugger = new WhyNotDebugger(_session!, null, Options);
        const string sql = "SELECT o.id, c.name FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.state = 'open' AND o.total > 60";

        var orphan = await debugger.AnalyzeAsync(sql, "o.id = 13");
        Assert.Contains("JOIN customers c", orphan.Verdict);
        Assert.Contains(orphan.Steps, s => s.Stage == "Join values" && s.Sample!.Rows[0][0]!.Equals(99));

        var cheap = await debugger.AnalyzeAsync(sql, "o.id = 11");
        Assert.Contains(cheap.Steps, s => s.Outcome == ProbeOutcome.Fail && s.Stage == "WHERE o.state = 'open'");
        Assert.Contains(cheap.Steps, s => s.Outcome == ProbeOutcome.Fail && s.Stage == "WHERE o.total > 60");

        var missing = await debugger.AnalyzeAsync(sql, "o.id = 500");
        Assert.Contains("does not exist", missing.Verdict);

        var limited = await debugger.AnalyzeAsync("SELECT * FROM customers ORDER BY id LIMIT 1", "id = 3");
        Assert.Contains("TOP / LIMIT", limited.Verdict);
        Assert.Contains(limited.Steps, s => s.Message.Contains("row 3"));

        var having = await debugger.AnalyzeAsync(
            "SELECT o.customer_id, COUNT(*) FROM orders o GROUP BY o.customer_id HAVING COUNT(*) > 1", "o.customer_id = 2");
        Assert.Contains("HAVING", having.Verdict);
    }

    [SkippableFact]
    public async Task Dry_run_shows_changes_and_rolls_them_back()
    {
        Skip.If(Settings is null);
        var result = await new DryRunService().RunAsync(_session!, _session!.Snapshot, null, """
            UPDATE customers SET status = 'gone', region = 'XX' WHERE status = 'active';
            DELETE FROM orders WHERE customer_id = 99;
            INSERT INTO settings VALUES ('tz', 'UTC');
            """, 30);

        Assert.All(result.Statements, s => Assert.Null(s.Error));
        var update = result.Statements[0];
        Assert.Equal(2, update.RowsAffected);
        Assert.Equal(2, update.Changes.Count);
        Assert.All(update.Changes, c => Assert.Equal("region, status", string.Join(", ", c.Columns.Where(x => x.Changed).Select(x => x.Column).Order())));
        Assert.Equal("id=13", Assert.Single(result.Statements[1].Changes).Key);
        Assert.Equal("key=tz", Assert.Single(result.Statements[2].Changes).Key);

        var check = await _session.Provider.QueryReadOnlyAsync("SELECT count(*) FROM customers WHERE status = 'active'", null, Options);
        Assert.Equal(2L, check.Rows[0][0]);
        check = await _session.Provider.QueryReadOnlyAsync("SELECT count(*) FROM settings WHERE key = 'tz'", null, Options);
        Assert.Equal(0L, check.Rows[0][0]);
    }

    [SkippableFact]
    public async Task Lock_impact_sees_estimates_and_the_session_in_the_way()
    {
        Skip.If(Settings is null);
        await using var other = new NpgsqlConnection(ConnectionString);
        await other.OpenAsync();
        await using var tx = await other.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT * FROM orders WHERE id = 10 FOR UPDATE", other, tx))
            await cmd.ExecuteNonQueryAsync();

        var report = await new LockImpactAnalyzer().AnalyzeAsync(_session!, null, "UPDATE orders SET total = 0; ALTER TABLE orders ADD note text;");

        Assert.Contains(report.Targets, t => t.Operation == "UPDATE" && t.EstimatedRows > 0);
        Assert.Contains(report.Targets, t => t.LockKind == "ACCESS EXCLUSIVE");
        Assert.Equal(ImpactSeverity.Danger, report.Worst);
        Assert.Contains(report.Findings, f => f.Message.Contains($"Session {other.ProcessID}"));
        await tx.RollbackAsync();

        // Nothing ran: the column was not added.
        Assert.DoesNotContain(await _session!.Provider.GetColumnsAsync(), c => c.Name == "note");
    }

    [SkippableFact]
    public async Task Relationship_inference_and_containment()
    {
        Skip.If(Settings is null);
        var inferred = RelationshipInference.Infer(_session!.Snapshot);
        var r = Assert.Single(inferred, x => x.ChildTable == "orders" && x.ChildColumn == "customer_id");
        Assert.Equal(("customers", "id"), (r.ParentTable, r.ParentColumn));

        var containment = await RelationshipInference.VerifyAsync(_session, r, 1000, Options);
        Assert.Equal((4L, 3L), (containment.Sampled, containment.Matched));
    }

    [SkippableFact]
    public async Task Value_cache_returns_frequent_values()
    {
        Skip.If(Settings is null);
        var cache = new ColumnValueCache(_session!);
        var table = _session!.Snapshot.Objects.Single(o => o.Name == "orders");
        var column = _session.Snapshot.Columns.Single(c => c.Table == "orders" && c.Name == "state");
        var loaded = new TaskCompletionSource();
        cache.ValuesLoaded += () => loaded.TrySetResult();

        Assert.Null(cache.TryGet(table, column));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var values = cache.TryGet(table, column)!;
        Assert.Equal("open", values[0].Value);
        Assert.Equal(3, values[0].Count);
    }

    [SkippableFact]
    public async Task Schema_history_records_a_change_on_refresh()
    {
        Skip.If(Settings is null);
        var paths = new AppPaths(_root);
        var history = new SchemaHistoryStore(paths);
        var key = MetadataCache.CacheKey(_profile!);
        await history.RecordAsync(key, _session!.Snapshot);

        await WriteAsync("ALTER TABLE customers ADD email text; CREATE VIEW v_open AS SELECT * FROM orders WHERE state = 'open';");
        var metadata = new MetadataService(new MetadataCache(paths), history, new VirtualForeignKeyStore(paths));
        var fresh = await metadata.LoadAsync(_profile!, _session.Provider, forceRefresh: true);
        await history.RecordAsync(key, fresh);

        var versions = await history.GetVersionsAsync(key);
        Assert.True(versions.Count >= 2);
        var changes = await history.CompareAsync(key, versions[^1].Id, versions[0].Id);
        Assert.Contains(changes, c => c.Kind == SchemaChangeKind.Changed && c.Name == "customers");
        Assert.Contains(changes, c => c.Kind == SchemaChangeKind.Added && c.Name == "v_open");
    }

    [SkippableFact]
    public async Task Accepted_virtual_keys_reach_the_snapshot_but_not_the_cache()
    {
        Skip.If(Settings is null);
        var paths = new AppPaths(_root);
        var store = new VirtualForeignKeyStore(paths);
        var key = MetadataCache.CacheKey(_profile!);
        var r = RelationshipInference.Infer(_session!.Snapshot).Single(x => x.ChildColumn == "customer_id");
        store.Save(key, [r.ToForeignKey()]);

        // The catalog read in InitializeAsync is written to the cache in the background.
        for (var i = 0; i < 50 && await new MetadataCache(paths).TryLoadAsync(key) is null; i++) await Task.Delay(100);
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), store);
        var loaded = await metadata.LoadAsync(_profile!, _session.Provider, forceRefresh: false);
        Assert.Contains(loaded.ForeignKeys, f => f.IsVirtual && f.Table == "orders");

        var cached = await new MetadataCache(paths).TryLoadAsync(key);
        Assert.DoesNotContain(cached!.ForeignKeys, f => f.IsVirtual);
    }
}
