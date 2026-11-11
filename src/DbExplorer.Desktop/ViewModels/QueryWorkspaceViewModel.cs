using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A past run shown in the History list.</summary>
public sealed record HistoryItem(ScriptHistoryEntry Entry)
{
    public string When => Entry.RanAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string Icon => Entry.Succeeded ? "✓" : "✗";
    public string Preview
    {
        get
        {
            var oneLine = string.Join(" ", Entry.Sql.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            return oneLine.Length > 140 ? oneLine[..140] + "…" : oneLine;
        }
    }
}

/// <summary>A recently closed tab in the Reopen list.</summary>
public sealed record ClosedTabItem(ClosedQueryTab Closed)
{
    public string Title => Closed.Tab.Title;
    public string When => Closed.ClosedAt.LocalDateTime.ToString("t", CultureInfo.CurrentCulture);
    public string Preview
    {
        get
        {
            if (Closed.Tab.FilePath is { } path && string.IsNullOrWhiteSpace(Closed.Tab.Sql)) return path;
            var oneLine = string.Join(" ", Closed.Tab.Sql.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            return oneLine.Length > 140 ? oneLine[..140] + "…" : oneLine;
        }
    }
    public string ToolTip => Closed.Tab.FilePath ?? Title;
}

/// <summary>A file in the Recent list.</summary>
public sealed record RecentFileItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>
/// The Query tab as a small SQL IDE: several query tabs (each with its own editor, undo history and results),
/// .sql files opened and saved, tabs restored on the next start, and the run history.
/// </summary>
public partial class QueryWorkspaceViewModel : ViewModelBase, ISessionAware
{
    private readonly Func<QueryViewModel> _createDocument;
    private readonly ScriptStore _scripts;
    private readonly IDialogService _dialogs;
    private readonly LocalHistoryStore _localHistory;
    private readonly MetadataCache _catalogCache;
    private readonly RecentFiles _recentFileStore;
    private readonly OpenConnections _open;

    /// <summary>The active connection (the one the other main tabs show); tabs not bound yet take it.</summary>
    private DatabaseSession? _active;
    private OfflineCatalog? _offline;
    private int _offlineVersion;
    private System.Windows.Input.ICommand? _connectCommand;
    private int _untitled;

    public QueryWorkspaceViewModel(Func<QueryViewModel> createDocument, ScriptStore scripts, IDialogService dialogs,
        LocalHistoryStore localHistory, MetadataCache catalogCache, RecentFiles recentFiles, OpenConnections open)
    {
        _open = open;
        // A connection opened or closed elsewhere (toolbar, navigator, a tab's Connect): tabs bound to it follow.
        _open.Changed += (_, _) => Reconcile();
        _recentFileStore = recentFiles;
        _catalogCache = catalogCache;
        _createDocument = createDocument;
        _scripts = scripts;
        _dialogs = dialogs;
        _localHistory = localHistory;
        NewTab();

        // Unsaved scratch text survives a crash: the tab list is written every 30 seconds while something changed.
        _autosave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosave.Tick += async (_, _) =>
        {
            var snapshot = string.Join("\u0001", Documents.Select(d => d.Title + "\u0002" + d.Sql + "\u0002" + d.CurrentDatabase));
            if (snapshot == _lastSaved) return;
            // Remembered only once written, so a failed save is tried again on the next tick.
            if (await SaveTabsAsync()) _lastSaved = snapshot;
        };
        _autosave.Start();
    }

    private readonly Avalonia.Threading.DispatcherTimer _autosave;
    private string _lastSaved = "";

    public ObservableCollection<QueryViewModel> Documents { get; } = [];

    /// <summary>A run of any tab ended; the window turns long ones the user did not watch into a notification.</summary>
    public event Action<QueryViewModel, TimeSpan, RunOutcome>? RunFinished;

    /// <summary>The command palette asks the workspace view to open or save a file (it owns the file pickers).</summary>
    public event Func<QueryFileAction, Task>? FileActionRequested;

    public Task RequestFileActionAsync(QueryFileAction action) => FileActionRequested?.Invoke(action) ?? Task.CompletedTask;

    [ObservableProperty] private QueryViewModel? _selectedDocument;
    [ObservableProperty] private IReadOnlyList<HistoryItem> _history = [];
    [ObservableProperty] private string _historyFilter = "";

    /// <summary>The recently closed tabs, newest first (Ctrl+Shift+T reopens the first).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentlyClosed))]
    [NotifyCanExecuteChangedFor(nameof(ReopenClosedTabCommand))]
    private IReadOnlyList<ClosedTabItem> _recentlyClosed = [];

    public bool HasRecentlyClosed => RecentlyClosed.Count > 0;

    private ClosedTabs _closed = new();

