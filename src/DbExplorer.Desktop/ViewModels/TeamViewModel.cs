using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Query;
using DbExplorer.Application.Team;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One shared file in the Team window.</summary>
/// <param name="Taken">A connection already in the user's saved connections.</param>
public sealed record TeamItemRow(TeamItem Item, bool Taken = false)
{
    public string PullText => Taken ? "Update mine" : "Add to my connections";
    public string Name => Item.Name;
    public bool IsChanged => Item.Status != TeamItemStatus.Seen;
    public string Badge => Item.Status switch
    {
        TeamItemStatus.New => "NEW",
        TeamItemStatus.Updated => "UPDATED",
        _ => ""
    };
    public string Modified => "changed " + Item.File.ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>
/// Sharing connections, queries and snippets with teammates through a folder the team already syncs (OneDrive,
/// Dropbox, a network share, a git checkout). The folder is watched, and the Team button counts what is new.
/// </summary>
public partial class TeamViewModel : ViewModelBase, IDisposable
{
    private readonly TeamSync _sync;
    private readonly SnippetLibrary _snippets;
    private readonly IDialogService _dialogs;
    private TeamFolderWatcher? _watcher;
    private int _watchVersion;
    private bool _disposed;
    private TeamSnapshot _snapshot = TeamSnapshot.Empty;
    private int _scanVersion;

    public TeamViewModel(TeamSync sync, SnippetLibrary snippets, IDialogService dialogs)
    {
        _sync = sync;
        _snippets = snippets;
        _dialogs = dialogs;
        _memberName = sync.MemberName;
        _rememberPassword = sync.CanRememberPassword;
        snippets.TeamItems = () => _sync.Snippets;
    }

    // ----- Set by the main window: the saved connections and the Query tab -----

    public Func<IReadOnlyList<ConnectionProfile>> GetProfiles { get; set; } = () => [];
    public Func<IReadOnlyList<ConnectionProfile>, Task> SetProfilesAsync { get; set; } = _ => Task.CompletedTask;
    public Action<string, string> OpenSql { get; set; } = (_, _) => { };
    public Func<(string Title, string Sql)?> CurrentQuery { get; set; } = () => null;

    /// <summary>The user's own snippets, for "Share a snippet".</summary>
    public IReadOnlyList<UserSnippet> MySnippets => _snippets.Items;

    public bool IsConfigured => _sync.IsConfigured;
    public string FolderText => _sync.FolderPath ?? "No team folder yet";

    [ObservableProperty] private IReadOnlyList<TeamItemRow> _connections = [];
    [ObservableProperty] private IReadOnlyList<TeamItemRow> _queries = [];
    [ObservableProperty] private IReadOnlyList<TeamItemRow> _sharedSnippets = [];
    [ObservableProperty] private string _removedText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _changedCount;
    [ObservableProperty] private bool _folderMissing;
    [ObservableProperty] private string _memberName;
    [ObservableProperty] private string _passwordInput = "";
    [ObservableProperty] private bool _rememberPassword;
    [ObservableProperty] private bool _includePasswords;

    public bool HasRemoved => RemovedText.Length > 0;
    public bool CanRememberPassword => _sync.CanRememberPassword;
    public bool HasTeamPassword => _sync.TeamPassword is not null;

    public string PasswordStatus => _sync.TeamPassword is null
        ? "No team password: connections are shared without passwords, and teammates type their own."
        : _sync.RemembersPassword
            ? "Team password set and remembered on this computer."
            : "Team password set until DB Explorer closes.";

    /// <summary>The toolbar button: "Team", or "Team · 3" when something is new.</summary>
    public string ButtonText => ChangedCount > 0 ? $"👥 Team · {ChangedCount}" : "👥 Team";

    public string ButtonTip => !IsConfigured
        ? "Share connections, queries and snippets with your team through a shared folder"
        : FolderMissing
            ? $"The team folder is not reachable: {_sync.FolderPath}"
            : ChangedCount > 0
                ? $"{ChangedCount} new or changed in the team folder"
                : "Team folder is up to date";

    partial void OnChangedCountChanged(int value) => NotifyButton();
    partial void OnFolderMissingChanged(bool value) => NotifyButton();
    partial void OnRemovedTextChanged(string value) => OnPropertyChanged(nameof(HasRemoved));

    partial void OnMemberNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _sync.MemberName = value;
    }

