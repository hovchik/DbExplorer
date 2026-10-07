using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Sessions;
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
        SecurityViewModel security)
    {
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
        Comparer = comparer;
        Comparer.Profiles = Profiles;
        Profiles.CollectionChanged += (_, _) => ExportConnectionsCommand.NotifyCanExecuteChanged();
        Lab = lab;
        Security = security;
        _tabs = [objects, search, dataSearch, indexes, locks, activity, diagram, tableDesigner, erModel, queryBuilder, query, comparer, lab, security];

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
    }

    public IReadOnlyList<AppThemeMode> ThemeModes { get; } = Enum.GetValues<AppThemeMode>();

    [ObservableProperty] private AppThemeMode _selectedTheme;

    partial void OnSelectedThemeChanged(AppThemeMode value) => _settings.SetTheme(value);

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
        try
        {
            await Query.RestoreTabsAsync();
            foreach (var p in ConnectionFolders.Order(await _store.LoadAsync())) Profiles.Add(p);
            SelectedProfile = Profiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = "Could not load saved connections: " + ex.Message;
        }
    }

    /// <summary>Every step runs even when an earlier one fails (e.g. the server went away), so tabs are saved and
    /// open transactions are rolled back as far as possible.</summary>
    public async Task ShutdownAsync()
    {
        await Step(Query.SaveTabsAsync);
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
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value) => RefreshCommands();

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    private bool HasSelection => SelectedProfile is not null && !IsConnected && !IsBusy;
    private bool CanConnect => SelectedProfile is not null && !IsConnected && !IsBusy;
    private bool CanUseSession => IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task NewConnectionAsync()
    {
        var draft = new ConnectionProfile { ProviderKey = _registry.All[0].Key };
        var profile = await _dialogs.EditConnectionAsync(draft, "New connection", ConnectionFolders.All(Profiles));
        if (profile is null) return;

        Profiles.Add(profile);
        SortProfiles(profile);
        await SaveProfilesAsync();
    }

    private bool CanCreate => !IsBusy && !IsConnected;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditConnectionAsync()
    {
        if (SelectedProfile is not { } current) return;

        var edited = await _dialogs.EditConnectionAsync(current.Clone(), "Edit connection", ConnectionFolders.All(Profiles));
        if (edited is null) return;

        var index = Profiles.IndexOf(current);
        Profiles[index] = edited;
        SortProfiles(edited);
        await SaveProfilesAsync();
    }

    /// <summary>Keeps the list grouped by folder, then by name, and selects <paramref name="select"/>.</summary>
    private void SortProfiles(ConnectionProfile select)
    {
        var ordered = ConnectionFolders.Order(Profiles);
        for (var i = 0; i < ordered.Count; i++)
        {
            var at = Profiles.IndexOf(ordered[i]);
            if (at != i) Profiles.Move(at, i);
        }
        SelectedProfile = select;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteConnectionAsync()
    {
        if (SelectedProfile is not { } current) return;
        Profiles.Remove(current);
        SelectedProfile = Profiles.FirstOrDefault();
        await SaveProfilesAsync();
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
            StatusText = "Could not export connections: " + ex.Message;
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
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
            await SaveProfilesAsync();

            var parts = new List<string> { $"added {result.Added}" };
            if (result.Replaced > 0) parts.Add($"overwrote {result.Replaced}");
            if (result.Skipped > 0) parts.Add($"skipped {result.Skipped}");
            StatusText = $"Imported connections from {picked.Name}: {string.Join(", ", parts)}";
        }
        catch (Exception ex)
        {
            StatusText = "Could not import connections: " + ex.Message;
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
            var index = Profiles.IndexOf(profile);
            Profiles[index] = withPassword;
            SelectedProfile = profile = withPassword;
            await SaveProfilesAsync();
        }

        var ct = BeginLoad();
        StatusText = $"Connecting to {profile}…";
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
            StatusText = BuildStatus(Session) + (Session.Snapshot.IsStale ? " · updating metadata in the background…" : "");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            StatusText = "Connect cancelled";
        }
        catch (Exception ex)
        {
            StatusText = "Connection failed: " + ex.Message;
        }
        finally
        {
            EndLoad();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private async Task DisconnectAsync()
    {
        if (Session is not { } s) return;
        Session = null;
        await s.DisposeAsync();
        StatusText = "Disconnected";
    }

    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private async Task RefreshMetadataAsync()
    {
        if (Session is not { } s) return;

        var ct = BeginLoad();
        StatusText = "Reading catalog…";
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
            StatusText = "Refresh failed: " + ex.Message;
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
            if (IsBusy && _loadCts?.Token == ct && !ct.IsCancellationRequested) StatusText = text;
        });

    private bool CanCancelLoad => IsBusy && _loadCts is { IsCancellationRequested: false };

    [RelayCommand(CanExecute = nameof(CanCancelLoad))]
    private void CancelLoad()
    {
        if (_loadCts is not { IsCancellationRequested: false } cts) return;
        cts.Cancel();
        StatusText = "Cancelling…";
        CancelLoadCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenCommandPalette() => _dialogs.ShowCommandPalette(BuildPaletteItems());

    private IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();

        void Command(string title, System.Windows.Input.ICommand command, string? subtitle = null)
        {
            if (command.CanExecute(null))
                items.Add(new PaletteItem(title, subtitle, PaletteItemKind.Command, _ => command.Execute(null)));
        }

        Command("Refresh metadata", RefreshMetadataCommand, "re-read the catalog");
        Command("Disconnect", DisconnectCommand, Session?.Profile.ToString());
        Command("Connect", ConnectCommand, SelectedProfile?.ToString());
        Command("New connection…", NewConnectionCommand);
        Command("Edit connection…", EditConnectionCommand, SelectedProfile?.ToString());
        Command("Export connections…", ExportConnectionsCommand, "with or without passwords");
        Command("Import connections…", ImportConnectionsCommand);
        Command("Team sharing…", OpenTeamCommand, "share connections, queries and snippets through a shared folder");

        if (!IsConnected && !IsBusy)
        {
            foreach (var profile in Profiles.ToList())
            {
                items.Add(new PaletteItem($"Connect to {profile}", profile.Host, PaletteItemKind.Command, _ =>
                {
                    SelectedProfile = profile;
                    if (ConnectCommand.CanExecute(null)) ConnectCommand.Execute(null);
                }));
            }
        }

        foreach (var theme in ThemeModes)
            items.Add(new PaletteItem($"Theme: {theme}", null, PaletteItemKind.Command, _ => SelectedTheme = theme));

        foreach (var tab in Enum.GetValues<AppTab>())
        {
            if (!IsConnected && tab is not (AppTab.Comparer or AppTab.ErModel)) continue;
            var title = Converters.HumanizeConverter.Instance.Convert(tab, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? tab.ToString();
            items.Add(new PaletteItem(title.Replace("Search names and code", "Search names & code").Replace("Er model", "ER model"), "tab", PaletteItemKind.Tab, _ => SelectedTab = tab));
        }

        if (Session is { } session)
        {
            var multipleDatabases = session.Snapshot.Objects.Select(o => o.Database).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            foreach (var obj in session.Snapshot.Objects.Where(o => o.Type != DbObjectType.Trigger))
            {
                var title = multipleDatabases && !string.IsNullOrEmpty(obj.Database) ? $"{obj.Database}.{obj.FullName}" : obj.FullName;
                var subtitle = obj.Type + (obj.RowCount is long rows ? $" · {rows:N0} rows" : "");
                items.Add(new PaletteItem(title, subtitle, PaletteItemKind.Object, modifier => OpenObject(obj, modifier)));
            }
        }

        return items;
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
            StatusText = "Could not save connections: " + ex.Message;
        }
    }

    private void RefreshCommands()
    {
        NewConnectionCommand.NotifyCanExecuteChanged();
        EditConnectionCommand.NotifyCanExecuteChanged();
        DeleteConnectionCommand.NotifyCanExecuteChanged();
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
    Query,
    Objects,
    Comparer,
    SearchNamesAndCode,
    SearchData,
    Diagram,
    TableDesigner,
    ErModel,
    QueryBuilder,
    Indexes,
    Locks,
    Activity,
    Lab,
    Security
}
