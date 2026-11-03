using DbExplorer.Core.Backup;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.MySql;
using MySqlConnector;

namespace DbExplorer.Tests;

/// <summary>
/// Backup and restore against a real MySQL or MariaDB server with the mysqldump and mysql found on this computer.
/// Skipped unless DBEXPLORER_TEST_MYSQL is set to "host;port;user;password" of a server where the user may create
/// databases, and mysqldump is installed.
/// </summary>
public sealed class MySqlBackupIntegrationTests : IAsyncLifetime
{
    private const string Source = "dbx_backup_src";
    private const string Copy = "dbx_backup_copy";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_MYSQL");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dbx-mybackup-" + Guid.NewGuid().ToString("N"));
    private MySqlProvider? _provider;

    private static ConnectionProfile Profile(string database)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = MySqlProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test"
        };
    }

    private static async Task<object?> ExecAsync(string sql, string database = "")
    {
        await using var cn = new MySqlConnection(MySqlSql.BuildConnectionString(Profile(database)));
        await cn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, cn);
        return await cmd.ExecuteScalarAsync();
    }

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        Directory.CreateDirectory(_folder);
        await ExecAsync($"DROP DATABASE IF EXISTS {Source}; DROP DATABASE IF EXISTS {Copy}; CREATE DATABASE {Source};");
        await ExecAsync("""
            CREATE TABLE customers (id int PRIMARY KEY, name varchar(50) NOT NULL, photo blob);
            CREATE TABLE orders (id int AUTO_INCREMENT PRIMARY KEY, customer_id int, total decimal(8,2),
                FOREIGN KEY (customer_id) REFERENCES customers (id));
            INSERT INTO customers VALUES (1, 'Ann', x'00FF10'), (2, 'Bob ''the'' builder', NULL);
            INSERT INTO orders (customer_id, total) VALUES (1, 10.5), (2, 20), (1, 30);
            CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 15;
            CREATE PROCEDURE add_order(IN c int) BEGIN INSERT INTO orders (customer_id, total) VALUES (c, 1); END;
            """, Source);
        _provider = new MySqlProvider(Profile(Source));
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        if (Settings is not null) await ExecAsync($"DROP DATABASE IF EXISTS {Source}; DROP DATABASE IF EXISTS {Copy};");
        MySqlConnection.ClearAllPools();
        try { Directory.Delete(_folder, true); } catch { /* best effort */ }
    }

    private sealed class Lines : IProgress<BackupProgress>
    {
        public List<BackupProgress> All { get; } = [];
        public void Report(BackupProgress value) { lock (All) All.Add(value); }
    }

    [SkippableFact]
    public async Task A_dump_restores_into_a_new_database_with_rows_views_and_procedures()
    {
        Skip.If(Settings is null, "DBEXPLORER_TEST_MYSQL is not set");
        var support = await _provider!.GetBackupSupportAsync(null);
        Skip.If(!support.IsAvailable, support.Problem ?? "mysqldump is not available");

        var file = Path.Combine(_folder, "shop backup.sql");
        var lines = new Lines();
        await _provider.BackupAsync(new BackupRequest { Database = Source, Path = file, Format = BackupFormat.PlainSql }, null, lines);
        Assert.Contains("--defaults-extra-file=<login options>", lines.All[0].Line);
        Assert.DoesNotContain(lines.All, l => l.Line.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("2 table(s) with their rows", lines.All[^1].Line);
        var text = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("USE `", text);
        Assert.Contains("0x00FF10", text);

        var info = await _provider.InspectBackupAsync(file, null);
        Assert.Equal(Source, info.Database);
        Assert.StartsWith("MySQL ", info.Server ?? "MySQL ");
        Assert.NotNull(info.TakenAt);

        var restore = new RestoreRequest { Path = file, Database = Copy, CreateDatabase = true };
        lines = new Lines();
        await _provider.RestoreAsync(restore, null, lines);
        Assert.Equal(3L, Convert.ToInt64(await ExecAsync("SELECT count(*) FROM orders", Copy)));
        Assert.Equal(2L, Convert.ToInt64(await ExecAsync("SELECT count(*) FROM big_orders", Copy)));
        Assert.Equal("Bob 'the' builder", await ExecAsync("SELECT name FROM customers WHERE id = 2", Copy));
        Assert.Equal(1L, Convert.ToInt64(await ExecAsync($"SELECT count(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{Copy}'")));
        Assert.Equal(100, lines.All[^1].Percent);

        // Over the existing copy: the backup's tables come back as they were, others stay.
        await ExecAsync("DELETE FROM orders; CREATE TABLE extra (x int);", Copy);
        await _provider.RestoreAsync(restore with { CreateDatabase = false }, null, new Lines());
        Assert.Equal(3L, Convert.ToInt64(await ExecAsync("SELECT count(*) FROM orders", Copy)));
        Assert.Equal(0L, Convert.ToInt64(await ExecAsync("SELECT count(*) FROM extra", Copy)));
    }

    [SkippableFact]
    public async Task A_failing_restore_into_a_new_database_drops_it()
    {
        Skip.If(Settings is null, "DBEXPLORER_TEST_MYSQL is not set");
        var support = await _provider!.GetBackupSupportAsync(null);
        Skip.If(!support.IsAvailable, support.Problem ?? "mysqldump is not available");

        var file = Path.Combine(_folder, "broken.sql");
        await File.WriteAllTextAsync(file, "CREATE TABLE t (x int);\nTHIS IS NOT SQL;\n");
        var ex = await Assert.ThrowsAsync<BackupException>(() =>
            _provider.RestoreAsync(new RestoreRequest { Path = file, Database = Copy, CreateDatabase = true }, null, new Lines()));
        Assert.Contains("error", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, Convert.ToInt64(await ExecAsync($"SELECT count(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{Copy}'")));
    }
}
