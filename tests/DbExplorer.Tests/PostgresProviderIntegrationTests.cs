using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// The PostgreSQL provider against a real server. Skipped unless DBEXPLORER_TEST_PG is set to "host;port;user;password"
/// of a superuser on a server where the database dbx_provider_test and the role dbx_prov_nodb may be created (they are
/// dropped again).
/// </summary>
public sealed class PostgresProviderIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_provider_test";
    private const string Login = "dbx_prov_nodb";
    private const string LoginPassword = "Dbx_prov_pw1";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");
    private PostgresProvider? _provider;

    private static ConnectionProfile Profile(string database, string? user = null, string? password = null)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = PostgresProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = user ?? p[2], Password = password ?? p[3], Database = database, Name = "test"
        };
    }

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await DropAllAsync();
        await using (var admin = await OpenAsync(Profile("postgres")))
        {
            await Exec(admin, $"CREATE DATABASE {Database}");
            await Exec(admin, $"CREATE ROLE {Login} LOGIN PASSWORD '{LoginPassword}'");
        }
        await using (var cn = await OpenAsync(Profile(Database)))
            await Exec(cn, """
                CREATE TABLE items (id integer PRIMARY KEY, name text DEFAULT 'none', CHECK (id > 0));
                INSERT INTO items (id) SELECT g FROM generate_series(1, 5) g;
                GRANT SELECT ON items TO dbx_prov_nodb;
                """);
        _provider = new PostgresProvider(Profile(Database));
    }

    public async Task DisposeAsync()
    {
        if (Settings is null) return;
        if (_provider is not null) await _provider.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await DropAllAsync();
    }

    private static async Task DropAllAsync()
    {
        await using var admin = await OpenAsync(Profile("postgres"));
        await Exec(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
        await Exec(admin, $"DROP ROLE IF EXISTS {Login}");
    }

    private static async Task<NpgsqlConnection> OpenAsync(ConnectionProfile profile)
    {
        var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(profile));
        await cn.OpenAsync();
        return cn;
    }

    private static async Task Exec(NpgsqlConnection cn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task All_databases_connection_works_for_a_login_without_a_database_of_its_name()
    {
        Skip.If(Settings is null);
        var profile = Profile("", Login, LoginPassword);
        await using var provider = new PostgresProvider(profile);

        Assert.NotEmpty(await provider.GetServerVersionAsync());
        var objects = await provider.GetObjectsAsync();
        var items = Assert.Single(objects, o => o.Database == Database && o.Name == "items");

        var script = await provider.ExecuteScriptAsync("SELECT current_database()", null, 30);
        Assert.Equal(PostgresSql.DefaultDatabase, script.ResultSets[0].Rows[0][0]);

        await using (var session = await provider.BeginScriptSessionAsync(null, transactional: false))
        {
            var result = await session.QueryAsync("SELECT current_database()", 30);
            Assert.Equal(PostgresSql.DefaultDatabase, result.ResultSets[0].Rows[0][0]);
        }

        // An object without its database (as a single-database connection would give it) falls back too.
        var constraints = await provider.GetTableConstraintsAsync(new DbObject { Schema = "pg_catalog", Name = "pg_class", Type = DbObjectType.Table });
        Assert.Empty(constraints.Checks);
        Assert.NotNull(await provider.GetTableChangeCountersAsync(null));
        await provider.GetChangeMarkerAsync(null);
        await provider.GetDebugSupportAsync(null);
        Assert.Contains(await new PostgresProviderFactory().ListDatabasesAsync(profile), d => d == Database);

        // The table's own database is still reached through its name.
        var tableConstraints = await provider.GetTableConstraintsAsync(items);
        Assert.Single(tableConstraints.Checks);
    }
}
