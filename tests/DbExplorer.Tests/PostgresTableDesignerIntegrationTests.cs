using DbExplorer.Application;
using DbExplorer.Application.Design;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// The table designer against a real PostgreSQL server: a design improved by its own suggestions is created and read
/// back from the catalog. Skipped unless DBEXPLORER_TEST_PG is set to "host;port;user;password" of a server where the
/// user may create the database dbx_designer_test.
/// </summary>
public sealed class PostgresTableDesignerIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_designer_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-pg-" + Guid.NewGuid().ToString("N"));
    private DatabaseSession? _session;
    private MetadataService? _metadata;

    private static ConnectionProfile Profile(string database)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = PostgresProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test"
        };
    }

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await using (var admin = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile("postgres"))))
        {
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
            await Exec(admin, $"CREATE DATABASE {Database}");
        }

        var profile = Profile(Database);
        await using (var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(profile)))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE TABLE customers (id integer PRIMARY KEY, name varchar(100) NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
                CREATE TABLE products (id integer PRIMARY KEY, title varchar(200), created_at timestamptz NOT NULL DEFAULT now());
                CREATE TABLE orders (id integer PRIMARY KEY, customer_id integer REFERENCES customers (id), created_at timestamptz NOT NULL DEFAULT now());
                """);
        }

        var paths = new AppPaths(_root);
        _metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        var provider = new PostgresProvider(profile);
        var snapshot = await _metadata.LoadAsync(profile, provider, forceRefresh: true);
        _session = new DatabaseSession(profile, new PostgresProviderFactory(), provider, await provider.GetServerVersionAsync(), snapshot);
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

    [SkippableFact]
    public async Task A_design_with_its_suggestions_applied_is_created_with_keys_and_indexes()
    {
        Skip.If(Settings is null);
        var context = DesignContext.From(_session!.Snapshot, PostgresProviderFactory.ProviderKey);
        var design = new TableDesign
        {
            Schema = "billing",
            Name = "Invoice",
            Columns =
            [
                new ColumnDesign { Name = "CustomerId", Type = "text" },
                new ColumnDesign { Name = "InvoiceDate", Type = "varchar", Size = "20" },
                new ColumnDesign { Name = "Total", Type = "real" }
            ]
        };

        // Apply every suggestion with a fix, one at a time, as "Apply all" does.
        var applied = new HashSet<string>();
        while (TableDesignAdvisor.Review(design, context).FirstOrDefault(s => s.Fix is not null && applied.Add(s.Key)) is { } next)
            design = next.Fix!(design);
        Assert.DoesNotContain(TableDesignAdvisor.Review(design, context), s => s.Severity == DesignSeverity.Error);

        var script = TableCreator.Script(design, context);
        await TableCreator.CreateAsync(_session, design, script, 30);

        var snapshot = await _metadata!.LoadAsync(_session.Profile, _session.Provider, forceRefresh: true);
        var table = Assert.Single(snapshot.Objects, o => o.Schema == "billing" && o.Type == DbObjectType.Table);
        Assert.Equal("invoices", table.Name);
        var columns = snapshot.ColumnsOf(table.Database, "billing", "invoices").OrderBy(c => c.Ordinal).ToList();
        Assert.Equal(["id", "customer_id", "invoice_date", "total", "created_at"], columns.Select(c => c.Name));
        Assert.True(columns[0].IsPrimaryKey && columns[0].IsIdentity);
        Assert.Equal("integer", columns[1].DataType);
        Assert.Equal("date", columns[2].DataType);
        Assert.StartsWith("numeric(18,2)", columns[3].DataType);
        var fk = Assert.Single(snapshot.ForeignKeysOf(table.Database, "billing", "invoices"));
        Assert.Equal(("customer_id", "customers", "id"), (fk.Columns, fk.ReferencedTable, fk.ReferencedColumns));
        Assert.Contains(snapshot.IndexesOf(table.Database, "billing", "invoices"), i => i.Columns == "customer_id");
    }

    private async Task<(TableDesign Design, DesignContext Context)> OpenAsync(string table)
    {
        var snapshot = await _metadata!.LoadAsync(_session!.Profile, _session.Provider, forceRefresh: true);
        var obj = snapshot.Objects.Single(o => o.Schema == "public" && o.Name == table && o.Type == DbObjectType.Table);
        var constraints = await _session.Provider.GetTableConstraintsAsync(obj);
        return (TableDesignLoader.Load(obj, snapshot, constraints, PostgresProviderFactory.ProviderKey).Design,
                DesignContext.From(snapshot, PostgresProviderFactory.ProviderKey));
    }

    [SkippableFact]
    public async Task An_existing_table_is_changed_in_place_and_keeps_its_rows()
    {
        Skip.If(Settings is null);
        await using (var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(_session!.Profile)))
        {
            await cn.OpenAsync();
            await Exec(cn, "INSERT INTO customers (id, name) VALUES (1, 'Ada'), (2, 'Grace'); ALTER TABLE customers ADD COLUMN city text; CREATE INDEX ix_customers_name ON customers (name);");
        }

        var (original, context) = await OpenAsync("customers");
        Assert.Equal("now()", original.Column("created_at")!.Default);
        Assert.Equal("ix_customers_name", Assert.Single(original.Indexes).Name);
        Assert.Equal(TableAlterScriptBuilder.NoChanges, TableCreator.Script(original, context, original));

        var edited = original
            .RenameColumn("name", "full_name")
            .WithColumn("full_name", c => c with { Size = "150" })
            .WithColumn("city", c => c with { IsNullable = false, Default = "'unknown'" })
            .AddColumn(new ColumnDesign { Name = "email", Type = "varchar", Size = "320", IsNullable = false, Default = "''" })
            .AddIndex(new IndexDesign { Columns = ["email"] });
        edited = edited with { Columns = edited.Columns.Where(c => c.Name != "created_at").ToList() };
        var review = TableDesignAdvisor.Review(edited, context, original);
        Assert.DoesNotContain(review, s => s.Severity == DesignSeverity.Error);
        Assert.Contains(review, s => s.Key == "alter-drop:created_at");

        await TableCreator.CreateAsync(_session, edited, TableCreator.Script(edited, context, original), 30);

        var snapshot = await _metadata!.LoadAsync(_session.Profile, _session.Provider, forceRefresh: true);
        var columns = snapshot.Columns.Where(c => c.Table == "customers" && c.Schema == "public").OrderBy(c => c.Ordinal).ToList();
        Assert.Equal(["id", "full_name", "city", "email"], columns.Select(c => c.Name));
        Assert.Equal("character varying(150)", columns[1].DataType);
        Assert.False(columns[2].IsNullable);
        Assert.Contains(snapshot.Indexes, i => i.Table == "customers" && i.Name == "ix_customers_name" && i.Columns == "full_name");
        Assert.Contains(snapshot.Indexes, i => i.Table == "customers" && i.Columns == "email");
        var rows = await _session.Provider.ExecuteScriptAsync("SELECT full_name || ':' || city FROM customers ORDER BY id;", null, 30);
        Assert.Equal(["Ada:unknown", "Grace:unknown"], rows.ResultSets[0].Rows.Select(r => (string)r[0]!));
    }

    [SkippableFact]
    public async Task A_change_existing_rows_do_not_fit_leaves_the_table_as_it_was()
    {
        Skip.If(Settings is null);
        await using (var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(_session!.Profile)))
        {
            await cn.OpenAsync();
            await Exec(cn, "INSERT INTO products (id, title) VALUES (1, 'A long product title');");
        }
        var (original, context) = await OpenAsync("products");
        var edited = original.RenameColumn("title", "name").WithColumn("name", c => c with { Size = "5" })
            .AddColumn(new ColumnDesign { Name = "sku", Type = "varchar", Size = "20" });
        Assert.Contains(TableDesignAdvisor.Review(edited, context, original), s => s.Key == "alter-type:title" && s.Severity == DesignSeverity.Warning);

        await Assert.ThrowsAnyAsync<Exception>(() => TableCreator.CreateAsync(_session, edited, TableCreator.Script(edited, context, original), 30));

        var snapshot = await _metadata!.LoadAsync(_session.Profile, _session.Provider, forceRefresh: true);
        Assert.Equal(["id", "title", "created_at"], snapshot.Columns.Where(c => c.Table == "products").OrderBy(c => c.Ordinal).Select(c => c.Name));
    }

    [SkippableFact]
    public async Task A_key_becomes_an_identity_and_gets_a_foreign_key()
    {
        Skip.If(Settings is null);
        var (original, context) = await OpenAsync("products");
        var edited = original.WithColumn("id", c => c with { IsIdentity = true })
            .AddColumn(new ColumnDesign { Name = "customer_id", Type = "integer" })
            .AddForeignKey(new ForeignKeyDesign { Column = "customer_id", ReferencedSchema = "public", ReferencedTable = "customers", ReferencedColumn = "id" });

        await TableCreator.CreateAsync(_session!, edited, TableCreator.Script(edited, context, original), 30);

        var snapshot = await _metadata!.LoadAsync(_session!.Profile, _session.Provider, forceRefresh: true);
        Assert.True(snapshot.Columns.Single(c => c.Table == "products" && c.Name == "id").IsIdentity);
        Assert.Contains(snapshot.ForeignKeys, f => f.Table == "products" && f.Columns == "customer_id" && f.ReferencedTable == "customers");
    }

    [SkippableFact]
    public async Task A_failing_script_leaves_nothing_behind()
    {
        Skip.If(Settings is null);
        var context = DesignContext.From(_session!.Snapshot, PostgresProviderFactory.ProviderKey);
        var design = new TableDesign
        {
            Schema = "public",
            Name = "broken",
            Columns = [new ColumnDesign { Name = "id", Type = "integer", IsPrimaryKey = true }],
            Indexes = [new IndexDesign { Columns = ["no_such_column"] }]
        };

        await Assert.ThrowsAnyAsync<Exception>(() => TableCreator.CreateAsync(_session, design, TableCreator.Script(design, context), 30));

        var snapshot = await _metadata!.LoadAsync(_session.Profile, _session.Provider, forceRefresh: true);
        Assert.DoesNotContain(snapshot.Objects, o => o.Name == "broken");
    }
}
