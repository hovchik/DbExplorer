using DbExplorer.Core.Backup;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// Backup and restore against a real PostgreSQL server with the pg_dump, pg_restore and psql found on this computer:
/// a database is dumped in both formats, inspected, restored into a new database and over an existing one, and a
/// failing restore leaves nothing behind. Skipped unless DBEXPLORER_TEST_PG is set to "host;port;user;password" of a
/// server where the user may create databases, and pg_dump is installed.
/// </summary>
public sealed class PostgresBackupIntegrationTests : IAsyncLifetime
{
    private const string Source = "dbx_backup_src";
    private const string Copy = "dbx_backup_copy";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dbx-backup-" + Guid.NewGuid().ToString("N"));
    private PostgresProvider? _provider;

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
        Directory.CreateDirectory(_folder);
        await AdminAsync($"DROP DATABASE IF EXISTS {Source} WITH (FORCE)", $"DROP DATABASE IF EXISTS {Copy} WITH (FORCE)", $"CREATE DATABASE {Source}");
        await using (var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile(Source))))
        {
            await cn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                CREATE TABLE customers (id integer PRIMARY KEY, name text NOT NULL);
                CREATE TABLE orders (id serial PRIMARY KEY, customer_id integer REFERENCES customers (id), total numeric(8,2));
                INSERT INTO customers SELECT g, 'customer ' || g FROM generate_series(1, 50) g;
                INSERT INTO orders (customer_id, total) SELECT 1 + g % 50, g FROM generate_series(1, 500) g;
                CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
                """, cn);
            await cmd.ExecuteNonQueryAsync();
        }
        _provider = new PostgresProvider(Profile(Source));
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        if (Settings is not null)
            await AdminAsync($"DROP DATABASE IF EXISTS {Source} WITH (FORCE)", $"DROP DATABASE IF EXISTS {Copy} WITH (FORCE)");
        try { Directory.Delete(_folder, true); } catch { /* best effort */ }
    }

    private static async Task AdminAsync(params string[] statements)
    {
        await using var admin = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile("postgres")));
        await admin.OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, admin);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile(database)));
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, cn);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<bool> DatabaseExistsAsync(string name) =>
        await ScalarAsync("postgres", $"SELECT count(*) FROM pg_database WHERE datname = '{name}'") is long n && n > 0;

    private sealed class Lines : IProgress<BackupProgress>
    {
        public List<BackupProgress> All { get; } = [];
        public void Report(BackupProgress value) { lock (All) All.Add(value); }
    }

    private async Task<BackupSupport> SupportOrSkipAsync()
    {
        Skip.If(Settings is null, "DBEXPLORER_TEST_PG is not set");
        var support = await _provider!.GetBackupSupportAsync(null);
        Skip.If(!support.IsAvailable, support.Problem ?? "pg_dump is not available");
        return support;
    }

    [SkippableFact]
    public async Task A_custom_archive_restores_into_a_new_database_and_over_an_existing_one()
    {
        var support = await SupportOrSkipAsync();
        Assert.Contains("pg_dump", support.Tool);

        var file = Path.Combine(_folder, "shop.dump");
        var lines = new Lines();
        await _provider!.BackupAsync(new BackupRequest { Database = Source, Path = file, Format = BackupFormat.Custom }, null, lines);
        Assert.True(new FileInfo(file).Length > 0);
        Assert.Contains(lines.All, l => l.Line.Contains("dumping contents of table"));
        Assert.Contains("2 table(s) with their rows", lines.All[^1].Line);

        var info = await _provider.InspectBackupAsync(file, null);
        Assert.Equal(BackupFormat.Custom, info.Format);
        Assert.Equal(Source, info.Database);
        Assert.StartsWith("PostgreSQL ", info.Server);
        Assert.Contains("2 table(s), rows of 2 table(s)", info.Details[0]);
        Assert.NotNull(info.TakenAt);

        // New database.
        var restore = new RestoreRequest { Path = file, Database = Copy, CreateDatabase = true };
        Assert.Contains("CREATE DATABASE", await _provider.DescribeRestoreAsync(restore, null));
        lines = new Lines();
        await _provider.RestoreAsync(restore, null, lines);
        Assert.Equal(500L, await ScalarAsync(Copy, "SELECT count(*) FROM orders"));
        Assert.Equal(400L, await ScalarAsync(Copy, "SELECT count(*) FROM big_orders"));
        Assert.Contains(lines.All, l => l.Percent is > 0 and < 100);
        Assert.Equal(100, lines.All[^1].Percent);

        // Over the existing copy, after changing it: the backup's tables come back as they were.
        await ScalarAsync(Copy, "DELETE FROM orders WHERE id > 10; CREATE TABLE extra (x int)");
        await _provider.RestoreAsync(restore with { CreateDatabase = false }, null, new Lines());
        Assert.Equal(500L, await ScalarAsync(Copy, "SELECT count(*) FROM orders"));
        // Objects that aren't in the backup are kept.
        Assert.Equal(0L, await ScalarAsync(Copy, "SELECT count(*) FROM extra"));
    }

    [SkippableFact]
    public async Task A_plain_script_backs_up_schema_only_and_restores_with_psql()
    {
        await SupportOrSkipAsync();
        var file = Path.Combine(_folder, "shop schema.sql");
        await _provider!.BackupAsync(new BackupRequest { Database = Source, Path = file, Format = BackupFormat.PlainSql, SchemaOnly = true }, null, new Lines());
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("CREATE TABLE public.orders", text);
        Assert.DoesNotContain("COPY public.orders", text);
        Assert.DoesNotContain("OWNER TO", text);

        var info = await _provider.InspectBackupAsync(file, null);
        Assert.Equal(BackupFormat.PlainSql, info.Format);
        Assert.Equal("pg_dump SQL script", info.Kind);

        var lines = new Lines();
        await _provider.RestoreAsync(new RestoreRequest { Path = file, Database = Copy, CreateDatabase = true }, null, lines);
        Assert.Equal(0L, await ScalarAsync(Copy, "SELECT count(*) FROM orders"));
        Assert.Contains(lines.All, l => l.Line.StartsWith("Sent "));
    }

    [SkippableFact]
    public async Task A_failing_restore_into_a_new_database_rolls_back_and_drops_it()
    {
        await SupportOrSkipAsync();
        var file = Path.Combine(_folder, "broken.sql");
        await File.WriteAllTextAsync(file, "CREATE TABLE t (x int);\nINSERT INTO t VALUES (1);\nTHIS IS NOT SQL;\n");
        var ex = await Assert.ThrowsAsync<BackupException>(() =>
            _provider!.RestoreAsync(new RestoreRequest { Path = file, Database = Copy, CreateDatabase = true }, null, new Lines()));
        Assert.Contains("rolled back", ex.Message);
        Assert.Contains("syntax error", ex.Message);
        Assert.False(await DatabaseExistsAsync(Copy));
    }

    [SkippableFact]
    public async Task A_pg_dumpall_script_is_refused_with_an_explanation()
    {
        await SupportOrSkipAsync();
        var file = Path.Combine(_folder, "all.sql");
        await File.WriteAllTextAsync(file, "--\n-- PostgreSQL database cluster dump\n--\n");
        var ex = await Assert.ThrowsAsync<BackupException>(() => _provider!.InspectBackupAsync(file, null));
        Assert.Contains("pg_dumpall", ex.Message);
    }
}
