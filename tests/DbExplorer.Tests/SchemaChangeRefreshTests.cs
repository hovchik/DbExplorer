using DbExplorer.Application;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;
using DbExplorer.Desktop;
using DbExplorer.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Tests;

/// <summary>Which scripts change the catalog, so the metadata is read again in the background after they run.</summary>
public sealed class SchemaChangesTests
{
    [Theory]
    [InlineData("CREATE TABLE orders (id int)")]
    [InlineData("create   or replace view v as select 1")]
    [InlineData("ALTER TABLE orders ADD note text")]
    [InlineData("DROP TABLE IF EXISTS orders")]
    [InlineData("CREATE UNIQUE INDEX ix ON orders (id)")]
    [InlineData("CREATE PROCEDURE p AS SELECT 1")]
    [InlineData("DROP FUNCTION f")]
    [InlineData("CREATE SCHEMA sales")]
    [InlineData("CREATE DATABASE shop")]
    [InlineData("SELECT 1;\nGO\nCREATE TABLE t (id int)")]
    [InlineData("IF OBJECT_ID('t') IS NULL CREATE TABLE t (id int)")]
    [InlineData("RENAME TABLE a TO b")]
    [InlineData("COMMENT ON TABLE orders IS 'x'")]
    [InlineData("EXEC sp_rename 'a', 'b'")]
    [InlineData("SELECT * INTO orders_backup FROM orders")]
    [InlineData("SELECT * INTO [orders backup] FROM orders")]
    [InlineData("SELECT * INTO UNLOGGED copy FROM orders")]
    [InlineData("CREATE TABLE IF NOT EXISTS t (id int)")]
    public void A_script_that_creates_alters_or_drops_objects_counts(string sql) =>
        Assert.True(SchemaChanges.Affects(sql));

    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("INSERT INTO orders VALUES (1)")]
    [InlineData("UPDATE orders SET drop = 1")]
    [InlineData("SELECT o.create, o.alter FROM orders o")]
    [InlineData("-- CREATE TABLE x\nSELECT 1")]
    [InlineData("/* DROP TABLE x */ SELECT 1")]
    [InlineData("SELECT 'CREATE TABLE x'")]
    [InlineData("CREATE TABLE #work (id int)")]
    [InlineData("DROP TABLE IF EXISTS #work")]
    [InlineData("CREATE TEMP TABLE work (id int)")]
    [InlineData("CREATE TEMPORARY TABLE work (id int)")]
    [InlineData("CREATE GLOBAL TEMPORARY TABLE work (id int)")]
    [InlineData("DROP TEMPORARY TABLE work")]
    [InlineData("SELECT * INTO #work FROM orders")]
    [InlineData("SELECT id INTO @id FROM orders")]
    [InlineData("SELECT * FROM orders INTO OUTFILE '/tmp/o.csv'")]
    [InlineData("INSERT INTO a SELECT * FROM b")]
    [InlineData("WITH x AS (SELECT 1 AS id) INSERT INTO a SELECT id FROM x")]
    [InlineData("CREATE LOGIN bob WITH PASSWORD = 'x'")]
    [InlineData("ALTER USER bob WITH PASSWORD 'x'")]
    [InlineData("CREATE ROLE readers")]
    [InlineData("ALTER SYSTEM SET work_mem = '8MB'")]
    [InlineData("ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO readers")]
    [InlineData("TRUNCATE TABLE orders")]
    public void Other_scripts_do_not(string sql) =>
        Assert.False(SchemaChanges.Affects(sql));
}

