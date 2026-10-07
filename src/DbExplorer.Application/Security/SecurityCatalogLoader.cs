using DbExplorer.Application.Copy;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.Security;

/// <summary>Reads logins, users, roles, memberships and permissions from the catalog with read-only queries.</summary>
public static class SecurityCatalogLoader
{
    private const int MaxRows = 50_000;
    private static readonly DataSearchOptions Options = new(MaxRows, QueryTimeoutSeconds: 30, LockTimeoutMs: 3000);

    /// <param name="database">The database whose users, database roles and permissions are read (the connection's
    /// default when null). Server-wide logins and roles come along either way.</param>
    public static async Task<SecurityCatalog> LoadAsync(IDatabaseProvider provider, string? database, CancellationToken ct = default)
    {
        var db = string.IsNullOrWhiteSpace(database) ? null : database;
        var warnings = new List<string>();

        async Task<QueryResultSet?> Read(string sql, string what)
        {
            try
            {
                var result = await provider.QueryReadOnlyAsync(sql, db, Options, MaxRows, ct);
                if (result.IsTruncated) warnings.Add($"Only the first {MaxRows:N0} {what} are shown.");
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"The server did not show the {what}: {ex.Message}");
                return null;
            }
        }

        if (provider.ProviderKey == SqlDialect.SqlServerKey)
        {
            var serverPrincipals = await Read(SecurityQueries.SqlServerServerPrincipals, "logins and server roles");
            var serverMembers = await Read(SecurityQueries.SqlServerServerRoleMembers, "server role members");
            var dbPrincipals = await Read(SecurityQueries.SqlServerDatabasePrincipals, "database users and roles");
            var dbMembers = await Read(SecurityQueries.SqlServerDatabaseRoleMembers, "database role members");
            var permissions = await Read(SecurityQueries.SqlServerPermissions, "permissions");
            return FromSqlServer(database ?? "", serverPrincipals, serverMembers, dbPrincipals, dbMembers, permissions, warnings);
        }

