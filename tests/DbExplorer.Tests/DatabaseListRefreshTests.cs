using DbExplorer.Application;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.MySql;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;

namespace DbExplorer.Tests;

/// <summary>Which scripts make the database pickers re-read the server's database list.</summary>
public sealed class DatabaseListChangesTests
{
    [Theory]
    [InlineData("CREATE DATABASE NewBase;", "SqlServer")]
    [InlineData("create   database [New Base]", "SqlServer")]
    [InlineData("DROP DATABASE IF EXISTS NewBase", "Postgres")]
    [InlineData("ALTER DATABASE NewBase MODIFY NAME = OldBase", "SqlServer")]
    [InlineData("ALTER DATABASE sales RENAME TO shop", "Postgres")]
    [InlineData("RESTORE DATABASE NewBase FROM DISK = 'x.bak'", "SqlServer")]
    [InlineData("EXEC sp_renamedb 'a', 'b'", "SqlServer")]
    [InlineData("SELECT 1;\nGO\nCREATE DATABASE x", "SqlServer")]
    [InlineData("CREATE SCHEMA shop", "MySql")]
    [InlineData("DROP SCHEMA shop", "MySql")]
    public void A_script_that_creates_drops_or_renames_a_database_counts(string sql, string provider) =>
        Assert.True(DatabaseListChanges.Affects(sql, provider));

    [Theory]
    [InlineData("SELECT * FROM sys.databases", "SqlServer")]
    [InlineData("-- CREATE DATABASE x\nSELECT 1", "SqlServer")]
    [InlineData("/* DROP DATABASE x */ SELECT 1", "Postgres")]
    [InlineData("SELECT 'CREATE DATABASE x'", "SqlServer")]
    [InlineData("CREATE SCHEMA sales", "SqlServer")]
    [InlineData("CREATE SCHEMA sales", "Postgres")]
    [InlineData("CREATE TABLE database_log (id int)", "SqlServer")]
    public void Other_scripts_do_not(string sql, string provider) =>
        Assert.False(DatabaseListChanges.Affects(sql, provider));
}

/// <summary>
/// A database created after connecting shows up in the pickers after Refresh metadata. The catalog snapshot only knows
/// databases that hold objects (or only the connection's own database), so a new, empty one was never listed: the
/// session now keeps the server's list and Refresh metadata re-reads it. Each engine runs against a real server when
/// DBEXPLORER_TEST_MSSQL / _PG / _MYSQL is set to "host;port;user;password".
/// </summary>
public sealed class DatabaseListRefreshIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-dblist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SessionService Sessions()
    {
        var paths = new AppPaths(_root);
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        var registry = new ProviderRegistry([new SqlServerProviderFactory(), new PostgresProviderFactory(), new MySqlProviderFactory()]);
        return new SessionService(registry, metadata, new SshTunnelService(new KnownHostsStore(paths), new RejectUnknownHostKeys()));
    }

    [SkippableTheory]
    [InlineData("DBEXPLORER_TEST_MSSQL", "SqlServer", "master")]
    [InlineData("DBEXPLORER_TEST_PG", "Postgres", "postgres")]
    [InlineData("DBEXPLORER_TEST_MYSQL", "MySql", "")]
    public async Task Refresh_metadata_lists_a_database_created_after_connecting(string variable, string provider, string database)
    {
        var settings = Environment.GetEnvironmentVariable(variable);
        Skip.If(settings is null);
        var p = settings!.Split(';');
        var profile = new ConnectionProfile
        {
            ProviderKey = provider, Host = p[0], Port = int.Parse(p[1]), UserName = p[2], Password = p[3],
            Database = database, Name = "test", TrustServerCertificate = true
        };
        var name = "dbx_list_" + Guid.NewGuid().ToString("N")[..8];
        var sessions = Sessions();
        await using var session = await sessions.ConnectAsync(profile);
        Assert.DoesNotContain(name, await session.GetServerDatabasesAsync(), StringComparer.OrdinalIgnoreCase);

        var changed = 0;
        session.DatabasesChanged += (_, _) => changed++;
        try
        {
            await ObjectCopyService.CreateDatabaseAsync(session, name);
            // Still the list read before: nothing told the session yet.
            Assert.DoesNotContain(name, await session.GetServerDatabasesAsync(), StringComparer.OrdinalIgnoreCase);

            await sessions.RefreshMetadataAsync(session);

            Assert.Equal(1, changed);
            // The empty database has no objects, so the catalog alone (what the pickers used to rely on) misses it.
            Assert.DoesNotContain(name, session.Snapshot.Databases, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(name, await session.GetServerDatabasesAsync(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            var drop = provider == "SqlServer"
                ? $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];"
                : $"DROP DATABASE {name};";
            await session.Provider.ExecuteScriptAsync(drop, database: null, 60);
        }
        session.InvalidateDatabases();
        Assert.DoesNotContain(name, await session.GetServerDatabasesAsync(), StringComparer.OrdinalIgnoreCase);
    }
}
