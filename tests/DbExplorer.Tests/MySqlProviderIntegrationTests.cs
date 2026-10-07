using DbExplorer.Application;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using DbExplorer.Providers.MySql;
using MySqlConnector;

namespace DbExplorer.Tests;

/// <summary>Against MySQL 8. Skipped unless DBEXPLORER_TEST_MYSQL is set to "host;port;user;password".</summary>
public sealed class MySqlProviderIntegrationTests() : MySqlProviderIntegrationTestsBase("DBEXPLORER_TEST_MYSQL");

/// <summary>Against MariaDB 10.6+. Skipped unless DBEXPLORER_TEST_MARIADB is set to "host;port;user;password".</summary>
public sealed class MariaDbProviderIntegrationTests() : MySqlProviderIntegrationTestsBase("DBEXPLORER_TEST_MARIADB");

/// <summary>
/// The MySQL/MariaDB provider against a real server: catalog, scripts, search, profiling, routines, constraints, locks
/// and activity. The login must be allowed to create the schema dbx_mysql_test and see other sessions (PROCESS).
/// </summary>
public abstract class MySqlProviderIntegrationTestsBase(string variable) : IAsyncLifetime
{
    protected const string Database = "dbx_mysql_test";
    private static readonly DataSearchOptions Options = new(1000, 30, 2000);

    private readonly string? _settings = Environment.GetEnvironmentVariable(variable);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-my-" + Guid.NewGuid().ToString("N"));
    private MySqlProvider? _provider;
    private ConnectionProfile? _profile;