    /// <summary>The files opened or saved most recently, newest first (Open ▾ and the command palette).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentFiles))]
    [NotifyPropertyChangedFor(nameof(RecentHeader))]
    private IReadOnlyList<RecentFileItem> _recentFiles = [];

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public string RecentHeader => HasRecentFiles
        ? "Recent files"
        : "No recent files yet. Files you open or save show up here; you can also drop .sql files on the window.";
    private IReadOnlyList<HistoryItem> _allHistory = [];

    /// <summary>The active connection changed. Tabs bound to another open connection keep running there; tabs not
    /// bound yet take the new active one.</summary>
    public void Attach(DatabaseSession? session)
    {
        _active = session;
        Reconcile();
    }

    // ----- Several connections: each tab is bound to one saved connection -----

    private ObservableCollection<ConnectionProfile>? _profiles;

    /// <summary>The saved connections, for the tabs' connection picker (kept in step with the toolbar's list).</summary>
    public ObservableCollection<ConnectionProfile>? Profiles
    {
        get => _profiles;
        set
        {
            if (_profiles is not null) _profiles.CollectionChanged -= OnProfilesChanged;
            _profiles = value;
            if (_profiles is not null) _profiles.CollectionChanged += OnProfilesChanged;
            RefreshConnections();
        }
    }

    private void OnProfilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshConnections();

    /// <summary>A tab's "Connect to …": open that saved connection (the window asks for a password when needed).</summary>
    public event Func<ConnectionProfile, Task>? ConnectProfileRequested;

    /// <summary>The open connection for a tab bound to <paramref name="id"/>; the active one for a tab not bound yet.</summary>
    private DatabaseSession? SessionFor(Guid? id)
    {
        if (id is not { } bound) return _active;
        if (_open.Find(bound) is { } open) return open;
        // The active connection was handed over without the registry (it is always registered in the app).
        return _active is { } a && a.Profile.Id == bound && !_open.All.Contains(a) ? a : null;
    }

    private bool IsOpen(Guid id) => SessionFor(id) is not null;

    private ConnectionProfile? ProfileOf(Guid id) =>
        _open.Find(id)?.Profile ?? (_active?.Profile.Id == id ? _active.Profile : null) ?? _profiles?.FirstOrDefault(p => p.Id == id);

    /// <summary>Attaches every tab to the connection it should be on now, then refreshes pickers and labels.</summary>
    private void Reconcile()
    {
        foreach (var d in Documents) Bind(d);
        RefreshConnections();
    }

    private void Bind(QueryViewModel doc)
    {
        var target = SessionFor(doc.ConnectionId);
        // The first connection a tab is attached to is the one it stays with.
        if (target is not null && doc.ConnectionId is null) doc.ConnectionId = target.Profile.Id;
        if (!ReferenceEquals(doc.AttachedSession, target)) doc.Attach(target);
    }

    /// <summary>The picker list, colour marks, offline catalogs and Connect labels of every tab.</summary>
    private void RefreshConnections()
    {
        var choices = BuildChoices();
        var bound = Documents.Select(d => d.ConnectionId).Where(id => id is not null).Distinct().Count();
        var showMarks = _open.Count > 1 || bound > 1;
        foreach (var d in Documents)
        {
            d.ShowConnection(choices, showMarks);
            UpdateOffline(d);
        }
        OnPropertyChanged(nameof(ConnectLabel));
    }

    private List<ConnectionChoice> BuildChoices()
    {
        var profiles = (_profiles ?? []).ToList();
        // Open connections are listed even when the saved list does not have them (yet).
        foreach (var s in _open.All.Append(_active).OfType<DatabaseSession>())
            if (profiles.All(p => p.Id != s.Profile.Id)) profiles.Add(s.Profile);
        return profiles.Select(p =>
        {
            var session = SessionFor(p.Id);
            var mark = _open.IndexOf(p.Id);
            return new ConnectionChoice(session?.Profile ?? p, session is not null, session is not null && ReferenceEquals(session, _active),
                mark >= 0 ? mark : 0);
        }).ToList();
    }

    private void UpdateOffline(QueryViewModel doc)
    {
        if (doc.AttachedSession is not null)
        {
            doc.ConnectLabel = "";
            doc.UseActiveLabel = "";
            doc.OfflineText = QueryViewModel.NotConnectedText;
            return;
        }
        if (doc.ConnectionId is { } id && ProfileOf(id) is { } profile)
        {
            doc.SetOfflineCatalog(OfflineCatalogOf(profile));
            doc.ConnectLabel = $"Connect to {profile.DisplayName}";
            doc.UseActiveLabel = _active is { } active ? $"Use {active.Profile.DisplayName} instead" : "";
            doc.OfflineText = $"{profile.DisplayName} is not open. You can write, open and save scripts here; connect to run them.";
            return;
        }
        doc.SetOfflineCatalog(_offline);
        doc.ConnectLabel = ConnectLabel;
        doc.UseActiveLabel = "";
        doc.OfflineText = QueryViewModel.NotConnectedText;
    }