    private void NotifyButton()
    {
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ButtonTip));
    }

    /// <summary>Starts watching the saved team folder, if any.</summary>
    public void Start()
    {
        _ = WatchAsync();
        _ = RefreshAsync();
    }

    public void SetFolder(string? path)
    {
        _sync.SetFolder(path);
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(FolderText));
        _ = WatchAsync();
        Status = path is null ? "Stopped sharing. Nothing in the folder was deleted." : $"Sharing through {_sync.FolderPath}";
        _ = RefreshAsync();
    }

    private async Task WatchAsync()
    {
        var version = ++_watchVersion;
        _watcher?.Dispose();
        _watcher = null;
        if (_sync.FolderPath is not { } path) return;
        TeamFolderWatcher watcher;
        try
        {
            // Starting the watcher reads the whole folder; an unreachable network share must not hold up the UI.
            watcher = await Task.Run(() => new TeamFolderWatcher(path));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("team folder watcher", ex);
            return;
        }
        // Another folder was picked (or the window closed) while this one started.
        if (version != _watchVersion || _disposed)
        {
            watcher.Dispose();
            return;
        }
        _watcher = watcher;
        _watcher.Changed += () => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    }

    [RelayCommand]
    private void StopSharing() => SetFolder(null);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var version = ++_scanVersion;
        TeamSnapshot snapshot;
        try
        {
            // A network share can take a while to answer: read it off the UI thread.
            snapshot = await Task.Run(_sync.Scan);
        }
        catch (Exception ex)
        {
            Status = "Could not read the team folder: " + ex.Message;
            return;
        }
        if (version != _scanVersion) return;

        _snapshot = snapshot;
        Rows(TeamItemKind.Connection, out var connections);
        Rows(TeamItemKind.Query, out var queries);
        Rows(TeamItemKind.Snippet, out var snippets);
        Connections = connections;
        Queries = queries;
        SharedSnippets = snippets;
        RemovedText = snapshot.Removed.Count == 0 ? ""
            : "Removed by a teammate: " + string.Join(", ", snapshot.Removed.Select(r => Path.GetFileNameWithoutExtension(r)));
        FolderMissing = snapshot.FolderMissing;
        ChangedCount = snapshot.ChangedCount;
        if (snapshot.FolderMissing) Status = $"The team folder is not reachable: {_sync.FolderPath}";

        void Rows(TeamItemKind kind, out IReadOnlyList<TeamItemRow> rows)
        {
            var taken = GetProfiles().Select(p => _sync.LinkedPath(p.Id)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            rows = snapshot.Items.Where(i => i.Kind == kind).Select(i => new TeamItemRow(i, taken.Contains(i.RelativePath))).ToList();
        }
    }

    [RelayCommand]
    private async Task MarkAllSeenAsync()
    {
        _sync.MarkSeen(_snapshot.Items, _snapshot.Removed);
        await RefreshAsync();
    }

    // ----- Team password -----

    [RelayCommand]
    private void SavePassword()
    {
        if (string.IsNullOrEmpty(PasswordInput))
        {
            Status = "Type the team password first.";
            return;
        }
        _sync.SetTeamPassword(PasswordInput, RememberPassword && CanRememberPassword);
        PasswordInput = "";
        NotifyPassword();
        Status = "Team password set. Connections shared with passwords can now be opened with them.";
    }

    [RelayCommand]
    private void ForgetPassword()
    {
        _sync.SetTeamPassword(null, remember: false);
        IncludePasswords = false;
        NotifyPassword();
        Status = "Team password forgotten on this computer.";
    }

    private void NotifyPassword()
    {
        OnPropertyChanged(nameof(PasswordStatus));
        OnPropertyChanged(nameof(HasTeamPassword));
    }

    // ----- Connections -----

    [RelayCommand]
    private Task PullAsync(TeamItemRow? row) => row is null ? Task.CompletedTask : PullConnectionsAsync([row.Item]);

    [RelayCommand]
    private Task PullAllAsync() =>
        PullConnectionsAsync(_snapshot.Items.Where(i => i.Kind == TeamItemKind.Connection && i.Status != TeamItemStatus.Seen).ToList());

    private async Task PullConnectionsAsync(IReadOnlyList<TeamItem> items)
    {
        if (items.Count == 0)
        {
            Status = "Your connections already have everything new from the team.";
            return;
        }
        try
        {
            var result = _sync.PullConnections(items, GetProfiles());
            await SetProfilesAsync(result.Profiles);

            var parts = new List<string>();
            if (result.Added.Count > 0) parts.Add("added " + string.Join(", ", result.Added));
            if (result.Updated.Count > 0) parts.Add("updated " + string.Join(", ", result.Updated));
            if (result.KeptBoth.Count > 0) parts.Add("kept both, added " + string.Join(", ", result.KeptBoth));
            if (parts.Count == 0) parts.Add(result.Failed.Count > 0 ? "nothing added" : "already up to date");
            Status = "Connections: " + string.Join("; ", parts) +
                     (result.PasswordsLeftOut ? ". Passwords were left out (set the right team password to get them); you are asked on connect." : ".") +
                     (result.Failed.Count > 0 ? " Could not read " + string.Join(", ", result.Failed) + "; try again later." : "");
        }
        catch (Exception ex)
        {
            Status = "Could not add the shared connection: " + ex.Message;
        }
        await RefreshAsync();
    }

    public async Task ShareConnectionsAsync(IReadOnlyList<ConnectionProfile> profiles)
    {
        if (profiles.Count == 0) return;
        if (IncludePasswords && _sync.TeamPassword is null)
        {
            Status = "Set a team password first: shared passwords are always encrypted with it.";
            return;
        }
        try
        {
            var results = profiles.Select(p => (Profile: p, Result: _sync.ShareConnection(p, IncludePasswords))).ToList();
            Status = Summary("Shared", results.Select(r => (r.Profile.DisplayName, r.Result)).ToList()) +
                     (IncludePasswords ? " Passwords are encrypted with the team password." : " Passwords were left out.");
        }
        catch (Exception ex)
        {
            Status = "Could not share: " + ex.Message;
        }
        await RefreshAsync();
    }

    // ----- Queries and snippets -----

    [RelayCommand]
    private void Open(TeamItemRow? row)
    {
        if (row is null) return;
        try
        {
            OpenSql(Path.GetFileName(row.Name), _sync.ReadText(row.Item));
            _sync.MarkSeen([row.Item]);
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "Could not open: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ShareCurrentQueryAsync()
    {
        if (CurrentQuery() is not { } query || string.IsNullOrWhiteSpace(query.Sql))
        {
            Status = "Open a query tab with some SQL first.";
            return;
        }
        var name = await _dialogs.PromptTextAsync("Share query",
            "Teammates see it under Queries in their Team window. Use / for folders, e.g. Reports/Monthly sales.",
            "Name", query.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        ShareText(TeamItemKind.Query, name, query.Sql);
        await RefreshAsync();
    }

    public async Task ShareSnippetAsync(UserSnippet snippet)
    {
        ShareText(TeamItemKind.Snippet, snippet.Name, snippet.Sql);
        await RefreshAsync();
    }

    private void ShareText(TeamItemKind kind, string name, string sql)
    {
        try
        {
            Status = Summary("Shared", [(name.Trim(), _sync.ShareText(kind, name, sql))]);
        }
        catch (Exception ex)
        {
            Status = "Could not share: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(TeamItemRow? row)
    {
        if (row is null) return;
        if (!await _dialogs.ConfirmAsync($"Remove \"{row.Name}\" from the team folder? Teammates stop seeing it; copies they already took stay.", "Remove"))
            return;
        try
        {
            Status = _sync.Remove(row.Item)
                ? $"Removed \"{row.Name}\" from the team folder."
                : $"\"{row.Name}\" was changed by a teammate since you looked, so it was kept. Check the new version first.";
        }
        catch (Exception ex)
        {
            Status = "Could not remove: " + ex.Message;
        }
        await RefreshAsync();
    }

    private static string Summary(string verb, IReadOnlyList<(string Name, TeamWriteResult Result)> results)
    {
        var written = results.Where(r => !r.Result.Unchanged && !r.Result.Conflict).Select(r => r.Name).ToList();
        var same = results.Where(r => r.Result.Unchanged && !r.Result.Conflict).Select(r => r.Name).ToList();
        var conflicts = results.Where(r => r.Result.Conflict).ToList();
        var parts = new List<string>();
        if (written.Count > 0) parts.Add($"{verb} {string.Join(", ", written)}.");
        if (same.Count > 0) parts.Add($"Already up to date: {string.Join(", ", same)}.");
        foreach (var (name, result) in conflicts)
            parts.Add($"A teammate changed \"{name}\" since you last looked, so both are kept: yours is \"{Path.GetFileNameWithoutExtension(result.RelativePath)}\".");
        return string.Join(" ", parts);
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
    }
}
