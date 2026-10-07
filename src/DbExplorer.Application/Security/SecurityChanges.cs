namespace DbExplorer.Application.Security;

/// <summary>A password typed in the app. <see cref="ToString"/> never shows it, so a change record that is logged,
/// formatted or shown in a debugger does not leak it; only <see cref="SecurityScriptBuilder"/> reads <see cref="Value"/>.</summary>
public sealed class Secret(string value)
{
    public const string Mask = "********";

    internal string Value { get; } = value;

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Mask;
}

/// <summary>One pending edit on the Security tab. Edits are collected, shown as one script, and run together.</summary>
public abstract record SecurityChange
{
    /// <summary>"Create login anna", "Grant SELECT on sales.orders to reporting", …</summary>
    public abstract string Summary { get; }

    /// <summary>Runs before later phases: creates, then property changes, then additions, then removals, then drops.</summary>
    internal abstract int Phase { get; }

    public sealed override string ToString() => Summary;
}

/// <summary>A principal that can sign in with a password: a SQL Server SQL login (and, when
/// <see cref="CreateUser"/>, a user of the same name in the database), or a PostgreSQL role WITH LOGIN.</summary>
public sealed record CreateLoginChange(string Name, Secret Password, bool CreateUser = false, string? DefaultDatabase = null) : SecurityChange
{
    public override string Summary => CreateUser ? $"Create login {Name} and its user" : $"Create login {Name}";
    internal override int Phase => 0;
}

/// <summary>A SQL Server database user for an existing login, or WITHOUT LOGIN when <see cref="LoginName"/> is null.</summary>
public sealed record CreateUserChange(string Name, string? LoginName) : SecurityChange
{
    public override string Summary => LoginName is null ? $"Create user {Name} without login" : $"Create user {Name} for login {LoginName}";
    internal override int Phase => 0;
}

/// <summary>A role: a SQL Server database role (or server role), or a PostgreSQL role WITHOUT LOGIN.</summary>
public sealed record CreateRoleChange(string Name, SecurityScope Scope = SecurityScope.Database) : SecurityChange
{
    public override string Summary => $"Create role {Name}";
    internal override int Phase => 0;
}

public sealed record DropPrincipalChange(SecurityPrincipal Principal) : SecurityChange
{
    public override string Summary => $"Drop {Principal.TypeDescription.ToLowerInvariant()} {Principal.Name}";
    internal override int Phase => 4;
}

public sealed record SetPasswordChange(SecurityPrincipal Principal, Secret Password) : SecurityChange
{
    public override string Summary => $"Change the password of {Principal.Name}";
    internal override int Phase => 1;
}

/// <summary>SQL Server: ALTER LOGIN … ENABLE/DISABLE. PostgreSQL: ALTER ROLE … LOGIN/NOLOGIN.</summary>
public sealed record SetLoginEnabledChange(SecurityPrincipal Principal, bool Enabled) : SecurityChange
{
    public override string Summary => Enabled ? $"Allow {Principal.Name} to log in" : $"Stop {Principal.Name} from logging in";
    internal override int Phase => 1;
}

public sealed record MembershipChange(string Member, string Role, SecurityScope Scope, bool Add) : SecurityChange
{
    public override string Summary => Add ? $"Add {Member} to {Role}" : $"Remove {Member} from {Role}";
    internal override int Phase => Add ? 2 : 3;
}

public enum PermissionAction
{
    Grant,
    GrantWithGrantOption,

    /// <summary>SQL Server only: refuses the permission even when a role grants it.</summary>
    Deny,

    /// <summary>Takes back a GRANT or a DENY.</summary>
    Revoke
}

public sealed record PermissionChange(string Grantee, string Permission, Securable On, PermissionAction Action) : SecurityChange
{
    /// <summary>For a revoke: the permission was held WITH GRANT OPTION, so grants made from it are revoked too.</summary>
    public bool Cascade { get; init; }

    public override string Summary => Action switch
    {
        PermissionAction.Revoke => $"Revoke {Permission} on {On.Display} from {Grantee}",
        PermissionAction.Deny => $"Deny {Permission} on {On.Display} to {Grantee}",
        PermissionAction.GrantWithGrantOption => $"Grant {Permission} on {On.Display} to {Grantee} with grant option",
        _ => $"Grant {Permission} on {On.Display} to {Grantee}"
    };

    internal override int Phase => Action == PermissionAction.Revoke ? 3 : 2;

    /// <summary>The change that takes an existing grant or deny back.</summary>
    public static PermissionChange RevokeOf(SecurityGrant grant) =>
        new(grant.Grantee, grant.Permission, grant.On, PermissionAction.Revoke) { Cascade = grant.State == GrantState.GrantWithGrantOption };
}
