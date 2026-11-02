using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Sessions;
using DbExplorer.Application.Workspace;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ConnectionStore _store;
    private readonly SessionService _sessions;
    private readonly ProviderRegistry _registry;
    private readonly IDialogService _dialogs;
    private readonly AppSettingsService _settings;
    private readonly ISessionAware[] _tabs;
    private readonly WorkspaceStore _workspace;

    public MainWindowViewModel(
        ConnectionStore store,
        SessionService sessions,
        ProviderRegistry registry,
        IDialogService dialogs,
        AppSettingsService settings,
        ObjectsViewModel objects,
        MetadataSearchViewModel search,
        DataSearchViewModel dataSearch,
        IndexesViewModel indexes,
        LocksViewModel locks,
        ActivityViewModel activity,
        DiagramViewModel diagram,
        TableDesignerViewModel tableDesigner,
        ErModelViewModel erModel,
        QueryBuilderViewModel queryBuilder,
        QueryWorkspaceViewModel query,
        ComparerViewModel comparer,
        LabViewModel lab,
        SecurityViewModel security,
        NavigatorViewModel navigator,
        WorkspaceStore workspace)
    {
        _workspace = workspace;
        SavedWorkspace = workspace.Load();
        if (Enum.TryParse<AppTab>(SavedWorkspace.MainTab, out var savedTab) && Enum.IsDefined(savedTab)) _selectedTab = savedTab;
        _store = store;
        _sessions = sessions;
        _registry = registry;
        _dialogs = dialogs;
        _settings = settings;
        _selectedTheme = settings.Theme;
        Objects = objects;
        Search = search;
        DataSearch = dataSearch;
        Indexes = indexes;
        Locks = locks;
        Activity = activity;
        Diagram = diagram;
        TableDesigner = tableDesigner;
        ErModel = erModel;
        QueryBuilder = queryBuilder;
        Query = query;
        Query.ConnectCommand = ConnectCommand;
        Comparer = comparer;
        Comparer.Profiles = Profiles;
        Profiles.CollectionChanged += (_, _) => ExportConnectionsCommand.NotifyCanExecuteChanged();
        Lab = lab;
        Security = security;
        Navigator = navigator;
        _showNavigator = settings.ShowNavigator;
        _tabs = [navigator, objects, search, dataSearch, indexes, locks, activity, diagram, tableDesigner, erModel, queryBuilder, query, comparer, lab, security];

        objects.ShowInDiagramRequested += table =>
        {
            SelectedTab = AppTab.Diagram;
            diagram.ShowTable(table);
        };
        diagram.OpenObjectRequested += table =>
        {
            SelectedTab = AppTab.Objects;
            objects.Reveal(table);
        };
        dataSearch.ShowTableRequested += table =>
        {
            SelectedTab = AppTab.Objects;
            objects.Reveal(table);
        };
        diagram.OpenSqlRequested += (sql, database) =>
        {
            SelectedTab = AppTab.Query;
            query.OpenInNewTab(sql, database: database);
        };
        queryBuilder.OpenSqlRequested += (sql, database) =>
        {
            SelectedTab = AppTab.Query;
            query.OpenInNewTab(sql, database: database);
        };
        tableDesigner.OpenSqlRequested += (sql, database) =>
        {
            SelectedTab = AppTab.Query;
            query.OpenInNewTab(sql, database: database);
        };
        erModel.OpenSqlRequested += (sql, database) =>
        {
            SelectedTab = AppTab.Query;
            query.OpenInNewTab(sql, database: database);
        };
        objects.OpenSqlRequested += (sql, database, title) =>
        {
            SelectedTab = AppTab.Query;
            query.OpenInNewTab(sql, title, database);
        };
        objects.OpenSqlAtRequested += (sql, database, title, offset) =>
        {
            SelectedTab = AppTab.Query;
            query.OpenInNewTab(sql, title, database, caretOffset: offset);
        };
        objects.DesignTableRequested += table =>
        {
            SelectedTab = AppTab.TableDesigner;
            _ = tableDesigner.OpenTableAsync(table);
        };
        tableDesigner.OpenObjectRequested += table =>
        {
            SelectedTab = AppTab.Objects;
            objects.Reveal(table);
        };
        security.OpenSqlRequested += (sql, database) =>
        {
            SelectedTab = AppTab.Query;
            query.OpenInNewTab(sql, database: database);
        };
        dataSearch.OpenSqlRequested += (sql, database) =>
        {
            SelectedTab = AppTab.Query;
            query.OpenInNewTab(sql, database: database);
        };

        navigator.Profiles = Profiles;
        navigator.OpenSqlRequested += (sql, database, title) =>
        {
            SelectedTab = AppTab.Query;
            // No database means the connection's default here, never the database of whichever tab is selected.
            query.OpenInNewTab(sql, title, database, connectionDefault: true);
        };
        navigator.HideRequested += () => ShowNavigator = false;
        navigator.ObjectActionRequested += OnNavigatorObjectAction;
        navigator.ConnectionActionRequested += (action, profile) => _ = OnNavigatorConnectionActionAsync(action, profile);
    }

    // ----- Database navigator (left of the tabs) -----

    public NavigatorViewModel Navigator { get; }

    [ObservableProperty] private bool _showNavigator;

    partial void OnShowNavigatorChanged(bool value) => _settings.SetShowNavigator(value);

    /// <summary>The navigator's width as the user last dragged it (saved with the settings).</summary>
    public double NavigatorWidth
    {
        get => _settings.NavigatorWidth;
        set => _settings.SetNavigatorWidth(value);
    }

    /// <summary>The app bar's sidebar button and Ctrl+B.</summary>
    [RelayCommand]
    private void ToggleNavigator() => ShowNavigator = !ShowNavigator;

    /// <summary>Runs a navigator action through the Objects tab, which already knows how to open, modify or drop
    /// the object; actions whose outcome is shown in the Objects tab switch to it.</summary>
    private void OnNavigatorObjectAction(NavigatorObjectAction action, DbObject obj)
    {
        Objects.Reveal(obj);
        if (Objects.SelectedObject is null)
        {
            StatusText = $"{obj.FullName} is not in the catalog any more. Refresh metadata to update the navigator.";
            return;
        }

        System.Windows.Input.ICommand command;
        switch (action)
        {
            case NavigatorObjectAction.Open:
                SelectedTab = AppTab.Objects;
                return;
            case NavigatorObjectAction.GetData:
                command = Objects.GetDataCommand;
                break;
            case NavigatorObjectAction.Execute:
                command = Objects.ExecuteSelectedCommand;
                break;
            case NavigatorObjectAction.Debug:
                command = Objects.DebugSelectedCommand;
                break;
            case NavigatorObjectAction.ShowInDiagram:
                command = Objects.ShowInDiagramCommand;
                break;
            case NavigatorObjectAction.Modify:
                command = Objects.ModifyCommand;
                break;
            case NavigatorObjectAction.DropTable:
                SelectedTab = AppTab.Objects; // the confirmation's outcome is reported there
                command = Objects.DropTableCommand;
                break;
            case NavigatorObjectAction.FindUsages:
                command = Objects.FindUsagesCommand;
                break;
            case NavigatorObjectAction.GenerateTestData:
                command = Objects.GenerateTestDataCommand;
                break;
            default:
                return;
        }
        if (command.CanExecute(null)) command.Execute(null);
    }

    private async Task OnNavigatorConnectionActionAsync(NavigatorConnectionAction action, ConnectionProfile profile)
    {
        switch (action)
        {
            case NavigatorConnectionAction.Connect when !IsConnected && !IsBusy:
                SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? SelectedProfile;
                if (ConnectCommand.CanExecute(null)) await ConnectCommand.ExecuteAsync(null);
                break;
            case NavigatorConnectionAction.Switch when IsConnected && !IsBusy && Session?.Profile.Id != profile.Id:
                if (!await ConfirmLeaveAsync(LeaveReason.Switch)) return;
                await DisconnectCoreAsync();
                if (IsConnected) return;
                SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? SelectedProfile;
                if (ConnectCommand.CanExecute(null)) await ConnectCommand.ExecuteAsync(null);
                break;
            case NavigatorConnectionAction.Disconnect:
                if (DisconnectCommand.CanExecute(null)) await DisconnectCommand.ExecuteAsync(null);
                break;
            case NavigatorConnectionAction.Edit when !IsBusy:
                await EditProfileAsync(profile);
                break;
            case NavigatorConnectionAction.Duplicate when !IsBusy:
                await DuplicateProfileAsync(profile);
                break;
            case NavigatorConnectionAction.Delete when !IsBusy:
                await DeleteProfileAsync(profile);
                break;
            case NavigatorConnectionAction.RefreshMetadata:
                if (RefreshMetadataCommand.CanExecute(null)) await RefreshMetadataCommand.ExecuteAsync(null);
                break;
        }
    }

    public IReadOnlyList<AppThemeMode> ThemeModes { get; } = Enum.GetValues<AppThemeMode>();

    [ObservableProperty] private AppThemeMode _selectedTheme;

    partial void OnSelectedThemeChanged(AppThemeMode value) => _settings.SetTheme(value);

    /// <summary>The toolbar's theme menu.</summary>
    [RelayCommand]
    private void SetTheme(AppThemeMode mode) => SelectedTheme = mode;

    /// <summary>Draw with the CPU instead of the graphics card from the next start (theme menu).</summary>
    public bool SoftwareRendering
    {
        get => _settings.SoftwareRendering;
        set
        {
            if (value == _settings.SoftwareRendering) return;
            _settings.SetSoftwareRendering(value);
            OnPropertyChanged();
            StatusText = value
                ? "Software rendering is on from the next start. Restart DbExplorer to use it."
                : "Graphics card rendering is back on from the next start. Restart DbExplorer to use it.";
        }
    }

    public ObjectsViewModel Objects { get; }
    public MetadataSearchViewModel Search { get; }
    public DataSearchViewModel DataSearch { get; }
    public IndexesViewModel Indexes { get; }
    public LocksViewModel Locks { get; }
    public ActivityViewModel Activity { get; }
    public DiagramViewModel Diagram { get; }
    public TableDesignerViewModel TableDesigner { get; }
    public ErModelViewModel ErModel { get; }
    public QueryBuilderViewModel QueryBuilder { get; }

    /// <summary>Order matches the TabItems in MainWindow.axaml.</summary>
    [ObservableProperty] private AppTab _selectedTab = AppTab.Objects;
    public QueryWorkspaceViewModel Query { get; }
    public ComparerViewModel Comparer { get; }
    public LabViewModel Lab { get; }
    public SecurityViewModel Security { get; }

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    [ObservableProperty] private DatabaseSession? _session;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Not connected";

    public bool IsConnected => Session is not null;

    public ConnectionEnvironment ConnectedEnvironment => Session?.Profile.Environment ?? ConnectionEnvironment.None;

    public bool ShowEnvironmentBanner => ConnectedEnvironment != ConnectionEnvironment.None;

    public string EnvironmentBanner => Session is not { } s ? "" : s.Profile.Environment switch
    {
        ConnectionEnvironment.Production => $"PRODUCTION · {s.Profile.DisplayName} · changes here affect live data",
        ConnectionEnvironment.Staging => $"STAGING · {s.Profile.DisplayName}",
        ConnectionEnvironment.Test => $"TEST · {s.Profile.DisplayName}",
        ConnectionEnvironment.Development => $"DEVELOPMENT · {s.Profile.DisplayName}",
        _ => ""
    };

    /// <summary>"1.2.0" (from -p:Version), without the "+commit" suffix the SDK appends.</summary>
    public static string AppVersion { get; } =
        (typeof(MainWindowViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "").Split('+')[0];

    public string WindowTitle => Session is { } s
        ? $"DB Explorer {AppVersion} — {s.Profile}"
        : $"DB Explorer {AppVersion}";

    public async Task InitializeAsync()
    {
        StartupConnectionAction action;
        string? note;
        try
        {
            await Query.RestoreTabsAsync();
            if (SavedWorkspace.QueryTabIndex is { } index && index >= 0 && index < Query.Documents.Count)
                Query.SelectedDocument = Query.Documents[index];
            foreach (var p in ConnectionFolders.Order(await _store.LoadAsync())) Profiles.Add(p);
            (action, var saved, note) = StartupConnection.Decide(SavedWorkspace, Profiles, _settings.ReconnectOnStart);
            SelectedProfile = saved ?? Profiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ShowError("Could not load saved connections: " + ex.Message);
            ShowQueryWhileOffline();
            return;
        }

        if (note is not null) StatusText = note;
        if (action == StartupConnectionAction.Connect && ConnectCommand.CanExecute(null)) await ConnectCommand.ExecuteAsync(null);
        // Nothing connected (not asked to, or the connect failed): open on the Query tab rather than a disabled one.
        ShowQueryWhileOffline();
    }

    // ----- Offline: the Query tab stands in for the tabs that need a connection -----

    /// <summary>The tab left for the Query tab when the connection went away, and the scripts at that moment.</summary>
    private AppTab? _tabBeforeOffline;
    private string _scriptsWhenOffline = "";

    private string QueryFingerprint() => OfflineTabs.Fingerprint(Query.Documents.Select(d => d.Sql));

    private void ShowQueryWhileOffline()
    {
        if (IsConnected || OfflineTabs.TabWhenOffline(SelectedTab) is not { } tab) return;
        _tabBeforeOffline = SelectedTab;
        _scriptsWhenOffline = QueryFingerprint();
        SelectedTab = tab;
    }

    private void ReturnFromOffline()
    {
        if (OfflineTabs.TabOnConnect(_tabBeforeOffline, SelectedTab, QueryFingerprint() != _scriptsWhenOffline) is { } tab) SelectedTab = tab;
        _tabBeforeOffline = null;
    }

    /// <summary>The main tab saved for the next start: the one left for the Query tab while offline, unless the
    /// Query tab was used meanwhile.</summary>
    private AppTab TabToRemember() =>
        OfflineTabs.TabOnConnect(_tabBeforeOffline, SelectedTab, QueryFingerprint() != _scriptsWhenOffline) ?? SelectedTab;

    // ----- Workspace: window, main tab, active query tab and connection, restored at the next start -----

    /// <summary>What was saved when the app last closed (read once at start).</summary>
    public WorkspaceState SavedWorkspace { get; }

    /// <summary>The window's current normal bounds, kept up to date by the window and saved on close.</summary>
    public WindowPlacement? WindowPlacement { get; set; }

    /// <summary>Theme menu: open the last connection again at start.</summary>
    public bool ReconnectOnStart
    {
        get => _settings.ReconnectOnStart;
        set
        {
            if (value == _settings.ReconnectOnStart) return;
            _settings.SetReconnectOnStart(value);
            OnPropertyChanged();
        }
    }

    /// <summary>The state to write on close; read before the session is closed so it knows whether one was open.</summary>
    public WorkspaceState CaptureWorkspace() => new()
    {
        Window = WindowPlacement ?? SavedWorkspace.Window,
        MainTab = TabToRemember().ToString(),
        QueryTabIndex = Query.SelectedDocument is { } doc && Query.Documents.IndexOf(doc) is >= 0 and var i ? i : null,
        ConnectionId = Session?.Profile.Id ?? SelectedProfile?.Id,
        WasConnected = Session is not null
    };

    /// <summary>Every step runs even when an earlier one fails (e.g. the server went away), so tabs are saved and
    /// open transactions are rolled back as far as possible.</summary>
    public async Task ShutdownAsync()
    {
        await Step(() => { _workspace.Save(CaptureWorkspace()); return Task.CompletedTask; });
        await Step(Query.SaveTabsAsync);
        await Step(() => { ErModel.FlushAutosave(); return Task.CompletedTask; });
        await Step(Query.RollbackOpenTransactionsAsync);
        foreach (var tab in _tabs) await Step(() => { tab.Attach(null); return Task.CompletedTask; });
        await Step(() => Comparer.DisposeIndependentSessionsAsync().AsTask());
        if (Session is { } s) await Step(() => s.DisposeAsync().AsTask());

        static async Task Step(Func<Task> step)
        {
            try { await step(); }
            catch (Exception ex) { Services.ErrorLog.Write("shutdown", ex); }
        }
    }

    partial void OnSessionChanging(DatabaseSession? value)
    {
        if (Session is { } old) old.SnapshotChanged -= OnSnapshotChanged;
    }

    /// <summary>A background catalog refresh (e.g. after an app update changed the cache format) finished.</summary>
    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        if (sender is DatabaseSession s && ReferenceEquals(s, Session) && !IsBusy) StatusText = BuildStatus(s);
    }

    partial void OnSessionChanged(DatabaseSession? value)
    {
        if (value is not null) value.SnapshotChanged += OnSnapshotChanged;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectedEnvironment));
        OnPropertyChanged(nameof(ShowEnvironmentBanner));
        OnPropertyChanged(nameof(EnvironmentBanner));
        OnPropertyChanged(nameof(WindowTitle));
        foreach (var tab in _tabs) tab.Attach(value);
        RefreshCommands();
        if (value is null)
        {
            _ = Query.SetOfflineProfileAsync(SelectedProfile);
            ShowQueryWhileOffline();
        }
        else ReturnFromOffline();
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        RefreshCommands();
        // While offline the Query tab suggests the picked connection's keywords and saved names.
        if (!IsConnected) _ = Query.SetOfflineProfileAsync(value);
    }

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    // Saved connections can be managed while connected: only the open connection itself cannot be deleted (or picked
    // in the locked toolbar picker), and edits to it reach the open session as described in ConnectionEdits.
    private bool HasSelection => SelectedProfile is not null && !IsBusy;
    private bool CanConnect => SelectedProfile is not null && !IsConnected && !IsBusy;
    private bool CanUseSession => IsConnected && !IsBusy;
    private bool CanCreate => !IsBusy;
    private bool CanDelete => SelectedProfile is { } p && !IsBusy && !IsOpen(p);

    private bool IsOpen(ConnectionProfile profile) => Session?.Profile.Id == profile.Id;

    /// <summary>The toolbar's delete button explains why it is greyed out on the open connection.</summary>
    public string DeleteConnectionTip => SelectedProfile is { } p && IsOpen(p)
        ? "Delete connection (disconnect first: this connection is open)"
        : "Delete connection…";

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task NewConnectionAsync()
    {
        var draft = new ConnectionProfile { ProviderKey = _registry.All[0].Key };
        var profile = await _dialogs.EditConnectionAsync(draft, "New connection", ConnectionFolders.All(Profiles), isNew: true);
        if (profile is null) return;

        Profiles.Add(profile);
        SortProfiles(profile);
        await SaveProfilesAsync();
        if (IsConnected) StatusText = $"Saved {profile}. It is in the navigator; disconnect to connect to it.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EditConnectionAsync() => SelectedProfile is { } current ? EditProfileAsync(current) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DuplicateConnectionAsync() => SelectedProfile is { } current ? DuplicateProfileAsync(current) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteConnectionAsync() => SelectedProfile is { } current ? DeleteProfileAsync(current) : Task.CompletedTask;

    private async Task EditProfileAsync(ConnectionProfile current)
    {
        var edited = await _dialogs.EditConnectionAsync(current.Clone(), "Edit connection", ConnectionFolders.All(Profiles));
        if (edited is null) return;

        // A Team pull can rebuild the list while the dialog is open: find the connection again.
        var index = IndexOfProfile(current.Id);
        if (index < 0)
        {
            ShowError($"{current} is no longer in the saved connections; the edit was not saved.");
            return;
        }
        Profiles[index] = edited;
        SortProfiles(edited);
        await SaveProfilesAsync();

        if (Session is { } session && session.Profile.Id == edited.Id) await AfterOpenConnectionEditedAsync(session, edited);
    }

    /// <summary>
    /// The open connection was edited: labels, environment and read-only apply at once; server, sign-in, TLS and SSH
    /// changes need a reconnect, which is offered (open transactions would be rolled back, so never done silently).
    /// </summary>
    private async Task AfterOpenConnectionEditedAsync(DatabaseSession session, ConnectionProfile edited)
    {
        var reconnect = ConnectionEdits.NeedsReconnect(session.Profile, edited);
        if (ConnectionEdits.ApplyToOpenSession(session.Profile, edited)) RefreshSessionLabels(session);
        if (!reconnect)
        {
            StatusText = $"Saved {edited}";
            return;
        }

        var now = await _dialogs.ConfirmAsync(
            $"{edited.DisplayName} is open with its previous server, sign-in or security settings. Reconnect now to use the " +
            "new ones? Open transactions and uncommitted grid edits are rolled back; Query tabs stay open.",
            "Reconnect");
        if (!now || !ReferenceEquals(Session, session))
        {
            StatusText = $"Saved {edited}. The open connection keeps its previous settings until you reconnect.";
            return;
        }

        await DisconnectAsync();
        if (IsConnected) return;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == edited.Id) ?? SelectedProfile;
        if (ConnectCommand.CanExecute(null)) await ConnectCommand.ExecuteAsync(null);
    }

    /// <summary>The window title, banner and status after the open connection's name or environment changed.</summary>
    private void RefreshSessionLabels(DatabaseSession session)
    {
        OnPropertyChanged(nameof(ConnectedEnvironment));
        OnPropertyChanged(nameof(ShowEnvironmentBanner));
        OnPropertyChanged(nameof(EnvironmentBanner));
        OnPropertyChanged(nameof(WindowTitle));
        if (!IsBusy) StatusText = BuildStatus(session);
    }

    private async Task DuplicateProfileAsync(ConnectionProfile source)
    {
        var draft = ConnectionEdits.Duplicate(source, Profiles);
        var copy = await _dialogs.EditConnectionAsync(draft, "Duplicate connection", ConnectionFolders.All(Profiles));
        if (copy is null) return;

        Profiles.Add(copy);
        SortProfiles(copy);
        await SaveProfilesAsync();
        StatusText = $"Saved {copy}" + (IsConnected ? ". It is in the navigator; disconnect to connect to it." : "");
    }

    private async Task DeleteProfileAsync(ConnectionProfile profile)
    {
        if (IsOpen(profile))
        {
            StatusText = $"{profile.DisplayName} is open. Disconnect before deleting it.";
            return;
        }
        if (!await _dialogs.ConfirmAsync(
                $"Delete the saved connection {profile.QualifiedName}? Its saved password is removed too. " +
                "Export it first if you might need it again.", "Delete"))
            return;

        var index = IndexOfProfile(profile.Id);
        if (index < 0) return;
        Profiles.RemoveAt(index);
        if (_failedProfileId == profile.Id) ConnectionFailure = null;
        SelectedProfile = OpenProfile() ?? (SelectedProfile is { } still && Profiles.Contains(still) ? still : Profiles.FirstOrDefault());
        await SaveProfilesAsync();
        StatusText = $"Deleted {profile.QualifiedName}";
    }

    /// <summary>The open connection's entry in <see cref="Profiles"/>, or null when disconnected.</summary>
    private ConnectionProfile? OpenProfile() =>
        Session is { } s ? Profiles.FirstOrDefault(p => p.Id == s.Profile.Id) : null;

    private int IndexOfProfile(Guid id)
    {
        for (var i = 0; i < Profiles.Count; i++)
            if (Profiles[i].Id == id) return i;
        return -1;
    }

    /// <summary>
    /// Keeps the list grouped by folder, then by name, and selects <paramref name="select"/>; while connected the
    /// (locked) picker keeps showing the open connection.
    /// </summary>
    private void SortProfiles(ConnectionProfile select)
    {
        var ordered = ConnectionFolders.Order(Profiles);
        for (var i = 0; i < ordered.Count; i++)
        {
            var at = Profiles.IndexOf(ordered[i]);
            if (at != i) Profiles.Move(at, i);
        }
        SelectedProfile = OpenProfile() ?? select;
    }

    private bool CanExport => Profiles.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportConnectionsAsync()
    {
        var request = await _dialogs.PromptConnectionExportAsync(Profiles.ToList());
        if (request is null) return;

        try
        {
            var json = ConnectionTransfer.Export(request.Profiles, request.Password);
            var name = await _dialogs.SaveTextFileAsync(
                "Export connections", "connections", ConnectionTransfer.FileExtension, "DB Explorer connections", json);
            if (name is null) return;
            StatusText = $"Exported {request.Profiles.Count} connection(s) to {name}" +
                         (request.Password is null ? " without passwords" : " with encrypted passwords");
        }
        catch (Exception ex)
        {
            ShowError("Could not export connections: " + ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task ImportConnectionsAsync()
    {
        try
        {
            var opened = await _dialogs.OpenTextFileAsync("Import connections", ConnectionTransfer.FileExtension, "DB Explorer connections");
            if (opened is not { } picked) return;

            var file = ConnectionTransfer.Read(picked.Content);
            IReadOnlyList<ConnectionProfile>? incoming = null;
            if (!file.HasPasswords) incoming = ConnectionTransfer.Profiles(file);

            string? error = null;
            while (incoming is null)
            {
                var answer = await _dialogs.PromptImportPasswordAsync(picked.Name, error);
                if (answer is null) return;
                try
                {
                    incoming = ConnectionTransfer.Profiles(file, answer.Password);
                }
                catch (WrongExportPasswordException ex)
                {
                    error = ex.Message;
                }
            }

            if (incoming.Count == 0)
            {
                StatusText = $"{picked.Name} has no connections";
                return;
            }

            var choice = ImportConflictChoice.KeepBoth;
            var conflicts = ConnectionTransfer.Conflicts(Profiles, incoming);
            if (conflicts.Count > 0)
            {
                if (await _dialogs.PromptImportConflictAsync(conflicts) is not { } conflictChoice) return;
                choice = conflictChoice;
            }

            var selectedId = SelectedProfile?.Id;
            var result = ConnectionTransfer.Merge(Profiles, incoming, choice);
            Profiles.Clear();
            foreach (var profile in ConnectionFolders.Order(result.Profiles)) Profiles.Add(profile);
            SelectedProfile = OpenProfile() ?? Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
            await SaveProfilesAsync();

            var parts = new List<string> { $"added {result.Added}" };
            if (result.Replaced > 0) parts.Add($"overwrote {result.Replaced}");
            if (result.Skipped > 0) parts.Add($"skipped {result.Skipped}");
            var note = "";
            // Overwriting the open connection: apply what applies at once, and say when the rest waits for a reconnect.
            if (Session is { } session && OpenProfile() is { } imported && !ReferenceEquals(imported, session.Profile))
            {
                if (ConnectionEdits.ApplyToOpenSession(session.Profile, imported)) RefreshSessionLabels(session);
                if (ConnectionEdits.NeedsReconnect(session.Profile, imported))
                    note = $" · {imported.DisplayName} keeps its previous settings until you reconnect";
            }
            StatusText = $"Imported connections from {picked.Name}: {string.Join(", ", parts)}{note}";
        }
        catch (Exception ex)
        {
            ShowError("Could not import connections: " + ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedProfile is not { } profile) return;

        if ((!profile.IntegratedSecurity && string.IsNullOrEmpty(profile.Password)) || profile.Ssh.NeedsPassword)
        {
            // Password (database or SSH) was not saved: ask for it.
            var withPassword = await _dialogs.EditConnectionAsync(profile.Clone(), "Enter password");
            if (withPassword is null) return;
            // A Team pull can rebuild the list while the dialog is open: find the connection again.
            var index = IndexOfProfile(profile.Id);
            profile = withPassword;
            if (index >= 0)
            {
                Profiles[index] = withPassword;
                SelectedProfile = withPassword;
                await SaveProfilesAsync();
            }
        }

        var ct = BeginLoad();
        ShowProgress($"Connecting to {profile}…");
        try
        {
            var session = await _sessions.ConnectAsync(profile, ct, LoadProgress(ct));
            if (ct.IsCancellationRequested)
            {
                await session.DisposeAsync();
                StatusText = "Connect cancelled";
                return;
            }
            Session = session;
            ConnectionFailure = null;
            StatusText = BuildStatus(Session) + (Session.Snapshot.IsStale ? " · updating metadata in the background…" : "");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            StatusText = "Connect cancelled";
        }
        catch (Exception ex)
        {
            ReportConnectionFailure(ex, profile);
        }
        finally
        {
            EndLoad();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private async Task DisconnectAsync()
    {
        if (!await ConfirmLeaveAsync(LeaveReason.Disconnect)) return;
        await DisconnectCoreAsync();
    }

    private async Task DisconnectCoreAsync()
    {
        if (Session is not { } s) return;
        Session = null;
        await s.DisposeAsync();
        StatusText = "Disconnected";
    }

    // ----- Before the connection goes away: say what is rolled back or discarded, and let the user stay -----

    /// <summary>What is lost if the connection goes away now for <paramref name="reason"/>; empty when nothing is.</summary>
    public IReadOnlyList<string> DescribeUnsavedWork(LeaveReason reason)
    {
        if (Session is null) return [];
        var state = new UnsavedWorkState
        {
            QueryTabs = Query.Documents
                .Select(d => new QueryTabWork(d.Title, d.HasOpenTransaction, ResultEditing.HasEdits(d.ResultSets), d.IsRunning))
                .ToList(),
            SecurityPending = Security.Pending.Count,
            TableDesign = TableDesigner.UnsavedDesign,
            QueryBuilderTables = QueryBuilder.Tables.Count,
            DataWindows = _dialogs.DataWindowsWithUncommittedEdits()
        };
        return UnsavedWork.Describe(state, reason);
    }

    /// <summary>True when nothing would be lost or the user chose to go ahead anyway; asks at most once per quit.</summary>
    public async Task<bool> ConfirmLeaveAsync(LeaveReason reason)
    {
        if (reason == LeaveReason.Quit && QuitConfirmed) return true;
        var lines = DescribeUnsavedWork(reason);
        if (lines.Count > 0 && Session is { } s &&
            !await _dialogs.ConfirmAsync(UnsavedWork.Message(reason, s.Profile.DisplayName, lines), UnsavedWork.ConfirmText(reason)))
            return false;
        if (reason == LeaveReason.Quit) QuitConfirmed = true;
        return true;
    }

    /// <summary>The user already agreed to close (or there was nothing to lose): later close requests do not ask again.</summary>
    public bool QuitConfirmed { get; private set; }

    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private async Task RefreshMetadataAsync()
    {
        if (Session is not { } s) return;

        var ct = BeginLoad();
        ShowProgress("Reading catalog…");
        try
        {
            await _sessions.RefreshMetadataAsync(s, ct, LoadProgress(ct));
            StatusText = BuildStatus(s);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            StatusText = BuildStatus(s) + " · refresh cancelled, keeping the previous catalog";
        }
        catch (Exception ex)
        {
            ShowError("Refresh failed: " + ex.Message);
        }
        finally
        {
            EndLoad();
        }
    }

    // ----- Connect / refresh run on the thread pool; the window stays live and the user can cancel -----

    private CancellationTokenSource? _loadCts;

    private CancellationToken BeginLoad()
    {
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        IsBusy = true;
        return _loadCts.Token;
    }

    private void EndLoad()
    {
        IsBusy = false;
        CancelLoadCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Catalog progress lands on the UI thread (Progress captures it) until the load is cancelled or done.</summary>
    private IProgress<string> LoadProgress(CancellationToken ct) =>
        new Progress<string>(text =>
        {
            // Reports are posted, so one can arrive after its load ended or a newer one began: drop those.
            if (IsBusy && _loadCts?.Token == ct && !ct.IsCancellationRequested) ShowProgress(text);
        });

    private bool CanCancelLoad => IsBusy && _loadCts is { IsCancellationRequested: false };

    [RelayCommand(CanExecute = nameof(CanCancelLoad))]
    private void CancelLoad()
    {
        if (_loadCts is not { IsCancellationRequested: false } cts) return;
        cts.Cancel();
        ShowProgress("Cancelling…");
        CancelLoadCommand.NotifyCanExecuteChanged();
    }

    private void OpenObject(DbObject obj, PaletteModifier modifier)
    {
        if (modifier == PaletteModifier.Shift && obj.Type is DbObjectType.Table or DbObjectType.ForeignTable)
        {
            SelectedTab = AppTab.Diagram;
            Diagram.ShowTable(obj);
            return;
        }

        SelectedTab = AppTab.Objects;
        Objects.Reveal(obj);
        if (ShowNavigator) _ = Navigator.RevealAsync(obj);
        if (modifier != PaletteModifier.Control) return;
        if (obj.IsTableLike && Objects.GetDataCommand.CanExecute(null)) Objects.GetDataCommand.Execute(null);
        else if (obj.IsRoutine && Objects.ExecuteSelectedCommand.CanExecute(null)) Objects.ExecuteSelectedCommand.Execute(null);
    }

    private static string BuildStatus(DatabaseSession s) =>
        $"{s.Profile}{(s.Tunnel is { } t ? $" via SSH {t.Server}" : "")} · {s.ServerVersion} · {s.Snapshot.Objects.Count:N0} objects, " +
        $"{s.Snapshot.Columns.Count:N0} columns · metadata from {s.Snapshot.RefreshedAt:g}";

    private async Task SaveProfilesAsync()
    {
        try
        {
            await _store.SaveAsync(Profiles);
        }
        catch (Exception ex)
        {
            ShowError("Could not save connections: " + ex.Message);
        }
    }

    private void RefreshCommands()
    {
        NewConnectionCommand.NotifyCanExecuteChanged();
        EditConnectionCommand.NotifyCanExecuteChanged();
        DuplicateConnectionCommand.NotifyCanExecuteChanged();
        DeleteConnectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DeleteConnectionTip));
        ExportConnectionsCommand.NotifyCanExecuteChanged();
        ImportConnectionsCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        RefreshMetadataCommand.NotifyCanExecuteChanged();
        CancelLoadCommand.NotifyCanExecuteChanged();
    }
}

public enum AppTab
{
    Objects,
    Query,
    QueryBuilder,
    TableDesigner,
    Comparer,
    Diagram,
    SearchNamesAndCode,
    SearchData,
    ErModel,
    Indexes,
    Locks,
    Activity,
    Lab,
    Security
}