    /// <summary>Catalogs of closed connections tabs are bound to, read once from the local cache for suggestions.</summary>
    private readonly Dictionary<Guid, OfflineCatalog> _offlineCatalogs = [];
    private readonly HashSet<Guid> _offlineLoading = [];

    private OfflineCatalog? OfflineCatalogOf(ConnectionProfile profile)
    {
        if (_offlineCatalogs.TryGetValue(profile.Id, out var known)) return known;
        if (_offlineLoading.Add(profile.Id)) _ = LoadOfflineCatalogAsync(profile);
        return new OfflineCatalog(profile.ProviderKey, profile.DisplayName, null);
    }

    private async Task LoadOfflineCatalogAsync(ConnectionProfile profile)
    {
        MetadataSnapshot? snapshot = null;
        try
        {
            snapshot = await _catalogCache.TryLoadAsync(MetadataCache.CacheKey(profile));
        }
        catch
        {
            // A missing or damaged cache only means fewer suggestions.
        }
        _offlineCatalogs[profile.Id] = new OfflineCatalog(profile.ProviderKey, profile.DisplayName, snapshot);
        _offlineLoading.Remove(profile.Id);
        foreach (var d in Documents.Where(d => d.ConnectionId == profile.Id && d.AttachedSession is null)) UpdateOffline(d);
    }

    /// <summary>Moves a tab to another saved connection (its picker): attached at once when that one is open, otherwise it
    /// waits with "Connect to …". The database it used there before comes back (<see cref="QueryViewModel.Databases"/>).</summary>
    public void BindTab(QueryViewModel doc, ConnectionProfile profile)
    {
        if (!doc.CanChangeConnection) return;
        doc.ConnectionId = profile.Id;
        Bind(doc);
        RefreshConnections();
        if (doc.AttachedSession is null) doc.Status = $"{profile.DisplayName} is not open: connect to run this tab there.";
    }

    private void WireConnection(QueryViewModel doc)
    {
        doc.ConnectionPicked += BindTab;
        doc.ConnectCommand = new RelayCommand(() =>
        {
            if (doc.ConnectionId is { } id && ProfileOf(id) is { } profile && ConnectProfileRequested is { } connect) _ = connect(profile);
            else if (ConnectCommand?.CanExecute(null) == true) ConnectCommand.Execute(null);
        });
        doc.UseActiveCommand = new RelayCommand(() =>
        {
            if (_active is { } active) BindTab(doc, active.Profile);
        });
    }

    /// <summary>The window closes every connection (shutdown): tabs keep their binding and show "Connect to …".</summary>
    public void DetachAll()
    {
        _active = null;
        foreach (var d in Documents) d.Attach(null);
    }

    // ----- Offline: the tabs open, edit and save scripts with nothing connected; only running needs a server -----

    /// <summary>The window's Connect command, for the button on the bar shown above the editor while offline.</summary>
    public System.Windows.Input.ICommand? ConnectCommand
    {
        get => _connectCommand;
        set
        {
            _connectCommand = value;
        }
    }

    /// <summary>"Connect to Shop" on that bar; empty while connected or when no connection is picked.</summary>
    public string ConnectLabel => _active is null && _offline is { } o ? $"Connect to {o.ConnectionName}" : "";

    /// <summary>While nothing is connected: the editor suggests the keywords of <paramref name="profile"/>'s engine and,
    /// when it was opened before, the names of its saved catalog (read from the local cache, never from the server).</summary>
    private void UpdateConnectLabels() => RefreshConnections();

    public async Task SetOfflineProfileAsync(ConnectionProfile? profile)
    {
        var version = ++_offlineVersion;
        MetadataSnapshot? snapshot = null;
        if (profile is not null)
        {
            try
            {
                snapshot = await _catalogCache.TryLoadAsync(MetadataCache.CacheKey(profile));
            }
            catch
            {
                // A missing or damaged cache only means fewer suggestions.
            }
        }
        if (version != _offlineVersion) return;
        _offline = profile is null ? null : new OfflineCatalog(profile.ProviderKey, profile.DisplayName, snapshot);
        UpdateConnectLabels();
    }

    public QueryViewModel NewTab() => AddTab($"Query {++_untitled}", "", filePath: null, dirty: false, autoTitle: true);

    /// <summary>Where a new tab runs: the selected tab's connection while it is open, else the active one.</summary>
    private Guid? NewTabConnection() =>
        QueryTabConnection.ForNewTab(SelectedDocument?.ConnectionId, IsOpen, _active?.Profile.Id);

    [RelayCommand]
    private void NewQuery() => NewTab();

