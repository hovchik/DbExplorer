using DbExplorer.Application;
using DbExplorer.Application.Documentation;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// Documentation export against a real PostgreSQL server: comments on the database, a schema, tables, columns, a
/// function and a trigger are read, defaults and checks are filled in, and both files are written. Skipped unless
/// DBEXPLORER_TEST_PG is set to "host;port;user;password" of a server where the user may create the database
/// dbx_doc_test.
/// </summary>
public sealed class PostgresDocumentationIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_doc_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-doc-" + Guid.NewGuid().ToString("N"));
    private PostgresProvider? _provider;
    private MetadataSnapshot? _snapshot;

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
            await Exec(admin, $"COMMENT ON DATABASE {Database} IS 'Shop data for the doc test'");
        }

        var profile = Profile(Database);
        await using (var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(profile)))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE SCHEMA sales;
                COMMENT ON SCHEMA sales IS 'Orders and invoices';
                CREATE TABLE sales.customers (id integer PRIMARY KEY, name varchar(100) NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
                CREATE TABLE sales.orders (
                    id integer PRIMARY KEY,
                    customer_id integer NOT NULL REFERENCES sales.customers (id),
                    qty integer NOT NULL CONSTRAINT qty_positive CHECK (qty > 0));
                CREATE INDEX orders_customer ON sales.orders (customer_id);
                COMMENT ON TABLE sales.customers IS 'People who buy';
                COMMENT ON COLUMN sales.customers.name IS 'Full name, as printed on invoices';
                CREATE VIEW sales.big_orders AS SELECT * FROM sales.orders WHERE qty > 10;
                COMMENT ON VIEW sales.big_orders IS 'Orders over ten items';
                CREATE FUNCTION sales.order_count(c integer) RETURNS bigint LANGUAGE sql AS 'SELECT count(*) FROM sales.orders WHERE customer_id = c';
                COMMENT ON FUNCTION sales.order_count(integer) IS 'Orders of one customer';
                CREATE FUNCTION sales.touch() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END';
                CREATE TRIGGER orders_touch BEFORE UPDATE ON sales.orders FOR EACH ROW EXECUTE FUNCTION sales.touch();
                COMMENT ON TRIGGER orders_touch ON sales.orders IS 'Keeps rows fresh';
                """);
        }

        var paths = new AppPaths(_root);
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        _provider = new PostgresProvider(profile);
        _snapshot = await metadata.LoadAsync(profile, _provider, forceRefresh: true);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static async Task Exec(NpgsqlConnection cn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Reads_comments_defaults_and_checks_into_both_formats()
    {
        Skip.If(Settings is null);

        var doc = await DocumentationBuilder.LoadAsync(_provider!, _snapshot!, Database, new DocumentationOptions());

        Assert.Empty(doc.Notes);
        Assert.Equal("Shop data for the doc test", doc.Comment);
        Assert.Equal("Orders and invoices", doc.SchemaComments["sales"]);

        var customers = doc.Tables.Single(t => t.Object.Name == "customers");
        Assert.Equal("People who buy", customers.Comment);
        Assert.Equal("Full name, as printed on invoices", customers.Columns.Single(c => c.Column.Name == "name").Comment);
        Assert.Equal("now()", customers.Columns.Single(c => c.Column.Name == "created_at").Default);

        var orders = doc.Tables.Single(t => t.Object.Name == "orders");
        Assert.Equal(new DocReference("sales", "customers", "id"), orders.Columns.Single(c => c.Column.Name == "customer_id").References);
        Assert.Contains(orders.Checks, c => c.Name == "qty_positive");
        Assert.Contains(orders.Indexes, i => i.Name == "orders_customer");

        Assert.Equal("Orders over ten items", doc.Views.Single().Comment);
        Assert.NotNull(doc.Views.Single().Definition);
        Assert.Equal("Orders of one customer", doc.Routines.Single(r => r.Object.Name == "order_count").Comment);
        Assert.Equal("Keeps rows fresh", doc.Triggers.Single().Comment);
        Assert.Equal(2, doc.Diagram!.Tables.Count);

        var html = DocumentationWriter.ToHtml(doc);
        Assert.Contains("Full name, as printed on invoices", html);
        Assert.Contains("qty_positive", html);
        var md = DocumentationWriter.ToMarkdown(doc);
        Assert.Contains("```mermaid", md);
        Assert.Contains("| Default |", md);
    }
}
