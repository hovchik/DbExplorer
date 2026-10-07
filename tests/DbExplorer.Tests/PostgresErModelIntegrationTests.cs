using DbExplorer.Application;
using DbExplorer.Application.Design;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Modeling;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// The ER model against a real PostgreSQL server: a model read from the database, changed (a new table with keys to and
/// from it, a renamed table and column), turned into a script that runs in one transaction, and found matching the
/// database afterwards. Skipped unless DBEXPLORER_TEST_PG is set to "host;port;user;password" of a server where the
/// user may create the database dbx_ermodel_test.
/// </summary>
public sealed class PostgresErModelIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_ermodel_test";
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

    private async Task<(MetadataSnapshot Snapshot, Dictionary<DbObject, DbTableConstraints> Constraints)> ReadAsync()
    {
        var snapshot = await _metadata!.LoadAsync(_session!.Profile, _session.Provider, forceRefresh: true);
        var constraints = new Dictionary<DbObject, DbTableConstraints>();
        foreach (var table in snapshot.Objects.Where(o => o.Type == DbObjectType.Table))
            constraints[table] = await _session.Provider.GetTableConstraintsAsync(table);
        return (snapshot, constraints);
    }

    private async Task RunAsync(string script)
    {
        await using var run = await _session!.Provider.BeginScriptSessionAsync(null, transactional: true);
        await run.ExecuteAsync(script, 60);
        await run.CommitAsync();
    }

    [SkippableFact]
    public async Task A_changed_model_is_applied_and_then_matches_the_database()
    {
        Skip.If(Settings is null);
        const string pg = PostgresProviderFactory.ProviderKey;
        var (snapshot, constraints) = await ReadAsync();
        var model = ErModelReader.Read(snapshot, pg, Database, "public", constraints);
        Assert.False(ErModelScriptBuilder.Build(model, snapshot, pg, constraints).HasChanges);

        // A new invoices table referencing customers, orders referencing it, and products renamed with a column.
        var invoices = new ModelTable
        {
            Design = new TableDesign
            {
                Schema = "public", Name = "invoices",
                Columns = [new ColumnDesign { Name = "id", Type = "integer", IsPrimaryKey = true, IsIdentity = true, IsNullable = false },
                           new ColumnDesign { Name = "total", Type = "numeric", Size = "12,2", IsNullable = false, Default = "0" }]
            }
        };
        model = model.Add(invoices);
        model = model.LinkToTable(invoices.Id, null, model.FindByName("public", "customers")!.Id)!;
        var orders = model.FindByName("public", "orders")!;
        model = model.LinkToTable(orders.Id, null, invoices.Id)!;
        var products = model.FindByName("public", "products")!;
        model = model.Update(products.Id, products.Design with { Name = "items" }).RenameColumn(products.Id, "title", "name");

        var script = ErModelScriptBuilder.Build(model, snapshot, pg, constraints);
        Assert.True(script.CanRun, string.Join("; ", script.Errors));
        Assert.Equal((1, 2), (script.Created, script.Altered));
        await RunAsync(script.Script);

        (snapshot, constraints) = await ReadAsync();
        var columns = snapshot.Columns.Where(c => c.Table == "orders").Select(c => c.Name).ToList();
        Assert.Contains("invoice_id", columns);
        Assert.Contains(snapshot.ForeignKeys, f => f.Table == "invoices" && f.ReferencedTable == "customers" && f.Columns == "customer_id");
        Assert.Contains(snapshot.ForeignKeys, f => f.Table == "orders" && f.ReferencedTable == "invoices");
        Assert.Contains(snapshot.Columns, c => c.Table == "items" && c.Name == "name");

        // As the tab does after Run: the changed tables are read back, and the model then matches the database.
        foreach (var plan in script.Tables.Where(t => t.Action != ModelTableAction.Unchanged))
        {
            var table = snapshot.Objects.Single(o => o.Type == DbObjectType.Table && o.Schema == "public" && o.Name == plan.Table.Name);
            model = ErModelReader.Rebase(model, plan.Table.Id, ErModelReader.Table(table, snapshot, pg, Database, constraints));
        }
        var again = ErModelScriptBuilder.Build(model, snapshot, pg, constraints);
        Assert.False(again.HasChanges, again.Script);
    }

    [SkippableFact]
    public async Task A_rename_run_elsewhere_is_found_by_the_new_name()
    {
        Skip.If(Settings is null);
        const string pg = PostgresProviderFactory.ProviderKey;
        var (snapshot, constraints) = await ReadAsync();
        var model = ErModelReader.Read(snapshot, pg, Database, "public", constraints);
        var products = model.FindByName("public", "products")!;
        model = model.Update(products.Id, products.Design with { Name = "goods" });

        // The script is taken to a query tab and run there; the model was not read back.
        await RunAsync(ErModelScriptBuilder.Build(model, snapshot, pg, constraints).Script);
        (snapshot, constraints) = await ReadAsync();

        var again = ErModelScriptBuilder.Build(model, snapshot, pg, constraints);
        Assert.False(again.HasChanges, again.Script);
    }
}
