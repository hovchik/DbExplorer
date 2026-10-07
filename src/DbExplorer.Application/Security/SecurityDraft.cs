using DbExplorer.Application.Copy;

namespace DbExplorer.Application.Security;

public enum PendingState
{
    None,
    Adding,
    Removing
}

/// <summary>A membership as the Security tab lists it: on the server, or about to be added or removed.</summary>
public sealed record MembershipView(SecurityMembership Membership, PendingState Pending);

/// <summary>A permission as the Security tab lists it: on the server, or about to be granted, denied or revoked.</summary>
public sealed record GrantView(SecurityGrant Grant, PendingState Pending);

/// <summary>
/// The catalog plus the edits not run yet. Adding an edit checks it against what is on the server and what is already
/// pending (an add after a pending remove of the same membership just cancels the remove), and the lists it hands out
/// show the server state with the pending edits applied, so the tab reads as "what it will be".
/// </summary>
public sealed class SecurityDraft(SecurityCatalog catalog)
{
    private readonly List<SecurityChange> _changes = [];

    public SecurityCatalog Catalog { get; } = catalog;

    public IReadOnlyList<SecurityChange> Changes => _changes;

    private bool SqlServer => Catalog.ProviderKey == SqlDialect.SqlServerKey;

    private StringComparison NameComparison => SqlServer ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private bool Same(string a, string b) => string.Equals(a, b, NameComparison);

    /// <summary>The catalog's principals, pending creates included (flagged <see cref="SecurityPrincipal.IsNew"/>).</summary>
    public IReadOnlyList<SecurityPrincipal> Principals
    {
        get
        {
            var list = Catalog.Principals.ToList();
            foreach (var change in _changes) list.AddRange(Created(change));
            return list;
        }
    }

    public bool IsPendingDrop(SecurityPrincipal principal) =>
        _changes.OfType<DropPrincipalChange>().Any(d => d.Principal.Scope == principal.Scope && Same(d.Principal.Name, principal.Name));

    public SecurityPrincipal? Find(string name, SecurityScope? scope = null) =>
        Principals.FirstOrDefault(p => (scope is null || p.Scope == scope) && Same(p.Name, name));

