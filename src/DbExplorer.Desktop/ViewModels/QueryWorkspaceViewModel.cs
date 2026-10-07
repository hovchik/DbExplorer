using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
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

/// <summary>
/// The Query tab as a small SQL IDE: several query tabs (each with its own editor, undo history and results),
/// .sql files opened and saved, tabs restored on the next start, and the run history.
/// </summary>
public partial class QueryWorkspaceViewModel : ViewModelBase, ISessionAware
{
    private readonly Func<QueryViewModel> _createDocument;
    private readonly ScriptStore _scripts;
    private readonly IDialogService _dialogs;
    private DatabaseSession? _session;
    private int _untitled;

    public QueryWorkspaceViewModel(Func<QueryViewModel> createDocument, ScriptStore scripts, IDialogService dialogs)
    {
        _createDocument = createDocument;
        _scripts = scripts;
        _dialogs = dialogs;
        NewTab();

        // Unsaved scratch text survives a crash: the tab list is written every 30 seconds while something changed.
        _autosave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosave.Tick += async (_, _) =>
        {
            var snapshot = string.Join("\u0001", Documents.Select(d => d.Title + "\u0002" + d.Sql + "\u0002" + d.CurrentDatabase));
            if (snapshot == _lastSaved) return;
            _lastSaved = snapshot;
            await SaveTabsAsync();
        };
        _autosave.Start();
    }

    private readonly Avalonia.Threading.DispatcherTimer _autosave;
    private string _lastSaved = "";

    public ObservableCollection<QueryViewModel> Documents { get; } = [];

    [ObservableProperty] private QueryViewModel? _selectedDocument;
    [ObservableProperty] private IReadOnlyList<HistoryItem> _history = [];
    [ObservableProperty] private string _historyFilter = "";
    private IReadOnlyList<HistoryItem> _allHistory = [];

    public void Attach(DatabaseSession? session)
    {
        _session = session;
        foreach (var d in Documents) d.Attach(session);
    }

    public QueryViewModel NewTab() => AddTab($"Query {++_untitled}", "", filePath: null, dirty: false, autoTitle: true);

    [RelayCommand]
    private void NewQuery() => NewTab();

    /// <param name="database">Database the tab starts in; by default the ones of the tab it is opened from.</param>
    /// <param name="autoTitle">Whether the tab is renamed after the queries it runs.</param>
    /// <param name="databases">The databases of a restored tab, in place of <paramref name="database"/>.</param>
    private QueryViewModel AddTab(string title, string sql, string? filePath, bool dirty, string? database = null, bool autoTitle = false,
        TabDatabaseMemory? databases = null)
    {
        var doc = _createDocument();
        doc.Title = title;
        doc.AutoTitle = autoTitle && filePath is null;
        doc.FilePath = filePath;
        doc.SetText(sql, markClean: !dirty);
        doc.Databases = databases
                        ?? (database is not null ? new TabDatabaseMemory { Pending = database } : SelectedDocument?.Databases.Clone())
                        ?? new TabDatabaseMemory();
        doc.Attach(_session);
        doc.OpenRequested += (docTitle, text) => OpenInNewTab(text, docTitle);
        doc.OpenAndRunRequested += (docTitle, text, db) =>
        {
            var opened = AddTab(docTitle, text, null, dirty: false, NullIfEmpty(db));
            if (opened.ExecuteCommand.CanExecute(null)) opened.ExecuteCommand.Execute(null);
        };
        Documents.Add(doc);
        SelectedDocument = doc;
        return doc;
    }

    /// <summary>Opens <paramref name="sql"/> in a new tab (history, "script as", …).</summary>
    public void OpenInNewTab(string sql, string? title = null, string? database = null, bool autoTitle = false) =>
        AddTab(title ?? $"Query {++_untitled}", sql, null, dirty: false, NullIfEmpty(database), autoTitle || title is null);

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
        AddTab(doc.Title + " (copy)", doc.Sql, filePath: null, dirty: false, databases: doc.Databases.Clone());
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
        if (doc.HasOpenTransaction &&
            !await _dialogs.ConfirmAsync($"{doc.Title} has an open transaction. Closing rolls it back. Continue?", "Roll back and close"))
            return false;

        var index = Documents.IndexOf(doc);
        await doc.EndTransactionAsync(commit: false, reason: "tab closed");
        doc.Attach(null);
        Documents.Remove(doc);
        if (Documents.Count == 0) NewTab();
        else if (SelectedDocument == doc || SelectedDocument is null) SelectedDocument = Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        await SaveTabsAsync();
        return true;
    }

    /// <summary>Opens a file in a new tab, or reuses the current tab when it is an empty, untouched scratch tab.</summary>
    public async Task OpenFileAsync(string path, string name)
    {
        var sql = await File.ReadAllTextAsync(path);
        if (SelectedDocument is { FilePath: null } current && string.IsNullOrWhiteSpace(current.Sql))
        {
            current.Title = name;
            current.AutoTitle = false;
            current.FilePath = path;
            current.SetText(sql, markClean: true);
        }
        else AddTab(name, sql, path, dirty: false);
    }

    public async Task SaveAsync(QueryViewModel doc, string path, string name)
    {
        await File.WriteAllTextAsync(path, doc.Sql);
        doc.FilePath = path;
        doc.Title = name;
        doc.AutoTitle = false;
        doc.IsDirty = false;
        await SaveTabsAsync();
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
        try
        {
            var tabs = await _scripts.LoadTabsAsync();
            if (tabs.Count == 0) return;
            var placeholder = Documents.Count == 1 && string.IsNullOrEmpty(Documents[0].Sql) ? Documents[0] : null;
            foreach (var t in tabs)
                AddTab(t.Title, t.Sql, t.FilePath, t.IsDirty, autoTitle: t.AutoTitle ?? IsUntitledName(t.Title), databases: TabDatabaseMemory.From(t));
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

    /// <summary>On shutdown: nothing is committed implicitly.</summary>
    public async Task RollbackOpenTransactionsAsync()
    {
        foreach (var doc in Documents.Where(d => d.HasOpenTransaction).ToList())
            await doc.EndTransactionAsync(commit: false, reason: "application closed");
    }

    public async Task SaveTabsAsync()
    {
        try
        {
            await _scripts.SaveTabsAsync(Documents.Select(d =>
            {
                var state = new QueryTabState { Title = d.Title, Sql = d.Sql, FilePath = d.FilePath, IsDirty = d.IsDirty, AutoTitle = d.AutoTitle };
                d.Databases.SaveTo(state, d.CurrentDatabase);
                return state;
            }));
        }
        catch
        {
            // Best-effort: losing the tab list is not worth an error dialog.
        }
    }
}