/// <summary>
/// After a script changes the schema the catalog is read again in the background: nothing waits for it, a second
/// request while one runs queues one more read instead of a second parallel one, and a failure keeps the old catalog.
/// </summary>
public sealed class BackgroundCatalogRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-schema-refresh-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ServiceProvider Services()
    {
        var services = App.ConfigureServices();
        services.AddSingleton(new AppPaths(_root));
        services.AddSingleton(DesktopTabTests.DialogFake.Create(out _));
        return services.BuildServiceProvider();
    }

    /// <summary>Counts catalog reads; each read can be held at <see cref="Gate"/> or made to fail.</summary>
    private sealed class Catalog
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public TaskCompletionSource? Gate { get; set; }
        public bool Fail { get; set; }
        public List<string> Ran { get; } = [];
        public string Table { get; set; } = "orders";

        public IDatabaseProvider Provider(string key = "Fake", IScriptSession? transaction = null) =>
            DesktopTabTests.ScriptedProvider.Create(new()
            {
                [nameof(IDatabaseProvider.ExecuteScriptAsync)] = args =>
                {
                    lock (Ran) Ran.Add((string)args[0]!);
                    return Task.FromResult(new QueryExecutionResult());
                },
                [nameof(IDatabaseProvider.BeginScriptSessionAsync)] = _ => Task.FromResult(transaction!),
                [nameof(IDatabaseProvider.GetObjectsAsync)] = _ => ObjectsAsync(),
                [nameof(IDatabaseProvider.GetColumnsAsync)] = _ => Task.FromResult<IReadOnlyList<DbColumn>>([]),
                [nameof(IDatabaseProvider.GetModulesAsync)] = _ => Task.FromResult<IReadOnlyList<DbModule>>([]),
                [nameof(IDatabaseProvider.GetForeignKeysAsync)] = _ => Task.FromResult<IReadOnlyList<DbForeignKey>>([]),
                [nameof(IDatabaseProvider.GetIndexesAsync)] = _ => Task.FromResult<IReadOnlyList<DbIndex>>([])
            }, key);

        private async Task<IReadOnlyList<DbObject>> ObjectsAsync()
        {
            Interlocked.Increment(ref _reads);
            if (Gate is { } gate) await gate.Task;
            if (Fail) throw new InvalidOperationException("catalog read failed");
            return [new DbObject { Database = "db", Schema = "dbo", Name = Table, Type = DbObjectType.Table }];
        }
    }

    private sealed class Transaction : IScriptSession
    {
        public bool Committed { get; private set; }
        public Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default) => Task.FromResult(0);
        public Task<QueryExecutionResult> QueryAsync(string sql, int timeoutSeconds, int maxRows = int.MaxValue,
            CancellationToken ct = default, ReadOnlyScript? readOnly = null) => Task.FromResult(new QueryExecutionResult());
        public Task CommitAsync(CancellationToken ct = default) { Committed = true; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public Task Requests_while_a_read_runs_queue_one_more_read_instead_of_starting_another() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var catalog = new Catalog { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var sessions = services.GetRequiredService<SessionService>();
        var session = DesktopTabTests.Session(catalog.Provider());
        var changes = 0;
        session.SnapshotChanged += (_, _) => changes++;

        var first = sessions.RefreshInBackgroundAsync(session);
        Assert.False(first.IsCompleted); // the caller does not wait
        await WaitFor(() => catalog.Reads == 1);
        var second = sessions.RefreshInBackgroundAsync(session);
        var third = sessions.RefreshInBackgroundAsync(session);
        Assert.Same(first, second);
        Assert.Same(first, third);
        catalog.Gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, catalog.Reads); // the first read plus one more for the requests made during it
        Assert.Equal(2, changes);
        Assert.Contains(session.Snapshot.Objects, o => o.Name == "orders");

        // Once idle, a new request reads again.
        catalog.Gate = null;
        catalog.Table = "invoices";
        await sessions.RefreshInBackgroundAsync(session).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, catalog.Reads);
        Assert.Contains(session.Snapshot.Objects, o => o.Name == "invoices");
    });

    [Fact]
    public Task A_failed_read_keeps_the_previous_catalog_and_is_reported() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var catalog = new Catalog { Fail = true };
        var sessions = services.GetRequiredService<SessionService>();
        var session = DesktopTabTests.Session(catalog.Provider());
        var before = session.Snapshot;
        Exception? failure = null;

        await sessions.RefreshInBackgroundAsync(session, failed: ex => failure = ex).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(before, session.Snapshot);
        Assert.IsType<InvalidOperationException>(failure);
    });

    [Fact]
    public Task A_Query_tab_reads_the_catalog_again_after_DDL_but_not_after_a_query() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var catalog = new Catalog();
        var session = DesktopTabTests.Session(catalog.Provider());
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SnapshotChanged += (_, _) => changed.TrySetResult();
        var tab = services.GetRequiredService<QueryViewModel>();
        tab.Attach(session);

        tab.Sql = "SELECT * FROM orders";
        await tab.ExecuteCommand.ExecuteAsync(null);
        await Task.Delay(100);
        Assert.Equal(0, catalog.Reads);

        tab.Sql = "CREATE TABLE invoices (id int)";
        catalog.Table = "invoices";
        await tab.ExecuteCommand.ExecuteAsync(null);
        Assert.False(tab.IsRunning); // the run ended without waiting for the catalog
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, catalog.Reads);
        Assert.Contains(session.Snapshot.Objects, o => o.Name == "invoices");
    });

    [Fact]
    public Task Inside_a_transaction_the_catalog_is_read_after_Commit_not_after_Rollback() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var catalog = new Catalog();
        var transaction = new Transaction();
        var session = DesktopTabTests.Session(catalog.Provider(transaction: transaction));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SnapshotChanged += (_, _) => changed.TrySetResult();
        var tab = services.GetRequiredService<QueryViewModel>();
        tab.Attach(session);
        tab.AutoCommit = false;

        tab.Sql = "CREATE TABLE invoices (id int)";
        await tab.ExecuteCommand.ExecuteAsync(null);
        Assert.True(tab.HasOpenTransaction);
        await Task.Delay(100);
        Assert.Equal(0, catalog.Reads);

        await tab.RollbackCommand.ExecuteAsync(null);
        await Task.Delay(100);
        Assert.Equal(0, catalog.Reads);

        await tab.ExecuteCommand.ExecuteAsync(null);
        await tab.CommitCommand.ExecuteAsync(null);
        Assert.True(transaction.Committed);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, catalog.Reads);
    });

    [Fact]
    public Task On_MySQL_DDL_in_a_transaction_is_read_at_once_because_it_commits_itself() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var catalog = new Catalog();
        var session = DesktopTabTests.Session(catalog.Provider("MySql", new Transaction()), providerKey: "MySql");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SnapshotChanged += (_, _) => changed.TrySetResult();
        var tab = services.GetRequiredService<QueryViewModel>();
        tab.Attach(session);
        tab.AutoCommit = false;

        tab.Sql = "CREATE TABLE invoices (id int)";
        await tab.ExecuteCommand.ExecuteAsync(null);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, catalog.Reads);
    });
}
