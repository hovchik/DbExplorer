using DbExplorer.Application;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.TestData;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// The test data generator against a real PostgreSQL server: a child table with a foreign key, a unique email, an
/// enum, CHECKs and a date ordering is filled twice, and every row the server accepts. Skipped unless
/// DBEXPLORER_TEST_PG is set to "host;port;user;password" of a server where the user may create dbx_testdata_test.
/// </summary>
public sealed class PostgresTestDataIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_testdata_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-pg-" + Guid.NewGuid().ToString("N"));
    private PostgresProvider? _provider;
    private MetadataSnapshot? _snapshot;
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
        await using (var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(_profile)))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE TYPE tier AS ENUM ('bronze', 'silver', 'gold');
                CREATE TABLE customers (
                    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    first_name varchar(40) NOT NULL,
                    last_name varchar(40) NOT NULL,
                    email varchar(80) NOT NULL UNIQUE,
                    tier tier NOT NULL,
                    status text NOT NULL CHECK (status IN ('new', 'vip', 'closed')),
                    created_at timestamptz NOT NULL DEFAULT now());
                CREATE TABLE orders (
                    order_no integer PRIMARY KEY,
                    customer_id integer NOT NULL REFERENCES customers (id),
                    quantity integer NOT NULL CHECK (quantity > 0 AND quantity <= 20),
                    total numeric(8,2) NOT NULL CHECK (total >= 0),
                    ordered_on date NOT NULL,
                    shipped_on date,
                    note text,
                    CHECK (shipped_on >= ordered_on));
                CREATE TABLE order_tags (
                    order_no integer REFERENCES orders (order_no),
                    tag_id integer NOT NULL CHECK (tag_id BETWEEN 1 AND 3),
                    PRIMARY KEY (order_no, tag_id));
                """);
        }

        var paths = new AppPaths(_root);
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        _provider = new PostgresProvider(_profile);
        _snapshot = await metadata.LoadAsync(_profile, _provider, forceRefresh: true);
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

    private async Task<long> CountAsync(string table)
    {
        await using var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(_profile!));
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", cn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private DbObject Table(string name) => _snapshot!.Objects.Single(o => o.Name == name && o.Type == DbObjectType.Table);

    [SkippableFact]
    public async Task Parent_child_and_junction_tables_are_filled_and_the_server_accepts_every_row()
    {
        Skip.If(Settings is null, "DBEXPLORER_TEST_PG is not set");

        // Orders need customers first: with none, nothing is generated.
        var orders = await TestDataService.LoadAsync(_provider!, _snapshot!, Table("orders"));
        var empty = TestDataGenerator.Generate(orders, 10, seed: 1);
        Assert.Empty(empty.Rows);
        Assert.Contains(empty.Notes, n => n.Contains("public.customers has no rows"));

        var customers = await TestDataService.LoadAsync(_provider!, _snapshot!, Table("customers"));
        Assert.Empty(customers.Warnings);
        var tier = customers.Columns.Single(c => c.Name == "tier");
        Assert.Equal(TestValueKind.OneOf, tier.Kind);
        Assert.Equal(["bronze", "silver", "gold"], tier.Choices);
        Assert.Equal(["new", "vip", "closed"], customers.Columns.Single(c => c.Name == "status").Choices);
        Assert.False(customers.Columns.Single(c => c.Name == "id").IsInserted);

        Assert.Equal(300, await TestDataService.InsertAsync(_provider!, Table("customers"), TestDataGenerator.Generate(customers, 300, seed: 10)));
        // A second run with fresh samples doesn't clash with the first one's emails.
        customers = await TestDataService.SampleAsync(_provider!, customers);
        await TestDataService.InsertAsync(_provider!, Table("customers"), TestDataGenerator.Generate(customers, 300, seed: 11));
        Assert.Equal(600, await CountAsync("customers"));

        orders = await TestDataService.LoadAsync(_provider!, _snapshot!, Table("orders"));
        Assert.Empty(orders.Warnings);
        Assert.Single(orders.Orders);
        var rows = TestDataGenerator.Generate(orders, 1000, seed: 3);
        Assert.Equal(1000, rows.Rows.Count);
        var progress = new List<int>();
        await TestDataService.InsertAsync(_provider!, Table("orders"), rows, new SyncProgress(progress.Add));
        Assert.Equal(1000, progress[^1]);
        Assert.Equal(1000, await CountAsync("orders"));

        // The key continues after the largest one.
        orders = await TestDataService.SampleAsync(_provider!, orders);
        Assert.Equal(1001, orders.Columns.Single(c => c.Name == "order_no").SequenceStart);
        await TestDataService.InsertAsync(_provider!, Table("orders"), TestDataGenerator.Generate(orders, 50, seed: 4));
        Assert.Equal(1050, await CountAsync("orders"));

        // Junction table: at most 3 tags per order, never the same pair twice, also across runs.
        var tags = await TestDataService.LoadAsync(_provider!, _snapshot!, Table("order_tags"));
        await TestDataService.InsertAsync(_provider!, Table("order_tags"), TestDataGenerator.Generate(tags, 2000, seed: 5));
        tags = await TestDataService.SampleAsync(_provider!, tags);
        var again = TestDataGenerator.Generate(tags, 500, seed: 6);
        await TestDataService.InsertAsync(_provider!, Table("order_tags"), again);
        Assert.Equal(2000 + again.Rows.Count, await CountAsync("order_tags"));
    }

    [SkippableFact]
    public async Task A_refused_row_rolls_the_whole_insert_back()
    {
        Skip.If(Settings is null, "DBEXPLORER_TEST_PG is not set");
        var customers = await TestDataService.LoadAsync(_provider!, _snapshot!, Table("customers"));
        // A fixed status the CHECK refuses: the first batch is fine until that row, and nothing must be kept.
        var status = customers.Columns.Single(c => c.Name == "status");
        var plan = customers.WithColumn(status with { Kind = TestValueKind.Fixed, FixedValue = "unknown" });
        await Assert.ThrowsAnyAsync<Exception>(() =>
            TestDataService.InsertAsync(_provider!, Table("customers"), TestDataGenerator.Generate(plan, 250, seed: 1)));
        Assert.Equal(0, await CountAsync("customers"));
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
