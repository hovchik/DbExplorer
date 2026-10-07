using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Copy;

namespace DbExplorer.Application.Security;

/// <summary>
/// The script for a set of pending security edits, in two forms: <see cref="Display"/> with every password replaced by
/// <see cref="Secret.Mask"/> (for the preview, the confirm box and the clipboard), and <see cref="Executable"/> with the
/// real values, which only goes to the server and is never shown, copied or logged.
/// </summary>
public sealed record SecurityScript(string Display, string Executable, int StatementCount, bool HasSecrets)
{
    public static SecurityScript Empty { get; } = new("", "", 0, false);
}

public static class SecurityScriptBuilder
{
    /// <summary>Permission names go into the script unquoted, so only letters and single spaces are accepted.</summary>
    private static readonly Regex PermissionName = new(@"^[A-Za-z]+( [A-Za-z]+)*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Permissions offered when granting, by engine and securable.</summary>
    public static IReadOnlyList<string> PermissionsFor(string providerKey, SecurableKind kind) => providerKey == SqlDialect.SqlServerKey
        ? kind switch
        {
            SecurableKind.Database => ["CONNECT", "SELECT", "INSERT", "UPDATE", "DELETE", "EXECUTE", "CREATE TABLE", "CREATE VIEW", "CREATE PROCEDURE", "CREATE FUNCTION", "CREATE SCHEMA", "ALTER", "ALTER ANY SCHEMA", "VIEW DEFINITION", "SHOWPLAN", "VIEW DATABASE STATE", "CONTROL"],
            SecurableKind.Schema => ["SELECT", "INSERT", "UPDATE", "DELETE", "EXECUTE", "ALTER", "REFERENCES", "VIEW DEFINITION", "CONTROL", "TAKE OWNERSHIP"],
            SecurableKind.Procedure => ["EXECUTE", "VIEW DEFINITION", "ALTER", "CONTROL", "TAKE OWNERSHIP"],
            SecurableKind.Function => ["EXECUTE", "SELECT", "REFERENCES", "VIEW DEFINITION", "ALTER", "CONTROL"],
            SecurableKind.Sequence => ["UPDATE", "REFERENCES", "VIEW DEFINITION", "ALTER", "CONTROL"],
            SecurableKind.Type => ["EXECUTE", "REFERENCES", "VIEW DEFINITION", "CONTROL"],
            _ => ["SELECT", "INSERT", "UPDATE", "DELETE", "REFERENCES", "VIEW DEFINITION", "ALTER", "CONTROL", "TAKE OWNERSHIP"]
        }
        : kind switch
        {
            SecurableKind.Database => ["CONNECT", "CREATE", "TEMPORARY", "ALL PRIVILEGES"],
            SecurableKind.Schema => ["USAGE", "CREATE", "ALL PRIVILEGES"],
            SecurableKind.Sequence => ["USAGE", "SELECT", "UPDATE", "ALL PRIVILEGES"],
            SecurableKind.Function or SecurableKind.Procedure => ["EXECUTE", "ALL PRIVILEGES"],
            SecurableKind.Type => ["USAGE", "ALL PRIVILEGES"],
            SecurableKind.View or SecurableKind.MaterializedView => ["SELECT", "INSERT", "UPDATE", "DELETE", "TRIGGER", "REFERENCES", "ALL PRIVILEGES"],
            _ => ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER", "ALL PRIVILEGES"]
        };

    /// <param name="database">The database the script runs in; PostgreSQL names it in GRANT … ON DATABASE.</param>
    /// <exception cref="ArgumentException">A change the engine cannot express (DENY on PostgreSQL, an odd permission name).</exception>
    public static SecurityScript Build(IEnumerable<SecurityChange> changes, string providerKey, string? database)
    {
        var ordered = changes.Select((c, i) => (c, i)).OrderBy(x => x.c.Phase).ThenBy(x => x.i).Select(x => x.c).ToList();
        if (ordered.Count == 0) return SecurityScript.Empty;

        var sqlServer = providerKey == SqlDialect.SqlServerKey;
        var display = new StringBuilder();
        var executable = new StringBuilder();
        var statements = 0;
        var secrets = false;
        foreach (var change in ordered)
        {
            display.Append("-- ").AppendLine(change.Summary);
            foreach (var statement in sqlServer ? SqlServer(change) : Postgres(change, database))
            {
                display.AppendLine(statement.Display);
                executable.AppendLine(statement.Executable);
                statements++;
                secrets |= !ReferenceEquals(statement.Display, statement.Executable);
            }
        }
        if (secrets)
            display.Insert(0, sqlServer
                ? "-- Passwords are shown as " + Secret.Mask + "; the script that runs carries them.\n"
                : "-- Passwords are shown as " + Secret.Mask + "; the script that runs sends them as SCRAM-SHA-256 verifiers, never in plain text.\n");
        return new SecurityScript(display.ToString().TrimEnd(), executable.ToString().TrimEnd(), statements, secrets);
    }

    private readonly record struct Statement(string Display, string Executable)
    {
        public static implicit operator Statement(string sql) => new(sql, sql);
    }

    // ---------------- SQL Server ----------------

    private static string Q(string name) => SqlDialect.SqlServer.Quote(name);

    private static string NLiteral(string value) => "N'" + value.Replace("'", "''") + "'";

    private static IEnumerable<Statement> SqlServer(SecurityChange change)
    {
        switch (change)
        {
            case CreateLoginChange c:
            {
                var options = c.DefaultDatabase is { Length: > 0 } db ? $", DEFAULT_DATABASE = {Q(db)}" : "";
                yield return WithPassword($"CREATE LOGIN {Q(c.Name)} WITH PASSWORD = ", c.Password, options + ";", NLiteral);
                if (c.CreateUser) yield return $"CREATE USER {Q(c.Name)} FOR LOGIN {Q(c.Name)};";
                break;
            }
            case CreateUserChange c:
                yield return c.LoginName is { Length: > 0 } login
                    ? $"CREATE USER {Q(c.Name)} FOR LOGIN {Q(login)};"
                    : $"CREATE USER {Q(c.Name)} WITHOUT LOGIN;";
                break;
            case CreateRoleChange c:
                yield return c.Scope == SecurityScope.Server ? $"CREATE SERVER ROLE {Q(c.Name)};" : $"CREATE ROLE {Q(c.Name)};";
                break;
            case DropPrincipalChange { Principal: var p }:
                yield return p.Kind switch
                {
                    PrincipalKind.Login => $"DROP LOGIN {Q(p.Name)};",
                    PrincipalKind.User => $"DROP USER {Q(p.Name)};",
                    PrincipalKind.ServerRole => $"DROP SERVER ROLE {Q(p.Name)};",
                    _ when p.TypeDescription == "Application role" => $"DROP APPLICATION ROLE {Q(p.Name)};",
                    _ => $"DROP ROLE {Q(p.Name)};"
                };
                break;
            case SetPasswordChange c:
                yield return c.Principal.Kind == PrincipalKind.User
                    ? WithPassword($"ALTER USER {Q(c.Principal.Name)} WITH PASSWORD = ", c.Password, ";", NLiteral)
                    : WithPassword($"ALTER LOGIN {Q(c.Principal.Name)} WITH PASSWORD = ", c.Password, ";", NLiteral);
                break;
            case SetLoginEnabledChange c:
                yield return $"ALTER LOGIN {Q(c.Principal.Name)} {(c.Enabled ? "ENABLE" : "DISABLE")};";
                break;
            case MembershipChange c:
                yield return $"ALTER {(c.Scope == SecurityScope.Server ? "SERVER ROLE" : "ROLE")} {Q(c.Role)} {(c.Add ? "ADD" : "DROP")} MEMBER {Q(c.Member)};";
                break;
            case PermissionChange c:
            {
                var permission = CheckPermission(c.Permission);
                var on = c.On.Kind switch
                {
                    SecurableKind.Database => "",
                    SecurableKind.Schema => $" ON SCHEMA::{Q(c.On.Schema)}",
                    SecurableKind.Type => $" ON TYPE::{Q(c.On.Schema)}.{Q(c.On.Name)}",
                    _ => $" ON {Q(c.On.Schema)}.{Q(c.On.Name)}" + (c.On.Column is { Length: > 0 } col ? $" ({Q(col)})" : "")
                };
                yield return c.Action switch
                {
                    PermissionAction.Revoke => $"REVOKE {permission}{on} FROM {Q(c.Grantee)}{(c.Cascade ? " CASCADE" : "")};",
                    PermissionAction.Deny => $"DENY {permission}{on} TO {Q(c.Grantee)};",
                    PermissionAction.GrantWithGrantOption => $"GRANT {permission}{on} TO {Q(c.Grantee)} WITH GRANT OPTION;",
                    _ => $"GRANT {permission}{on} TO {Q(c.Grantee)};"
                };
                break;
            }
            default:
                throw new ArgumentException($"{change.Summary} is not supported on SQL Server.");
        }
    }

    // ---------------- PostgreSQL ----------------

    private static string P(string name) => name == "PUBLIC" ? "PUBLIC" : SqlDialect.Postgres.Quote(name);

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    private static IEnumerable<Statement> Postgres(SecurityChange change, string? database)
    {
        switch (change)
        {
            case CreateLoginChange c:
                yield return WithPassword($"CREATE ROLE {P(c.Name)} LOGIN PASSWORD ", c.Password, ";", ScramLiteral);
                break;
            case CreateUserChange c:
                throw new ArgumentException($"PostgreSQL has no database users; create a role that can log in instead of {c.Name}.");
            case CreateRoleChange c:
                yield return $"CREATE ROLE {P(c.Name)} NOLOGIN;";
                break;
            case DropPrincipalChange { Principal: var p }:
                yield return $"DROP ROLE {P(p.Name)};";
                break;
            case SetPasswordChange c:
                yield return WithPassword($"ALTER ROLE {P(c.Principal.Name)} PASSWORD ", c.Password, ";", ScramLiteral);
                break;
            case SetLoginEnabledChange c:
                yield return $"ALTER ROLE {P(c.Principal.Name)} {(c.Enabled ? "LOGIN" : "NOLOGIN")};";
                break;
            case MembershipChange c:
                yield return c.Add ? $"GRANT {P(c.Role)} TO {P(c.Member)};" : $"REVOKE {P(c.Role)} FROM {P(c.Member)};";
                break;
            case PermissionChange c:
            {
                if (c.Action == PermissionAction.Deny)
                    throw new ArgumentException("PostgreSQL has no DENY: revoke the permission, or the role that gives it, instead.");
                var permission = CheckPermission(c.Permission);
                var on = c.On.Kind switch
                {
                    SecurableKind.Database => "DATABASE " + P(string.IsNullOrEmpty(database)
                        ? throw new ArgumentException("Pick a database to grant on.")
                        : database),
                    SecurableKind.Schema => "SCHEMA " + P(c.On.Schema),
                    SecurableKind.Sequence => $"SEQUENCE {P(c.On.Schema)}.{P(c.On.Name)}",
                    SecurableKind.Function or SecurableKind.Procedure => $"ROUTINE {P(c.On.Schema)}.{P(c.On.Name)}({c.On.Arguments})",
                    SecurableKind.Type => $"TYPE {P(c.On.Schema)}.{P(c.On.Name)}",
                    _ => $"TABLE {P(c.On.Schema)}.{P(c.On.Name)}"
                };
                if (c.On.Column is { Length: > 0 } column) permission += $" ({P(column)})";
                yield return c.Action switch
                {
                    PermissionAction.Revoke => $"REVOKE {permission} ON {on} FROM {P(c.Grantee)}{(c.Cascade ? " CASCADE" : "")};",
                    PermissionAction.GrantWithGrantOption => $"GRANT {permission} ON {on} TO {P(c.Grantee)} WITH GRANT OPTION;",
                    _ => $"GRANT {permission} ON {on} TO {P(c.Grantee)};"
                };
                break;
            }
            default:
                throw new ArgumentException($"{change.Summary} is not supported on PostgreSQL.");
        }
    }

    private static string ScramLiteral(string password) => Literal(PostgresScram.Verifier(password));

    private static Statement WithPassword(string before, Secret password, string after, Func<string, string> literal)
    {
        if (password.IsEmpty) throw new ArgumentException("Type a password.");
        return new Statement(before + "'" + Secret.Mask + "'" + after, before + literal(password.Value) + after);
    }

    private static string CheckPermission(string permission)
    {
        var p = permission.Trim();
        if (!PermissionName.IsMatch(p)) throw new ArgumentException($"\"{permission}\" is not a permission name.");
        return p.ToUpperInvariant();
    }
}
