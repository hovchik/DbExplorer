using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ConnectionStore _store;
    private readonly SessionService _sessions;
    private readonly ProviderRegistry _registry;
    private readonly IDialogService _dialogs;
    private readonly ISessionAware[] _tabs;

    public MainWindowViewModel(
        ConnectionStore store,
        SessionService sessions,
        ProviderRegistry registry,
        IDialogService dialogs,
        ObjectsViewModel objects,
        MetadataSearchViewModel search,
        DataSearchViewModel dataSearch,
        IndexesViewModel indexes,
        LocksViewModel locks,
        QueryViewModel query,
        ComparerViewModel comparer)
    {
        _store = store;
        _sessions = sessions;
        _registry = registry;
        _dialogs = dialogs;
        Objects = objects;
        Search = search;
        DataSearch = dataSearch;
        Indexes = indexes;
        Locks = locks;
        Query = query;
        Comparer = comparer;
        Comparer.Profiles = Profiles;
        _tabs = [objects, search, dataSearch, indexes, locks, query, comparer];
    }

    public ObjectsViewModel Objects { get; }
    public MetadataSearchViewModel Search { get; }
    public DataSearchViewModel DataSearch { get; }
    public IndexesViewModel Indexes { get; }
    public LocksViewModel Locks { get; }
    public QueryViewModel Query { get; }
    public ComparerViewModel Comparer { get; }

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    [ObservableProperty] private DatabaseSession? _session;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Not connected";

    public bool IsConnected => Session is not null;

    public async Task InitializeAsync()
    {
        try
        {
            foreach (var p in await _store.LoadAsync()) Profiles.Add(p);
            SelectedProfile = Profiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = "Could not load saved connections: " + ex.Message;
        }
    }

    public void Shutdown()
    {
        foreach (var tab in _tabs) tab.Attach(null);
        Session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    partial void OnSessionChanged(DatabaseSession? value)
    {
        OnPropertyChanged(nameof(IsConnected));
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
            StatusText = BuildStatus(Session);
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
