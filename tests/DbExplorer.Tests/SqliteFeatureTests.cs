using DbExplorer.Application;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Query;
using DbExplorer.Application.Query.Plans;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.Sqlite;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Tests;

/// <summary>The app's engine-aware features on SQLite: dialect, scripts, plans, connection strings, and a real session.</summary>
public sealed class SqliteFeatureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-sqlite-app-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_root, "data", "app.db");

    private ConnectionProfile Profile() => new() { ProviderKey = SqliteProviderFactory.ProviderKey, Host = File, Name = "app" };

    public async Task InitializeAsync()
    {
        await SqliteProviderFactory.CreateFileAsync(File);
        await using var provider = new SqliteProviderFactory().Create(Profile());
        await provider.ExecuteScriptAsync("""
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers (id), total REAL);
            INSERT INTO customers (name, city) VALUES ('Acme', 'Oslo'), ('Globex', 'Rome');
            INSERT INTO orders (customer_id, total) VALUES (1, 10), (2, 20);
            """, null, 30);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private SessionService Sessions()
    {
        var paths = new AppPaths(Path.Combine(_root, "app"));
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        var registry = new ProviderRegistry([new SqliteProviderFactory()]);
        return new SessionService(registry, metadata, new SshTunnelService(new KnownHostsStore(paths), new RejectUnknownHostKeys()));
    }

    [Fact]
    public async Task A_session_loads_the_catalog_and_lists_main()
    {
        await using var session = await Sessions().ConnectAsync(Profile());
        Assert.StartsWith("SQLite 3.", session.ServerVersion);
        Assert.Contains(session.Snapshot.Objects, o => o is { Name: "orders", Schema: "main", Database: "main" });
        Assert.Contains(session.Snapshot.ForeignKeys, k => k is { Table: "orders", ReferencedTable: "customers" });
        Assert.Equal(["main"], await session.GetServerDatabasesAsync());
    }

    [Fact]
    public void Dialect_writes_sqlite_sql()
    {
        var d = SqlDialect.For(SqlDialect.SqliteKey);
        Assert.Equal("SQLite", SqlDialect.EngineName(SqlDialect.SqliteKey));
        Assert.True(SqlDialect.SchemaIsDatabase(SqlDialect.SqliteKey));
        Assert.Equal("\"orders\"", d.Table("main", "orders"));
        Assert.Equal("\"aux\".\"orders\"", d.Table("aux", "orders"));
        Assert.Equal("DELETE FROM \"t\";", d.Truncate("\"t\""));
        Assert.Equal("", d.CreateSchemaIfMissing("main"));
        Assert.Equal("1", d.Literal(true));
        Assert.Equal("X'0AFF'", d.Literal(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("'2024-05-06 07:08:09'", d.Literal(new DateTime(2024, 5, 6, 7, 8, 9)));
        Assert.Equal("'it''s'", d.Literal("it's"));
        Assert.Equal("DROP VIEW IF EXISTS v;\nCREATE VIEW v AS SELECT 1", d.ToReplaceDefinition("CREATE VIEW v AS SELECT 1", DbObjectType.View));
    }

    [Fact]
    public async Task Table_designer_script_with_identity_runs_on_sqlite()
    {
        var design = new TableDesign
        {
            Schema = "main",
            Name = "invoices",
            Columns =
            [
                new ColumnDesign { Name = "id", Type = "integer", IsNullable = false, IsPrimaryKey = true, IsIdentity = true },
                new ColumnDesign { Name = "customer_id", Type = "integer", IsNullable = false },
                new ColumnDesign { Name = "created", Type = "datetime", IsNullable = false, Default = "CURRENT_TIMESTAMP" }
            ],
            ForeignKeys = [new ForeignKeyDesign { Column = "customer_id", ReferencedSchema = "main", ReferencedTable = "customers", ReferencedColumn = "id" }],
            Indexes = [new IndexDesign { Columns = ["customer_id"] }]
        };
        var script = TableScriptBuilder.Build(design, SqlDialect.SqliteKey, createSchema: true);
        Assert.Contains("\"id\" INTEGER NOT NULL CONSTRAINT \"PK_invoices\" PRIMARY KEY AUTOINCREMENT", script);
        Assert.DoesNotContain("CONSTRAINT \"PK_invoices\" PRIMARY KEY (", script);
        Assert.Contains("ON \"invoices\" (\"customer_id\")", script);

        await using var provider = new SqliteProviderFactory().Create(Profile());
        await provider.ExecuteScriptAsync(script, null, 30);
        await provider.ExecuteScriptAsync("INSERT INTO invoices (customer_id) VALUES (1), (2);", null, 30);
        var columns = await provider.GetColumnsAsync();
        Assert.True(columns.Single(c => c is { Table: "invoices", Name: "id" }).IsIdentity);
        var rows = await provider.ExecuteScriptAsync("SELECT id FROM invoices ORDER BY id;", null, 30);
        Assert.Equal([1L, 2L], rows.ResultSets[0].Rows.Select(r => (long)r[0]!));
    }

    [Fact]
    public void Designer_flags_an_identity_that_is_not_the_whole_key()
    {
        var design = new TableDesign
        {
            Name = "t",
            Columns =
            [
                new ColumnDesign { Name = "id", Type = "integer", IsIdentity = true, IsPrimaryKey = true },
                new ColumnDesign { Name = "part", Type = "integer", IsPrimaryKey = true }
            ]
        };
        var review = TableDesignAdvisor.Review(design, new DesignContext { ProviderKey = SqlDialect.SqliteKey });
        var error = Assert.Single(review, s => s.Key == "identity-key:id");
        Assert.Equal(DesignSeverity.Error, error.Severity);
        var fixedDesign = error.Fix!(design);
        Assert.Equal(["id"], fixedDesign.PrimaryKey.Select(c => c.Name));
    }

    [Fact]
    public void Alter_allows_renames_adds_and_drops_but_flags_what_sqlite_cannot_change()
    {
        var original = new TableDesign
        {
            Schema = "main",
            Name = "customers",
            Columns =
            [
                new ColumnDesign { Name = "id", OriginalName = "id", Type = "INTEGER", IsNullable = false, IsPrimaryKey = true },
                new ColumnDesign { Name = "name", OriginalName = "name", Type = "TEXT", IsNullable = false },
                new ColumnDesign { Name = "city", OriginalName = "city", Type = "TEXT" }
            ]
        };
        var edited = original with
        {
            Name = "clients",
            Columns =
            [
                original.Columns[0],
                original.Columns[1] with { Name = "full_name" },
                new ColumnDesign { Name = "email", Type = "TEXT" }
            ]
        };
        Assert.Empty(TableAlterAdvisor.SqliteLimits(original, edited));
        var steps = TableAlterScriptBuilder.Steps(original, edited, SqlDialect.SqliteKey);
        Assert.Equal(
        [
            "ALTER TABLE \"customers\" RENAME TO \"clients\";",
            "ALTER TABLE \"clients\" DROP COLUMN \"city\";",
            "ALTER TABLE \"clients\" RENAME COLUMN \"name\" TO \"full_name\";",
            "ALTER TABLE \"clients\" ADD COLUMN \"email\" TEXT NULL;"
        ], steps);

        var retyped = original with { Columns = [original.Columns[0], original.Columns[1] with { Type = "INTEGER" }, original.Columns[2]] };
        Assert.Contains(TableAlterAdvisor.SqliteLimits(original, retyped), l => l.Key == "sqlite-alter:name");
        var notNullAdd = original with { Columns = [.. original.Columns, new ColumnDesign { Name = "code", Type = "TEXT", IsNullable = false }] };
        Assert.Contains(TableAlterAdvisor.SqliteLimits(original, notNullAdd), l => l.Key == "sqlite-add:code");
    }

    /// <summary>What <see cref="TableCreator.CreateAsync"/> does: the whole script in one transactional session, then commit.</summary>
    private static async Task RunInOneTransaction(DbExplorer.Core.Abstractions.IDatabaseProvider provider, string script)
    {
        await using var run = await provider.BeginScriptSessionAsync(null, transactional: true);
        await run.ExecuteAsync(script, 30);
        await run.CommitAsync();
    }

    private static TableDesign Customers() => new()
    {
        Schema = "main", Name = "customers",
        Columns =
        [
            new ColumnDesign { Name = "id", OriginalName = "id", Type = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new ColumnDesign { Name = "name", OriginalName = "name", Type = "TEXT", IsNullable = false },
            new ColumnDesign { Name = "city", OriginalName = "city", Type = "TEXT" }
        ],
        Indexes = [new IndexDesign { Name = "ix_customers_city", Columns = ["city"] }]
    };

    [Fact]
    public void A_change_alter_table_cannot_make_is_written_as_a_rebuild()
    {
        var original = Customers();
        var edited = original with { Columns = [original.Columns[0], original.Columns[1], original.Columns[2] with { IsNullable = false, Default = "'Unknown'" }] };
        var steps = TableAlterScriptBuilder.Steps(original, edited, SqlDialect.SqliteKey);

        Assert.StartsWith("-- SQLite can't make this change", steps[0]);
        Assert.EndsWith("PRAGMA foreign_keys = OFF;", steps[0]);
        Assert.StartsWith("CREATE TABLE \"_dbx_rebuild_customers\"", steps[1]);
        Assert.Contains("CONSTRAINT \"PK_customers\" PRIMARY KEY", steps[1]);
        Assert.Contains("\"city\" TEXT NOT NULL DEFAULT 'Unknown'", steps[1]);
        Assert.DoesNotContain("CREATE INDEX", steps[1]);
        Assert.Equal("INSERT INTO \"_dbx_rebuild_customers\" (\"id\", \"name\", \"city\")\n    SELECT \"id\", \"name\", COALESCE(\"city\", 'Unknown') FROM \"customers\";", steps[2]);
        Assert.Equal(
        [
            "DROP TABLE \"customers\";",
            "PRAGMA legacy_alter_table = ON;",
            "ALTER TABLE \"_dbx_rebuild_customers\" RENAME TO \"customers\";",
            "PRAGMA legacy_alter_table = OFF;",
            "CREATE INDEX \"ix_customers_city\" ON \"customers\" (\"city\");",
            "PRAGMA foreign_key_check;",
            "PRAGMA foreign_keys = ON;"
        ], steps.Skip(3));

        var review = TableAlterAdvisor.Review(original, edited, new DesignContext { ProviderKey = SqlDialect.SqliteKey });
        Assert.DoesNotContain(review, s => s.Severity == DesignSeverity.Error);
        Assert.Contains(review, s => s.Key == "sqlite-rebuild");
        Assert.Contains(review, s => s.Key == "sqlite-rebuild-loses" && s.Severity == DesignSeverity.Warning);
    }

    [Fact]
    public void A_unique_constraint_index_gets_a_name_sqlite_allows()
    {
        var original = Customers() with { Indexes = [new IndexDesign { Name = "sqlite_autoindex_customers_1", Columns = ["name"], IsUnique = true, IsConstraint = true }] };
        var edited = original with { Columns = [original.Columns[0], original.Columns[1] with { Type = "VARCHAR(200)" }, original.Columns[2]] };
        var steps = TableAlterScriptBuilder.Steps(original, edited, SqlDialect.SqliteKey);
        Assert.Contains("CREATE UNIQUE INDEX \"UX_customers_name\" ON \"customers\" (\"name\");", steps);
    }

    [Fact]
    public async Task A_rebuild_keeps_rows_indexes_views_and_references()
    {
        await using var provider = new SqliteProviderFactory().Create(Profile());
        await provider.ExecuteScriptAsync("""
            CREATE INDEX ix_customers_city ON customers (city);
            CREATE VIEW customer_cities AS SELECT name, city FROM customers;
            INSERT INTO customers (name, city) VALUES ('Initech', NULL);
            """, null, 30);
        var original = Customers();
        var edited = original with { Columns = [original.Columns[0], original.Columns[1] with { Type = "VARCHAR(200)" }, original.Columns[2] with { IsNullable = false, Default = "'Unknown'" }] };

        await RunInOneTransaction(provider, TableAlterScriptBuilder.Build(original, edited, SqlDialect.SqliteKey));

        var rows = await provider.QueryReadOnlyAsync("SELECT c.id, c.name, v.city FROM customers c JOIN customer_cities v ON v.name = c.name ORDER BY c.id;", null, new DbExplorer.Core.Search.DataSearchOptions(10, 30, 1000));
        Assert.Equal(["Oslo", "Rome", "Unknown"], rows.Rows.Select(r => (string)r[2]!));
        var columns = (await provider.GetColumnsAsync()).Where(c => c.Table == "customers").ToList();
        Assert.Contains(columns, c => c.Name == "city" && !c.IsNullable);
        Assert.Contains((await provider.GetIndexesAsync(false)), i => i.Name == "ix_customers_city");
        Assert.DoesNotContain((await provider.GetObjectsAsync()), o => o.Name.StartsWith("_dbx_rebuild_", StringComparison.Ordinal));
        // orders still references customers, and foreign keys are on again afterwards.
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteScriptAsync("INSERT INTO orders (customer_id, total) VALUES (99, 1);", null, 30));
    }

    [Fact]
    public async Task A_rebuild_that_breaks_a_reference_changes_nothing()
    {
        await using var provider = new SqliteProviderFactory().Create(Profile());
        await provider.ExecuteScriptAsync("""
            CREATE TABLE notes (id INTEGER PRIMARY KEY, customer_id INTEGER, body TEXT);
            INSERT INTO notes (customer_id, body) VALUES (1, 'fine'), (42, 'orphan');
            """, null, 30);
        var original = new TableDesign
        {
            Schema = "main", Name = "notes",
            Columns =
            [
                new ColumnDesign { Name = "id", OriginalName = "id", Type = "INTEGER", IsNullable = false, IsPrimaryKey = true },
                new ColumnDesign { Name = "customer_id", OriginalName = "customer_id", Type = "INTEGER" },
                new ColumnDesign { Name = "body", OriginalName = "body", Type = "TEXT" }
            ]
        };
        var edited = original.AddForeignKey(new ForeignKeyDesign { Column = "customer_id", ReferencedSchema = "main", ReferencedTable = "customers", ReferencedColumn = "id" });

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunInOneTransaction(provider, TableAlterScriptBuilder.Build(original, edited, SqlDialect.SqliteKey)));
        Assert.Contains("notes", error.Message);
        Assert.Contains("Nothing was changed", error.Message);
        Assert.DoesNotContain((await provider.GetForeignKeysAsync()), f => f.Table == "notes");
        Assert.Equal(2L, (await provider.QueryReadOnlyAsync("SELECT count(*) FROM notes;", null, new DbExplorer.Core.Search.DataSearchOptions(10, 30, 1000))).Rows[0][0]);
        Assert.DoesNotContain((await provider.GetObjectsAsync()), o => o.Name.StartsWith("_dbx_rebuild_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Alter_steps_run_on_a_real_file()
    {
        await using var provider = new SqliteProviderFactory().Create(Profile());
        var original = new TableDesign
        {
            Schema = "main", Name = "customers",
            Columns =
            [
                new ColumnDesign { Name = "id", OriginalName = "id", Type = "INTEGER", IsNullable = false, IsPrimaryKey = true },
                new ColumnDesign { Name = "name", OriginalName = "name", Type = "TEXT", IsNullable = false },
                new ColumnDesign { Name = "city", OriginalName = "city", Type = "TEXT" }
            ]
        };
        var edited = original with
        {
            Columns = [original.Columns[0], original.Columns[1] with { Name = "full_name" }, new ColumnDesign { Name = "email", Type = "TEXT", Default = "''" }]
        };
        await using (var session = await provider.BeginScriptSessionAsync(null, transactional: true))
        {
            foreach (var step in TableAlterScriptBuilder.Steps(original, edited, SqlDialect.SqliteKey)) await session.ExecuteAsync(step, 30);
            await session.CommitAsync();
        }
        var columns = (await provider.GetColumnsAsync()).Where(c => c.Table == "customers").OrderBy(c => c.Ordinal).Select(c => c.Name);
        Assert.Equal(["id", "full_name", "email"], columns);
    }

    [Fact]
    public async Task Plans_come_from_explain_query_plan()
    {
        await using var provider = new SqliteProviderFactory().Create(Profile());
        const string sql = "SELECT c.name FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.total > 5 ORDER BY c.city;";
        var script = PlanReader.BuildScript(sql, SqlDialect.SqliteKey, analyze: true);
        Assert.StartsWith("EXPLAIN QUERY PLAN SELECT", script);
        var result = await provider.ExecuteScriptAsync(script, null, 30);
        var plan = Assert.Single(PlanReader.Read(result.ResultSets, SqlDialect.SqliteKey, sql));
        Assert.False(plan.IsActual);
        Assert.Contains(plan.Nodes, n => n is { Operator: "SCAN", Object: "o" });
        Assert.Contains(plan.Nodes, n => n is { Operator: "SEARCH", Object: "c" } && n.Detail!.Contains("PRIMARY KEY"));
        Assert.Contains(plan.Warnings, w => w.Kind == PlanWarningKind.FullScan);
        Assert.Contains(plan.Warnings, w => w.Message.Contains("temporary b-tree"));
        Assert.Contains("|--SCAN o", plan.Raw);
    }

    [Fact]
    public void Lock_impact_explains_the_file_lock()
    {
        var report = LockImpactAnalyzer.SqliteReport(["UPDATE orders SET total = 1", "SELECT 1"]);
        Assert.Equal(ImpactSeverity.Warning, report.Worst);
        Assert.Contains("whole database file", report.Findings[0].Message);
        Assert.Equal(ImpactSeverity.Info, LockImpactAnalyzer.SqliteReport(["SELECT 1"]).Worst);
    }

    [Theory]
    [InlineData("Data Source=C:\\data\\shop.db", "C:\\data\\shop.db")]
    [InlineData("Data Source=shop.sqlite;Cache=Shared", "shop.sqlite")]
    [InlineData("sqlite:///relative/shop.db", "relative/shop.db")]
    [InlineData("sqlite:////var/data/shop.db", "/var/data/shop.db")]
    [InlineData("jdbc:sqlite:C:/data/shop.db", "C:/data/shop.db")]
    public void Connection_strings_name_the_file(string text, string path)
    {
        Assert.True(ConnectionStrings.LooksLikeConnectionString(text));
        Assert.True(ConnectionStrings.TryParse(text, "Postgres", out var parsed, out var error), error);
        Assert.Equal("Sqlite", parsed.ProviderKey);
        Assert.Equal(path, parsed.Host);
    }

    [Fact]
    public void Sql_server_data_source_stays_sql_server()
    {
        Assert.True(ConnectionStrings.TryParse("Data Source=db01;Initial Catalog=Shop;User ID=sa", "Postgres", out var parsed, out _));
        Assert.Equal("SqlServer", parsed.ProviderKey);
    }

    [Fact]
    public void A_profile_is_copied_as_a_sqlite_string()
    {
        var p = new ConnectionProfile { ProviderKey = "Sqlite", Host = "C:\\data\\shop.db", ReadOnly = true };
        Assert.Equal("Data Source=C:\\data\\shop.db;Mode=ReadOnly", ConnectionStrings.Format(p, ConnectionStringStyle.KeyValue));
        Assert.Equal("sqlite:///C:/data/shop.db", ConnectionStrings.Format(p, ConnectionStringStyle.Url));
    }

    [Fact]
    public void Missing_or_foreign_files_get_file_advice()
    {
        var missing = ConnectionFailure.Classify(new FileNotFoundException("There is no SQLite file at x.db."));
        Assert.Equal("Database file not found", missing.Title);
        var notDb = ConnectionFailure.Classify(new SqliteException("SQLite Error 26: 'file is not a database'.", 26));
        Assert.Equal("Not a SQLite database", notDb.Title);
    }

    [Fact]
    public void Types_map_to_and_from_sqlite()
    {
        DbColumn Col(string type) => new() { Name = "c", DataType = type, BaseType = SqliteSql.BaseType(type) };
        Assert.Equal("integer", ColumnTypeMapper.Map(Col("bigint"), SqlDialect.PostgresKey, SqlDialect.SqliteKey).Type);
        Assert.Equal("varchar(50)", ColumnTypeMapper.Map(Col("nvarchar(50)"), SqlDialect.SqlServerKey, SqlDialect.SqliteKey).Type);
        Assert.Equal("numeric(10,2)", ColumnTypeMapper.Map(Col("decimal(10,2)"), SqlDialect.MySqlKey, SqlDialect.SqliteKey).Type);
        Assert.Equal("text", ColumnTypeMapper.Map(Col("TEXT"), SqlDialect.SqliteKey, SqlDialect.PostgresKey).Type);
        Assert.Equal("nvarchar(max)", ColumnTypeMapper.Map(Col("TEXT"), SqlDialect.SqliteKey, SqlDialect.SqlServerKey).Type);
        Assert.Equal("bigint", ColumnTypeMapper.Map(Col("INTEGER"), SqlDialect.SqliteKey, SqlDialect.MySqlKey).Type);
        var untyped = ColumnTypeMapper.Map(Col(""), SqlDialect.SqliteKey, SqlDialect.PostgresKey);
        Assert.Equal("bytea", untyped.Type);
        Assert.NotNull(untyped.Warning);
        Assert.Equal("CURRENT_TIMESTAMP", DefaultTranslator.Translate("(getdate())", SqlDialect.SqlServerKey, SqlDialect.SqliteKey, "datetime"));
        Assert.Equal("1", DefaultTranslator.Translate("true", SqlDialect.PostgresKey, SqlDialect.SqliteKey, "boolean"));
        Assert.Equal("public", ObjectCopyService.MapSchema("main", SqlDialect.SqliteKey, SqlDialect.PostgresKey));
    }

    [Fact]
    public void Completion_offers_sqlite_ddl_and_functions()
    {
        var ddl = DdlTemplates.For("Sqlite").Select(i => i.Label).ToList();
        Assert.Contains("CREATE VIRTUAL TABLE", ddl);
        Assert.Contains("ALTER TABLE RENAME COLUMN", ddl);
        Assert.DoesNotContain("CREATE PROCEDURE", ddl);
        Assert.DoesNotContain("ALTER TABLE ALTER COLUMN", ddl);
        Assert.Contains("\"a\"\"b\"", SqlSymbolFinder.QuoteFor("Sqlite")("a\"b"));
    }

    [Fact]
    public void A_file_connection_never_asks_for_a_password()
    {
        var file = new ConnectionProfile { Name = "Shop", ProviderKey = SqlDialect.SqliteKey, Host = Path.Combine(_root, "shop.db") };
        Assert.True(file.IsFile);
        Assert.False(file.MissingPassword);
        Assert.True(new ConnectionProfile { ProviderKey = SqlDialect.PostgresKey, UserName = "u" }.MissingPassword);

        // Reopened on start like any connection with its password saved.
        var (action, _, note) = DbExplorer.Application.Workspace.StartupConnection.Decide(
            new DbExplorer.Application.Workspace.WorkspaceState { ConnectionId = file.Id, WasConnected = true }, [file], true);
        Assert.Equal(DbExplorer.Application.Workspace.StartupConnectionAction.Connect, action);
        Assert.Null(note);
    }

    [Fact]
    public async Task Jobs_explain_that_a_file_has_no_scheduler()
    {
        await using var provider = new SqliteProviderFactory().Create(Profile());
        var catalog = await DbExplorer.Application.Jobs.JobCatalogLoader.LoadAsync(provider, null);
        Assert.False(catalog.Available);
        Assert.Contains("no job scheduler", catalog.Setup);
    }

    [Fact]
    public void A_new_file_connection_starts_with_an_empty_path()
    {
        var registry = new ProviderRegistry([new SqliteProviderFactory()]);
        var vm = new DbExplorer.Desktop.ViewModels.ConnectionDialogViewModel(registry, new ConnectionProfile { ProviderKey = SqliteProviderFactory.ProviderKey }, isNew: true);
        Assert.Equal("", vm.Host);
        Assert.True(vm.IsFileEngine);
    }
}