    /// <param name="database">Database the tab starts in; by default the ones of the tab it is opened from.</param>
    /// <param name="autoTitle">Whether the tab is renamed after the queries it runs.</param>
    /// <param name="databases">The databases of a restored tab, in place of <paramref name="database"/>.</param>
    /// <param name="connection">The saved connection the tab is bound to; by default the selected tab's (when open) or
    /// the active one (see <see cref="NewTabConnection"/>).</param>
    /// <param name="restored">A restored or reopened tab keeps <paramref name="connection"/> as saved, even null.</param>
    private QueryViewModel AddTab(string title, string sql, string? filePath, bool dirty, string? database = null, bool autoTitle = false,
        TabDatabaseMemory? databases = null, Guid? id = null, Guid? connection = null, bool restored = false)
    {
        var doc = _createDocument();
        doc.ConnectionId = connection ?? (restored ? null : NewTabConnection());
        if (id is { } known) doc.Id = known;
        doc.Title = title;
        doc.AutoTitle = autoTitle && filePath is null;
        doc.FilePath = filePath;
        doc.SetText(sql, markClean: !dirty);
        doc.Databases = databases
                        ?? (database is not null ? new TabDatabaseMemory { Pending = database } : SelectedDocument?.Databases.Clone())
                        ?? new TabDatabaseMemory();
        WireConnection(doc);
        var target = SessionFor(doc.ConnectionId);
        if (target is not null) doc.ConnectionId ??= target.Profile.Id;
        doc.Attach(target);
        doc.OpenRequested += (docTitle, text) => OpenInNewTab(text, docTitle);
        doc.OpenAtRequested += (docTitle, text, offset) => OpenInNewTab(text, docTitle, caretOffset: offset);
        doc.OpenAndRunRequested += (docTitle, text, db) =>
        {
            var opened = AddTab(docTitle, text, null, dirty: false, NullIfEmpty(db), connection: doc.ConnectionId);
            if (opened.ExecuteCommand.CanExecute(null)) opened.ExecuteCommand.Execute(null);
        };
        doc.RunFinished += (finished, elapsed, outcome) => RunFinished?.Invoke(finished, elapsed, outcome);
        doc.RunStarting += started => _ = RecordAsync(started, LocalHistoryReason.Run);
        Documents.Add(doc);
        SelectedDocument = doc;
        RefreshConnections();
        return doc;
    }

    /// <summary>Opens <paramref name="sql"/> in a new tab (history, "script as", …).</summary>
    /// <param name="connectionDefault">With no <paramref name="database"/>: start in the connection's default database
    /// instead of the selected tab's.</param>
    /// <param name="caretOffset">Where the caret goes in the new tab (a usage in a view's source).</param>
    /// <param name="connection">The saved connection the script is for; by default the active one (the Objects tab, the
    /// Diagram and the other main tabs all work on it).</param>
    public void OpenInNewTab(string sql, string? title = null, string? database = null, bool autoTitle = false, bool connectionDefault = false,
        int? caretOffset = null, Guid? connection = null)
    {
        var doc = AddTab(title ?? $"Query {++_untitled}", sql, null, dirty: false, NullIfEmpty(database), autoTitle || title is null,
            connectionDefault && string.IsNullOrEmpty(database) ? new TabDatabaseMemory() : null,
            connection: connection ?? _active?.Profile.Id);
        doc.PendingCaretOffset = caretOffset;
    }

