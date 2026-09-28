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
    }

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

    public QueryViewModel NewTab() => AddTab($"Query {++_untitled}", "", filePath: null, dirty: false);

    [RelayCommand]
    private void NewQuery() => NewTab();

    private QueryViewModel AddTab(string title, string sql, string? filePath, bool dirty)
    {
        var doc = _createDocument();
        doc.Title = title;
        doc.FilePath = filePath;
        doc.SetText(sql, markClean: !dirty);
        doc.Attach(_session);
        Documents.Add(doc);
        SelectedDocument = doc;
        return doc;
    }

    /// <summary>Opens <paramref name="sql"/> in a new tab (history, "script as", …).</summary>
    public void OpenInNewTab(string sql, string? title = null) => AddTab(title ?? $"Query {++_untitled}", sql, null, dirty: false);

    [RelayCommand]
    private async Task CloseTabAsync(QueryViewModel? doc)
    {
        doc ??= SelectedDocument;
        if (doc is null) return;
        if (doc.IsDirty && doc.FilePath is not null &&
            !await _dialogs.ConfirmAsync($"{doc.Title} has unsaved changes. Close it anyway?", "Close without saving"))
            return;

        var index = Documents.IndexOf(doc);
        doc.Attach(null);
        Documents.Remove(doc);
        if (Documents.Count == 0) NewTab();
        else if (SelectedDocument == doc || SelectedDocument is null) SelectedDocument = Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        await SaveTabsAsync();
    }

    /// <summary>Opens a file in a new tab, or reuses the current tab when it is an empty, untouched scratch tab.</summary>
    public async Task OpenFileAsync(string path, string name)
    {
        var sql = await File.ReadAllTextAsync(path);
        if (SelectedDocument is { FilePath: null } current && string.IsNullOrWhiteSpace(current.Sql))
        {
            current.Title = name;
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
        if (item is not null) OpenInNewTab(item.Entry.Sql, "History " + item.Entry.RanAt.LocalDateTime.ToString("t", CultureInfo.CurrentCulture));
    }

    /// <summary>Restores the tabs of the previous run (best-effort).</summary>
    public async Task RestoreTabsAsync()
    {
        try
        {
            var tabs = await _scripts.LoadTabsAsync();
            if (tabs.Count == 0) return;
            var placeholder = Documents.Count == 1 && string.IsNullOrEmpty(Documents[0].Sql) ? Documents[0] : null;
            foreach (var t in tabs) AddTab(t.Title, t.Sql, t.FilePath, t.IsDirty);
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

    public async Task SaveTabsAsync()
    {
        try
        {
            await _scripts.SaveTabsAsync(Documents.Select(d => new QueryTabState
            {
                Title = d.Title, Sql = d.Sql, FilePath = d.FilePath, IsDirty = d.IsDirty
            }));
        }
        catch
        {
            // Best-effort: losing the tab list is not worth an error dialog.
        }
    }
}