    /// <summary>The roles a principal can be added to: same scope (SQL Server server roles take logins, database roles
    /// take users and roles), not itself, not already one of its roles.</summary>
    public IReadOnlyList<string> RolesAvailableTo(SecurityPrincipal principal)
    {
        var current = RolesOf(principal).Where(m => m.Pending != PendingState.Removing).Select(m => m.Membership.Role).ToList();
        return Principals
            .Where(p => p.IsRole && p.Scope == principal.Scope && !Same(p.Name, principal.Name) && p.Name != "PUBLIC" &&
                        !(SqlServer && Same(p.Name, "public")) && !current.Any(c => Same(c, p.Name)))
            .Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The principals that can be added to a role.</summary>
    public IReadOnlyList<string> MembersAvailableFor(SecurityPrincipal role)
    {
        var current = MembersOf(role).Where(m => m.Pending != PendingState.Removing).Select(m => m.Membership.Member).ToList();
        return Principals
            .Where(p => p.Scope == role.Scope && !Same(p.Name, role.Name) && p.Name != "PUBLIC" && !current.Any(c => Same(c, p.Name)) &&
                        (!SqlServer || role.Kind != PrincipalKind.ServerRole || p.Kind == PrincipalKind.Login || p.Kind == PrincipalKind.ServerRole) &&
                        !(SqlServer && p.IsFixedRole))
            .Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<MembershipView> RolesOf(SecurityPrincipal principal) =>
        Memberships(m => m.Scope == principal.Scope && Same(m.Member, principal.Name));

    public IReadOnlyList<MembershipView> MembersOf(SecurityPrincipal role) =>
        Memberships(m => m.Scope == role.Scope && Same(m.Role, role.Name));

    private IReadOnlyList<MembershipView> Memberships(Func<SecurityMembership, bool> match)
    {
        var list = Catalog.Memberships.Where(match)
            .Select(m => new MembershipView(m, PendingMembership(m) is { Add: false } ? PendingState.Removing : PendingState.None))
            .ToList();
        list.AddRange(_changes.OfType<MembershipChange>().Where(c => c.Add)
            .Select(c => new SecurityMembership(c.Member, c.Role, c.Scope))
            .Where(match)
            .Select(m => new MembershipView(m, PendingState.Adding)));
        return list;
    }

    public IReadOnlyList<GrantView> GrantsOf(SecurityPrincipal principal)
    {
        var list = (principal.IsNew ? [] : Catalog.GrantsOf(principal))
            .Select(g => new GrantView(g, PendingPermission(g.Grantee, g.Permission, g.On) is { Action: PermissionAction.Revoke }
                ? PendingState.Removing
                : PendingState.None))
            .ToList();
        if (principal.Kind == PrincipalKind.ServerRole || (SqlServer && principal.Kind == PrincipalKind.Login)) return list;
        list.AddRange(_changes.OfType<PermissionChange>()
            .Where(c => c.Action != PermissionAction.Revoke && Same(c.Grantee, principal.Name))
            .Select(c => new GrantView(new SecurityGrant
            {
                Grantee = c.Grantee, Permission = c.Permission, On = c.On,
                State = c.Action switch
                {
                    PermissionAction.Deny => GrantState.Deny,
                    PermissionAction.GrantWithGrantOption => GrantState.GrantWithGrantOption,
                    _ => GrantState.Grant
                }
            }, PendingState.Adding)));
        return list;
    }

    /// <summary>Adds an edit; returns why it was refused, or null. An edit that undoes a pending one cancels it instead.</summary>
    public string? Add(SecurityChange change)
    {
        switch (change)
        {
            case CreateLoginChange or CreateUserChange or CreateRoleChange:
            {
                var created = Created(change).ToList();
                foreach (var p in created)
                {
                    if (string.IsNullOrWhiteSpace(p.Name)) return "Type a name.";
                    if (Find(p.Name, p.Scope) is { } existing)
                        return $"{existing.Name} already exists{(existing.IsNew ? " in the pending changes" : "")}.";
                }
                if (change is CreateUserChange { LoginName: { } login } && SqlServer &&
                    Catalog.Principals.Any(p => p.Kind == PrincipalKind.User && p.LoginName is { } l && Same(l, login)))
                    return $"Login {login} already has a user in this database.";
                break;
            }
            case DropPrincipalChange { Principal: var p }:
                if (p.IsSystem) return $"{p.Name} is built in and cannot be dropped.";
                if (p.IsNew)
                {
                    DiscardCreated(p);
                    return null;
                }
                if (IsPendingDrop(p)) return null;
                // Additions for a principal that is about to go would fail or mean nothing; removals clear the way.
                _changes.RemoveAll(c => Mentions(c, p.Name) && !IsRemoval(c));
                ClearTheWayToDrop(p);
                break;
            case SetPasswordChange c:
                if (!c.Principal.HasPassword && !c.Principal.IsNew) return $"{c.Principal.Name} does not sign in with a password set here.";
                _changes.RemoveAll(x => x is SetPasswordChange s && Same(s.Principal.Name, c.Principal.Name));
                break;
            case SetLoginEnabledChange c:
                if (_changes.RemoveAll(x => x is SetLoginEnabledChange s && Same(s.Principal.Name, c.Principal.Name)) > 0 &&
                    IsCurrentlyEnabled(c.Principal) == c.Enabled)
                    return null;
                break;
            case MembershipChange c:
            {
                if (Same(c.Member, c.Role)) return "A role cannot be a member of itself.";
                var onServer = Catalog.Memberships.Any(m => m.Scope == c.Scope && Same(m.Member, c.Member) && Same(m.Role, c.Role));
                if (PendingMembership(new SecurityMembership(c.Member, c.Role, c.Scope)) is { } pending)
                {
                    if (pending.Add == c.Add) return c.Add ? $"{c.Member} is already being added to {c.Role}." : null;
                    _changes.Remove(pending);
                    return null;
                }
                if (c.Add && onServer) return $"{c.Member} is already a member of {c.Role}.";
                if (!c.Add && !onServer) return $"{c.Member} is not a member of {c.Role}.";
                break;
            }
            case PermissionChange c:
            {
                if (string.IsNullOrWhiteSpace(c.Permission)) return "Pick a permission.";
                if (c.Action == PermissionAction.Deny && !SqlServer) return "PostgreSQL has no DENY: revoke the permission, or the role that gives it, instead.";
                var pending = PendingPermission(c.Grantee, c.Permission, c.On);
                var onServer = Catalog.Grants.FirstOrDefault(g => Same(g.Grantee, c.Grantee) && SamePermission(g.Permission, c.Permission) && g.On == c.On);
                if (pending is not null)
                {
                    _changes.Remove(pending);
                    // Revoking a pending grant just drops the grant.
                    if (c.Action == PermissionAction.Revoke && pending.Action != PermissionAction.Revoke) return null;
                    // Re-granting what a pending revoke takes back keeps it as it is.
                    if (pending.Action == PermissionAction.Revoke && onServer is not null && SameState(onServer.State, c.Action)) return null;
                }
                if (c.Action == PermissionAction.Revoke && onServer is null) return $"{c.Grantee} has no {c.Permission} on {c.On.Display} to revoke.";
                if (c.Action != PermissionAction.Revoke && onServer is not null && SameState(onServer.State, c.Action))
                    return $"{c.Grantee} already has {onServer.StateText} {c.Permission} on {c.On.Display}.";
                break;
            }
        }
        _changes.Add(change);
        return null;
    }

    public void Remove(SecurityChange change)
    {
        if (change is CreateLoginChange or CreateUserChange or CreateRoleChange)
        {
            foreach (var p in Created(change).ToList()) DiscardCreated(p);
            return;
        }
        _changes.Remove(change);
    }

    public void Clear() => _changes.Clear();

    public SecurityScript Script(string? database) => SecurityScriptBuilder.Build(_changes, Catalog.ProviderKey, database);

    /// <summary>Whether a SQL Server login can sign in now (or a PostgreSQL role has LOGIN), counting pending edits.</summary>
    public bool IsEnabled(SecurityPrincipal principal) =>
        _changes.OfType<SetLoginEnabledChange>().LastOrDefault(c => Same(c.Principal.Name, principal.Name)) is { } pending
            ? pending.Enabled
            : IsCurrentlyEnabled(principal);

    private bool IsCurrentlyEnabled(SecurityPrincipal principal) =>
        SqlServer ? !principal.IsDisabled : principal.Kind == PrincipalKind.Login;

    private static bool IsRemoval(SecurityChange change) =>
        change is PermissionChange { Action: PermissionAction.Revoke } or MembershipChange { Add: false };

    /// <summary>
    /// What the engine wants gone before a DROP: PostgreSQL refuses to drop a role that still holds privileges, so its
    /// privileges in this database are revoked first (privileges in other databases, and owned objects, still stop the
    /// drop, and the server says so); SQL Server refuses to drop a role that has members, so they are removed first.
    /// </summary>
    private void ClearTheWayToDrop(SecurityPrincipal p)
    {
        if (!SqlServer)
        {
            foreach (var grant in Catalog.GrantsOf(p))
                if (PendingPermission(grant.Grantee, grant.Permission, grant.On) is null)
                    _changes.Add(PermissionChange.RevokeOf(grant));
        }
        else if (p.IsRole)
        {
            foreach (var m in Catalog.MembersOf(p))
                if (PendingMembership(m) is null)
                    _changes.Add(new MembershipChange(m.Member, m.Role, m.Scope, Add: false));
        }
    }

    private void DiscardCreated(SecurityPrincipal p)
    {
        _changes.RemoveAll(c => Mentions(c, p.Name));
    }

    private bool Mentions(SecurityChange change, string name) => change switch
    {
        CreateLoginChange c => Same(c.Name, name),
        CreateUserChange c => Same(c.Name, name),
        CreateRoleChange c => Same(c.Name, name),
        DropPrincipalChange c => Same(c.Principal.Name, name),
        SetPasswordChange c => Same(c.Principal.Name, name),
        SetLoginEnabledChange c => Same(c.Principal.Name, name),
        MembershipChange c => Same(c.Member, name) || Same(c.Role, name),
        PermissionChange c => Same(c.Grantee, name),
        _ => false
    };

    private MembershipChange? PendingMembership(SecurityMembership m) =>
        _changes.OfType<MembershipChange>().FirstOrDefault(c => c.Scope == m.Scope && Same(c.Member, m.Member) && Same(c.Role, m.Role));

    private PermissionChange? PendingPermission(string grantee, string permission, Securable on) =>
        _changes.OfType<PermissionChange>().FirstOrDefault(c => Same(c.Grantee, grantee) && SamePermission(c.Permission, permission) && c.On == on);

    private static bool SamePermission(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameState(GrantState state, PermissionAction action) => (state, action) switch
    {
        (GrantState.Grant, PermissionAction.Grant) => true,
        (GrantState.GrantWithGrantOption, PermissionAction.GrantWithGrantOption) => true,
        (GrantState.GrantWithGrantOption, PermissionAction.Grant) => true,
        (GrantState.Deny, PermissionAction.Deny) => true,
        _ => false
    };

    private IEnumerable<SecurityPrincipal> Created(SecurityChange change)
    {
        switch (change)
        {
            case CreateLoginChange c:
                yield return new SecurityPrincipal
                {
                    Name = c.Name.Trim(), Kind = PrincipalKind.Login, Scope = SecurityScope.Server, IsNew = true, HasPassword = true,
                    TypeDescription = SqlServer ? "SQL login" : "Role that can log in", DefaultDatabase = c.DefaultDatabase
                };
                if (SqlServer && c.CreateUser)
                    yield return new SecurityPrincipal
                    {
                        Name = c.Name.Trim(), Kind = PrincipalKind.User, Scope = SecurityScope.Database, IsNew = true,
                        TypeDescription = "SQL user", LoginName = c.Name.Trim()
                    };
                break;
            case CreateUserChange c:
                yield return new SecurityPrincipal
                {
                    Name = c.Name.Trim(), Kind = PrincipalKind.User, Scope = SecurityScope.Database, IsNew = true,
                    TypeDescription = c.LoginName is null ? "User without login" : "SQL user", LoginName = c.LoginName
                };
                break;
            case CreateRoleChange c:
                var server = !SqlServer || c.Scope == SecurityScope.Server;
                yield return new SecurityPrincipal
                {
                    Name = c.Name.Trim(), Kind = SqlServer && server ? PrincipalKind.ServerRole : PrincipalKind.Role,
                    Scope = server ? SecurityScope.Server : SecurityScope.Database, IsNew = true,
                    TypeDescription = SqlServer ? server ? "Server role" : "Database role" : "Role"
                };
                break;
        }
    }
}