    protected ConnectionProfile Profile(string database, bool readOnly = false)
    {
        var p = _settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = MySqlProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test", ReadOnly = readOnly
        };
    }

    private string AdminConnectionString => MySqlSql.BuildConnectionString(Profile(""));

    public async Task InitializeAsync()
    {
        if (_settings is null) return;
        await using (var cn = new MySqlConnection(AdminConnectionString))
        {
            await cn.OpenAsync();
            await Exec(cn, $"DROP DATABASE IF EXISTS {Database}; CREATE DATABASE {Database}; USE {Database};");
            await Exec(cn, """
                CREATE TABLE customers (
                    id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    name varchar(100) NOT NULL,
                    email varchar(200) NULL,
                    status varchar(20) NOT NULL DEFAULT 'active',
                    created_at datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    score decimal(10,2) DEFAULT 0,
                    name_upper varchar(100) AS (UPPER(name)) VIRTUAL,
                    CONSTRAINT ck_score CHECK (score >= 0),
                    UNIQUE KEY ux_email (email),
                    KEY ix_status_created (status, created_at DESC)
                );
                CREATE TABLE orders (
                    id bigint NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    customer_id int NOT NULL,
                    total decimal(10,2) NOT NULL,
                    note text,
                    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id)
                );
                CREATE VIEW open_orders AS SELECT o.id, o.total, c.name FROM orders o JOIN customers c ON c.id = o.customer_id;
                CREATE PROCEDURE count_orders(IN p_customer int, OUT p_count int) BEGIN SELECT COUNT(*) INTO p_count FROM orders WHERE customer_id = p_customer; SELECT id, total FROM orders WHERE customer_id = p_customer ORDER BY id; END;
                CREATE FUNCTION add_tax(amount decimal(10,2)) RETURNS decimal(10,2) DETERMINISTIC RETURN amount * 1.2;
                CREATE TRIGGER trg_orders_bi BEFORE INSERT ON orders FOR EACH ROW SET NEW.note = COALESCE(NEW.note, 'n/a');
                INSERT INTO customers (name, email, status) VALUES ('Acme', 'acme@example.com', 'active'), ('Globex', NULL, 'active'), ('Initech', 'it@example.com', 'closed');
                INSERT INTO orders (customer_id, total) VALUES (1, 100), (1, 50), (2, 75);
                """);
        }

        _profile = Profile(Database);
        _provider = new MySqlProvider(_profile);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        MySqlConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static async Task Exec(MySqlConnection cn, string sql)
    {
        await using var cmd = new MySqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    private MySqlProvider Provider => _provider!;

    private async Task<MetadataSnapshot> LoadAsync(ConnectionProfile profile)
    {
        var paths = new AppPaths(_root);
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        await using var provider = new MySqlProvider(profile);
        return await metadata.LoadAsync(profile, provider, forceRefresh: true);
    }

    [SkippableFact]
    public async Task The_catalog_lists_objects_columns_keys_indexes_and_definitions()
    {
        Skip.If(_settings is null, variable + " not set");
        var snapshot = await LoadAsync(_profile!);

        var objects = snapshot.Objects.Where(o => o.Database == Database).ToList();
        Assert.All(objects, o => Assert.Equal(Database, o.Schema));
        Assert.Contains(objects, o => o is { Name: "customers", Type: DbObjectType.Table, RowCount: not null });
        Assert.Contains(objects, o => o is { Name: "open_orders", Type: DbObjectType.View });
        Assert.Contains(objects, o => o is { Name: "count_orders", Type: DbObjectType.Procedure });
        Assert.Contains(objects, o => o is { Name: "add_tax", Type: DbObjectType.Function });
        Assert.Contains(objects, o => o is { Name: "trg_orders_bi", Type: DbObjectType.Trigger });

        var columns = snapshot.ColumnsOf(Database, Database, "customers").OrderBy(c => c.Ordinal).ToList();
        Assert.Equal(["id", "name", "email", "status", "created_at", "score", "name_upper"], columns.Select(c => c.Name));
        Assert.True(columns[0].IsPrimaryKey && columns[0].IsIdentity && !columns[0].IsNullable);
        Assert.Equal("varchar", columns[1].BaseType);
        Assert.Equal("varchar(100)", columns[1].DataType);
        Assert.True(columns[2].IsNullable);
        Assert.False(columns[2].IsPrimaryKey);
        Assert.True(columns[6].IsComputed);

        var fk = Assert.Single(snapshot.ForeignKeys, f => f.Database == Database);
        Assert.Equal(("orders", "customer_id", "customers", "id", Database), (fk.Table, fk.Columns, fk.ReferencedTable, fk.ReferencedColumns, fk.ReferencedSchema));

        var indexes = await Provider.GetIndexesAsync(includePhysicalStats: true);
        var composite = Assert.Single(indexes, i => i.Name == "ix_status_created");
        Assert.Equal("status, created_at DESC", composite.Columns);
        Assert.False(composite.IsUnique);
        Assert.Contains(indexes, i => i is { Name: "ux_email", IsUnique: true });
        Assert.Contains(indexes, i => i is { Name: "PRIMARY", IsPrimaryKey: true, Table: "orders" });

        var proc = Assert.Single(snapshot.Modules, m => m.Name == "count_orders");
        // MariaDB keeps the display width: int(11).
        Assert.Matches(@"^CREATE PROCEDURE `dbx_mysql_test`\.`count_orders`\(IN `p_customer` int(\(11\))?, OUT `p_count` int(\(11\))?\)", proc.Definition);
        var fn = Assert.Single(snapshot.Modules, m => m.Name == "add_tax");
        Assert.Contains("RETURNS decimal(10,2)", fn.Definition);
        Assert.Contains("DETERMINISTIC", fn.Definition);
        Assert.Contains("CREATE OR REPLACE VIEW `dbx_mysql_test`.`open_orders` AS", Assert.Single(snapshot.Modules, m => m.Name == "open_orders").Definition);
        Assert.StartsWith("CREATE TRIGGER `dbx_mysql_test`.`trg_orders_bi` BEFORE INSERT ON `dbx_mysql_test`.`orders`",
            Assert.Single(snapshot.Modules, m => m.Name == "trg_orders_bi").Definition);

        var showCreate = await Provider.GetDefinitionAsync(objects.Single(o => o.Name == "count_orders"));
        Assert.Contains("CREATE", showCreate);
    }

    [SkippableFact]
    public async Task A_server_level_connection_sees_every_application_schema_but_not_the_system_ones()
    {
        Skip.If(_settings is null, variable + " not set");
        var snapshot = await LoadAsync(Profile(""));
        Assert.Contains(snapshot.Objects, o => o.Database == Database && o.Name == "orders");
        Assert.DoesNotContain(snapshot.Objects, o => o.Database is "mysql" or "information_schema" or "performance_schema" or "sys");

        var databases = await new MySqlProviderFactory().ListDatabasesAsync(Profile(""));
        Assert.Contains(Database, databases);
        Assert.DoesNotContain("mysql", databases);
    }

    [SkippableFact]
    public async Task Scripts_return_every_result_set_and_report_errors_where_they_are()
    {
        Skip.If(_settings is null, variable + " not set");
        var result = await Provider.ExecuteScriptAsync(
            "SET @x = 2;\nSELECT id, name FROM customers ORDER BY id;\nUPDATE orders SET total = total WHERE customer_id = 1;\nSELECT @x AS x;",
            null, 30);
        Assert.Equal(2, result.ResultSets.Count);
        Assert.Equal(["id", "name"], result.ResultSets[0].Columns);
        Assert.Equal(3, result.ResultSets[0].Rows.Count);
        Assert.Equal(2L, Convert.ToInt64(result.ResultSets[1].Rows[0][0]));

        var ex = await Assert.ThrowsAsync<SqlExecutionException>(() =>
            Provider.ExecuteScriptAsync("SELECT 1;\nSELECT *\n  FORM customers;", null, 30));
        Assert.Equal(3, ex.Line);
        Assert.Equal(3, ex.Column);

        // Written for the mysql client: DELIMITER lines are understood.
        await Provider.ExecuteScriptAsync("""
            DROP PROCEDURE IF EXISTS hello;
            DELIMITER $$
            CREATE PROCEDURE hello() BEGIN SELECT 'hi' AS greeting; SELECT 2 AS two; END$$
            DELIMITER ;
            """, null, 30);
        var hello = await Provider.ExecuteScriptAsync("CALL hello();", null, 30);
        Assert.Equal("hi", hello.ResultSets[0].Rows[0][0]);
    }

    [SkippableFact]
    public async Task A_read_only_script_stops_on_the_server_at_the_row_limit()
    {
        Skip.If(_settings is null, variable + " not set");
        var sql = "SELECT id FROM customers ORDER BY id;";
        var script = new ReadOnlyScript([new ReadOnlyStatement(sql, 0, true)]);
        var result = await Provider.ExecuteScriptAsync(sql, null, 30, maxRows: 2, readOnly: script);
        var rs = Assert.Single(result.ResultSets);
        Assert.Equal(2, rs.Rows.Count);
        Assert.True(rs.IsTruncated);
        Assert.False(rs.TotalRowCountIsExact);

        // The limit does not leak into the next script on a pooled connection.
        var all = await Provider.ExecuteScriptAsync(sql, null, 30);
        Assert.Equal(3, all.ResultSets[0].Rows.Count);
    }

    [SkippableFact]
    public async Task Search_profile_and_top_values_read_the_table()
    {
        Skip.If(_settings is null, variable + " not set");
        var snapshot = await LoadAsync(_profile!);
        var table = snapshot.Objects.Single(o => o.Name == "customers");
        var columns = snapshot.ColumnsOf(Database, Database, "customers").ToList();

        var matches = await Provider.SearchTableAsync(new DbTableTarget(Database, Database, "customers", columns),
            SearchTerm.Create("ACME", SearchMatchMode.Contains, includeNumeric: true, includeGuid: false), Options);
        Assert.Contains(matches, m => m.Column == "name" && m.Value == "Acme" && m.RowKey == "id=1");
        Assert.Contains(matches, m => m.Column == "email");

        var underscore = await Provider.SearchTableAsync(new DbTableTarget(Database, Database, "customers", columns),
            SearchTerm.Create("_", SearchMatchMode.Contains, includeNumeric: false, includeGuid: false), Options);
        Assert.Empty(underscore);

        var profile = await Provider.ProfileTableAsync(table, columns, 100, Options);
        Assert.Equal(3, profile.SampledRows);
        var email = profile.Columns.Single(c => c.Column == "email");
        Assert.Equal((2L, 1L, 2L), (email.NonNullCount, email.NullCount, email.DistinctCount!.Value));
        Assert.Equal("acme@example.com", email.MinValue);

        var top = await Provider.GetTopValuesAsync(table, columns.Single(c => c.Name == "status"), 100, 5, Options);
        Assert.Equal(("active", 2L), (top[0].Value, top[0].Count));

        var probe = await Provider.QueryReadOnlyAsync("SELECT COUNT(*) FROM orders", Database, Options);
        Assert.Equal(3L, Convert.ToInt64(probe.Rows[0][0]));
    }

    [SkippableFact]
    public async Task Read_only_query_stops_on_the_server_at_the_row_limit()
    {
        Skip.If(_settings is null, variable + " not set");
        await using (var cn = new MySqlConnection(MySqlSql.BuildConnectionString(_profile!)))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE TABLE ten (n int PRIMARY KEY);
                INSERT INTO ten VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9);
                """);
        }
        // 10^9 rows: reading them all would hit the statement timeout.
        const string sql = "SELECT a.n FROM ten a, ten b, ten c, ten d, ten e, ten f, ten g, ten h, ten i";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await Provider.QueryReadOnlyAsync(sql, Database, Options, maxRows: 10);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal(10, result.Rows.Count);
        Assert.True(result.IsTruncated);

        // The pooled connections that were cancelled still work.
        for (var i = 0; i < 3; i++)
        {
            var again = await Provider.QueryReadOnlyAsync("SELECT COUNT(*) FROM orders", Database, Options);
            Assert.Equal(3L, Convert.ToInt64(again.Rows[0][0]));
        }
    }

    [SkippableFact]
    public async Task A_caller_giving_up_does_not_fail_the_shared_server_lookup()
    {
        Skip.If(_settings is null, variable + " not set");
        await using var provider = new MySqlProvider(_profile!);
        using var cts = new CancellationTokenSource();
        var first = provider.GetIndexesAsync(false, cts.Token, includeUsageStats: false);
        var second = provider.GetIndexesAsync(false, includeUsageStats: false);
        cts.Cancel();

        try { await first; } catch (Exception) { /* cancelled: its own outcome does not matter here */ }
        Assert.Contains(await second, i => i.Database == Database && i.Table == "customers");
    }

    [SkippableFact]
    public async Task Disposing_the_provider_closes_its_pooled_connections()
    {
        Skip.If(_settings is null, variable + " not set");
        // A login of its own, so other tests' connections are not counted.
        var user = "dbx_pool_" + variable.Split('_')[^1].ToLowerInvariant();
        await using var admin = new MySqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await Exec(admin, $"DROP USER IF EXISTS '{user}'@'%'; CREATE USER '{user}'@'%' IDENTIFIED BY 'Dbx_pool_pw1'; GRANT SELECT ON {Database}.* TO '{user}'@'%';");
        try
        {
            async Task<long> CountAsync()
            {
                await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM information_schema.processlist WHERE user = @u", admin);
                cmd.Parameters.AddWithValue("@u", user);
                return Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            var profile = Profile(Database);
            profile.UserName = user;
            profile.Password = "Dbx_pool_pw1";
            var provider = new MySqlProvider(profile);
            await provider.GetObjectsAsync();
            await provider.ExecuteScriptAsync("SELECT 1", null, 30);
            await provider.ExecuteScriptAsync("SELECT 1", "information_schema", 30);
            Assert.True(await CountAsync() > 0);

            await provider.DisposeAsync();
            var remaining = await CountAsync();
            for (var i = 0; i < 50 && remaining > 0; i++)
            {
                await Task.Delay(100);
                remaining = await CountAsync();
            }
            Assert.Equal(0, remaining);
        }
        finally
        {
            await Exec(admin, $"DROP USER IF EXISTS '{user}'@'%';");
        }
    }

    [SkippableFact]
    public async Task Procedures_return_out_values_and_functions_their_result()
    {
        Skip.If(_settings is null, variable + " not set");
        var snapshot = await LoadAsync(_profile!);
        var proc = snapshot.Objects.Single(o => o.Name == "count_orders");
        var parameters = await Provider.GetRoutineParametersAsync(proc);
        Assert.Equal(["p_customer", "p_count"], parameters.Select(p => p.Name));
        Assert.Equal(DbParameterDirection.Output, parameters[1].Direction);

        var result = await Provider.ExecuteRoutineAsync(proc, parameters, new Dictionary<string, object?> { ["p_customer"] = 1 }, 30);
        Assert.Equal(2L, Convert.ToInt64(result.OutputValues["p_count"]));
        Assert.Equal(2, Assert.Single(result.ResultSets).Rows.Count);

        var fn = snapshot.Objects.Single(o => o.Name == "add_tax");
        var fnParams = await Provider.GetRoutineParametersAsync(fn);
        var value = await Provider.ExecuteRoutineAsync(fn, fnParams, new Dictionary<string, object?> { ["amount"] = 10m }, 30);
        Assert.Equal(12m, Convert.ToDecimal(value.ResultSets[0].Rows[0][0]));
    }

    [SkippableFact]
    public async Task Table_constraints_come_back_as_sql()
    {
        Skip.If(_settings is null, variable + " not set");
        var constraints = await Provider.GetTableConstraintsAsync(new DbObject { Database = Database, Schema = Database, Name = "customers", Type = DbObjectType.Table });
        Assert.Equal("'active'", constraints.Defaults["status"]);
        Assert.Matches("(?i)^current_timestamp(\\(\\))?$", constraints.Defaults["created_at"]);
        Assert.Equal("0.00", constraints.Defaults["score"]);
        Assert.False(constraints.Defaults.ContainsKey("email"));
        var check = Assert.Single(constraints.Checks);
        Assert.Equal("ck_score", check.Name);
        Assert.Contains("score", check.Expression);
    }

    [SkippableFact]
    public async Task A_blocked_session_shows_in_locks_and_activity_with_its_blocker()
    {
        Skip.If(_settings is null, variable + " not set");
        await using var holder = new MySqlConnection(MySqlSql.BuildConnectionString(_profile!));
        await holder.OpenAsync();
        await Exec(holder, "START TRANSACTION; UPDATE orders SET total = total + 1 WHERE id = 1;");
        var holderId = holder.ServerThread;

        await using var waiter = new MySqlConnection(MySqlSql.BuildConnectionString(_profile!));
        await waiter.OpenAsync();
        var waiterId = waiter.ServerThread;
        var blocked = Exec(waiter, "SET SESSION innodb_lock_wait_timeout = 20; UPDATE orders SET total = total + 1 WHERE id = 1;");

        try
        {
            IReadOnlyList<DbLock> locks = [];
            for (var i = 0; i < 50 && !locks.Any(l => l.IsWaiting && l.SessionId == waiterId); i++)
            {
                await Task.Delay(100);
                locks = await Provider.GetLocksAsync();
            }
            var wait = Assert.Single(locks, l => l.IsWaiting && l.SessionId == waiterId && l.ResourceType != "TRANSACTION");
            Assert.Equal(holderId, wait.BlockedBy);
            Assert.Contains(locks, l => l.SessionId == holderId && !l.IsWaiting);

            var active = await Provider.GetActiveRequestsAsync();
            Assert.Contains(active, a => a.SessionId == waiterId && a.BlockedBy == holderId);
            Assert.Contains(active, a => a.SessionId == holderId && a.Status == "idle in transaction");
        }
        finally
        {
            await Exec(holder, "ROLLBACK;");
            await blocked;
        }
    }

    [SkippableFact]
    public async Task A_read_only_connection_is_refused_writes_by_the_server()
    {
        Skip.If(_settings is null, variable + " not set");
        await using var provider = new MySqlProvider(Profile(Database, readOnly: true));
        var read = await provider.ExecuteScriptAsync("SELECT COUNT(*) FROM customers;", null, 30);
        Assert.Single(read.ResultSets);
        await Assert.ThrowsAsync<SqlExecutionException>(() =>
            provider.ExecuteScriptAsync("UPDATE customers SET name = name;", null, 30));
    }

    [SkippableFact]
    public async Task Change_counters_and_top_queries_need_performance_schema()
    {
        Skip.If(_settings is null, variable + " not set");
        var enabled = Convert.ToInt64((await Provider.ExecuteScriptAsync("SELECT @@performance_schema;", null, 30)).ResultSets[0].Rows[0][0]) == 1;
        if (!enabled)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Provider.GetTopQueriesAsync(QueryStatOrder.TotalDuration, 10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Provider.GetTableChangeCountersAsync(Database));
            return;
        }

        var before = await Provider.GetTableChangeCountersAsync(Database);
        await Provider.ExecuteScriptAsync("INSERT INTO orders (customer_id, total) VALUES (3, 1);", null, 30);
        var after = await Provider.GetTableChangeCountersAsync(Database);
        long Inserts(IEnumerable<TableChangeCounter> c) => c.Where(x => x.Table == "orders").Sum(x => x.Inserts);
        Assert.Equal(1, Inserts(after) - Inserts(before));

        var top = await Provider.GetTopQueriesAsync(QueryStatOrder.TotalDuration, 10);
        Assert.NotEmpty(top);
        Assert.All(top, q => Assert.True(q.ExecutionCount > 0));
    }

    [SkippableFact]
    public async Task The_server_version_names_the_engine()
    {
        Skip.If(_settings is null, variable + " not set");
        var version = await Provider.GetServerVersionAsync();
        Assert.Matches("^(MySQL 8|MariaDB 1[0-9])", version);
    }
}
