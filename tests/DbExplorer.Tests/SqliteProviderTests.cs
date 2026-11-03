using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using DbExplorer.Providers.Sqlite;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Tests;

/// <summary>The SQLite provider against a real file in the temp folder (no server needed, so these always run).</summary>
public sealed class SqliteProviderTests : IAsyncLifetime
{
    private static readonly DataSearchOptions Options = new(1000, 30, 2000);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dbx-sqlite-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_folder, "shop.db");
    private readonly SqliteProviderFactory _factory = new();

    private ConnectionProfile Profile(bool readOnly = false) => new()
    {
        ProviderKey = SqliteProviderFactory.ProviderKey, Host = File, Database = "", Name = "shop", ReadOnly = readOnly
    };

    public async Task InitializeAsync()
    {
        await SqliteProviderFactory.CreateFileAsync(File);
        await using var provider = _factory.Create(Profile());
        await provider.ExecuteScriptAsync("""
            CREATE TABLE customers (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                email varchar(200),
                status TEXT NOT NULL DEFAULT 'active',
                score NUMERIC(10,2) DEFAULT 0,
                name_upper TEXT GENERATED ALWAYS AS (upper(name)) VIRTUAL,
                CONSTRAINT ck_score CHECK (score >= 0)
            );
            CREATE UNIQUE INDEX ux_email ON customers (email);
            CREATE INDEX ix_active ON customers (status DESC) WHERE status = 'active';
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                customer_id INTEGER NOT NULL REFERENCES customers,
                total REAL NOT NULL CHECK (total > 0),
                note
            );
            CREATE TABLE order_lines (order_id INTEGER, line INTEGER, qty INTEGER, PRIMARY KEY (order_id, line),
                FOREIGN KEY (order_id) REFERENCES orders (id)) WITHOUT ROWID;
            CREATE VIEW open_orders AS SELECT o.id, o.total, c.name FROM orders o JOIN customers c ON c.id = o.customer_id;
            CREATE TRIGGER trg_orders_ai AFTER INSERT ON orders BEGIN UPDATE customers SET score = score + 1 WHERE id = NEW.customer_id; END;
            INSERT INTO customers (name, email, status) VALUES ('Acme', 'acme@example.com', 'active'), ('Globex', NULL, 'active'), ('Initech', 'it@example.com', 'closed');
            INSERT INTO orders (customer_id, total) VALUES (1, 100), (1, 50), (2, 75);
            """, null, 30);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Version_names_sqlite_and_the_file()
    {
        await using var provider = _factory.Create(Profile());
        var version = await provider.GetServerVersionAsync();
        Assert.StartsWith("SQLite 3.", version);
        Assert.Contains("shop.db", version);
    }

    [Fact]
    public void A_missing_file_is_not_created()
    {
        var missing = Profile();
        missing.Host = Path.Combine(_folder, "nope.db");
        var ex = Assert.Throws<FileNotFoundException>(() => _factory.Create(missing));
        Assert.Contains("New file", ex.Message);
        Assert.False(System.IO.File.Exists(missing.Host));
    }

    [Fact]
    public async Task Catalog_lists_tables_views_and_triggers_in_main()
    {
        await using var provider = _factory.Create(Profile());
        var objects = await provider.GetObjectsAsync();
        Assert.All(objects, o => { Assert.Equal("main", o.Database); Assert.Equal("main", o.Schema); });
        Assert.Contains(objects, o => o is { Name: "customers", Type: DbObjectType.Table });
        Assert.Contains(objects, o => o is { Name: "open_orders", Type: DbObjectType.View });
        Assert.Contains(objects, o => o is { Name: "trg_orders_ai", Type: DbObjectType.Trigger });
        Assert.DoesNotContain(objects, o => o.Name.StartsWith("sqlite_", StringComparison.Ordinal));
        Assert.DoesNotContain(objects, o => o.Name == "ux_email");
    }

    [Fact]
    public async Task A_lone_integer_key_of_a_without_rowid_table_is_not_an_identity()
    {
        await using var provider = _factory.Create(Profile());
        await provider.ExecuteScriptAsync("CREATE TABLE codes (id INTEGER PRIMARY KEY, label TEXT) WITHOUT ROWID;", null, 30);
        var id = (await provider.GetColumnsAsync()).Single(c => c is { Table: "codes", Name: "id" });
        Assert.True(id.IsPrimaryKey);
        Assert.False(id.IsIdentity); // the caller supplies it: SQLite does not number a WITHOUT ROWID key
    }

    [Fact]
    public async Task Columns_report_keys_rowid_identity_and_generated_columns()
    {
        await using var provider = _factory.Create(Profile());
        var columns = await provider.GetColumnsAsync();
        var id = columns.Single(c => c is { Table: "customers", Name: "id" });
        Assert.True(id.IsPrimaryKey);
        Assert.True(id.IsIdentity);
        Assert.False(id.IsNullable);
        var email = columns.Single(c => c is { Table: "customers", Name: "email" });
        Assert.Equal("varchar(200)", email.DataType);
        Assert.Equal("varchar", email.BaseType);
        Assert.True(email.IsNullable);
        Assert.True(columns.Single(c => c.Name == "name_upper").IsComputed);
        Assert.Equal("", columns.Single(c => c is { Table: "orders", Name: "note" }).DataType);
        // A composite key is not a rowid alias.
        Assert.DoesNotContain(columns.Where(c => c.Table == "order_lines"), c => c.IsIdentity);
        // Views have columns too.
        Assert.Equal(["id", "total", "name"], columns.Where(c => c.Table == "open_orders").OrderBy(c => c.Ordinal).Select(c => c.Name));
    }

    [Fact]
    public async Task Indexes_foreign_keys_and_constraints()
    {
        await using var provider = _factory.Create(Profile());
        var indexes = await provider.GetIndexesAsync(includePhysicalStats: true);
        var unique = indexes.Single(i => i.Name == "ux_email");
        Assert.True(unique.IsUnique);
        Assert.Equal("email", unique.Columns);
        var partial = indexes.Single(i => i.Name == "ix_active");
        Assert.Equal("status DESC", partial.Columns);
        Assert.Equal("status = 'active'", partial.Filter);
        Assert.Contains(indexes, i => i is { Table: "order_lines", IsPrimaryKey: true, Columns: "order_id, line" });

        var keys = await provider.GetForeignKeysAsync();
        var toCustomers = keys.Single(k => k.Table == "orders");
        Assert.Equal("customer_id", toCustomers.Columns);
        Assert.Equal("customers", toCustomers.ReferencedTable);
        Assert.Equal("id", toCustomers.ReferencedColumns); // REFERENCES customers with no column list means its key
        Assert.Contains(keys, k => k is { Table: "order_lines", ReferencedTable: "orders", ReferencedColumns: "id" });

        var constraints = await provider.GetTableConstraintsAsync(new DbObject { Name = "customers", Schema = "main" });
        Assert.Equal("'active'", constraints.Defaults["status"]);
        Assert.Equal("ck_score", Assert.Single(constraints.Checks).Name);
        Assert.Equal("(score >= 0)", constraints.Checks[0].Expression);
        var orderChecks = (await provider.GetTableConstraintsAsync(new DbObject { Name = "orders" })).Checks;
        Assert.Equal("(total > 0)", Assert.Single(orderChecks).Expression);
    }

    [Fact]
    public async Task Definitions_are_the_stored_create_statements()
    {
        await using var provider = _factory.Create(Profile());
        var view = await provider.GetDefinitionAsync(new DbObject { Name = "open_orders", Type = DbObjectType.View });
        Assert.StartsWith("CREATE VIEW open_orders", view);
        var table = await provider.GetDefinitionAsync(new DbObject { Name = "customers", Type = DbObjectType.Table });
        Assert.StartsWith("CREATE TABLE customers", table);
        Assert.Contains("CREATE UNIQUE INDEX ux_email", table);
        var modules = await provider.GetModulesAsync();
        Assert.Contains(modules, m => m is { Name: "trg_orders_ai", Type: DbObjectType.Trigger } && m.Definition!.Contains("AFTER INSERT"));
    }

    [Fact]
    public async Task Scripts_return_every_result_set_and_count_changes()
    {
        await using var provider = _factory.Create(Profile());
        var result = await provider.ExecuteScriptAsync(
            "UPDATE customers SET status = 'vip' WHERE id = 1; SELECT id, name FROM customers ORDER BY id; SELECT COUNT(*) AS n FROM orders;", null, 30);
        Assert.Equal(2, result.ResultSets.Count);
        Assert.Equal(["id", "name"], result.ResultSets[0].Columns);
        Assert.Equal(3, result.ResultSets[0].Rows.Count);
        Assert.Equal(3L, result.ResultSets[1].Rows[0][0]);
        Assert.Equal(1, result.RowsAffected);
    }

    [Fact]
    public async Task A_read_only_script_stops_just_past_the_row_limit()
    {
        await using var provider = _factory.Create(Profile());
        var sql = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 100000) SELECT i FROM n;";
        var result = await provider.ExecuteScriptAsync(sql, null, 30, maxRows: 10, readOnly: new ReadOnlyScript([new ReadOnlyStatement(sql, 0, true)]));
        var set = Assert.Single(result.ResultSets);
        Assert.Equal(10, set.Rows.Count);
        Assert.True(set.IsTruncated);
        Assert.False(set.TotalRowCountIsExact);

        var all = await provider.ExecuteScriptAsync(sql, null, 30, maxRows: 10);
        Assert.Equal(100000, all.ResultSets[0].TotalRowCount);
        Assert.True(all.ResultSets[0].TotalRowCountIsExact);
    }

    [Fact]
    public async Task Syntax_errors_point_at_the_token()
    {
        await using var provider = _factory.Create(Profile());
        var ex = await Assert.ThrowsAsync<SqlExecutionException>(() => provider.ExecuteScriptAsync("SELECT 1;\nSELECT * FORM customers;", null, 30));
        Assert.Equal(2, ex.Line);
        Assert.Equal(10, ex.Column);
    }

    [Fact]
    public async Task Cancelling_interrupts_a_long_query()
    {
        await using var provider = _factory.Create(Profile());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sql = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT COUNT(*) FROM n;";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteScriptAsync(sql, null, 0, cts.Token));
    }

    [Fact]
    public async Task A_timeout_stops_a_long_query()
    {
        await using var provider = _factory.Create(Profile());
        var sql = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT COUNT(*) FROM n;";
        await Assert.ThrowsAsync<TimeoutException>(() => provider.ExecuteScriptAsync(sql, null, timeoutSeconds: 1));
    }

    [Fact]
    public async Task Script_sessions_commit_or_roll_back()
    {
        await using var provider = _factory.Create(Profile());
        await using (var session = await provider.BeginScriptSessionAsync(null, transactional: true))
        {
            await session.ExecuteAsync("INSERT INTO customers (name) VALUES ('Rolled back');", 30);
            await session.RollbackAsync();
        }
        await using (var session = await provider.BeginScriptSessionAsync(null, transactional: true))
        {
            Assert.Equal(1, await session.ExecuteAsync("INSERT INTO customers (name) VALUES ('Kept');", 30));
            var inside = await session.QueryAsync("SELECT COUNT(*) FROM customers;", 30);
            Assert.Equal(4L, inside.ResultSets[0].Rows[0][0]);
            await session.CommitAsync();
        }
        var names = await provider.QueryReadOnlyAsync("SELECT name FROM customers ORDER BY id;", null, Options);
        Assert.Equal(["Acme", "Globex", "Initech", "Kept"], names.Rows.Select(r => (string)r[0]!));
    }

    [Fact]
    public async Task Foreign_keys_are_enforced()
    {
        await using var provider = _factory.Create(Profile());
        var ex = await Assert.ThrowsAsync<SqlExecutionException>(() =>
            provider.ExecuteScriptAsync("INSERT INTO orders (customer_id, total) VALUES (999, 1);", null, 30));
        Assert.Contains("FOREIGN KEY", ex.Message);
    }

    [Fact]
    public async Task A_read_only_connection_refuses_writes()
    {
        await using var provider = _factory.Create(Profile(readOnly: true));
        var ex = await Assert.ThrowsAsync<SqlExecutionException>(() =>
            provider.ExecuteScriptAsync("DELETE FROM orders;", null, 30));
        Assert.Contains("readonly", ex.Message.Replace("-", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_profile_and_top_values()
    {
        await using var provider = _factory.Create(Profile());
        var columns = await provider.GetColumnsAsync();
        var customerColumns = columns.Where(c => c.Table == "customers").ToList();
        var term = SearchTerm.Create("ACME", SearchMatchMode.Contains, includeNumeric: true, includeGuid: false);
        var matches = await provider.SearchTableAsync(new DbTableTarget("main", "main", "customers", customerColumns), term, Options);
        Assert.Contains(matches, m => m is { Column: "name", Value: "Acme", RowKey: "id=1" });
        Assert.Contains(matches, m => m is { Column: "email", Value: "acme@example.com" });

        var number = SearchTerm.Create("75", SearchMatchMode.Exact, includeNumeric: true, includeGuid: false);
        var orderColumns = columns.Where(c => c.Table == "orders").ToList();
        var byTotal = await provider.SearchTableAsync(new DbTableTarget("main", "main", "orders", orderColumns), number, Options);
        Assert.Contains(byTotal, m => m is { Column: "total", RowKey: "id=3" });

        var table = new DbObject { Name = "customers", Schema = "main", Database = "main" };
        var profile = await provider.ProfileTableAsync(table, customerColumns, 1000, Options);
        Assert.Equal(3, profile.SampledRows);
        var email = profile.Columns.Single(c => c.Column == "email");
        Assert.Equal(1, email.NullCount);
        Assert.Equal("acme@example.com", email.MinValue);

        var top = await provider.GetTopValuesAsync(table, customerColumns.Single(c => c.Name == "status"), 1000, 5, Options);
        Assert.Equal(("active", 2L), (top[0].Value, top[0].Count));
    }

    [Fact]
    public async Task Server_only_features_explain_themselves()
    {
        await using var provider = _factory.Create(Profile());
        Assert.Contains("file, not a server", (await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetLocksAsync())).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetActiveRequestsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTopQueriesAsync(QueryStatOrder.TotalDuration, 10));
        Assert.Null(await provider.GetChangeMarkerAsync(null));
        Assert.Equal(["main"], await _factory.ListDatabasesAsync(Profile()));
    }

    [Fact]
    public async Task Streaming_hands_over_every_row()
    {
        await using var provider = _factory.Create(Profile());
        var rows = new List<object?[]>();
        var stream = new ResultSetStream(0, _ => { }, rows.Add);
        var count = await provider.StreamScriptAsync("SELECT id FROM orders ORDER BY id;", null, stream);
        Assert.Equal(3, count);
        Assert.Equal(3L, rows[^1][0]);
    }

    [Fact]
    public async Task Disposing_releases_the_file()
    {
        var provider = _factory.Create(Profile());
        await provider.GetObjectsAsync();
        await provider.DisposeAsync();
        // Pooled connections would keep the file open (and locked on Windows).
        var moved = File + ".moved";
        System.IO.File.Move(File, moved);
        Assert.True(System.IO.File.Exists(moved));
    }

    [Theory]
    [InlineData("INTEGER", SqliteAffinity.Integer)]
    [InlineData("varchar(20)", SqliteAffinity.Text)]
    [InlineData("", SqliteAffinity.Blob)]
    [InlineData("DOUBLE PRECISION", SqliteAffinity.Real)]
    [InlineData("decimal(10,2)", SqliteAffinity.Numeric)]
    [InlineData("datetime", SqliteAffinity.Numeric)]
    public void Affinity_follows_sqlite_rules(string type, SqliteAffinity expected) =>
        Assert.Equal(expected, SqliteSql.Affinity(type));

    [Fact]
    public void File_paths_accept_quotes_urls_and_connection_strings()
    {
        var full = Path.GetFullPath(Path.Combine(_folder, "a b.db"));
        Assert.Equal(full, SqliteSql.FilePath("\"" + full + "\""));
        Assert.Equal(full, SqliteSql.FilePath("Data Source=" + full));
        Assert.Equal(full, SqliteSql.FilePath(new Uri(full).AbsoluteUri));
    }

    [Fact]
    public void Check_constraints_are_read_from_the_create_statement()
    {
        var checks = SqliteScript.CheckConstraints(
            "CREATE TABLE t (a INT CHECK (a > 0), b TEXT CONSTRAINT \"b ok\" CHECK (length(b) < 5 AND b <> ')'), CHECK (a < 100))");
        Assert.Equal([("ck_1", "(a > 0)"), ("b ok", "(length(b) < 5 AND b <> ')')"), ("ck_3", "(a < 100)")], checks);
    }
}
