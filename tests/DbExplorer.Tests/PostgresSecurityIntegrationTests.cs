using DbExplorer.Application.Security;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// The Security tab's catalog and scripts against a real PostgreSQL server. Skipped unless DBEXPLORER_TEST_PG is set
/// to "host;port;user;password" of a superuser on a server where the database dbx_security_test and roles named
/// dbx_sec_* may be created (they are dropped again).
/// </summary>
public sealed class PostgresSecurityIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_security_test";
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
            await Exec(admin, "CREATE ROLE dbx_sec_readers NOLOGIN");
        }
        await using (var cn = await OpenAsync(Profile(Database)))
            await Exec(cn, """
                CREATE SCHEMA sales;
                CREATE TABLE sales.orders (id integer PRIMARY KEY, amount numeric);
                INSERT INTO sales.orders VALUES (1, 10), (2, 20);
                GRANT USAGE ON SCHEMA sales TO dbx_sec_readers;
                GRANT SELECT ON sales.orders TO dbx_sec_readers;
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
        foreach (var role in new[] { "dbx_sec_anna", "dbx_sec_readers", "dbx_sec_auditors", "dbx_sec_Mixed" })
            await Exec(admin, $"DROP ROLE IF EXISTS \"{role}\"");
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
    public async Task Catalog_shows_roles_memberships_and_object_privileges()
    {
        Skip.If(Settings is null);
        var catalog = await SecurityCatalogLoader.LoadAsync(_provider!, Database);

        Assert.Empty(catalog.Warnings);
        var readers = catalog.Find("dbx_sec_readers")!;
        Assert.Equal(PrincipalKind.Role, readers.Kind);
        Assert.False(readers.IsSystem);
        var grants = catalog.GrantsOf(readers).ToList();
        Assert.Contains(grants, g => g.Permission == "SELECT" && g.On == Securable.Object(SecurableKind.Table, "sales", "orders"));
        Assert.Contains(grants, g => g.Permission == "USAGE" && g.On == Securable.OfSchema("sales"));
        Assert.Contains(catalog.Grants, g => g.Grantee == "PUBLIC" && g.On.Kind == SecurableKind.Database && g.Permission == "CONNECT");
        Assert.True(catalog.Find("pg_read_all_data")?.IsSystem ?? true);
    }

    [SkippableFact]
    public async Task A_new_login_gets_a_password_role_and_grants_and_can_sign_in_and_read()
    {
        Skip.If(Settings is null);
        var draft = new SecurityDraft(await SecurityCatalogLoader.LoadAsync(_provider!, Database));
        Assert.Null(draft.Add(new CreateLoginChange("dbx_sec_anna", new Secret("Pa ss'wörd!"))));
        Assert.Null(draft.Add(new CreateRoleChange("dbx_sec_auditors")));
        Assert.Null(draft.Add(new MembershipChange("dbx_sec_anna", "dbx_sec_readers", SecurityScope.Server, Add: true)));
        Assert.Null(draft.Add(new PermissionChange("dbx_sec_auditors", "CONNECT", Securable.Database, PermissionAction.Grant)));

        var script = draft.Script(Database);
        Assert.DoesNotContain("wörd", script.Executable);
        await SecurityScriptRunner.RunAsync(_provider!, Database, script, 30);

        // The SCRAM verifier the app computed is accepted for the typed password.
        await using (var anna = await OpenAsync(Profile(Database, "dbx_sec_anna", "Pa ss'wörd!")))
        {
            await using var cmd = new NpgsqlCommand("SELECT sum(amount) FROM sales.orders", anna);
            Assert.Equal(30m, await cmd.ExecuteScalarAsync());
        }

        var after = await SecurityCatalogLoader.LoadAsync(_provider!, Database);
        var annaRole = after.Find("dbx_sec_anna")!;
        Assert.Equal(PrincipalKind.Login, annaRole.Kind);
        Assert.Equal("dbx_sec_readers", Assert.Single(after.RolesOf(annaRole)).Role);
        Assert.Contains(after.GrantsOf(after.Find("dbx_sec_auditors")!), g => g.Permission == "CONNECT" && g.On.Kind == SecurableKind.Database);

        // Revoke, change the password and stop logins in one go.
        var edit = new SecurityDraft(after);
        Assert.Null(edit.Add(new MembershipChange("dbx_sec_anna", "dbx_sec_readers", SecurityScope.Server, Add: false)));
        Assert.Null(edit.Add(new SetPasswordChange(annaRole, new Secret("second"))));
        await SecurityScriptRunner.RunAsync(_provider!, Database, edit.Script(Database), 30);
        NpgsqlConnection.ClearAllPools();
        await Assert.ThrowsAnyAsync<PostgresException>(() => OpenAsync(Profile(Database, "dbx_sec_anna", "Pa ss'wörd!")));
        await using (var anna = await OpenAsync(Profile(Database, "dbx_sec_anna", "second")))
        {
            await using var cmd = new NpgsqlCommand("SELECT sum(amount) FROM sales.orders", anna);
            await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
        }
    }

    [SkippableFact]
    public async Task A_failing_statement_rolls_back_the_whole_script()
    {
        Skip.If(Settings is null);
        var draft = new SecurityDraft(await SecurityCatalogLoader.LoadAsync(_provider!, Database));
        Assert.Null(draft.Add(new CreateRoleChange("dbx_sec_Mixed")));
        Assert.Null(draft.Add(new PermissionChange("dbx_sec_Mixed", "SELECT", Securable.Object(SecurableKind.Table, "sales", "missing"), PermissionAction.Grant)));

        await Assert.ThrowsAnyAsync<Exception>(() => SecurityScriptRunner.RunAsync(_provider!, Database, draft.Script(Database), 30));

        var after = await SecurityCatalogLoader.LoadAsync(_provider!, Database);
        Assert.Null(after.Find("dbx_sec_Mixed"));
    }

    [SkippableFact]
    public async Task Dropping_a_role_revokes_its_privileges_first_so_the_server_accepts_it()
    {
        Skip.If(Settings is null);
        var catalog = await SecurityCatalogLoader.LoadAsync(_provider!, Database);
        var readers = catalog.Find("dbx_sec_readers")!;

        var bare = SecurityScriptBuilder.Build([new DropPrincipalChange(readers)], PostgresProviderFactory.ProviderKey, Database);
        await Assert.ThrowsAnyAsync<Exception>(() => SecurityScriptRunner.RunAsync(_provider!, Database, bare, 30));

        // Dropping in the app revokes the role's privileges in this database first, so the drop goes through.
        var drop = new SecurityDraft(catalog);
        Assert.Null(drop.Add(new DropPrincipalChange(readers)));
        Assert.Equal(catalog.GrantsOf(readers).Count(), drop.Changes.OfType<PermissionChange>().Count());
        await SecurityScriptRunner.RunAsync(_provider!, Database, drop.Script(Database), 30);
        Assert.Null((await SecurityCatalogLoader.LoadAsync(_provider!, Database)).Find("dbx_sec_readers"));
    }
}
