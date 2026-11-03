using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Core.Connections;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A key on the welcome screen: what it does and its current keys (changeable in the F1 sheet).</summary>
public sealed record WelcomeKey(string Keys, string Action);

/// <summary>When the Query tab shows the welcome screen instead of an empty editor.</summary>
public static class WelcomeRules
{
    /// <summary>Nothing connected or connecting, and only one Query tab, still empty: the user has not started anything.</summary>
    public static bool Show(bool connected, bool busy, bool dismissed, IReadOnlyList<string> scripts) =>
        !connected && !busy && !dismissed && scripts.Count <= 1 && scripts.All(string.IsNullOrWhiteSpace);
}

/// <summary>
/// The welcome screen over the Query tab when the app starts with nothing connected: the saved connections to open,
/// new connection, .sql and SQLite files, the recent files, and the keys worth knowing. Typing a script, opening
/// anything or choosing "Start with an empty script" puts the editor back; it comes back only after a restart.
/// </summary>
public partial class MainWindowViewModel
{
    public const int WelcomeConnections = 8;
    public const int WelcomeFiles = 5;

    private bool _welcomeDismissed;
    private QueryViewModel? _welcomeWatched;

    [ObservableProperty] private bool _showWelcome;

    public IReadOnlyList<ConnectionProfile> WelcomeProfiles => Profiles.Take(WelcomeConnections).ToList();
    public bool HasWelcomeProfiles => Profiles.Count > 0;
    public IReadOnlyList<RecentFileItem> WelcomeRecentFiles => Query.RecentFiles.Take(WelcomeFiles).ToList();
    public bool HasWelcomeRecentFiles => Query.RecentFiles.Count > 0;

    public IReadOnlyList<WelcomeKey> WelcomeKeys =>
    [
        new(K(ShortcutCommands.Palette) ?? "", "Go to a table, a tab or any command"),
        new(K(ShortcutCommands.NewTab) ?? "", "New query tab"),
        new(K(ShortcutCommands.OpenFile) ?? "", "Open a .sql file"),
        new(K(ShortcutCommands.ToggleNavigator) ?? "", "Show or hide the database navigator"),
        new(K(ShortcutCommands.ShortcutSheet) ?? "", "Every keyboard shortcut"),
        new(K(ShortcutCommands.Preferences) ?? "", "Preferences")
    ];

    private void HookWelcome()
    {
        PropertyChanged += OnWelcomeInputChanged;
        Query.PropertyChanged += OnWelcomeInputChanged;
        Query.Documents.CollectionChanged += OnWelcomeDocumentsChanged;
        Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WelcomeProfiles));
            OnPropertyChanged(nameof(HasWelcomeProfiles));
        };
        WatchWelcomeDocument();
        UpdateWelcome();
    }

    private void OnWelcomeInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShowWelcome)) return;
        if (sender == Query && e.PropertyName == nameof(QueryWorkspaceViewModel.RecentFiles))
        {
            OnPropertyChanged(nameof(WelcomeRecentFiles));
            OnPropertyChanged(nameof(HasWelcomeRecentFiles));
        }
        if (e.PropertyName is nameof(IsConnected) or nameof(IsBusy) or nameof(Session)) UpdateWelcome();
    }

    private void OnWelcomeDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchWelcomeDocument();
        UpdateWelcome();
    }

    /// <summary>The first tab's text decides; once it has text the welcome screen has done its job.</summary>
    private void WatchWelcomeDocument()
    {
        if (_welcomeWatched is not null) _welcomeWatched.PropertyChanged -= OnWelcomeDocumentChanged;
        _welcomeWatched = Query.Documents.FirstOrDefault();
        if (_welcomeWatched is not null) _welcomeWatched.PropertyChanged += OnWelcomeDocumentChanged;
    }

    private void OnWelcomeDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryViewModel.Sql)) UpdateWelcome();
    }

    private void UpdateWelcome()
    {
        var show = WelcomeRules.Show(IsConnected, IsBusy, _welcomeDismissed, Query.Documents.Select(d => d.Sql).ToList());
        // Once real work shows up (a connection, a script), the screen stays away for this run of the app.
        if (ShowWelcome && !show && !IsBusy) _welcomeDismissed = true;
        ShowWelcome = show;
    }

    [RelayCommand]
    private void DismissWelcome()
    {
        _welcomeDismissed = true;
        ShowWelcome = false;
    }

    [RelayCommand]
    private async Task WelcomeConnectAsync(ConnectionProfile? profile)
    {
        if (profile is null) return;
        SelectedProfile = profile;
        await ConnectProfileAsync(profile);
    }

    [RelayCommand]
    private void WelcomeOpenFile()
    {
        DismissWelcome();
        RunInWorkspace(QueryFileAction.Open);
    }

    [RelayCommand]
    private void WelcomeOpenRecent(RecentFileItem? file)
    {
        if (file is null) return;
        DismissWelcome();
        SelectedTab = AppTab.Query;
        Query.OpenRecentCommand.Execute(file);
    }
}
