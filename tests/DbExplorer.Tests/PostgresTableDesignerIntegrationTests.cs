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
