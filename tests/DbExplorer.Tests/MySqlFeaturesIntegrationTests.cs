using DbExplorer.Application;
using DbExplorer.Application.Design;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Modeling;
using DbExplorer.Application.Query.Plans;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.MySql;
using MySqlConnector;

namespace DbExplorer.Tests;

/// <summary>Against MySQL 8. Skipped unless DBEXPLORER_TEST_MYSQL is set to "host;port;user;password".</summary>
public sealed class MySqlFeaturesIntegrationTests() : MySqlFeaturesIntegrationTestsBase("DBEXPLORER_TEST_MYSQL");

/// <summary>Against MariaDB 10.6+. Skipped unless DBEXPLORER_TEST_MARIADB is set to "host;port;user;password".</summary>
public sealed class MariaDbFeaturesIntegrationTests() : MySqlFeaturesIntegrationTestsBase("DBEXPLORER_TEST_MARIADB");

/// <summary>
/// The engine-neutral features on a real MySQL/MariaDB server: the table designer (create and alter), execution plans
/// and the dry run. The login must be allowed to create the schema dbx_mysql_features.
/// </summary>
public abstract class MySqlFeaturesIntegrationTestsBase(string variable) : IAsyncLifetime
{
    private const string Database = "dbx_mysql_features";
    private const string Key = MySqlProviderFactory.ProviderKey;

    private readonly string? _settings = Environment.GetEnvironmentVariable(variable);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-myf-" + Guid.NewGuid().ToString("N"));
    private DatabaseSession? _session;
    private MetadataService? _metadata;

