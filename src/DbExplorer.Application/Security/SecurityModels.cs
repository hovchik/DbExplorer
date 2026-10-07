namespace DbExplorer.Application.Security;

/// <summary>Where a principal or membership lives: the whole server (SQL Server logins and server roles, every
/// PostgreSQL role) or one database (SQL Server users and database roles).</summary>
public enum SecurityScope
{
    Server,
    Database
}

public enum PrincipalKind
{
    /// <summary>Can sign in: a SQL Server login, or a PostgreSQL role with LOGIN.</summary>
    Login,

    /// <summary>A SQL Server database user.</summary>
    User,

    /// <summary>A group of permissions: a SQL Server database role, or a PostgreSQL role without LOGIN.</summary>
    Role,

    /// <summary>A SQL Server server role (sysadmin, …).</summary>
    ServerRole
}

/// <summary>A login, user or role. Never carries a password or its hash.</summary>
public sealed record SecurityPrincipal
{
    public required string Name { get; init; }
    public required PrincipalKind Kind { get; init; }
    public required SecurityScope Scope { get; init; }

    /// <summary>"SQL login", "Windows group", "Database role", "Role", …</summary>
    public string TypeDescription { get; init; } = "";

    /// <summary>Built in to the engine (sa, dbo, public, pg_read_all_data, fixed roles): shown, but not dropped.</summary>
    public bool IsSystem { get; init; }

    /// <summary>A SQL Server fixed role (db_datareader, sysadmin): members can change, permissions cannot.</summary>
    public bool IsFixedRole { get; init; }

    public bool IsDisabled { get; init; }

    /// <summary>Not on the server yet: a pending create on the Security tab.</summary>
    public bool IsNew { get; init; }

    /// <summary>The login a SQL Server database user signs in as.</summary>
    public string? LoginName { get; init; }

    public string? DefaultSchema { get; init; }
    public string? DefaultDatabase { get; init; }

    /// <summary>Engine flags shown as tags: SUPERUSER, CREATEDB, "valid until 2027-01-01", "SQL password", …</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    public bool IsRole => Kind is PrincipalKind.Role or PrincipalKind.ServerRole;

    /// <summary>Can have a password set from the app: SQL Server SQL logins and PostgreSQL roles.</summary>
    public bool HasPassword { get; init; }

    public string Key => Scope + ":" + Name;
}

/// <summary><see cref="Member"/> belongs to <see cref="Role"/>.</summary>
public sealed record SecurityMembership(string Member, string Role, SecurityScope Scope, bool AdminOption = false);

public enum GrantState
{
    Grant,
    GrantWithGrantOption,
    Deny
}

public enum SecurableKind
{
    Database,
    Schema,
    Table,
    View,
    MaterializedView,
    Sequence,
    Function,
    Procedure,
    Type,
    Other
}

/// <summary>What a permission is on: the database itself, a schema, or one object.</summary>
public sealed record Securable
{
    public required SecurableKind Kind { get; init; }

    /// <summary>The schema of an object, or the schema itself; empty for the database.</summary>
    public string Schema { get; init; } = "";

    /// <summary>The object's name; empty for a schema or the database.</summary>
    public string Name { get; init; } = "";

    /// <summary>PostgreSQL function argument types, "integer, text", needed to name an overloaded function.</summary>
    public string? Arguments { get; init; }

    /// <summary>A single column the permission is limited to (SQL Server column permissions).</summary>
    public string? Column { get; init; }

    public static Securable Database { get; } = new() { Kind = SecurableKind.Database };

    public static Securable OfSchema(string schema) => new() { Kind = SecurableKind.Schema, Schema = schema };

    public static Securable Object(SecurableKind kind, string schema, string name, string? arguments = null) =>
        new() { Kind = kind, Schema = schema, Name = name, Arguments = arguments };

    public bool IsObject => Kind is not (SecurableKind.Database or SecurableKind.Schema);

    /// <summary>"DATABASE", "SCHEMA sales", "sales.orders", "sales.total(integer)", "sales.orders (amount)".</summary>
    public string Display => Kind switch
    {
        SecurableKind.Database => "DATABASE",
        SecurableKind.Schema => "SCHEMA " + Schema,
        _ => Schema + "." + Name + (Arguments is null ? "" : "(" + Arguments + ")") + (Column is null ? "" : " (" + Column + ")")
    };
}

/// <summary>One permission given to (or denied) a principal.</summary>
public sealed record SecurityGrant
{
    public required string Grantee { get; init; }
    public required string Permission { get; init; }
    public required Securable On { get; init; }
    public GrantState State { get; init; } = GrantState.Grant;
    public string? Grantor { get; init; }

    public string StateText => State switch
    {
        GrantState.Deny => "DENY",
        GrantState.GrantWithGrantOption => "GRANT (with grant option)",
        _ => "GRANT"
    };
}

/// <summary>Everything the Security tab shows for one database of a connection.</summary>
public sealed record SecurityCatalog
{
    public required string ProviderKey { get; init; }

    /// <summary>The database the users, database roles and permissions were read from.</summary>
    public string Database { get; init; } = "";

    public IReadOnlyList<SecurityPrincipal> Principals { get; init; } = [];
    public IReadOnlyList<SecurityMembership> Memberships { get; init; } = [];
    public IReadOnlyList<SecurityGrant> Grants { get; init; } = [];

    /// <summary>Parts the server would not show (e.g. logins without VIEW ANY DEFINITION), in plain words.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public SecurityPrincipal? Find(string name, SecurityScope? scope = null) =>
        Principals.FirstOrDefault(p => (scope is null || p.Scope == scope) && string.Equals(p.Name, name, StringComparison.Ordinal)) ??
        Principals.FirstOrDefault(p => (scope is null || p.Scope == scope) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The roles <paramref name="principal"/> belongs to directly.</summary>
    public IEnumerable<SecurityMembership> RolesOf(SecurityPrincipal principal) =>
        Memberships.Where(m => m.Scope == principal.Scope && string.Equals(m.Member, principal.Name, StringComparison.Ordinal));

    /// <summary>The direct members of a role.</summary>
    public IEnumerable<SecurityMembership> MembersOf(SecurityPrincipal role) =>
        Memberships.Where(m => m.Scope == role.Scope && string.Equals(m.Role, role.Name, StringComparison.Ordinal));

    /// <summary>The permissions given to the principal itself (not inherited through roles).</summary>
    public IEnumerable<SecurityGrant> GrantsOf(SecurityPrincipal principal) =>
        principal.Kind == PrincipalKind.ServerRole || (principal.Kind == PrincipalKind.Login && ProviderKey == Copy.SqlDialect.SqlServerKey)
            ? []
            : Grants.Where(g => string.Equals(g.Grantee, principal.Name, StringComparison.Ordinal));

    /// <summary>Every role the principal belongs to, directly or through other roles (cycles are cut).</summary>
    public IReadOnlyList<string> AllRolesOf(SecurityPrincipal principal)
    {
        var seen = new List<string>();
        var queue = new Queue<string>([principal.Name]);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            foreach (var m in Memberships.Where(m => m.Scope == principal.Scope && string.Equals(m.Member, name, StringComparison.Ordinal)))
            {
                if (seen.Contains(m.Role, StringComparer.Ordinal) || m.Role == principal.Name) continue;
                seen.Add(m.Role);
                queue.Enqueue(m.Role);
            }
        }
        return seen;
    }
}
