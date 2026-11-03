using DbExplorer.Application.Assistant;
using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Query;
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
    private readonly AssistantSettings _assistantSettings;
    private readonly ISessionAware[] _tabs;
    private readonly WorkspaceStore _workspace;
    private readonly OpenConnections _open;

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
        JobsViewModel jobs,
        NavigatorViewModel navigator,
        WorkspaceStore workspace,
        AssistantSettings assistantSettings,
        OpenConnections open)
    {
        _open = open;
        _open.Changed += (_, _) => OnOpenConnectionsChanged();
        _assistantSettings = assistantSettings;
        _workspace = workspace;
        SavedWorkspace = workspace.Load();
        if (Enum.TryParse<AppTab>(SavedWorkspace.MainTab, out var savedTab) && Enum.IsDefined(savedTab)) _selectedTab = savedTab;
        _store = store;
        _sessions = sessions;
        _registry = registry;
        _dialogs = dialogs;
        _settings = settings;
        _selectedTheme = settings.Theme;
        // The Preferences window changes the theme too; the toolbar icon follows.
        settings.ThemeChanged += theme => SelectedTheme = theme;
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
        Query.Profiles = Profiles;
        Query.ConnectProfileRequested += profile => ConnectProfileAsync(profile);
        Comparer = comparer;
        Comparer.Profiles = Profiles;
        Profiles.CollectionChanged += (_, _) => ExportConnectionsCommand.NotifyCanExecuteChanged();
        Lab = lab;
        Security = security;
        Jobs = jobs;
        Navigator = navigator;
        _showNavigator = settings.ShowNavigator;
        _tabs = [navigator, objects, search, dataSearch, indexes, locks, activity, diagram, tableDesigner, erModel, queryBuilder, query, comparer, lab, security, jobs];

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
        jobs.OpenSqlRequested += (sql, database) =>
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
        comparer.OpenSqlRequested += (sql, database) =>
        {
            SelectedTab = AppTab.Query;
            query.OpenInNewTab(sql, title: "Migration", database: database);
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
        navigator.OpenSqlRequested += (sql, database, title, profile) =>
        {
            SelectedTab = AppTab.Query;
            // No database means the connection's default here, never the database of whichever tab is selected.
            // The tab runs on the connection the row belongs to, whichever one is active.
            query.OpenInNewTab(sql, title, database, connectionDefault: true, connection: profile?.Id);
        };
        navigator.HideRequested += () => ShowNavigator = false;
        navigator.ObjectActionRequested += (action, obj, session) => _ = OnNavigatorObjectActionAsync(action, obj, session);
        navigator.DocumentationRequested += (database, schema, session) => _ = ExportDocumentationFromNavigatorAsync(database, schema, session);
        navigator.BackupRequested += (database, restore, session) => _ = BackupFromNavigatorAsync(database, restore, session);
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
    /// the object; actions whose outcome is shown in the Objects tab switch to it. An object of another open
    /// connection makes that connection active first (the Objects tab shows the active one).</summary>
    private async Task OnNavigatorObjectActionAsync(NavigatorObjectAction action, DbObject obj, DatabaseSession? session)
    {
        if (session is not null && !ReferenceEquals(session, Session) && !await ActivateAsync(session)) return;
        OnNavigatorObjectAction(action, obj);
    }

    private async Task BackupFromNavigatorAsync(string? database, bool restore, DatabaseSession? session)
    {
        if (session is not null && !ReferenceEquals(session, Session) && !await ActivateAsync(session)) return;
        OpenBackupRestore(database, restore);
    }

    private async Task ExportDocumentationFromNavigatorAsync(string? database, string? schema, DatabaseSession? session)
    {
        if (session is not null && !ReferenceEquals(session, Session) && !await ActivateAsync(session)) return;
        await ExportDocumentationAsync(database, schema);
    }

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
            case NavigatorObjectAction.ImportData:
                command = Objects.ImportDataCommand;
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
            case NavigatorConnectionAction.Connect when !IsBusy:
                await ConnectProfileAsync(Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile);
                break;
            case NavigatorConnectionAction.Activate when !IsBusy && _open.Find(profile.Id) is { } open:
                await ActivateAsync(open);
                break;
            case NavigatorConnectionAction.Disconnect when !IsBusy && _open.Find(profile.Id) is { } open:
                await CloseConnectionAsync(open);
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
            case NavigatorConnectionAction.RefreshMetadata when !IsBusy && _open.Find(profile.Id) is { } open:
                await RefreshSessionAsync(open);
                break;
        }
    }

    public IReadOnlyList<AppThemeMode> ThemeModes { get; } = Enum.GetValues<AppThemeMode>();

    [ObservableProperty] private AppThemeMode _selectedTheme;

    partial void OnSelectedThemeChanged(AppThemeMode value) => _settings.SetTheme(value);

    /// <summary>The toolbar's theme menu.</summary>
    [RelayCommand]
    private void SetTheme(AppThemeMode mode) => SelectedTheme = mode;

    /// <summary>Every app-wide setting in one window (Ctrl+, or the theme menu).</summary>
    [RelayCommand]
    private void OpenPreferences() => _dialogs.ShowPreferences(() => new PreferencesViewModel(_settings, _assistantSettings, _dialogs)
    {
        CurrentVersion = AppVersion,
        CheckForUpdatesNow = _updates is null ? null : CheckForUpdatesNowAsync
    });

    /// <summary>Runs at least this long notify when they finish out of sight (0 = never), from Preferences.</summary>
    public TimeSpan LongRunNotifyThreshold => TimeSpan.FromSeconds(_settings.LongRunNotifySeconds);

    /// <summary>Draw with the CPU instead of the graphics card from the next start (palette; Preferences has the same switch).</summary>
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

    public JobsViewModel Jobs { get; }

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    [ObservableProperty] private DatabaseSession? _session;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Not connected";

    public bool IsConnected => Session is not null;

    // ----- Several connections: all open ones, of which the active one (Session) is shown by the main tabs -----

    /// <summary>The open connections; <see cref="Session"/> is the active one among them.</summary>
    public OpenConnections OpenConnections => _open;

    /// <summary>How many connections are open (the badge next to the toolbar picker shows it from two on).</summary>
    public int OpenCount => _open.Count;

    public bool ShowOpenCount => _open.Count > 1;

    public string OpenCountTip => _open.Count < 2 ? "" :
        $"{_open.Count} connections are open: {string.Join(", ", _open.All.Select(s => s.Profile.DisplayName))}. " +
        $"The tabs other than Query show {Session?.Profile.DisplayName}; pick another open one here or in the navigator to show it. " +
        "Each Query tab runs on its own connection (the picker in its toolbar).";

    /// <summary>Whether the connection picked in the toolbar is open (it is then the active one): the status dot and
    /// Connect / Disconnect follow it.</summary>
    public bool SelectedIsOpen => SelectedProfile is { } p && _open.IsOpen(p.Id);

    /// <summary>The colour mark of each open connection while several are open (the toolbar picker's dots); empty
    /// otherwise.</summary>
    public IReadOnlyDictionary<Guid, Avalonia.Media.IBrush> OpenMarks =>
        _open.Count < 2 ? new Dictionary<Guid, Avalonia.Media.IBrush>() : _open.All.Select((s, i) => (s.Profile.Id, Brush: ConnectionMarks.Brush(i))).ToDictionary(x => x.Id, x => x.Brush);

    private void OnOpenConnectionsChanged()
    {
        OnPropertyChanged(nameof(OpenMarks));
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(ShowOpenCount));
        OnPropertyChanged(nameof(OpenCountTip));
        OnPropertyChanged(nameof(SelectedIsOpen));
        RefreshCommands();
    }

    /// <summary>Set while the picker is moved to the active connection by code, so it does not try to activate it again.</summary>
    private bool _syncingPicker;

    private void ShowActiveInPicker()
    {
        if (OpenProfile() is not { } active || ReferenceEquals(SelectedProfile, active)) return;
        _syncingPicker = true;
        try { SelectedProfile = active; }
        finally { _syncingPicker = false; }
    }

    /// <summary>
    /// Makes an open connection the one the main tabs show. They start over on it (as after a reconnect), so what
    /// they would lose is listed first; Query tabs are not affected, as each keeps its own connection.
    /// </summary>
    /// <returns>False when the user stayed on the current connection.</returns>
    public async Task<bool> ActivateAsync(DatabaseSession session)
    {
        if (ReferenceEquals(session, Session)) return true;
        if (IsBusy || !_open.All.Contains(session)) return false;
        if (!await ConfirmSwitchAsync()) return false;
        _open.Activate(session);
        Session = session;
        ShowActiveInPicker();
        StatusText = BuildStatus(session);
        return true;
    }

    /// <summary>The main tabs are about to move to another connection: say what they lose (not Query tabs).</summary>
    private async Task<bool> ConfirmSwitchAsync()
    {
        if (Session is not { } current) return true;
        var lines = UnsavedWork.Describe(MainTabsWork(), LeaveReason.Switch);
        return lines.Count == 0 ||
               await _dialogs.ConfirmAsync(UnsavedWork.Message(LeaveReason.Switch, current.Profile.DisplayName, lines),
                   UnsavedWork.ConfirmText(LeaveReason.Switch));
    }

    /// <summary>What the main tabs (not the Query tabs) hold on the active connection.</summary>
    private UnsavedWorkState MainTabsWork() => new()
    {
        SecurityPending = Security.Pending.Count,
        TableDesign = TableDesigner.UnsavedDesign,
        QueryBuilderTables = QueryBuilder.Tables.Count
    };

    /// <summary>Closes one open connection after saying what it loses. When it was the active one, the connection used
    /// before it becomes active; Query tabs on it stay open and show "Connect to …".</summary>
    public async Task<bool> CloseConnectionAsync(DatabaseSession session)
    {
        if (!await ConfirmCloseAsync(session)) return false;
        await CloseCoreAsync(session);
        return true;
    }

    private async Task<bool> ConfirmCloseAsync(DatabaseSession session)
    {
        var lines = DescribeUnsavedWork(LeaveReason.Disconnect, session);
        return lines.Count == 0 ||
               await _dialogs.ConfirmAsync(UnsavedWork.Message(LeaveReason.Disconnect, session.Profile.DisplayName, lines),
                   UnsavedWork.ConfirmText(LeaveReason.Disconnect));
    }

    private async Task CloseCoreAsync(DatabaseSession session)
    {
        if (!_open.All.Contains(session) && !ReferenceEquals(session, Session)) return;
        var wasActive = ReferenceEquals(session, Session);
        // The main tabs move first, while the closing connection is still registered, so the Query tabs bound to it
        // detach once (when it is removed) and never take the next connection by mistake.
        if (wasActive)
        {
            var next = _open.SuccessorOf(session);
            if (next is not null) _open.Activate(next);
            Session = next;
        }
        _open.Remove(session);
        await session.DisposeAsync();
        ShowActiveInPicker();
        StatusText = Session is { } now
            ? $"Disconnected {session.Profile.DisplayName} · " + BuildStatus(now)
            : "Disconnected";
    }

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
            // Files named on the command line (Explorer's Open) come after the restored tabs and are the ones shown.
            if (StartupFiles.Count > 0) await OpenFilesAsync(StartupFiles);
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
        if (action == StartupConnectionAction.Connect)
        {
            // The other connections that were open too come first, so the one that was active is active again.
            var main = SelectedProfile;
            foreach (var other in StartupConnection.Others(SavedWorkspace, Profiles, _settings.ReconnectOnStart))
                await ConnectProfileAsync(other, confirmSwitch: false, activate: false);
            if (main is not null) await ConnectProfileAsync(main, confirmSwitch: false);
        }
        // Nothing connected (not asked to, or the connect failed): open on the Query tab rather than a disabled one.
        ShowQueryWhileOffline();
    }

    /// <summary>The files the app was started with (Explorer's Open on a .sql file).</summary>
    public IReadOnlyList<string> StartupFiles { get; set; } = [];

    /// <summary>Opens files in Query tabs (from Explorer, another start of the app or a drop on the window) and shows
    /// the Query tab; a file that cannot be opened is named in the status bar.</summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> paths)
    {
        var failed = new List<string>();
        var opened = 0;
        // A SQLite file opens as a connection, not as a script; with several, the first one is connected.
        var databases = paths.Where(DatabaseFiles.IsDatabaseFile).ToList();
        if (databases.Count > 0) await OpenDatabaseFileAsync(databases[0]);
        foreach (var path in paths.Except(databases))
        {
            try
            {
                await Query.OpenPathAsync(path);
                opened++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqlFileException or ArgumentException or NotSupportedException)
            {
                failed.Add(ex is FileNotFoundException or DirectoryNotFoundException ? $"{Path.GetFileName(path)} was not found" : ex.Message);
            }
        }
        if (opened > 0) SelectedTab = AppTab.Query;
        if (failed.Count > 0)
            ShowError((failed.Count == 1 ? "Could not open a file: " : $"Could not open {failed.Count} files: ") + string.Join("; ", failed));
    }

    /// <summary>
    /// Connects to a SQLite file: the saved connection for that path, or a new one named after the file, saved first so
    /// it is in the connection list next time.
    /// </summary>
    public async Task OpenDatabaseFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            ShowError($"Could not open {Path.GetFileName(path)}: the file was not found.");
            return;
        }
        var (profile, isNew) = DatabaseFiles.ProfileFor(Profiles, path);
        if (isNew)
        {
            Profiles.Add(profile);
            SortProfiles(profile);
            await SaveProfilesAsync();
        }
        SelectedProfile = profile;
        await ConnectProfileAsync(profile);
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task OpenDatabaseFileDialogAsync()
    {
        var path = await _dialogs.PickFilePathAsync("Open SQLite file",
        [
            ("SQLite databases", DatabaseFiles.Extensions.Select(e => "*" + e).ToArray()),
            ("All files", ["*"])
        ]);
        if (path is not null) await OpenDatabaseFileAsync(path);
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

    /// <summary>Open the last connection again at start (Preferences and the palette).</summary>
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
        WasConnected = Session is not null,
        OtherOpenConnectionIds = _open.All.Where(s => !ReferenceEquals(s, Session)).Select(s => s.Profile.Id).ToList() is { Count: > 0 } others
            ? others
            : null
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
        await Step(() => { Query.DetachAll(); return Task.CompletedTask; });
        await Step(() => Comparer.DisposeIndependentSessionsAsync().AsTask());
        // Left in the list: removing them one by one would only re-attach the tabs to the ones still open.
        foreach (var s in _open.All.Append(Session).OfType<DatabaseSession>().Distinct().ToList())
            await Step(() => s.DisposeAsync().AsTask());

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
        OnPropertyChanged(nameof(SelectedIsOpen));
        OnPropertyChanged(nameof(OpenCountTip));
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

    partial void OnSelectedProfileChanged(ConnectionProfile? oldValue, ConnectionProfile? newValue)
    {
        var value = newValue;
        RefreshCommands();
        OnPropertyChanged(nameof(SelectedIsOpen));
        // Picking another open connection in the toolbar makes it the active one (nothing reconnects).
        if (!_syncingPicker && value is not null && _open.Find(value.Id) is { } open && !ReferenceEquals(open, Session))
            _ = ActivateFromPickerAsync(open);
        // While offline the Query tab suggests the picked connection's keywords and saved names.
        if (!IsConnected) _ = Query.SetOfflineProfileAsync(value);
    }

    private async Task ActivateFromPickerAsync(DatabaseSession session)
    {
        // After the picker finished its own selection change.
        await Task.Yield();
        if (!await ActivateAsync(session)) ShowActiveInPicker();
    }

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    // Saved connections can be managed while connected: only the open connection itself cannot be deleted (or picked
    // in the locked toolbar picker), and edits to it reach the open session as described in ConnectionEdits.
    private bool HasSelection => SelectedProfile is not null && !IsBusy;
    private bool CanConnect => SelectedProfile is { } p && !_open.IsOpen(p.Id) && !IsBusy;
    private bool CanUseSession => IsConnected && !IsBusy;
    private bool CanCreate => !IsBusy;
    private bool CanDelete => SelectedProfile is { } p && !IsBusy && !IsOpen(p);

    private bool IsOpen(ConnectionProfile profile) => _open.IsOpen(profile.Id) || Session?.Profile.Id == profile.Id;

    /// <summary>The toolbar's delete button explains why it is greyed out on an open connection.</summary>
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
        if (IsConnected) StatusText = $"Saved {profile}. Connect opens it next to {Session!.Profile.DisplayName}.";
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

        if (_open.Find(edited.Id) is { } session) await AfterOpenConnectionEditedAsync(session, edited);
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
        if (!now || !_open.All.Contains(session))
        {
            StatusText = $"Saved {edited}. The open connection keeps its previous settings until you reconnect.";
            return;
        }

        var wasActive = ReferenceEquals(session, Session);
        if (!await CloseConnectionAsync(session)) return;
        // Reconnected, it is active again only if it was before; otherwise the main tabs stay where they are.
        await ConnectProfileAsync(Profiles.FirstOrDefault(p => p.Id == edited.Id) ?? edited, confirmSwitch: false, activate: wasActive);
    }

    /// <summary>The window title, banner and status after the open connection's name or environment changed.</summary>
    private void RefreshSessionLabels(DatabaseSession session)
    {
        Navigator.Rebuild();
        if (!ReferenceEquals(session, Session)) return;
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
        StatusText = $"Saved {copy}" + (IsConnected ? $". Connect opens it next to {Session!.Profile.DisplayName}." : "");
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
            // Overwriting an open connection: apply what applies at once, and say when the rest waits for a reconnect.
            foreach (var session in _open.All.ToList())
            {
                if (Profiles.FirstOrDefault(p => p.Id == session.Profile.Id) is not { } imported || ReferenceEquals(imported, session.Profile)) continue;
                if (ConnectionEdits.ApplyToOpenSession(session.Profile, imported)) RefreshSessionLabels(session);
                if (ConnectionEdits.NeedsReconnect(session.Profile, imported))
                    note += $" · {imported.DisplayName} keeps its previous settings until you reconnect";
            }
            StatusText = $"Imported connections from {picked.Name}: {string.Join(", ", parts)}{note}";
        }
        catch (Exception ex)
        {
            ShowError("Could not import connections: " + ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync() => SelectedProfile is { } profile ? ConnectProfileAsync(profile) : Task.CompletedTask;

    /// <summary>
    /// Opens a saved connection next to the ones already open and makes it the active one (the main tabs show it;
    /// Query tabs bound to it attach). One that is open already is just made active.
    /// </summary>
    /// <param name="confirmSwitch">Ask first when the main tabs would lose work by moving to it (not at start-up).</param>
    /// <param name="activate">False: open it in the background, for its Query tabs and the navigator, and leave the
    /// main tabs on the active connection (reconnecting an open connection that was not active, start-up).</param>
    public async Task ConnectProfileAsync(ConnectionProfile profile, bool confirmSwitch = true, bool activate = true)
    {
        if (IsBusy) return;
        if (_open.Find(profile.Id) is { } already)
        {
            if (activate) await ActivateAsync(already);
            return;
        }
        activate |= Session is null;
        if (activate && confirmSwitch && !await ConfirmSwitchAsync()) return;

        if (profile.MissingPassword || profile.Ssh.NeedsPassword)
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
            if (ct.IsCancellationRequested || _open.IsOpen(profile.Id))
            {
                await session.DisposeAsync();
                StatusText = "Connect cancelled";
                return;
            }
            ConnectionFailure = null;
            if (activate)
            {
                _open.Add(session);
                Session = session;
                ShowActiveInPicker();
            }
            else if (Session is { } current)
            {
                _open.Add(session);
                _open.Activate(current);
                StatusText = $"Opened {session.Profile.DisplayName} next to {current.Profile.DisplayName}" +
                             (_open.Count > 1 ? $" · {_open.Count} connections open" : "");
                return;
            }
            StatusText = BuildStatus(session) + (session.Snapshot.IsStale ? " · updating metadata in the background…" : "") +
                         (_open.Count > 1 ? $" · {_open.Count} connections open" : "");
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

    /// <summary>Closes the active connection (the other open ones stay open; the one used before it becomes active).</summary>
    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private async Task DisconnectAsync()
    {
        if (Session is { } s) await CloseConnectionAsync(s);
    }

    // ----- Before the connection goes away: say what is rolled back or discarded, and let the user stay -----

    /// <summary>What is lost if connections go away now for <paramref name="reason"/>; empty when nothing is. With
    /// <paramref name="session"/>, only what that connection holds: its Query tabs, and the main tabs when it is the
    /// active one. Without, everything open (quitting).</summary>
    public IReadOnlyList<string> DescribeUnsavedWork(LeaveReason reason, DatabaseSession? session = null)
    {
        if (Session is null && _open.Count == 0) return [];
        var mainTabs = session is null || ReferenceEquals(session, Session);
        var state = new UnsavedWorkState
        {
            QueryTabs = Query.Documents
                .Where(d => session is null || ReferenceEquals(d.AttachedSession, session))
                .Select(d => new QueryTabWork(d.Title, d.HasOpenTransaction, ResultEditing.HasEdits(d.ResultSets), d.IsRunning))
                .ToList(),
            SecurityPending = mainTabs ? Security.Pending.Count : 0,
            TableDesign = mainTabs ? TableDesigner.UnsavedDesign : null,
            QueryBuilderTables = mainTabs ? QueryBuilder.Tables.Count : 0,
            DataWindows = mainTabs ? _dialogs.DataWindowsWithUncommittedEdits() : []
        };
        return UnsavedWork.Describe(state, reason);
    }

    /// <summary>True when nothing would be lost or the user chose to go ahead anyway; asks at most once per quit.</summary>
    public async Task<bool> ConfirmLeaveAsync(LeaveReason reason)
    {
        if (reason == LeaveReason.Quit && QuitConfirmed) return true;
        var lines = DescribeUnsavedWork(reason);
        var names = string.Join(", ", _open.All.Append(Session).OfType<DatabaseSession>().Distinct().Select(s => s.Profile.DisplayName));
        if (lines.Count > 0 && names.Length > 0 &&
            !await _dialogs.ConfirmAsync(UnsavedWork.Message(reason, names, lines), UnsavedWork.ConfirmText(reason)))
            return false;
        if (reason == LeaveReason.Quit) QuitConfirmed = true;
        return true;
    }

    /// <summary>The user already agreed to close (or there was nothing to lose): later close requests do not ask again.</summary>
    public bool QuitConfirmed { get; private set; }

    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private Task RefreshMetadataAsync() => Session is { } s ? RefreshSessionAsync(s) : Task.CompletedTask;

    /// <summary>Re-reads one open connection's catalog (the active one from the toolbar, any from the navigator).</summary>
    private async Task RefreshSessionAsync(DatabaseSession s)
    {
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
        ExportDocumentationCommand.NotifyCanExecuteChanged();
        BackupDatabaseCommand.NotifyCanExecuteChanged();
        RestoreDatabaseCommand.NotifyCanExecuteChanged();
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
    Security,
    Jobs
}
