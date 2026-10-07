using DbExplorer.Application.Copy;
using DbExplorer.Application.Security;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public sealed class SecurityTests
{
    private static SecurityPrincipal Login(string name) => new()
    {
        Name = name, Kind = PrincipalKind.Login, Scope = SecurityScope.Server, TypeDescription = "SQL login", HasPassword = true
    };

    private static SecurityPrincipal User(string name) => new()
    {
        Name = name, Kind = PrincipalKind.User, Scope = SecurityScope.Database, TypeDescription = "SQL user", LoginName = name
    };

    private static SecurityPrincipal DbRole(string name, bool system = false) => new()
    {
        Name = name, Kind = PrincipalKind.Role, Scope = SecurityScope.Database, TypeDescription = "Database role",
        IsSystem = system, IsFixedRole = system
    };

    private static SecurityPrincipal PgRole(string name, bool login = false) => new()
    {
        Name = name, Kind = login ? PrincipalKind.Login : PrincipalKind.Role, Scope = SecurityScope.Server,
        TypeDescription = login ? "Role that can log in" : "Role", HasPassword = login
    };

    private static readonly Securable Orders = Securable.Object(SecurableKind.Table, "sales", "orders");

    private static SecurityCatalog SqlServerCatalog() => new()
    {
        ProviderKey = SqlDialect.SqlServerKey,
        Database = "Shop",
        Principals = [Login("anna"), User("anna"), DbRole("reporting"), DbRole("db_datareader", system: true), DbRole("public", system: true)],
        Memberships = [new SecurityMembership("anna", "reporting", SecurityScope.Database)],
        Grants = [new SecurityGrant { Grantee = "reporting", Permission = "SELECT", On = Orders }]
    };

    private static SecurityCatalog PostgresCatalog() => new()
    {
        ProviderKey = SqlDialect.PostgresKey,
        Database = "shop",
        Principals = [PgRole("anna", login: true), PgRole("reporting"), PgRole("Readers")],
        Memberships = [new SecurityMembership("anna", "reporting", SecurityScope.Server)],
        Grants =
        [
            new SecurityGrant { Grantee = "reporting", Permission = "SELECT", On = Orders, State = GrantState.GrantWithGrantOption },
            new SecurityGrant { Grantee = "PUBLIC", Permission = "CONNECT", On = Securable.Database }
        ]
    };

    // ---------------- Script builder ----------------

    [Fact]
    public void Sql_server_script_hides_passwords_in_the_display_and_carries_them_in_the_executable()
    {
        var script = SecurityScriptBuilder.Build(
            [new CreateLoginChange("o'brien", new Secret("p@ss'word"), CreateUser: true, DefaultDatabase: "Shop")],
            SqlDialect.SqlServerKey, "Shop");

        Assert.True(script.HasSecrets);
        Assert.Equal(2, script.StatementCount);
        Assert.DoesNotContain("p@ss", script.Display);
        Assert.Contains("CREATE LOGIN [o'brien] WITH PASSWORD = '********', DEFAULT_DATABASE = [Shop];", script.Display);
        Assert.Contains("CREATE LOGIN [o'brien] WITH PASSWORD = N'p@ss''word', DEFAULT_DATABASE = [Shop];", script.Executable);
        Assert.Contains("CREATE USER [o'brien] FOR LOGIN [o'brien];", script.Executable);
    }

    [Fact]
    public void Postgres_script_sends_a_scram_verifier_never_the_password()
    {
        var script = SecurityScriptBuilder.Build(
            [new CreateLoginChange("Anna", new Secret("hunter2")), new SetPasswordChange(PgRole("bob", login: true), new Secret("s3cret"))],
            SqlDialect.PostgresKey, "shop");

        Assert.DoesNotContain("hunter2", script.Executable);
        Assert.DoesNotContain("s3cret", script.Executable);
        Assert.Matches(@"CREATE ROLE ""Anna"" LOGIN PASSWORD 'SCRAM-SHA-256\$4096:[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+';", script.Executable);
        Assert.Contains("ALTER ROLE \"bob\" PASSWORD '********';", script.Display);
        Assert.Contains("SCRAM-SHA-256", script.Display.Split('\n')[0]);
    }

    [Fact]
    public void A_secret_never_prints_its_value()
    {
        var change = new CreateLoginChange("anna", new Secret("hunter2"));
        Assert.Equal(Secret.Mask, change.Password.ToString());
        Assert.DoesNotContain("hunter2", change.ToString());
        Assert.DoesNotContain("hunter2", $"{change.Password}");
    }

    [Fact]
    public void Scram_verifier_is_deterministic_for_a_salt_and_random_otherwise()
    {
        var salt = Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ==");
        var a = PostgresScram.Verifier("pencil", salt);
        Assert.Equal(a, PostgresScram.Verifier("pencil", salt));
        Assert.StartsWith("SCRAM-SHA-256$4096:W22ZaJ0SNY7soEsUEjb6gQ==$", a);
        Assert.NotEqual(PostgresScram.Verifier("pencil"), PostgresScram.Verifier("pencil"));
    }

    [Fact]
    public void Scripts_run_creates_first_and_drops_last()
    {
        var script = SecurityScriptBuilder.Build(
        [
            new DropPrincipalChange(DbRole("old")),
            new PermissionChange("reporting", "SELECT", Orders, PermissionAction.Revoke),
            new MembershipChange("anna", "auditors", SecurityScope.Database, Add: true),
            new CreateRoleChange("auditors")
        ], SqlDialect.SqlServerKey, "Shop");

        var lines = script.Executable.Split('\n');
        Assert.Equal(
        [
            "CREATE ROLE [auditors];",
            "ALTER ROLE [auditors] ADD MEMBER [anna];",
            "REVOKE SELECT ON [sales].[orders] FROM [reporting];",
            "DROP ROLE [old];"
        ], lines);
        Assert.False(script.HasSecrets);
        Assert.Equal(script.Executable, string.Join('\n', script.Display.Split('\n').Where(l => !l.StartsWith("--"))));
    }

    [Theory]
    [InlineData(SecurableKind.Database, "", "", null, "GRANT CONNECT TO [anna];")]
    [InlineData(SecurableKind.Schema, "sales", "", null, "GRANT CONNECT ON SCHEMA::[sales] TO [anna];")]
    [InlineData(SecurableKind.Table, "sales", "orders", "amount", "GRANT CONNECT ON [sales].[orders] ([amount]) TO [anna];")]
    [InlineData(SecurableKind.Type, "dbo", "Money2", null, "GRANT CONNECT ON TYPE::[dbo].[Money2] TO [anna];")]
    public void Sql_server_names_each_kind_of_securable(SecurableKind kind, string schema, string name, string? column, string expected)
    {
        var on = new Securable { Kind = kind, Schema = schema, Name = name, Column = column };
        var script = SecurityScriptBuilder.Build([new PermissionChange("anna", "connect", on, PermissionAction.Grant)], SqlDialect.SqlServerKey, "Shop");
        Assert.Equal(expected, script.Executable);
    }

    [Fact]
    public void Postgres_grants_name_database_schema_sequence_routine_and_public()
    {
        var script = SecurityScriptBuilder.Build(
        [
            new PermissionChange("PUBLIC", "CONNECT", Securable.Database, PermissionAction.Revoke),
            new PermissionChange("app", "USAGE", Securable.OfSchema("sales"), PermissionAction.Grant),
            new PermissionChange("app", "USAGE", Securable.Object(SecurableKind.Sequence, "sales", "order_id_seq"), PermissionAction.Grant),
            new PermissionChange("app", "EXECUTE", Securable.Object(SecurableKind.Function, "sales", "total", "integer, text"), PermissionAction.GrantWithGrantOption),
            new PermissionChange("app", "SELECT", Orders, PermissionAction.Revoke) { Cascade = true },
            new MembershipChange("anna", "Readers", SecurityScope.Server, Add: false)
        ], SqlDialect.PostgresKey, "shop");

        Assert.Equal(
        [
            "GRANT USAGE ON SCHEMA \"sales\" TO \"app\";",
            "GRANT USAGE ON SEQUENCE \"sales\".\"order_id_seq\" TO \"app\";",
            "GRANT EXECUTE ON ROUTINE \"sales\".\"total\"(integer, text) TO \"app\" WITH GRANT OPTION;",
            "REVOKE CONNECT ON DATABASE \"shop\" FROM PUBLIC;",
            "REVOKE SELECT ON TABLE \"sales\".\"orders\" FROM \"app\" CASCADE;",
            "REVOKE \"Readers\" FROM \"anna\";"
        ], script.Executable.Split('\n'));
    }

    [Theory]
    [InlineData("SELECT; DROP TABLE x")]
    [InlineData("SELECT--")]
    [InlineData("")]
    public void Permission_names_that_are_not_words_are_refused(string permission)
    {
        Assert.Throws<ArgumentException>(() =>
            SecurityScriptBuilder.Build([new PermissionChange("anna", permission, Orders, PermissionAction.Grant)], SqlDialect.SqlServerKey, "Shop"));
    }

    [Fact]
    public void Postgres_has_no_deny_and_no_database_users()
    {
        Assert.Throws<ArgumentException>(() =>
            SecurityScriptBuilder.Build([new PermissionChange("anna", "SELECT", Orders, PermissionAction.Deny)], SqlDialect.PostgresKey, "shop"));
        Assert.Throws<ArgumentException>(() =>
            SecurityScriptBuilder.Build([new CreateUserChange("anna", null)], SqlDialect.PostgresKey, "shop"));
    }

    [Fact]
    public void Identifiers_are_quoted_so_names_cannot_break_out()
    {
        var sqlServer = SecurityScriptBuilder.Build([new CreateRoleChange("x]; DROP LOGIN sa; --")], SqlDialect.SqlServerKey, "Shop");
        Assert.Equal("CREATE ROLE [x]]; DROP LOGIN sa; --];", sqlServer.Executable);
        var postgres = SecurityScriptBuilder.Build([new CreateRoleChange("x\"; DROP ROLE postgres; --")], SqlDialect.PostgresKey, "shop");
        Assert.Equal("CREATE ROLE \"x\"\"; DROP ROLE postgres; --\" NOLOGIN;", postgres.Executable);
    }

    [Fact]
    public void Sql_server_drops_use_the_statement_for_each_kind()
    {
        var script = SecurityScriptBuilder.Build(
        [
            new DropPrincipalChange(User("anna")),
            new DropPrincipalChange(Login("anna")),
            new DropPrincipalChange(new SecurityPrincipal { Name = "ops", Kind = PrincipalKind.ServerRole, Scope = SecurityScope.Server }),
            new DropPrincipalChange(new SecurityPrincipal { Name = "app", Kind = PrincipalKind.Role, Scope = SecurityScope.Database, TypeDescription = "Application role" }),
            new SetLoginEnabledChange(Login("bob"), Enabled: false),
            new MembershipChange("bob", "sysadmin", SecurityScope.Server, Add: true)
        ], SqlDialect.SqlServerKey, "Shop");

        Assert.Equal(
        [
            "ALTER LOGIN [bob] DISABLE;",
            "ALTER SERVER ROLE [sysadmin] ADD MEMBER [bob];",
            "DROP USER [anna];",
            "DROP LOGIN [anna];",
            "DROP SERVER ROLE [ops];",
            "DROP APPLICATION ROLE [app];"
        ], script.Executable.Split('\n'));
    }

    // ---------------- Draft ----------------

    [Fact]
    public void Draft_shows_pending_memberships_and_cancels_opposite_edits()
    {
        var catalog = SqlServerCatalog();
        var draft = new SecurityDraft(catalog);
        var anna = catalog.Find("anna", SecurityScope.Database)!;

        Assert.Null(draft.Add(new MembershipChange("anna", "db_datareader", SecurityScope.Database, Add: true)));
        Assert.Null(draft.Add(new MembershipChange("anna", "reporting", SecurityScope.Database, Add: false)));
        var roles = draft.RolesOf(anna);
        Assert.Contains(roles, r => r.Membership.Role == "reporting" && r.Pending == PendingState.Removing);
        Assert.Contains(roles, r => r.Membership.Role == "db_datareader" && r.Pending == PendingState.Adding);

        // Adding back what is being removed just cancels the removal.
        Assert.Null(draft.Add(new MembershipChange("anna", "reporting", SecurityScope.Database, Add: true)));
        Assert.Single(draft.Changes);
        Assert.NotNull(draft.Add(new MembershipChange("anna", "reporting", SecurityScope.Database, Add: true)));
        Assert.NotNull(draft.Add(new MembershipChange("anna", "anna", SecurityScope.Database, Add: true)));
        Assert.DoesNotContain("reporting", draft.RolesAvailableTo(anna));
        Assert.DoesNotContain("public", draft.RolesAvailableTo(anna));
    }

    [Fact]
    public void Draft_checks_grants_against_the_server()
    {
        var draft = new SecurityDraft(SqlServerCatalog());
        Assert.NotNull(draft.Add(new PermissionChange("reporting", "select", Orders, PermissionAction.Grant)));
        Assert.NotNull(draft.Add(new PermissionChange("reporting", "INSERT", Orders, PermissionAction.Revoke)));
        Assert.Null(draft.Add(new PermissionChange("reporting", "INSERT", Orders, PermissionAction.Grant)));
        Assert.Null(draft.Add(new PermissionChange("reporting", "DELETE", Orders, PermissionAction.Deny)));

        var reporting = draft.Find("reporting")!;
        var grants = draft.GrantsOf(reporting);
        Assert.Equal(3, grants.Count);
        Assert.Contains(grants, g => g.Grant.Permission == "DELETE" && g.Grant.State == GrantState.Deny && g.Pending == PendingState.Adding);

        // Revoking a pending grant drops it instead of adding a revoke.
        Assert.Null(draft.Add(new PermissionChange("reporting", "INSERT", Orders, PermissionAction.Revoke)));
        Assert.Single(draft.Changes);
    }

    [Fact]
    public void Draft_refuses_deny_on_postgres_and_duplicate_names()
    {
        var draft = new SecurityDraft(PostgresCatalog());
        Assert.NotNull(draft.Add(new PermissionChange("anna", "SELECT", Orders, PermissionAction.Deny)));
        Assert.NotNull(draft.Add(new CreateRoleChange("reporting")));
        // PostgreSQL role names are case sensitive.
        Assert.Null(draft.Add(new CreateRoleChange("readers")));
        Assert.NotNull(draft.Add(new CreateRoleChange("readers")));
        // SQL Server names are not.
        Assert.NotNull(new SecurityDraft(SqlServerCatalog()).Add(new CreateRoleChange("REPORTING")));
    }

    [Fact]
    public void Dropping_a_pending_principal_removes_every_edit_that_mentions_it()
    {
        var draft = new SecurityDraft(SqlServerCatalog());
        Assert.Null(draft.Add(new CreateLoginChange("bob", new Secret("pw"), CreateUser: true)));
        var bobUser = draft.Find("bob", SecurityScope.Database)!;
        Assert.True(bobUser.IsNew);
        Assert.Null(draft.Add(new MembershipChange("bob", "reporting", SecurityScope.Database, Add: true)));
        Assert.Null(draft.Add(new PermissionChange("bob", "SELECT", Orders, PermissionAction.Grant)));
        Assert.Equal(3, draft.Changes.Count);

        Assert.Null(draft.Add(new DropPrincipalChange(bobUser)));
        Assert.Empty(draft.Changes);
        Assert.Null(draft.Find("bob"));
    }

    [Fact]
    public void Dropping_an_existing_principal_drops_its_other_pending_edits_and_system_ones_are_refused()
    {
        var catalog = SqlServerCatalog();
        var draft = new SecurityDraft(catalog);
        Assert.Null(draft.Add(new PermissionChange("reporting", "INSERT", Orders, PermissionAction.Grant)));
        Assert.Null(draft.Add(new DropPrincipalChange(catalog.Find("reporting")!)));
        // SQL Server will not drop a role with members, so they are removed first; the pending grant is gone.
        Assert.Equal(2, draft.Changes.Count);
        Assert.Contains(draft.Changes, c => c is MembershipChange { Member: "anna", Role: "reporting", Add: false });
        Assert.Equal(
            "-- Remove anna from reporting\nALTER ROLE [reporting] DROP MEMBER [anna];\n-- Drop database role reporting\nDROP ROLE [reporting];",
            draft.Script("Shop").Display.ReplaceLineEndings("\n"));
        Assert.True(draft.IsPendingDrop(catalog.Find("reporting")!));
        Assert.NotNull(draft.Add(new DropPrincipalChange(catalog.Find("db_datareader")!)));
    }

    [Fact]
    public void Enabling_back_a_pending_disable_cancels_it()
    {
        var catalog = SqlServerCatalog();
        var draft = new SecurityDraft(catalog);
        var anna = catalog.Find("anna", SecurityScope.Server)!;
        Assert.True(draft.IsEnabled(anna));
        Assert.Null(draft.Add(new SetLoginEnabledChange(anna, false)));
        Assert.False(draft.IsEnabled(anna));
        Assert.Null(draft.Add(new SetLoginEnabledChange(anna, true)));
        Assert.Empty(draft.Changes);
    }

    [Fact]
    public void Sql_server_login_permissions_are_not_confused_with_a_user_of_the_same_name()
    {
        var catalog = SqlServerCatalog() with
        {
            Grants = [new SecurityGrant { Grantee = "anna", Permission = "SELECT", On = Orders }]
        };
        Assert.Empty(catalog.GrantsOf(catalog.Find("anna", SecurityScope.Server)!));
        Assert.Single(catalog.GrantsOf(catalog.Find("anna", SecurityScope.Database)!));
    }

    [Fact]
    public void All_roles_follow_nested_memberships_and_stop_at_cycles()
    {
        var catalog = PostgresCatalog() with
        {
            Memberships =
            [
                new SecurityMembership("anna", "reporting", SecurityScope.Server),
                new SecurityMembership("reporting", "Readers", SecurityScope.Server),
                new SecurityMembership("Readers", "reporting", SecurityScope.Server)
            ]
        };
        Assert.Equal(["reporting", "Readers"], catalog.AllRolesOf(catalog.Find("anna")!));
    }

    // ---------------- Catalog mapping ----------------

    private static QueryResultSet Set(string[] columns, params object?[][] rows) =>
        new() { Columns = columns, Rows = rows.Select(r => (IReadOnlyList<object?>)r).ToList() };

    [Fact]
    public void Postgres_rows_map_to_roles_attributes_and_grants()
    {
        var roles = Set(
            ["name", "can_login", "superuser", "create_db", "create_role", "inherit", "replication", "bypass_rls", "conn_limit", "valid_until", "is_system"],
            ["anna", true, false, true, false, true, false, false, 5, "2027-01-01 00:00", false],
            ["pg_read_all_data", false, false, false, false, true, false, false, -1, null, true]);
        var members = Set(["member", "role_name", "admin_option"], ["anna", "pg_read_all_data", false]);
        var grants = Set(
            ["kind", "schema_name", "object_name", "arguments", "grantee", "grantor", "permission", "is_grantable"],
            ["FUNCTION", "public", "total", "integer", "PUBLIC", "postgres", "EXECUTE", false],
            ["SEQUENCE", "public", "s", null, "anna", "postgres", "USAGE", true]);

        var catalog = SecurityCatalogLoader.FromPostgres("shop", roles, members, grants, []);

        var anna = catalog.Find("anna")!;
        Assert.Equal(PrincipalKind.Login, anna.Kind);
        Assert.Equal(["CREATEDB", "connection limit 5", "valid until 2027-01-01 00:00"], anna.Attributes);
        Assert.True(catalog.Find("pg_read_all_data")!.IsSystem);
        Assert.True(catalog.Find("PUBLIC")!.IsSystem);
        Assert.Equal("public.total(integer)", catalog.Grants[0].On.Display);
        Assert.Equal(GrantState.GrantWithGrantOption, catalog.Grants[1].State);
        Assert.Equal(SecurableKind.Sequence, catalog.Grants[1].On.Kind);
        Assert.Single(catalog.RolesOf(anna));
    }

    [Fact]
    public void Sql_server_rows_map_to_logins_users_roles_and_permissions()
    {
        var server = Set(["name", "type_code", "type_desc", "is_disabled", "default_database_name", "is_fixed_role", "is_system"],
            ["anna", "S", "SQL_LOGIN", false, "Shop", false, false],
            ["CORP\\bob", "U", "WINDOWS_LOGIN", true, "master", false, false],
            ["sysadmin", "R", "SERVER_ROLE", false, null, true, true]);
        var serverMembers = Set(["member", "role_name"], ["CORP\\bob", "sysadmin"]);
        var database = Set(["name", "type_code", "type_desc", "default_schema_name", "is_fixed_role", "is_system", "login_name", "authentication"],
            ["anna_user", "S", "SQL_USER", "dbo", false, false, "anna", "INSTANCE"],
            ["loader", "S", "SQL_USER", "dbo", false, false, null, "NONE"],
            ["reporting", "R", "DATABASE_ROLE", null, false, false, null, "NONE"]);
        var dbMembers = Set(["member", "role_name"], ["anna_user", "reporting"]);
        var permissions = Set(["grantee", "grantor", "permission", "state", "class", "schema_name", "object_name", "object_type", "column_name"],
            ["reporting", "dbo", "SELECT", "G", (byte)1, "sales", "orders", "U ", null],
            ["loader", "dbo", "UPDATE", "D", (byte)1, "sales", "orders", "U ", "amount"],
            ["anna_user", "dbo", "CONNECT", "W", (byte)0, "", "", null, null],
            ["reporting", "dbo", "EXECUTE", "G", (byte)3, "sales", "", null, null]);

        var catalog = SecurityCatalogLoader.FromSqlServer("Shop", server, serverMembers, database, dbMembers, permissions, []);

        Assert.Equal("SQL login", catalog.Find("anna", SecurityScope.Server)!.TypeDescription);
        var bob = catalog.Find("CORP\\bob")!;
        Assert.True(bob.IsDisabled);
        Assert.False(bob.HasPassword);
        Assert.Equal("sysadmin", Assert.Single(catalog.RolesOf(bob)).Role);
        Assert.Equal(PrincipalKind.ServerRole, catalog.Find("sysadmin")!.Kind);
        Assert.Equal(["login anna"], catalog.Find("anna_user")!.Attributes);
        Assert.Equal("User without login", catalog.Find("loader")!.TypeDescription);
        Assert.Equal("sales.orders (amount)", catalog.Grants[1].On.Display);
        Assert.Equal(GrantState.Deny, catalog.Grants[1].State);
        Assert.Equal(SecurableKind.Database, catalog.Grants[2].On.Kind);
        Assert.Equal(GrantState.GrantWithGrantOption, catalog.Grants[2].State);
        Assert.Equal("SCHEMA sales", catalog.Grants[3].On.Display);
        Assert.Equal(2, catalog.GrantsOf(catalog.Find("reporting")!).Count());
    }

    [Fact]
    public void Permission_choices_follow_engine_and_securable()
    {
        Assert.Contains("TRUNCATE", SecurityScriptBuilder.PermissionsFor(SqlDialect.PostgresKey, SecurableKind.Table));
        Assert.DoesNotContain("TRUNCATE", SecurityScriptBuilder.PermissionsFor(SqlDialect.SqlServerKey, SecurableKind.Table));
        Assert.Contains("EXECUTE", SecurityScriptBuilder.PermissionsFor(SqlDialect.SqlServerKey, SecurableKind.Procedure));
        Assert.Contains("USAGE", SecurityScriptBuilder.PermissionsFor(SqlDialect.PostgresKey, SecurableKind.Schema));
    }
}
