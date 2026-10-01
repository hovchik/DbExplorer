using System.Collections.ObjectModel;
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
        QueryWorkspaceViewModel query,
        ComparerViewModel comparer,
        LabViewModel lab)
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
        Query = query;
        Comparer = comparer;
        Comparer.Profiles = Profiles;
        Lab = lab;
        _tabs = [objects, search, dataSearch, indexes, locks, activity, diagram, query, comparer, lab];

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

    /// <summary>Order matches the TabItems in MainWindow.axaml.</summary>
    [ObservableProperty] private AppTab _selectedTab = AppTab.Objects;
    public QueryWorkspaceViewModel Query { get; }
    public ComparerViewModel Comparer { get; }
    public LabViewModel Lab { get; }

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

    public string WindowTitle => Session is { } s
        ? $"DB Explorer — {s.Profile}"
        : "DB Explorer";

    public async Task InitializeAsync()
    {
        try
        {
            await Query.RestoreTabsAsync();
            foreach (var p in await _store.LoadAsync()) Profiles.Add(p);
            SelectedProfile = Profiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = "Could not load saved connections: " + ex.Message;
        }
    }

    public async Task ShutdownAsync()
    {
        await Query.SaveTabsAsync();
        await Query.RollbackOpenTransactionsAsync();
        foreach (var tab in _tabs) tab.Attach(null);
        await Comparer.DisposeIndependentSessionsAsync();
        if (Session is { } s) await s.DisposeAsync();
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
        var profile = await _dialogs.EditConnectionAsync(draft, "New connection");
        if (profile is null) return;

        Profiles.Add(profile);
        SelectedProfile = profile;
        await SaveProfilesAsync();
    }

    private bool CanCreate => !IsBusy && !IsConnected;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditConnectionAsync()
    {
        if (SelectedProfile is not { } current) return;

        var edited = await _dialogs.EditConnectionAsync(current.Clone(), "Edit connection");
        if (edited is null) return;

        var index = Profiles.IndexOf(current);
        Profiles[index] = edited;
        SelectedProfile = edited;
        await SaveProfilesAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteConnectionAsync()
    {
        if (SelectedProfile is not { } current) return;
        Profiles.Remove(current);
        SelectedProfile = Profiles.FirstOrDefault();
        await SaveProfilesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedProfile is not { } profile) return;

        if (!profile.IntegratedSecurity && string.IsNullOrEmpty(profile.Password))
        {
            // Password was not saved: ask for it.
            var withPassword = await _dialogs.EditConnectionAsync(profile.Clone(), "Enter password");
            if (withPassword is null) return;
            var index = Profiles.IndexOf(profile);
            Profiles[index] = withPassword;
            SelectedProfile = profile = withPassword;
            await SaveProfilesAsync();
        }

        IsBusy = true;
        StatusText = $"Connecting to {profile}…";
        try
        {
            Session = await _sessions.ConnectAsync(profile);
            StatusText = BuildStatus(Session) + (Session.Snapshot.IsStale ? " · updating metadata in the background…" : "");
        }
        catch (Exception ex)
        {
            StatusText = "Connection failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
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

        IsBusy = true;
        StatusText = "Reading catalog…";
        try
        {
            await _sessions.RefreshMetadataAsync(s);
            StatusText = BuildStatus(s);
        }
        catch (Exception ex)
        {
            StatusText = "Refresh failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
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
            if (!IsConnected && tab != AppTab.Comparer) continue;
            var title = Converters.HumanizeConverter.Instance.Convert(tab, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? tab.ToString();
            items.Add(new PaletteItem(title.Replace("Search names and code", "Search names & code"), "tab", PaletteItemKind.Tab, _ => SelectedTab = tab));
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
        $"{s.Profile} · {s.ServerVersion} · {s.Snapshot.Objects.Count:N0} objects, " +
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
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        RefreshMetadataCommand.NotifyCanExecuteChanged();
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
    Indexes,
    Locks,
    Activity,
    Lab
}
