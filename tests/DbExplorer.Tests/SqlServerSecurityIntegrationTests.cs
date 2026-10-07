using DbExplorer.Application.Security;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.SqlServer;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Tests;

/// <summary>
/// The Security tab's catalog and scripts against a real SQL Server. Skipped unless DBEXPLORER_TEST_MSSQL is set to
/// "host;port;user;password" of a sysadmin on a server where the database DbxSecurityTest and logins named dbx_sec_*
/// may be created (they are dropped again).
/// </summary>
public sealed class SqlServerSecurityIntegrationTests : IAsyncLifetime
{
    private const string Database = "DbxSecurityTest";
    private const string Password = "Str0ng'Pass!word";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_MSSQL");
    private SqlServerProvider? _provider;

    private static ConnectionProfile Profile(string database, string? user = null, string? password = null)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = SqlServerProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = user ?? p[2], Password = password ?? p[3], Database = database, Name = "test", TrustServerCertificate = true
        };
    }

    private static async Task<SqlConnection> OpenAsync(ConnectionProfile profile)
    {
        var cn = new SqlConnection(SqlServerSql.BuildConnectionString(profile, "tests", forceReadWrite: true));
        await cn.OpenAsync();
        return cn;
    }

    private static async Task Exec(SqlConnection cn, string sql)
    {
        await using var cmd = new SqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await DropAllAsync();
        await using (var admin = await OpenAsync(Profile("master")))
            await Exec(admin, $"CREATE DATABASE {Database}");
        await using (var cn = await OpenAsync(Profile(Database)))
            await Exec(cn, """
                CREATE TABLE dbo.Orders (Id int PRIMARY KEY, Amount decimal(10, 2));
                INSERT INTO dbo.Orders VALUES (1, 10), (2, 20);
                CREATE ROLE reporting;
                GRANT SELECT ON dbo.Orders TO reporting;
                DENY DELETE ON dbo.Orders TO reporting;
                """);
        _provider = new SqlServerProvider(Profile(Database));
    }

    public async Task DisposeAsync()
    {
        if (Settings is null) return;
        if (_provider is not null) await _provider.DisposeAsync();
        SqlConnection.ClearAllPools();
        await DropAllAsync();
    }

    private static async Task DropAllAsync()
    {
        await using var admin = await OpenAsync(Profile("master"));
        await Exec(admin, $"IF DB_ID('{Database}') IS NOT NULL BEGIN ALTER DATABASE {Database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Database}; END");
        foreach (var login in new[] { "dbx_sec_anna", "dbx_sec_bob" })
            await Exec(admin, $"IF SUSER_ID('{login}') IS NOT NULL DROP LOGIN {login}");
    }

    [SkippableFact]
    public async Task Catalog_shows_logins_server_roles_users_roles_and_permissions()
    {
        Skip.If(Settings is null);
        var catalog = await SecurityCatalogLoader.LoadAsync(_provider!, Database);

        Assert.Empty(catalog.Warnings);
        var sa = catalog.Find("sa", SecurityScope.Server)!;
        Assert.True(sa.IsSystem);
        Assert.Equal("SQL login", sa.TypeDescription);
        Assert.Contains(catalog.RolesOf(sa), m => m.Role == "sysadmin");
        Assert.Equal(PrincipalKind.ServerRole, catalog.Find("sysadmin")!.Kind);
        Assert.True(catalog.Find("db_datareader", SecurityScope.Database)!.IsFixedRole);

        var reporting = catalog.Find("reporting", SecurityScope.Database)!;
        Assert.False(reporting.IsSystem);
        var grants = catalog.GrantsOf(reporting).ToList();
        Assert.Contains(grants, g => g.Permission == "SELECT" && g.State == GrantState.Grant && g.On.Display == "dbo.Orders" && g.On.Kind == SecurableKind.Table);
        Assert.Contains(grants, g => g.Permission == "DELETE" && g.State == GrantState.Deny);
    }

    [SkippableFact]
    public async Task A_new_login_with_a_user_and_a_role_can_sign_in_and_read_and_a_deny_holds()
    {
        Skip.If(Settings is null);
        var draft = new SecurityDraft(await SecurityCatalogLoader.LoadAsync(_provider!, Database));
        Assert.Null(draft.Add(new CreateLoginChange("dbx_sec_anna", new Secret(Password), CreateUser: true, DefaultDatabase: Database)));
        Assert.Null(draft.Add(new MembershipChange("dbx_sec_anna", "reporting", SecurityScope.Database, Add: true)));
        Assert.Null(draft.Add(new CreateRoleChange("auditors")));
        Assert.Null(draft.Add(new PermissionChange("auditors", "VIEW DEFINITION", Securable.OfSchema("dbo"), PermissionAction.Grant)));

        var script = draft.Script(Database);
        Assert.DoesNotContain(Password, script.Display);
        await SecurityScriptRunner.RunAsync(_provider!, Database, script, 30);

        await using (var anna = await OpenAsync(Profile(Database, "dbx_sec_anna", Password)))
        {
            await using var read = new SqlCommand("SELECT SUM(Amount) FROM dbo.Orders", anna);
            Assert.Equal(30m, await read.ExecuteScalarAsync());
            await using var delete = new SqlCommand("DELETE FROM dbo.Orders", anna);
            await Assert.ThrowsAsync<SqlException>(() => delete.ExecuteNonQueryAsync());
        }

        var after = await SecurityCatalogLoader.LoadAsync(_provider!, Database);
        var user = after.Find("dbx_sec_anna", SecurityScope.Database)!;
        Assert.Equal("dbx_sec_anna", user.LoginName);
        Assert.Equal("reporting", Assert.Single(after.RolesOf(user)).Role);
        Assert.Contains(after.GrantsOf(after.Find("auditors", SecurityScope.Database)!), g => g.Permission == "VIEW DEFINITION" && g.On.Kind == SecurableKind.Schema);

        // Disable, change the password, revoke the deny, then drop the role (its members go first).
        var login = after.Find("dbx_sec_anna", SecurityScope.Server)!;
        var edit = new SecurityDraft(after);
        Assert.Null(edit.Add(new SetPasswordChange(login, new Secret("An0ther!Passw0rd"))));
        Assert.Null(edit.Add(new SetLoginEnabledChange(login, Enabled: false)));
        Assert.Null(edit.Add(PermissionChange.RevokeOf(after.Grants.Single(g => g.State == GrantState.Deny))));
        Assert.Null(edit.Add(new DropPrincipalChange(after.Find("reporting", SecurityScope.Database)!)));
        await SecurityScriptRunner.RunAsync(_provider!, Database, edit.Script(Database), 30);

        var final = await SecurityCatalogLoader.LoadAsync(_provider!, Database);
        Assert.True(final.Find("dbx_sec_anna", SecurityScope.Server)!.IsDisabled);
        Assert.Null(final.Find("reporting", SecurityScope.Database));
        SqlConnection.ClearAllPools();
        await Assert.ThrowsAsync<SqlException>(() => OpenAsync(Profile(Database, "dbx_sec_anna", "An0ther!Passw0rd")));
    }

    [SkippableFact]
    public async Task A_failing_statement_rolls_back_logins_and_users_too()
    {
        Skip.If(Settings is null);
        var draft = new SecurityDraft(await SecurityCatalogLoader.LoadAsync(_provider!, Database));
        Assert.Null(draft.Add(new CreateLoginChange("dbx_sec_bob", new Secret(Password), CreateUser: true)));
        Assert.Null(draft.Add(new MembershipChange("dbx_sec_bob", "no_such_role", SecurityScope.Database, Add: true)));

        await Assert.ThrowsAnyAsync<Exception>(() => SecurityScriptRunner.RunAsync(_provider!, Database, draft.Script(Database), 30));

        var after = await SecurityCatalogLoader.LoadAsync(_provider!, Database);
        Assert.Null(after.Find("dbx_sec_bob"));
    }

    [SkippableFact]
    public async Task A_login_without_a_user_gets_one_for_the_database()
    {
        Skip.If(Settings is null);
        await using (var admin = await OpenAsync(Profile("master")))
            await Exec(admin, $"CREATE LOGIN dbx_sec_bob WITH PASSWORD = N'{Password.Replace("'", "''")}'");
        var catalog = await SecurityCatalogLoader.LoadAsync(_provider!, Database);
        var draft = new SecurityDraft(catalog);
        Assert.Null(draft.Add(new CreateUserChange("dbx_sec_bob", "dbx_sec_bob")));
        Assert.NotNull(draft.Add(new CreateUserChange("dbx_sec_bob", "dbx_sec_bob")));
        await SecurityScriptRunner.RunAsync(_provider!, Database, draft.Script(Database), 30);

        var after = await SecurityCatalogLoader.LoadAsync(_provider!, Database);
        Assert.Equal("dbx_sec_bob", after.Find("dbx_sec_bob", SecurityScope.Database)!.LoginName);
    }
}