        var roles = await Read(SecurityQueries.PostgresRoles, "roles");
        var members = await Read(SecurityQueries.PostgresMemberships, "role memberships");
        var grants = await Read(SecurityQueries.PostgresGrants, "privileges");
        return FromPostgres(database ?? "", roles, members, grants, warnings);
    }

    public static SecurityCatalog FromPostgres(
        string database, QueryResultSet? roles, QueryResultSet? members, QueryResultSet? grants, IReadOnlyList<string> warnings)
    {
        var principals = Rows(roles).Select(r =>
        {
            var canLogin = r.Bool("can_login");
            var attributes = new List<string>();
            if (r.Bool("superuser")) attributes.Add("SUPERUSER");
            if (r.Bool("create_db")) attributes.Add("CREATEDB");
            if (r.Bool("create_role")) attributes.Add("CREATEROLE");
            if (!r.Bool("inherit")) attributes.Add("NOINHERIT");
            if (r.Bool("replication")) attributes.Add("REPLICATION");
            if (r.Bool("bypass_rls")) attributes.Add("BYPASSRLS");
            if (r.Int("conn_limit") is { } limit and >= 0) attributes.Add($"connection limit {limit}");
            if (r.Text("valid_until") is { Length: > 0 } until) attributes.Add("valid until " + until);
            return new SecurityPrincipal
            {
                Name = r.Text("name") ?? "",
                Kind = canLogin ? PrincipalKind.Login : PrincipalKind.Role,
                Scope = SecurityScope.Server,
                TypeDescription = canLogin ? "Role that can log in" : "Role",
                IsSystem = r.Bool("is_system"),
                HasPassword = canLogin,
                Attributes = attributes
            };
        }).ToList();

        var memberships = Rows(members)
            .Select(r => new SecurityMembership(r.Text("member") ?? "", r.Text("role_name") ?? "", SecurityScope.Server, r.Bool("admin_option")))
            .ToList();

        var grantList = Rows(grants).Select(r =>
        {
            var kind = (r.Text("kind") ?? "") switch
            {
                "DATABASE" => SecurableKind.Database,
                "SCHEMA" => SecurableKind.Schema,
                "VIEW" => SecurableKind.View,
                "MATERIALIZED VIEW" => SecurableKind.MaterializedView,
                "SEQUENCE" => SecurableKind.Sequence,
                "FUNCTION" => SecurableKind.Function,
                "TABLE" => SecurableKind.Table,
                _ => SecurableKind.Other
            };
            return new SecurityGrant
            {
                Grantee = r.Text("grantee") ?? "",
                Grantor = r.Text("grantor"),
                Permission = r.Text("permission") ?? "",
                State = r.Bool("is_grantable") ? GrantState.GrantWithGrantOption : GrantState.Grant,
                On = new Securable
                {
                    Kind = kind,
                    Schema = r.Text("schema_name") ?? "",
                    Name = r.Text("object_name") ?? "",
                    Arguments = kind == SecurableKind.Function ? r.Text("arguments") ?? "" : null
                }
            };
        }).ToList();

        // PUBLIC holds privileges in every database (CONNECT, TEMPORARY, USAGE on public…); show it as a role.
        if (grantList.Any(g => g.Grantee == "PUBLIC") && principals.All(p => p.Name != "PUBLIC"))
            principals.Add(new SecurityPrincipal
            {
                Name = "PUBLIC", Kind = PrincipalKind.Role, Scope = SecurityScope.Server, IsSystem = true,
                TypeDescription = "Every role"
            });

        return new SecurityCatalog
        {
            ProviderKey = SqlDialect.PostgresKey,
            Database = database,
            Principals = principals.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Memberships = memberships,
            Grants = grantList,
            Warnings = warnings
        };
    }

    public static SecurityCatalog FromSqlServer(
        string database, QueryResultSet? serverPrincipals, QueryResultSet? serverMembers, QueryResultSet? dbPrincipals,
        QueryResultSet? dbMembers, QueryResultSet? permissions, IReadOnlyList<string> warnings)
    {
        var principals = new List<SecurityPrincipal>();
        foreach (var r in Rows(serverPrincipals))
        {
            var type = (r.Text("type_code") ?? "").Trim();
            principals.Add(new SecurityPrincipal
            {
                Name = r.Text("name") ?? "",
                Kind = type == "R" ? PrincipalKind.ServerRole : PrincipalKind.Login,
                Scope = SecurityScope.Server,
                TypeDescription = type switch
                {
                    "R" => "Server role",
                    "S" => "SQL login",
                    "U" => "Windows login",
                    "G" => "Windows group login",
                    "E" => "Microsoft Entra login",
                    "X" => "Microsoft Entra group login",
                    "C" => "Certificate login",
                    "K" => "Asymmetric key login",
                    _ => r.Text("type_desc") ?? type
                },
                IsSystem = r.Bool("is_system") || r.Bool("is_fixed_role"),
                IsFixedRole = r.Bool("is_fixed_role"),
                IsDisabled = r.Bool("is_disabled"),
                DefaultDatabase = r.Text("default_database_name"),
                HasPassword = type == "S",
                Attributes = r.Bool("is_disabled") ? ["disabled"] : []
            });
        }

        foreach (var r in Rows(dbPrincipals))
        {
            var type = (r.Text("type_code") ?? "").Trim();
            var login = r.Text("login_name");
            var authentication = r.Text("authentication");
            principals.Add(new SecurityPrincipal
            {
                Name = r.Text("name") ?? "",
                Kind = type is "R" or "A" ? PrincipalKind.Role : PrincipalKind.User,
                Scope = SecurityScope.Database,
                TypeDescription = type switch
                {
                    "R" => "Database role",
                    "A" => "Application role",
                    "S" when string.Equals(authentication, "DATABASE", StringComparison.OrdinalIgnoreCase) => "Contained user (password)",
                    "S" when login is null => "User without login",
                    "S" => "SQL user",
                    "U" => "Windows user",
                    "G" => "Windows group",
                    "E" => "Microsoft Entra user",
                    "X" => "Microsoft Entra group",
                    "C" => "Certificate user",
                    "K" => "Asymmetric key user",
                    _ => r.Text("type_desc") ?? type
                },
                IsSystem = r.Bool("is_system"),
                IsFixedRole = r.Bool("is_fixed_role"),
                LoginName = login,
                DefaultSchema = r.Text("default_schema_name"),
                Attributes = login is not null && !string.Equals(login, r.Text("name"), StringComparison.Ordinal) ? ["login " + login] : []
            });
        }

        var memberships = Rows(serverMembers)
            .Select(r => new SecurityMembership(r.Text("member") ?? "", r.Text("role_name") ?? "", SecurityScope.Server))
            .Concat(Rows(dbMembers).Select(r => new SecurityMembership(r.Text("member") ?? "", r.Text("role_name") ?? "", SecurityScope.Database)))
            .ToList();

        var grants = Rows(permissions).Select(r =>
        {
            var cls = r.Int("class") ?? 0;
            var kind = cls switch
            {
                0 => SecurableKind.Database,
                3 => SecurableKind.Schema,
                6 => SecurableKind.Type,
                _ => (r.Text("object_type") ?? "").Trim() switch
                {
                    "U" => SecurableKind.Table,
                    "V" => SecurableKind.View,
                    "P" or "PC" or "X" => SecurableKind.Procedure,
                    "FN" or "IF" or "TF" or "FS" or "FT" or "AF" => SecurableKind.Function,
                    "SO" => SecurableKind.Sequence,
                    _ => SecurableKind.Other
                }
            };
            return new SecurityGrant
            {
                Grantee = r.Text("grantee") ?? "",
                Grantor = r.Text("grantor"),
                Permission = r.Text("permission") ?? "",
                State = (r.Text("state") ?? "G").Trim() switch
                {
                    "D" => GrantState.Deny,
                    "W" => GrantState.GrantWithGrantOption,
                    _ => GrantState.Grant
                },
                On = new Securable
                {
                    Kind = kind,
                    Schema = r.Text("schema_name") ?? "",
                    Name = r.Text("object_name") ?? "",
                    Column = r.Text("column_name")
                }
            };
        }).ToList();

        return new SecurityCatalog
        {
            ProviderKey = SqlDialect.SqlServerKey,
            Database = database,
            Principals = principals,
            Memberships = memberships,
            Grants = grants,
            Warnings = warnings
        };
    }

    private static IEnumerable<Row> Rows(QueryResultSet? set)
    {
        if (set is null) yield break;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < set.Columns.Count; i++) index.TryAdd(set.Columns[i], i);
        foreach (var values in set.Rows) yield return new Row(index, values);
    }

    private readonly record struct Row(Dictionary<string, int> Index, IReadOnlyList<object?> Values)
    {
        private object? Get(string column) =>
            Index.TryGetValue(column, out var i) && i < Values.Count && Values[i] is not (null or DBNull) ? Values[i] : null;

        public string? Text(string column) => Get(column) is { } v ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;

        public bool Bool(string column) => Get(column) switch
        {
            null => false,
            bool b => b,
            string s => s is "1" or "t" or "true" or "True",
            var v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) != 0
        };

        public int? Int(string column) => Get(column) is { } v ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
