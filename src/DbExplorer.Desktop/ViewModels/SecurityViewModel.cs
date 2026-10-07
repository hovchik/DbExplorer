using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Security;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// The Security tab: logins, users and roles of the connection with their memberships and permissions. Edits are
/// collected as pending changes, shown as one script (passwords masked) and run together in one transaction after a
/// confirmation, typing PRODUCTION on production connections; read-only connections only show.
/// </summary>
public partial class SecurityViewModel(SessionService sessions, IDialogService dialogs) : ViewModelBase, ISessionAware
{
    private const int TimeoutSeconds = 60;
    private DatabaseSession? _session;
    private SecurityDraft? _draft;
    /// <summary>The database <see cref="_draft"/> was read for: its database-level edits belong to that database only.</summary>
    private string? _draftDatabase;
    private int _loadVersion;
    private Dictionary<string, Securable> _targets = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private IReadOnlyList<string> _databases = [];
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _showSystem;
    [ObservableProperty] private string _kindFilter = AllKinds;
    [ObservableProperty] private string _status = "Connect to see logins, users and roles.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private PrincipalItem? _selectedPrincipal;
    [ObservableProperty] private string _script = "";
    [ObservableProperty] private string _warnings = "";

    // Selected principal
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailType = "";
    [ObservableProperty] private string _detailFacts = "";
    [ObservableProperty] private string _inheritedRoles = "";
    [ObservableProperty] private IReadOnlyList<string> _roleChoices = [];
    [ObservableProperty] private string _roleToAdd = "";
    [ObservableProperty] private IReadOnlyList<string> _memberChoices = [];
    [ObservableProperty] private string _memberToAdd = "";
    [ObservableProperty] private IReadOnlyList<string> _grantTargets = [];
    [ObservableProperty] private string _grantTarget = "";
    [ObservableProperty] private IReadOnlyList<string> _permissionChoices = [];
    [ObservableProperty] private string _grantPermission = "";
    [ObservableProperty] private bool _withGrantOption;

    public const string AllKinds = "All";
    public const string LoginKinds = "Can log in";
    public const string UserKinds = "Database users";
    public const string RoleKinds = "Roles";

    public IReadOnlyList<string> KindFilters => IsSqlServer ? [AllKinds, LoginKinds, UserKinds, RoleKinds] : [AllKinds, LoginKinds, RoleKinds];

    public ObservableCollection<PrincipalItem> Principals { get; } = [];
    public ObservableCollection<MembershipItem> MemberOf { get; } = [];
    public ObservableCollection<MembershipItem> Members { get; } = [];
    public ObservableCollection<GrantItem> Grants { get; } = [];
    public ObservableCollection<PendingItem> Pending { get; } = [];

    /// <summary>Raised to open SQL in a new query tab, against a database.</summary>
    public event Action<string, string?>? OpenSqlRequested;