    private ConnectionProfile Profile(string database)
    {
        var p = _settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = Key, Host = p[0], Port = int.Parse(p[1]), UserName = p[2], Password = p[3], Database = database, Name = "test"
        };
    }

    public async Task InitializeAsync()
    {
        if (_settings is null) return;
        await using (var cn = new MySqlConnection(MySqlSql.BuildConnectionString(Profile(""))))
        {
            await cn.OpenAsync();
            await Exec(cn, $"""
                DROP DATABASE IF EXISTS {Database}; CREATE DATABASE {Database}; USE {Database};
                CREATE TABLE customers (id int NOT NULL AUTO_INCREMENT PRIMARY KEY, name varchar(100) NOT NULL,
                    created_at datetime NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE products (id int NOT NULL PRIMARY KEY, title varchar(200), created_at datetime NOT NULL DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE orders (id int NOT NULL AUTO_INCREMENT PRIMARY KEY, customer_id int NOT NULL, status varchar(20) NOT NULL,
                    created_at datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id));
                INSERT INTO customers (name) VALUES ('Ada'), ('Grace');
                INSERT INTO orders (customer_id, status) VALUES (1, 'new'), (1, 'new'), (2, 'paid');
                """);
        }

        var profile = Profile(Database);
        var paths = new AppPaths(_root);
        _metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        var provider = new MySqlProvider(profile);
        var snapshot = await _metadata.LoadAsync(profile, provider, forceRefresh: true);
        _session = new DatabaseSession(profile, new MySqlProviderFactory(), provider, await provider.GetServerVersionAsync(), snapshot);
    }

    public async Task DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
        MySqlConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static async Task Exec(MySqlConnection cn, string sql)
    {
        await using var cmd = new MySqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<MetadataSnapshot> ReloadAsync() =>
        await _metadata!.LoadAsync(_session!.Profile, _session.Provider, forceRefresh: true);

    private async Task<(TableDesign Design, DesignContext Context)> OpenAsync(string table)
    {
        var snapshot = await ReloadAsync();
        var obj = snapshot.Objects.Single(o => o.Name == table && o.Type == DbObjectType.Table);
        var constraints = await _session!.Provider.GetTableConstraintsAsync(obj);
        return (TableDesignLoader.Load(obj, snapshot, constraints, Key).Design, DesignContext.From(snapshot, Key));
    }

    [SkippableFact]
    public async Task A_design_with_its_suggestions_applied_is_created_in_the_database()
    {
        Skip.If(_settings is null);
        var context = DesignContext.From(_session!.Snapshot, Key);
        var design = new TableDesign
        {
            Database = Database,
            Name = "Invoice",
            Columns =
            [
                new ColumnDesign { Name = "Id", Type = "int", IsPrimaryKey = true, IsIdentity = true, IsNullable = false },
                new ColumnDesign { Name = "CustomerId", Type = "text" },
                new ColumnDesign { Name = "InvoiceDate", Type = "varchar", Size = "20" },
                new ColumnDesign { Name = "Total", Type = "double" },
                new ColumnDesign { Name = "Reference", Type = "varchar" }
            ]
        };
        Assert.Contains(TableDesignAdvisor.Review(design, context), s => s.Key == "length:Reference" && s.Severity == DesignSeverity.Error);

        var applied = new HashSet<string>();
        while (TableDesignAdvisor.Review(design, context).FirstOrDefault(s => s.Fix is not null && applied.Add(s.Key)) is { } next)
            design = next.Fix!(design);
        Assert.DoesNotContain(TableDesignAdvisor.Review(design, context), s => s.Severity == DesignSeverity.Error);

        var script = TableCreator.Script(design, context);
        Assert.Contains($"CREATE TABLE `{Database}`.", script);
        await TableCreator.CreateAsync(_session, design, script, 30);

        var snapshot = await ReloadAsync();
        var table = Assert.Single(snapshot.Objects, o => o.Type == DbObjectType.Table && o.Name.StartsWith("invoice", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Database, table.Schema);
        var columns = snapshot.ColumnsOf(table.Database, table.Schema, table.Name).OrderBy(c => c.Ordinal).ToList();
        Assert.True(columns[0].IsPrimaryKey && columns[0].IsIdentity);
        Assert.Equal("int", columns.Single(c => c.Name == "customer_id").BaseType);
        Assert.Equal("date", columns.Single(c => c.Name == "invoice_date").BaseType);
        Assert.Equal("decimal(18,2)", columns.Single(c => c.Name == "total").DataType);
        Assert.Contains(snapshot.ForeignKeysOf(table.Database, table.Schema, table.Name), f => f.Columns == "customer_id" && f.ReferencedTable == "customers");
    }

    [SkippableFact]
    public async Task An_existing_table_is_changed_in_place_and_keeps_its_rows()
    {
        Skip.If(_settings is null);
        await _session!.Provider.ExecuteScriptAsync(
            "ALTER TABLE customers ADD COLUMN city varchar(50) NULL; CREATE INDEX ix_customers_name ON customers (name);", Database, 30);

        var (original, context) = await OpenAsync("customers");
        Assert.Equal(Database, original.Schema);
        Assert.Equal("ix_customers_name", Assert.Single(original.Indexes).Name);
        Assert.Equal(TableAlterScriptBuilder.NoChanges, TableCreator.Script(original, context, original));

        var edited = original
            .RenameColumn("name", "full_name")
            .WithColumn("full_name", c => c with { Size = "150" })
            .WithColumn("city", c => c with { IsNullable = false, Default = "'unknown'" })
            .AddColumn(new ColumnDesign { Name = "email", Type = "varchar", Size = "320", IsNullable = false, Default = "''" })
            .AddIndex(new IndexDesign { Columns = ["email"] });
        edited = edited with { Columns = edited.Columns.Where(c => c.Name != "created_at").ToList() };
        Assert.DoesNotContain(TableDesignAdvisor.Review(edited, context, original), s => s.Severity == DesignSeverity.Error);

        var script = TableCreator.Script(edited, context, original);
        Assert.Contains("MODIFY COLUMN", script);
        await TableCreator.CreateAsync(_session, edited, script, 30);

        var snapshot = await ReloadAsync();
        var columns = snapshot.Columns.Where(c => c.Table == "customers").OrderBy(c => c.Ordinal).ToList();
        Assert.Equal(["id", "full_name", "city", "email"], columns.Select(c => c.Name));
        Assert.Equal("varchar(150)", columns[1].DataType);
        Assert.False(columns[2].IsNullable);
        Assert.Contains(snapshot.Indexes, i => i.Table == "customers" && i.Name == "ix_customers_name" && i.Columns == "full_name");
        var rows = await _session.Provider.ExecuteScriptAsync("SELECT CONCAT(full_name, ':', city) FROM customers ORDER BY id;", Database, 30);
        Assert.Equal(["Ada:unknown", "Grace:unknown"], rows.ResultSets[0].Rows.Select(r => (string)r[0]!));
    }

    [SkippableFact]
    public async Task A_key_becomes_auto_increment_and_gets_a_foreign_key_and_the_primary_key_moves()
    {
        Skip.If(_settings is null);
        var (original, context) = await OpenAsync("products");
        var edited = original.WithColumn("id", c => c with { IsIdentity = true })
            .AddColumn(new ColumnDesign { Name = "customer_id", Type = "int" })
            .AddForeignKey(new ForeignKeyDesign { Column = "customer_id", ReferencedSchema = Database, ReferencedTable = "customers", ReferencedColumn = "id" });
        await TableCreator.CreateAsync(_session!, edited, TableCreator.Script(edited, context, original), 30);

        var snapshot = await ReloadAsync();
        Assert.True(snapshot.Columns.Single(c => c.Table == "products" && c.Name == "id").IsIdentity);
        Assert.Contains(snapshot.ForeignKeys, f => f.Table == "products" && f.Columns == "customer_id" && f.ReferencedTable == "customers");

        // Drop the foreign key again, and move the primary key to a new column.
        (original, context) = await OpenAsync("products");
        var fk = Assert.Single(original.ForeignKeys);
        edited = original with { ForeignKeys = [] };
        edited = edited.WithColumn("id", c => c with { IsIdentity = false, IsPrimaryKey = false })
            .AddColumn(new ColumnDesign { Name = "sku", Type = "varchar", Size = "20", IsNullable = false, Default = "''" });
        var script = TableCreator.Script(edited, context, original);
        Assert.Contains($"DROP FOREIGN KEY `{fk.Name}`", script);
        Assert.Contains("DROP PRIMARY KEY", script);
        await TableCreator.CreateAsync(_session!, edited, script, 30);

        snapshot = await ReloadAsync();
        Assert.DoesNotContain(snapshot.ForeignKeys, f => f.Table == "products");
        Assert.False(snapshot.Columns.Single(c => c.Table == "products" && c.Name == "id").IsPrimaryKey);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plans_are_read_from_the_server(bool analyze)
    {
        Skip.If(_settings is null);
        const string sql = "SELECT c.name, COUNT(*) FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.status = 'new' GROUP BY c.name";
        var result = await _session!.Provider.ExecuteScriptAsync(PlanReader.BuildScript(sql, Key, analyze, _session.ServerVersion), Database, 30);
        var plan = Assert.Single(PlanReader.Read(result.ResultSets, Key, sql));
        Assert.Equal(analyze, plan.IsActual);
        // Both engines name a table by its alias in the plan.
        static bool Names(PlanNode n, string alias, string table) =>
            n.Object?.Split(' ')[0] is { } o && (o == alias || o.Contains(table, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Nodes, n => Names(n, "o", "orders"));
        Assert.Contains(plan.Nodes, n => Names(n, "c", "customers"));
        if (analyze) Assert.Contains(plan.Nodes, n => n.ActualRows is not null);
    }

    [SkippableFact]
    public async Task Dry_run_previews_an_update_rolls_it_back_and_stops_at_ddl()
    {
        Skip.If(_settings is null);
        var snapshot = await ReloadAsync();
        var result = await new DryRunService().RunAsync(_session!, snapshot, Database,
            "UPDATE orders SET status = 'paid' WHERE status = 'new';\nINSERT INTO orders (customer_id, status) VALUES (2, 'new');\nALTER TABLE orders ADD COLUMN x int;", 30);

        Assert.Equal(3, result.Statements.Count);
        var update = result.Statements[0];
        Assert.Null(update.Error);
        Assert.Equal(2, update.RowsAffected);
        Assert.Equal(2, update.Changes.Count);
        Assert.Null(result.Statements[1].Error);
        Assert.Equal(1, result.Statements[1].RowsAffected);
        Assert.Contains("commits", result.Statements[2].Error);

        var rows = await _session!.Provider.ExecuteScriptAsync("SELECT COUNT(*) FROM orders WHERE status = 'new';", Database, 30);
        Assert.Equal(2L, Convert.ToInt64(rows.ResultSets[0].Rows[0][0]));
        Assert.DoesNotContain((await ReloadAsync()).Columns, c => c.Table == "orders" && c.Name == "x");
    }
    [SkippableFact]
    public async Task A_changed_er_model_is_applied_and_then_matches_the_database()
    {
        Skip.If(_settings is null);
        var snapshot = await ReloadAsync();
        var constraints = new Dictionary<DbObject, DbTableConstraints>();
        foreach (var t in snapshot.Objects.Where(o => o.Type == DbObjectType.Table))
            constraints[t] = await _session!.Provider.GetTableConstraintsAsync(t);
        var model = ErModelReader.Read(snapshot, Key, Database, Database, constraints);
        Assert.False(ErModelScriptBuilder.Build(model, snapshot, Key, constraints).HasChanges);

        var invoices = new ModelTable
        {
            Design = new TableDesign
            {
                Schema = Database, Name = "invoices",
                Columns = [new ColumnDesign { Name = "id", Type = "int", IsPrimaryKey = true, IsIdentity = true, IsNullable = false },
                           new ColumnDesign { Name = "total", Type = "decimal", Size = "12,2", IsNullable = false, Default = "0" }]
            }
        };
        model = model.Add(invoices);
        model = model.LinkToTable(invoices.Id, null, model.FindByName(Database, "customers")!.Id)!;
        var products = model.FindByName(Database, "products")!;
        model = model.Update(products.Id, products.Design with { Name = "items" }).RenameColumn(products.Id, "title", "name");

        var script = ErModelScriptBuilder.Build(model, snapshot, Key, constraints);
        Assert.True(script.CanRun, string.Join("; ", script.Errors));
        Assert.DoesNotContain("\nGO\n", script.Script);
        await _session!.Provider.ExecuteScriptAsync(script.Script, Database, 60);

        snapshot = await ReloadAsync();
        Assert.Contains(snapshot.ForeignKeys, f => f.Table == "invoices" && f.ReferencedTable == "customers");
        Assert.Contains(snapshot.Columns, c => c.Table == "items" && c.Name == "name");
        Assert.All(snapshot.Objects.Where(o => o.Type == DbObjectType.Table), o => Assert.Equal(Database, o.Schema));
    }
}