    /// <summary>Opens <paramref name="sql"/> in a new tab on <paramref name="database"/> and runs it (Query builder's Run).</summary>
    public void OpenAndRun(string sql, string? title = null, string? database = null)
    {
        var doc = AddTab(title ?? $"Query {++_untitled}", sql, null, dirty: false, NullIfEmpty(database), title is null,
            connection: _active?.Profile.Id);
        if (doc.ExecuteCommand.CanExecute(null)) doc.ExecuteCommand.Execute(null);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>"Query 3": the name a new tab gets before it is named after its queries.</summary>
    private static bool IsUntitledName(string title) =>
        title.StartsWith("Query ", StringComparison.Ordinal) && int.TryParse(title.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    [RelayCommand]
    private Task CloseTabAsync(QueryViewModel? doc) => TryCloseAsync(doc ?? SelectedDocument);

    /// <summary>Closes the other tabs, left to right; stops at the first one whose close the user cancels.</summary>
    [RelayCommand]
    private Task CloseOtherTabsAsync(QueryViewModel? doc) =>
        CloseManyAsync(Documents.Where(d => !ReferenceEquals(d, doc ?? SelectedDocument)).ToList());

    [RelayCommand]
    private Task CloseTabsToRightAsync(QueryViewModel? doc)
    {
        var index = Documents.IndexOf(doc ?? SelectedDocument!);
        return index < 0 ? Task.CompletedTask : CloseManyAsync(Documents.Skip(index + 1).ToList());
    }

    [RelayCommand]
    private Task CloseAllTabsAsync() => CloseManyAsync(Documents.ToList());

    private async Task CloseManyAsync(IReadOnlyList<QueryViewModel> docs)
    {
        foreach (var d in docs)
            if (!await TryCloseAsync(d)) return;
    }

    [RelayCommand]
    private Task MoveTabLeftAsync(QueryViewModel? doc) => ShiftTabAsync(doc, -1);

    [RelayCommand]
    private Task MoveTabRightAsync(QueryViewModel? doc) => ShiftTabAsync(doc, 1);

    private Task ShiftTabAsync(QueryViewModel? doc, int offset)
    {
        doc ??= SelectedDocument;
        var from = doc is null ? -1 : Documents.IndexOf(doc);
        return MoveTabAsync(doc, TabOrder.Shift(from, offset, Documents.Count));
    }

    /// <summary>Moves <paramref name="doc"/> to <paramref name="index"/>, keeps the selection and remembers the new order.</summary>
    public async Task MoveTabAsync(QueryViewModel? doc, int index)
    {
        if (doc is null) return;
        var from = Documents.IndexOf(doc);
        if (from < 0 || index < 0 || index >= Documents.Count || index == from) return;
        var selected = SelectedDocument;
        Documents.Move(from, index);
        // The tab strip can drop its selection while the item is moved.
        SelectedDocument = null;
        SelectedDocument = selected ?? doc;
        await SaveTabsAsync();
    }

    /// <summary>Selects the tab <paramref name="offset"/> places away, wrapping around (Ctrl+PageUp / Ctrl+PageDown).</summary>
    public void SelectNeighbour(int offset)
    {
        if (Documents.Count == 0) return;
        var from = SelectedDocument is null ? 0 : Math.Max(0, Documents.IndexOf(SelectedDocument));
        SelectedDocument = Documents[((from + offset) % Documents.Count + Documents.Count) % Documents.Count];
    }

    /// <summary>A copy of the tab's text in a new tab, in the same database.</summary>
    [RelayCommand]
    private void DuplicateTab(QueryViewModel? doc)
    {
        doc ??= SelectedDocument;
        if (doc is null) return;
        AddTab(doc.Title + " (copy)", doc.Sql, filePath: null, dirty: false, databases: doc.Databases.Clone(), connection: doc.ConnectionId,
            restored: true);
    }

    /// <summary>Gives the tab a name of the user's choice; it is then no longer renamed after the queries it runs.</summary>
    [RelayCommand]
    private async Task RenameTabAsync(QueryViewModel? doc)
    {
        doc ??= SelectedDocument;
        if (doc is null) return;
        var name = await _dialogs.PromptTextAsync("Rename tab", "The tab keeps this name until you rename it again.", "Name", doc.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        doc.Title = name.Trim();
        doc.AutoTitle = false;
        await SaveTabsAsync();
    }

    /// <summary>Closes <paramref name="doc"/> after the usual unsaved-text and open-transaction checks; false when the user kept it.</summary>
    private async Task<bool> TryCloseAsync(QueryViewModel? doc)
    {
        if (doc is null || !Documents.Contains(doc)) return true;
        var unsaved = doc.FilePath is not null ? doc.IsDirty : !string.IsNullOrWhiteSpace(doc.Sql);
        if (unsaved && !await _dialogs.ConfirmAsync(
                doc.FilePath is null
                    ? $"{doc.Title} is not saved to a file. Close it and discard its text?"
                    : $"{doc.Title} has unsaved changes. Close it anyway?",
                "Close without saving"))
            return false;
        if (ResultEditing.HasEdits(doc.ResultSets) &&
            !await _dialogs.ConfirmAsync(
                $"The results of {doc.Title} have changes that are not committed yet (edited, new or deleted rows). Close it and discard them?",
                "Discard and close"))
            return false;
        if (doc.HasOpenTransaction &&
            !await _dialogs.ConfirmAsync($"{doc.Title} has an open transaction. Closing rolls it back. Continue?", "Roll back and close"))
            return false;

        var index = Documents.IndexOf(doc);
        if (_closed.Add(StateOf(doc), index, DateTimeOffset.Now)) await ClosedTabsChangedAsync();
        // Its last text stays in the local history, so a tab closed by mistake can be brought back.
        await RecordAsync(doc, LocalHistoryReason.Closed);
        await doc.EndTransactionAsync(commit: false, reason: "tab closed");
        doc.Attach(null);
        // Move to the nearest tab on the left before the tab goes away. Removing the selected item first lets the
        // TabControl pick its own replacement, and it always picked the first tab.
        if (ReferenceEquals(SelectedDocument, doc) || SelectedDocument is null)
            SelectedDocument = TabAfterClosing(Documents, index);
        Documents.Remove(doc);
        if (Documents.Count == 0) NewTab();
        else if (SelectedDocument is null || !Documents.Contains(SelectedDocument)) SelectedDocument = Documents[Math.Clamp(index - 1, 0, Documents.Count - 1)];
        RefreshConnections();
        await SaveTabsAsync();
        return true;
    }

    /// <summary>
    /// The tab that gets the focus when the tab at <paramref name="closingIndex"/> closes: the nearest one on its left, or the
    /// new first tab when the leftmost closes; null when it is the only tab.
    /// </summary>
    public static T? TabAfterClosing<T>(IReadOnlyList<T> tabs, int closingIndex) where T : class =>
        tabs.Count <= 1 || closingIndex < 0 || closingIndex >= tabs.Count ? null
        : closingIndex > 0 ? tabs[closingIndex - 1]
        : tabs[1];

    /// <summary>Brings back the most recently closed tab (Ctrl+Shift+T).</summary>
    [RelayCommand(CanExecute = nameof(HasRecentlyClosed))]
    private Task ReopenClosedTabAsync() => ReopenAsync(_closed.TakeLatest());

    /// <summary>Brings back one tab picked from the Recently closed list.</summary>
    [RelayCommand]
    private Task ReopenTabAsync(ClosedTabItem? item) => item is null ? Task.CompletedTask : ReopenAsync(_closed.Take(item.Closed));

    private async Task ReopenAsync(ClosedQueryTab? closed)
    {
        if (closed is null) return;
        var t = closed.Tab;
        await ClosedTabsChangedAsync();

        // A file that is open again in another tab is just shown.
        if (t.FilePath is not null && Documents.FirstOrDefault(d => ClosedTabs.SamePath(d.FilePath, t.FilePath)) is { } open)
        {
            SelectedDocument = open;
            return;
        }

        // A tab with no unsaved changes shows its file as it is now; otherwise the text it had when closed.
        var sql = t.Sql;
        var dirty = t.IsDirty;
        if (t is { FilePath: { } path, IsDirty: false })
        {
            try
            {
                if (File.Exists(path)) sql = await File.ReadAllTextAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the text it had when closed.
            }
        }

        // The empty tab left behind by closing the last one gives its place up.
        var placeholder = Documents.Count == 1 && Documents[0] is { FilePath: null } only && string.IsNullOrEmpty(only.Sql) ? Documents[0] : null;
        var id = t.Id is { } known && Documents.All(d => d.Id != known) ? known : (Guid?)null;
        var doc = AddTab(t.Title, sql, t.FilePath, dirty, autoTitle: t.AutoTitle ?? IsUntitledName(t.Title),
            databases: TabDatabaseMemory.From(t), id: id, connection: t.ConnectionId, restored: true);
        RestoreFileState(doc, t);
        if (placeholder is not null)
        {
            placeholder.Attach(null);
            Documents.Remove(placeholder);
        }
        var target = ClosedTabs.InsertIndex(closed.Index, Documents.Count - 1);
        var from = Documents.IndexOf(doc);
        if (from != target)
        {
            Documents.Move(from, target);
            SelectedDocument = null;
        }
        SelectedDocument = doc;
        await SaveTabsAsync();
    }

    private async Task ClosedTabsChangedAsync()
    {
        RecentlyClosed = _closed.Items.Select(c => new ClosedTabItem(c)).ToList();
        try
        {
            await _scripts.SaveClosedTabsAsync(_closed.Items);
        }
        catch
        {
            // Best-effort, like the tab list.
        }
    }

    [RelayCommand]
    private Task ClearRecentlyClosedAsync()
    {
        _closed.Clear();
        return ClosedTabsChangedAsync();
    }

    /// <summary>Keeps the tab's current text in its local history (best-effort, never an error dialog).</summary>
    public async Task RecordAsync(QueryViewModel doc, LocalHistoryReason reason)
    {
        try
        {
            await _localHistory.RecordAsync(LocalHistoryStore.KeyFor(doc.Id, doc.FilePath), doc.Title, doc.FilePath, doc.Sql, reason);
        }
        catch
        {
            // The local history is a safety net; failing to write it must not get in the way of running or saving.
        }
    }

    private async Task MoveHistoryAsync(QueryViewModel doc, string path, string name)
    {
        try
        {
            await _localHistory.MoveAsync(LocalHistoryStore.KeyFor(doc.Id, null), LocalHistoryStore.KeyFor(doc.Id, path), name, path);
        }
        catch
        {
            // Best-effort, as above.
        }
    }

    /// <summary>Opens the local history of <paramref name="doc"/> (every run, save, open and close kept a version).</summary>
    [RelayCommand]
    private void ShowLocalHistory(QueryViewModel? doc)
    {
        doc ??= SelectedDocument;
        if (doc is null) return;
        _dialogs.ShowLocalHistory(new LocalHistoryViewModel(_localHistory, this, doc, _dialogs));
    }

    /// <summary>Opens a file in a new tab, or reuses the current tab when it is an empty, untouched scratch tab. A file
    /// that is open already is just shown. The encoding it was stored in is kept for saving.</summary>
    public async Task OpenFileAsync(string path, string name)
    {
        if (Documents.FirstOrDefault(d => ClosedTabs.SamePath(d.FilePath, path)) is { } open)
        {
            SelectedDocument = open;
            await RememberRecentAsync(path);
            await open.CheckDiskAsync();
            return;
        }
        var file = await SqlFile.ReadAsync(path);
        QueryViewModel doc;
        if (SelectedDocument is { FilePath: null } current && string.IsNullOrWhiteSpace(current.Sql) && !current.IsRunning)
        {
            doc = current;
            doc.Title = name;
            doc.AutoTitle = false;
            doc.FilePath = path;
            doc.SetText(file.Text, markClean: true);
        }
        else doc = AddTab(name, file.Text, path, dirty: false);
        doc.FileEncoding = file.Encoding;
        doc.FileStamp = file.Stamp;
        await RecordAsync(doc, LocalHistoryReason.Opened);
        await RememberRecentAsync(path);
        await SaveTabsAsync();
    }

    /// <summary>Opens <paramref name="path"/> (from Explorer, a drop or the Recent list) under its file name.</summary>
    public Task OpenPathAsync(string path) => OpenFileAsync(Path.GetFullPath(path), Path.GetFileName(path));

    /// <summary>Opens a file from the Recent list; one that is gone is dropped from the list.</summary>
    [RelayCommand]
    private async Task OpenRecentAsync(RecentFileItem? item)
    {
        if (item is null) return;
        try
        {
            await OpenPathAsync(item.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqlFileException)
        {
            if (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                await ChangeRecentAsync(r => r.RemoveAsync(item.Path));
                if (SelectedDocument is { } doc) doc.Status = $"{item.Name} is not there any more ({item.Path}); it was taken off the Recent list.";
            }
            else if (SelectedDocument is { } doc) doc.Status = $"Could not open {item.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task ClearRecentFilesAsync() => ChangeRecentAsync(r => r.ClearAsync());

    private Task RememberRecentAsync(string path) => ChangeRecentAsync(r => r.AddAsync(path));

    private async Task ChangeRecentAsync(Func<RecentFiles, Task<IReadOnlyList<string>>> change)
    {
        try
        {
            RecentFiles = (await change(_recentFileStore)).Select(p => new RecentFileItem(p)).ToList();
        }
        catch
        {
            // The list is a convenience; failing to write it must not stop an open or a save.
        }
    }

    /// <summary>Saves the tab to <paramref name="path"/> in the encoding its file had (UTF-8 for a new file). When the
    /// file was changed by another program since this tab read it, asks before overwriting that change.</summary>
    /// <returns>False when the user chose not to overwrite.</returns>
    public async Task<bool> SaveAsync(QueryViewModel doc, string path, string name)
    {
        if (ClosedTabs.SamePath(doc.FilePath, path) && doc.FileStamp is { } known && FileStamp.Of(path) is { } now && now != known &&
            !await _dialogs.ConfirmAsync(
                $"{Path.GetFileName(path)} was changed by another program since you opened it. Save over those changes?",
                "Overwrite"))
            return false;

        var previousEncoding = doc.FileEncoding;
        var written = await SqlFile.WriteAsync(path, doc.Sql, previousEncoding);
        // A tab saved to its first file takes its versions so far along; another file starts with its own.
        if (doc.FilePath is null) await MoveHistoryAsync(doc, path, name);
        doc.FilePath = path;
        doc.Title = name;
        doc.AutoTitle = false;
        doc.IsDirty = false;
        doc.FileEncoding = written.Encoding;
        doc.FileStamp = written.Stamp;
        doc.DiskNotice = "";
        doc.CanReloadFromDisk = false;
        doc.Status = written.EncodingChanged
            ? $"Saved to {path} as UTF-8 with BOM: the text has characters that {SqlFileEncodings.Label(previousEncoding)} cannot hold."
            : $"Saved to {path}";
        await RecordAsync(doc, LocalHistoryReason.Saved);
        await RememberRecentAsync(path);
        await SaveTabsAsync();
        return true;
    }

    /// <summary>Shows the tab's file selected in Explorer (Finder, or the folder on Linux).</summary>
    [RelayCommand]
    private void ShowInFolder(QueryViewModel? doc)
    {
        doc ??= SelectedDocument;
        if (doc?.FilePath is not { } path) return;
        try
        {
            var start = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : OperatingSystem.IsMacOS()
                    ? new System.Diagnostics.ProcessStartInfo("open") { ArgumentList = { "-R", path } }
                    : new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path) ?? path } };
            start.UseShellExecute = false;
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            doc.Status = "Could not show the folder: " + ex.Message;
        }
    }

    /// <summary>Checks every tab's file against the disk (the window came back to the front).</summary>
    public async Task CheckFilesOnDiskAsync()
    {
        foreach (var doc in Documents.Where(d => d.FilePath is not null).ToList())
            await doc.CheckDiskAsync();
    }

    partial void OnSelectedDocumentChanged(QueryViewModel? value)
    {
        if (value is { FilePath: not null }) _ = value.CheckDiskAsync();
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            _allHistory = (await _scripts.LoadHistoryAsync()).Select(e => new HistoryItem(e)).ToList();
        }
        catch
        {
            _allHistory = [];
        }
        ApplyHistoryFilter();
    }

    partial void OnHistoryFilterChanged(string value) => ApplyHistoryFilter();

    private void ApplyHistoryFilter()
    {
        var f = HistoryFilter.Trim();
        History = f.Length == 0 ? _allHistory : _allHistory.Where(h => h.Entry.Sql.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    [RelayCommand]
    private void OpenHistory(HistoryItem? item)
    {
        if (item is not null) OpenInNewTab(item.Entry.Sql, "History " + item.Entry.RanAt.LocalDateTime.ToString("t", CultureInfo.CurrentCulture), autoTitle: true);
    }

    /// <summary>Restores the tabs of the previous run (best-effort).</summary>
    public async Task RestoreTabsAsync()
    {
        await ChangeRecentAsync(async r => await r.LoadAsync());
        try
        {
            _closed = new ClosedTabs(await _scripts.LoadClosedTabsAsync());
            RecentlyClosed = _closed.Items.Select(c => new ClosedTabItem(c)).ToList();
        }
        catch
        {
            // A damaged list only loses the reopen entries.
        }
        try
        {
            var tabs = await _scripts.LoadTabsAsync();
            if (tabs.Count == 0) return;
            var placeholder = Documents.Count == 1 && string.IsNullOrEmpty(Documents[0].Sql) ? Documents[0] : null;
            foreach (var t in tabs)
            {
                var doc = AddTab(t.Title, t.Sql, t.FilePath, t.IsDirty, autoTitle: t.AutoTitle ?? IsUntitledName(t.Title), databases: TabDatabaseMemory.From(t), id: t.Id,
                    connection: t.ConnectionId, restored: true);
                RestoreFileState(doc, t);
            }
            if (placeholder is not null)
            {
                placeholder.Attach(null);
                Documents.Remove(placeholder);
            }
            _untitled = Math.Max(_untitled, tabs.Count);
            SelectedDocument = Documents.FirstOrDefault();
        }
        catch
        {
            // A damaged tabs file must not block the application.
        }
    }

    /// <summary>The encoding and stamp a restored or reopened tab had; the next check against the disk then reloads a
    /// file that changed while the app was closed (or says so when the tab has edits).</summary>
    private static void RestoreFileState(QueryViewModel doc, QueryTabState t)
    {
        if (t.FilePath is null) return;
        // Tabs saved before encodings were kept: the encoding the file has on disk now.
        doc.FileEncoding = t.FileEncoding is null ? SqlFile.TryDetectEncoding(t.FilePath) : SqlFileEncodings.Normalize(t.FileEncoding);
        doc.FileStamp = t.FileWriteUtc is { } written && t.FileLength is { } length
            ? new FileStamp(DateTime.SpecifyKind(written, DateTimeKind.Utc), length)
            // Written before stamps were kept: take the file as it is now as the known state.
            : FileStamp.Of(t.FilePath);
    }

    /// <summary>On shutdown: nothing is committed implicitly.</summary>
    public async Task RollbackOpenTransactionsAsync()
    {
        foreach (var doc in Documents.Where(d => d.HasOpenTransaction).ToList())
            await doc.EndTransactionAsync(commit: false, reason: "application closed");
    }

    private static QueryTabState StateOf(QueryViewModel d)
    {
        var state = new QueryTabState
        {
            Title = d.Title, Sql = d.Sql, FilePath = d.FilePath, IsDirty = d.IsDirty, AutoTitle = d.AutoTitle, Id = d.Id, ConnectionId = d.ConnectionId,
            FileEncoding = d.FilePath is null ? null : SqlFileEncodings.Normalize(d.FileEncoding), FileWriteUtc = d.FileStamp?.LastWriteUtc, FileLength = d.FileStamp?.Length
        };
        d.Databases.SaveTo(state, d.CurrentDatabase);
        return state;
    }

    /// <returns>False when the tab list could not be written.</returns>
    public async Task<bool> SaveTabsAsync()
    {
        try
        {
            await _scripts.SaveTabsAsync(Documents.Select(StateOf));
            return true;
        }
        catch
        {
            // Best-effort: losing the tab list is not worth an error dialog.
            return false;
        }
    }
}