    public bool IsSqlServer => _session?.Provider.ProviderKey == SqlDialect.SqlServerKey;
    public bool IsReadOnly => _session?.Profile.ReadOnly == true;
    public bool HasSelection => SelectedPrincipal is not null;
    public bool SelectedIsRole => SelectedPrincipal?.Principal.IsRole == true;
    public bool SelectedTakesGrants => SelectedPrincipal?.Principal is { } p && p.Kind != PrincipalKind.ServerRole && !(IsSqlServer && p.Kind == PrincipalKind.Login);
    public bool SelectedTakesRoles => SelectedPrincipal?.Principal is { } p && !(IsSqlServer && p.IsFixedRole) && p.Name != "PUBLIC";
    public bool SelectedAcceptsMembers => SelectedPrincipal?.Principal is { IsRole: true } p && p.Name != "PUBLIC" && !(IsSqlServer && p.Name == "public");
    public bool CanSetPassword => SelectedPrincipal?.Principal is { HasPassword: true, IsNew: false } && !IsPendingDrop;
    public bool CanToggleLogin => SelectedPrincipal?.Principal is { } p && !p.IsNew && !p.IsSystem && !IsPendingDrop &&
                                  (IsSqlServer ? p.Kind == PrincipalKind.Login : !p.IsRole || p.Kind == PrincipalKind.Role);
    public string ToggleLoginLabel => SelectedPrincipal is { } s && _draft?.IsEnabled(s.Principal) == true
        ? IsSqlServer ? "Disable login" : "Remove LOGIN"
        : IsSqlServer ? "Enable login" : "Allow LOGIN";
    public bool CanDrop => SelectedPrincipal?.Principal is { IsSystem: false } && !IsPendingDrop;
    public bool CanCreateUserForLogin => IsSqlServer && SelectedPrincipal?.Principal is { Kind: PrincipalKind.Login, IsNew: false } login &&
                                         _draft is { } d && !d.Principals.Any(p => p.Kind == PrincipalKind.User &&
                                             string.Equals(p.LoginName ?? (p.IsNew ? p.Name : null), login.Name, StringComparison.OrdinalIgnoreCase));
    public bool CanDeny => IsSqlServer;
    public bool HasPending => Pending.Count > 0;
    public bool HasWarnings => Warnings.Length > 0;
    public string ApplyLabel => Pending.Count == 0 ? "Apply…" : $"Apply {Pending.Count} change(s)…";
    private bool IsPendingDrop => SelectedPrincipal is { } s && _draft?.IsPendingDrop(s.Principal) == true;

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.DatabasesChanged -= OnDatabasesChanged;
        _session = session;
        if (_session is not null) _session.DatabasesChanged += OnDatabasesChanged;
        _draft = null;
        _draftDatabase = null;
        Principals.Clear();
        Pending.Clear();
        SelectedPrincipal = null;
        Script = "";
        Warnings = "";
        OnPropertyChanged(nameof(IsSqlServer));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(KindFilters));
        OnPropertyChanged(nameof(CanDeny));
        KindFilter = AllKinds;
        NotifyPending();
        if (session is null)
        {
            Databases = [];
            SelectedDatabase = null;
            Status = "Connect to see logins, users and roles.";
            return;
        }

        Databases = Merge(session.Snapshot.Databases, [session.Profile.Database]);
        var preferred = Databases.FirstOrDefault(d => Same(d, session.Profile.Database)) ?? Databases.FirstOrDefault();
        if (string.Equals(SelectedDatabase, preferred, StringComparison.Ordinal)) _ = LoadAsync();
        else SelectedDatabase = preferred;
        _ = LoadServerDatabasesAsync(session);
    }

    private async Task LoadServerDatabasesAsync(DatabaseSession session)
    {
        try
        {
            var names = await session.GetServerDatabasesAsync();
            if (!ReferenceEquals(session, _session)) return;
            var selected = SelectedDatabase;
            Databases = Merge(names, session.Snapshot.Databases, [session.Profile.Database]);
            SelectedDatabase = Databases.FirstOrDefault(d => Same(d, selected ?? "")) ?? selected;
        }
        catch
        {
            // Without the server list the picker still offers the databases already in the catalog.
        }
    }

    private void OnDatabasesChanged(object? sender, EventArgs e)
    {
        if (_session is { } session) _ = LoadServerDatabasesAsync(session);
    }

    partial void OnSelectedDatabaseChanged(string? value) => _ = LoadAsync();
    partial void OnFilterChanged(string value) => RebuildPrincipals();
    partial void OnShowSystemChanged(bool value) => RebuildPrincipals();
    partial void OnKindFilterChanged(string value) => RebuildPrincipals();
    partial void OnSelectedPrincipalChanged(PrincipalItem? value) => RebuildDetails();
    partial void OnWarningsChanged(string value) => OnPropertyChanged(nameof(HasWarnings));

    partial void OnGrantTargetChanged(string value)
    {
        var kind = _targets.TryGetValue(value.Trim(), out var target) ? target.Kind : SecurableKind.Table;
        PermissionChoices = SecurityScriptBuilder.PermissionsFor(_session?.Provider.ProviderKey ?? SqlDialect.PostgresKey, kind);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Reads the catalog of the selected database. Pending edits carry over; the ones that no longer fit
    /// (the role they add is gone, the grant exists now) are dropped and the status says how many. Edits inside the
    /// previous database (its users, roles and permissions) don't carry over to another database.</summary>
    private async Task LoadAsync()
    {
        if (_session is not { } session) return;
        var version = ++_loadVersion;
        if (session.Provider.ProviderKey == SqlDialect.MySqlKey)
        {
            Status = SecurityCatalogLoader.MySqlNotSupported;
            IsBusy = false;
            return;
        }
        var database = SelectedDatabase;
        IsBusy = true;
        Status = $"Reading logins, users and roles{(string.IsNullOrEmpty(database) ? "" : " of " + database)}…";
        try
        {
            var catalogTask = Task.Run(() => SecurityCatalogLoader.LoadAsync(session.Provider, database));
            var targetsTask = LoadTargetsAsync(session, database);
            var catalog = await catalogTask;
            var targets = await targetsTask;
            if (version != _loadVersion || !ReferenceEquals(session, _session)) return;

            var previous = _draft?.Changes.ToList() ?? [];
            var otherDatabase = _draft is not null && !string.Equals(_draftDatabase, database, StringComparison.OrdinalIgnoreCase);
            var left = otherDatabase ? previous.Count(IsDatabaseLevel) : 0;
            if (otherDatabase) previous = previous.Where(c => !IsDatabaseLevel(c)).ToList();
            var draft = new SecurityDraft(catalog);
            var dropped = previous.Count(change => draft.Add(change) is not null);
            _draft = draft;
            _draftDatabase = database;

            // Securables that already carry permissions, including PostgreSQL routines with their argument types.
            foreach (var g in catalog.Grants) targets.TryAdd(g.On.Display, g.On);
            _targets = targets;
            GrantTargets = targets.Keys.OrderBy(k => k.StartsWith("DATABASE", StringComparison.Ordinal) ? 0 : k.StartsWith("SCHEMA ", StringComparison.Ordinal) ? 1 : 2)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            Warnings = string.Join("\n", catalog.Warnings);

            var selectedKey = SelectedPrincipal?.Principal.Key;
            RebuildPrincipals(selectedKey);
            NotifyPending();
            var logins = catalog.Principals.Count(p => p.Kind == PrincipalKind.Login);
            var roles = catalog.Principals.Count(p => p.IsRole);
            var users = catalog.Principals.Count(p => p.Kind == PrincipalKind.User);
            Status = (IsSqlServer
                    ? $"{logins:N0} login(s), {users:N0} user(s) and {roles:N0} role(s)"
                    : $"{logins:N0} role(s) that can log in and {roles:N0} other role(s)") +
                $", {catalog.Grants.Count:N0} permission(s)." +
                (dropped > 0 ? $" {dropped} pending change(s) no longer applied and were dropped." : "") +
                (left > 0 ? $" {left} pending change(s) inside the previous database were dropped." : "") +
                (IsReadOnly ? " Read-only connection: changes can be scripted, not applied." : "");
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) Status = "Could not read security information: " + ex.Message;
        }
        finally
        {
            if (version == _loadVersion) IsBusy = false;
        }
    }

    /// <summary>An edit inside the selected database (a database user or role, membership or permission there), as
    /// opposed to one on the server (a login, a server role or, on PostgreSQL, any role).</summary>
    private static bool IsDatabaseLevel(SecurityChange change) => change switch
    {
        CreateLoginChange c => c.CreateUser,
        CreateUserChange => true,
        CreateRoleChange c => c.Scope == SecurityScope.Database,
        DropPrincipalChange c => c.Principal.Scope == SecurityScope.Database,
        SetPasswordChange c => c.Principal.Scope == SecurityScope.Database,
        SetLoginEnabledChange c => c.Principal.Scope == SecurityScope.Database,
        MembershipChange c => c.Scope == SecurityScope.Database,
        _ => true // permissions are on the database, its schemas or its objects
    };

    /// <summary>What permissions can be granted on: the database, its schemas and its objects, by display name.</summary>
    private async Task<Dictionary<string, Securable>> LoadTargetsAsync(DatabaseSession session, string? database)
    {
        var targets = new Dictionary<string, Securable>(StringComparer.OrdinalIgnoreCase) { [Securable.Database.Display] = Securable.Database };
        try
        {
            var snapshot = string.IsNullOrEmpty(database) || Same(database, session.Profile.Database)
                ? session.Snapshot
                : await sessions.GetDatabaseSnapshotAsync(session, database);
            var sqlServer = session.Provider.ProviderKey == SqlDialect.SqlServerKey;
            foreach (var o in snapshot.Objects.Where(o => string.IsNullOrEmpty(o.Database) || Same(o.Database, database ?? "")))
            {
                var schema = Securable.OfSchema(o.Schema);
                targets.TryAdd(schema.Display, schema);
                var kind = o.Type switch
                {
                    DbObjectType.Table or DbObjectType.ForeignTable => SecurableKind.Table,
                    DbObjectType.View => SecurableKind.View,
                    DbObjectType.MaterializedView => SecurableKind.MaterializedView,
                    DbObjectType.Sequence => SecurableKind.Sequence,
                    DbObjectType.Procedure when sqlServer => SecurableKind.Procedure,
                    DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction when sqlServer => SecurableKind.Function,
                    _ => (SecurableKind?)null
                };
                // PostgreSQL routines need their argument types; the catalog's grants list carries those.
                if (kind is { } k)
                {
                    var target = Securable.Object(k, o.Schema, o.Name);
                    targets.TryAdd(target.Display, target);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("security targets", ex);
        }
        return targets;
    }

    private void RebuildPrincipals(string? keepKey = null)
    {
        keepKey ??= SelectedPrincipal?.Principal.Key;
        Principals.Clear();
        if (_draft is not { } draft) return;
        var filter = Filter.Trim();
        var items = draft.Principals
            .Where(p => ShowSystem || !p.IsSystem || p.IsNew)
            .Where(p => filter.Length == 0 || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        (p.LoginName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(p => KindFilter switch
            {
                LoginKinds => p.Kind == PrincipalKind.Login,
                UserKinds => p.Kind == PrincipalKind.User,
                RoleKinds => p.IsRole,
                _ => true
            })
            .OrderBy(p => p.Kind switch { PrincipalKind.Login => 0, PrincipalKind.User => 1, PrincipalKind.Role => 2, _ => 3 })
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PrincipalItem(p, draft.IsPendingDrop(p), !draft.IsEnabled(p) && (IsSqlServer ? p.Kind == PrincipalKind.Login : false)));
        foreach (var item in items) Principals.Add(item);
        SelectedPrincipal = Principals.FirstOrDefault(p => p.Principal.Key == keepKey);
        if (SelectedPrincipal is null) RebuildDetails();
    }

    private void RebuildDetails()
    {
        MemberOf.Clear();
        Members.Clear();
        Grants.Clear();
        foreach (var name in new[]
                 {
                     nameof(HasSelection), nameof(SelectedIsRole), nameof(SelectedTakesGrants), nameof(SelectedTakesRoles),
                     nameof(SelectedAcceptsMembers), nameof(CanSetPassword), nameof(CanToggleLogin), nameof(ToggleLoginLabel),
                     nameof(CanDrop), nameof(CanCreateUserForLogin)
                 })
            OnPropertyChanged(name);
        if (SelectedPrincipal?.Principal is not { } p || _draft is not { } draft)
        {
            DetailTitle = "";
            DetailType = "";
            DetailFacts = "";
            InheritedRoles = "";
            return;
        }

        DetailTitle = p.Name;
        DetailType = p.TypeDescription + (p.Scope == SecurityScope.Database && !string.IsNullOrEmpty(SelectedDatabase) ? " in " + SelectedDatabase : "") +
                     (p.IsNew ? " · new, not created yet" : draft.IsPendingDrop(p) ? " · will be dropped" : "");
        var facts = new List<string>(p.Attributes);
        if (p.DefaultDatabase is { Length: > 0 } db) facts.Add("default database " + db);
        if (p.DefaultSchema is { Length: > 0 } schema) facts.Add("default schema " + schema);
        if (p.IsSystem) facts.Add("built in");
        if (IsSqlServer && p.Kind == PrincipalKind.Login)
        {
            var users = draft.Principals.Where(u => u.Kind == PrincipalKind.User && Same(u.LoginName ?? "", p.Name)).Select(u => u.Name).ToList();
            facts.Add(users.Count > 0 ? $"user {string.Join(", ", users)} in {SelectedDatabase}" : $"no user in {SelectedDatabase}");
        }
        DetailFacts = string.Join(" · ", facts);

        foreach (var m in draft.RolesOf(p)) MemberOf.Add(new MembershipItem(m, m.Membership.Role));
        foreach (var m in draft.MembersOf(p)) Members.Add(new MembershipItem(m, m.Membership.Member));
        foreach (var g in draft.GrantsOf(p).OrderBy(g => g.Grant.On.Kind).ThenBy(g => g.Grant.On.Display, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Grant.Permission))
            Grants.Add(new GrantItem(g));

        var direct = draft.RolesOf(p).Select(m => m.Membership.Role).ToHashSet(StringComparer.Ordinal);
        var inherited = draft.Catalog.AllRolesOf(p).Where(r => !direct.Contains(r)).ToList();
        InheritedRoles = inherited.Count > 0 ? "Also through those roles: " + string.Join(", ", inherited) : "";
        RoleChoices = draft.RolesAvailableTo(p);
        MemberChoices = p.IsRole ? draft.MembersAvailableFor(p) : [];
        RoleToAdd = "";
        MemberToAdd = "";
    }

    // ----- Edits -----

    private void AddChange(SecurityChange change)
    {
        if (_draft is not { } draft) return;
        if (draft.Add(change) is { } refusal)
        {
            Status = refusal;
            return;
        }
        Status = change.Summary + " is pending. Apply runs the changes together.";
        RebuildPrincipals();
        RebuildDetails();
        NotifyPending();
    }

    [RelayCommand]
    private async Task NewLoginAsync()
    {
        if (_draft is null) return;
        var sqlServer = IsSqlServer;
        var answer = await dialogs.PromptLoginAsync(
            sqlServer ? "New login" : "New role that can log in",
            sqlServer
                ? "A SQL Server login with a password. Windows and Entra logins are created by their name, from a query tab."
                : "A role WITH LOGIN and a password. The password is sent as a SCRAM-SHA-256 verifier, never in plain text.",
            name: null,
            createUserLabel: sqlServer && !string.IsNullOrEmpty(SelectedDatabase) ? $"Also create a user in {SelectedDatabase}" : null);
        if (answer is null) return;
        AddChange(new CreateLoginChange(answer.Name, new Secret(answer.Password), answer.CreateUser,
            sqlServer && !string.IsNullOrEmpty(SelectedDatabase) ? SelectedDatabase : null));
        SelectByName(answer.Name, answer.CreateUser ? SecurityScope.Database : SecurityScope.Server);
    }

    [RelayCommand]
    private async Task NewRoleAsync()
    {
        if (_draft is null) return;
        var name = await dialogs.PromptTextAsync("New role",
            IsSqlServer ? $"A database role in {SelectedDatabase}, to group permissions and users." : "A role without LOGIN, to group privileges and other roles.",
            "Name");
        if (string.IsNullOrWhiteSpace(name)) return;
        AddChange(new CreateRoleChange(name.Trim(), IsSqlServer ? SecurityScope.Database : SecurityScope.Server));
        SelectByName(name.Trim(), IsSqlServer ? SecurityScope.Database : SecurityScope.Server);
    }

    [RelayCommand]
    private void CreateUserForLogin()
    {
        if (SelectedPrincipal?.Principal is not { Kind: PrincipalKind.Login } login) return;
        AddChange(new CreateUserChange(login.Name, login.Name));
    }

    [RelayCommand]
    private async Task SetPasswordAsync()
    {
        if (SelectedPrincipal?.Principal is not { } p) return;
        var answer = await dialogs.PromptLoginAsync("Change password", $"New password for {p.Name}.", p.Name);
        if (answer is null) return;
        AddChange(new SetPasswordChange(p, new Secret(answer.Password)));
    }

    [RelayCommand]
    private void ToggleLogin()
    {
        if (SelectedPrincipal?.Principal is not { } p || _draft is null) return;
        AddChange(new SetLoginEnabledChange(p, !_draft.IsEnabled(p)));
    }

    [RelayCommand]
    private void Drop()
    {
        if (SelectedPrincipal?.Principal is not { } p) return;
        AddChange(new DropPrincipalChange(p));
    }

    [RelayCommand]
    private void AddToRole()
    {
        if (SelectedPrincipal?.Principal is not { } p || string.IsNullOrWhiteSpace(RoleToAdd)) return;
        AddChange(new MembershipChange(p.Name, RoleToAdd.Trim(), p.Scope, Add: true));
    }

    [RelayCommand]
    private void AddMember()
    {
        if (SelectedPrincipal?.Principal is not { } p || string.IsNullOrWhiteSpace(MemberToAdd)) return;
        AddChange(new MembershipChange(MemberToAdd.Trim(), p.Name, p.Scope, Add: true));
    }

    [RelayCommand]
    private void RemoveMembership(MembershipItem? item)
    {
        if (item is null) return;
        var m = item.View.Membership;
        AddChange(new MembershipChange(m.Member, m.Role, m.Scope, Add: item.View.Pending == PendingState.Removing));
    }

    [RelayCommand]
    private void Grant() => AddPermission(WithGrantOption ? PermissionAction.GrantWithGrantOption : PermissionAction.Grant);

    [RelayCommand]
    private void Deny() => AddPermission(PermissionAction.Deny);

    private void AddPermission(PermissionAction action)
    {
        if (SelectedPrincipal?.Principal is not { } p) return;
        if (!_targets.TryGetValue(GrantTarget.Trim(), out var target))
        {
            Status = "Pick what to grant on from the list: DATABASE, a schema or an object.";
            return;
        }
        AddChange(new PermissionChange(p.Name, GrantPermission.Trim().ToUpperInvariant(), target, action));
    }

    [RelayCommand]
    private void Revoke(GrantItem? item)
    {
        if (item is null) return;
        var g = item.View.Grant;
        // A pending revoke is undone by granting back the same way; a pending grant is cancelled by revoking it.
        AddChange(item.View.Pending == PendingState.Removing
            ? new PermissionChange(g.Grantee, g.Permission, g.On, g.State switch
            {
                GrantState.Deny => PermissionAction.Deny,
                GrantState.GrantWithGrantOption => PermissionAction.GrantWithGrantOption,
                _ => PermissionAction.Grant
            })
            : PermissionChange.RevokeOf(g));
    }

    [RelayCommand]
    private void RemovePending(PendingItem? item)
    {
        if (item is null || _draft is null) return;
        _draft.Remove(item.Change);
        RebuildPrincipals();
        RebuildDetails();
        NotifyPending();
    }

    [RelayCommand]
    private void DiscardAll()
    {
        if (_draft is null) return;
        _draft.Clear();
        RebuildPrincipals();
        RebuildDetails();
        NotifyPending();
        Status = "Pending changes discarded.";
    }

    [RelayCommand]
    private async Task CopyScriptAsync()
    {
        // The display form: passwords stay masked on the clipboard too.
        if (Script.Length > 0) await dialogs.CopyTextAsync(Script);
    }

    [RelayCommand]
    private void OpenInQuery()
    {
        if (_draft is null || Script.Length == 0) return;
        OpenSqlRequested?.Invoke(Script, SelectedDatabase);
        if (_draft.Script(SelectedDatabase).HasSecrets)
            Status = "Opened with passwords masked: type them in the query tab before running it, or use Apply here.";
    }

    /// <summary>Shows the script (passwords masked), asks (typing PRODUCTION on a production connection), then runs it
    /// in one transaction and reads the catalog again.</summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsBusy || _session is not { } session || _draft is not { } draft || draft.Changes.Count == 0) return;
        // Exactly these edits run; anything added meanwhile stays pending.
        var applied = draft.Changes.ToList();
        var database = SelectedDatabase;
        SecurityScript script;
        try
        {
            script = draft.Script(database);
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
            return;
        }
        if (session.Profile.ReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(session.Profile, "The security script") + " Copy script still hands it over.";
            return;
        }

        var production = session.Profile.IsProduction;
        var where = string.IsNullOrEmpty(database) ? session.Profile.DisplayName : $"{session.Profile.DisplayName} / {database}";
        var ok = await dialogs.ConfirmAsync(
            $"Apply {applied.Count} security change(s) on {where}? The script below runs in one transaction; if any statement fails, nothing changes.",
            "Apply", production ? "PRODUCTION" : null,
            production ? $"PRODUCTION · {session.Profile.DisplayName}" : null, script.Display);
        if (!ok) return;

        IsBusy = true;
        Status = "Applying security changes…";
        try
        {
            await SecurityScriptRunner.RunAsync(session.Provider, database, script, TimeoutSeconds);
        }
        catch (Exception ex)
        {
            IsBusy = false;
            // Server messages name the statement's objects, never its password literal.
            Status = "The server refused the changes, nothing was changed: " + ex.Message;
            return;
        }

        if (ReferenceEquals(_draft, draft)) draft.RemoveAll(applied);
        NotifyPending();
        await LoadAsync();
        Status = $"Applied {applied.Count} change(s). " + Status;
    }

    private void NotifyPending()
    {
        Pending.Clear();
        SecurityScript script;
        try
        {
            script = _draft?.Script(SelectedDatabase) ?? SecurityScript.Empty;
        }
        catch (ArgumentException ex)
        {
            script = SecurityScript.Empty with { Display = "-- " + ex.Message };
        }
        foreach (var change in _draft?.Changes ?? []) Pending.Add(new PendingItem(change));
        Script = script.Display;
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ApplyLabel));
    }

    private void SelectByName(string name, SecurityScope scope)
    {
        SelectedPrincipal = Principals.FirstOrDefault(p => p.Principal.Scope == scope && Same(p.Principal.Name, name)) ??
                            Principals.FirstOrDefault(p => Same(p.Principal.Name, name)) ?? SelectedPrincipal;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Merge(params IEnumerable<string?>[] lists) =>
        lists.SelectMany(l => l).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed class PrincipalItem(SecurityPrincipal principal, bool pendingDrop, bool disabled)
{
    public SecurityPrincipal Principal { get; } = principal;
    public string Name => Principal.Name;
    public bool IsNew => Principal.IsNew;
    public bool IsPendingDrop { get; } = pendingDrop;
    public bool IsLoginKind => Principal.Kind == PrincipalKind.Login;
    public bool IsUserKind => Principal.Kind == PrincipalKind.User;
    public bool IsRoleKind => Principal.Kind == PrincipalKind.Role;
    public bool IsServerRoleKind => Principal.Kind == PrincipalKind.ServerRole;

    /// <summary>L login, U user, R role, S server role: a short badge in front of the name.</summary>
    public string Badge => Principal.Kind switch
    {
        PrincipalKind.Login => "L",
        PrincipalKind.User => "U",
        PrincipalKind.ServerRole => "S",
        _ => "R"
    };

    public string Detail => Principal.TypeDescription +
                            (Principal.LoginName is { } l && !string.Equals(l, Principal.Name, StringComparison.Ordinal) ? " · login " + l : "") +
                            (Principal.IsSystem ? " · built in" : "") +
                            (disabled || Principal.IsDisabled ? " · disabled" : "") +
                            (IsNew ? " · new" : IsPendingDrop ? " · will be dropped" : "");
}

public sealed class MembershipItem(MembershipView view, string name)
{
    public MembershipView View { get; } = view;
    public string Name { get; } = name;
    public bool IsAdding => View.Pending == PendingState.Adding;
    public bool IsRemoving => View.Pending == PendingState.Removing;
    public string ActionTip => IsRemoving ? "Keep (undo the removal)" : IsAdding ? "Cancel adding" : "Remove";
    public string ActionGlyph => IsRemoving ? "↶" : "✕";
    public string Note => IsAdding ? "adding" : IsRemoving ? "removing" : View.Membership.AdminOption ? "with admin option" : "";
}

public sealed class GrantItem(GrantView view)
{
    public GrantView View { get; } = view;
    public string Permission => View.Grant.Permission;
    public string On => View.Grant.On.Display;
    public string Kind => View.Grant.On.Kind.ToString();
    public string State => View.Grant.StateText;
    public bool IsDeny => View.Grant.State == GrantState.Deny;
    public bool IsAdding => View.Pending == PendingState.Adding;
    public bool IsRemoving => View.Pending == PendingState.Removing;
    public string ActionTip => IsRemoving ? "Keep (undo the revoke)" : IsAdding ? "Cancel" : "Revoke";
    public string ActionGlyph => IsRemoving ? "↶" : "✕";

    /// <summary>The grantor, or what is about to happen to the permission.</summary>
    public string Note => IsAdding ? "adding" : IsRemoving ? "revoking" : View.Grant.Grantor ?? "";
}

public sealed class PendingItem(SecurityChange change)
{
    public SecurityChange Change { get; } = change;
    public string Summary => Change.Summary;
}
