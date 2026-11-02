using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One kept version in the local history list.</summary>
public sealed record LocalHistoryEntry(LocalHistoryDocument Script, LocalHistoryVersion Version, LocalHistoryVersion? Previous, string When)
{
    public string Reason => LocalHistoryDiff.Label(Version.Reason);
    public string ScriptTitle => Version.Title ?? Script.Title;
    public string Preview => LocalHistoryDiff.Preview(Version.Text);
    public string Size
    {
        get
        {
            var lines = Version.Text.Count(c => c == '\n') + 1;
            return lines == 1 ? "1 line" : $"{lines:N0} lines";
        }
    }
}

/// <summary>
/// The local history window of one query tab: every version its runs, saves, opens and closes kept, a diff of the
/// selected one against the tab's current text (or the version before it), and Restore, which puts it back into the
/// tab as one undoable edit. "All scripts" also lists closed tabs and other files.
/// </summary>
public partial class LocalHistoryViewModel : ViewModelBase
{
    private readonly LocalHistoryStore _store;
    private readonly QueryWorkspaceViewModel _workspace;
    private readonly QueryViewModel _tab;
    private readonly IDialogService _dialogs;
    private IReadOnlyList<LocalHistoryEntry> _all = [];

    public LocalHistoryViewModel(LocalHistoryStore store, QueryWorkspaceViewModel workspace, QueryViewModel tab, IDialogService dialogs)
    {
        _store = store;
        _workspace = workspace;
        _tab = tab;
        _dialogs = dialogs;
    }

    public string Title => $"Local history · {_tab.Title}";
    public string RestoreLabel => $"Restore into {Trim(_tab.Title, 28)}";

    /// <summary>Off: only this tab (or its file). On: every script with kept versions, closed tabs included.</summary>
    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private IReadOnlyList<LocalHistoryEntry> _entries = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(OpenInNewTabCommand), nameof(CopyCommand))]
    private LocalHistoryEntry? _selectedEntry;

    /// <summary>Off: compare with the tab's current text (what Restore would change). On: with the version before it.</summary>
    [ObservableProperty] private bool _compareWithPrevious;
    [ObservableProperty] private bool _onlyChanges = true;
    [ObservableProperty] private IReadOnlyList<LocalHistoryDiffRow> _diffRows = [];
    [ObservableProperty] private string _diffSummary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _emptyText = "";

    public bool IsEmpty => Entries.Count == 0;
    public bool HasSelection => SelectedEntry is not null;

    public string Legend => CompareWithPrevious
        ? "− only in the previous version · + only in this version"
        : "− only in the current text · + only in this version";

    partial void OnShowAllChanged(bool value) => _ = LoadAsync();
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedEntryChanged(LocalHistoryEntry? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        UpdateDiff();
    }
    partial void OnCompareWithPreviousChanged(bool value)
    {
        OnPropertyChanged(nameof(Legend));
        UpdateDiff();
    }
    partial void OnOnlyChangesChanged(bool value) => UpdateDiff();
    partial void OnEntriesChanged(IReadOnlyList<LocalHistoryEntry> value) => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand]
    public async Task LoadAsync()
    {
        IReadOnlyList<LocalHistoryDocument> scripts;
        try
        {
            if (ShowAll) scripts = await _store.LoadAllAsync();
            else
            {
                var own = await _store.LoadAsync(LocalHistoryStore.KeyFor(_tab.Id, _tab.FilePath));
                scripts = own is null ? [] : [own];
            }
        }
        catch (Exception ex)
        {
            scripts = [];
            Status = "Could not read the local history: " + ex.Message;
        }

        var now = _store.Now();
        _all = scripts
            .SelectMany(s => s.Versions.Select((v, i) => new LocalHistoryEntry(
                s, v, i + 1 < s.Versions.Count ? s.Versions[i + 1] : null, LocalHistoryDiff.When(v.At, now))))
            .OrderByDescending(e => e.Version.At)
            .ToList();
        EmptyText = ShowAll
            ? "Nothing kept yet. Running, saving, opening or closing a query tab keeps a version of its text here."
            : "Nothing kept for this tab yet. Each run, save or close keeps a version of its text. Tick \"All scripts\" to see closed tabs and other files.";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        var selected = SelectedEntry;
        Entries = f.Length == 0
            ? _all
            : _all.Where(e => e.Version.Text.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                              e.ScriptTitle.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        SelectedEntry = Entries.FirstOrDefault(e => selected is not null && Same(e, selected.Version)) ?? Entries.FirstOrDefault();
    }

    /// <summary>Recomputed when the tab's text may have changed (after a restore, or when the window is activated).</summary>
    public void UpdateDiff()
    {
        if (SelectedEntry is not { } entry)
        {
            DiffRows = [];
            DiffSummary = "";
            return;
        }
        var baseText = CompareWithPrevious ? entry.Previous?.Text ?? "" : _tab.Sql;
        var (rows, added, removed) = LocalHistoryDiff.Build(baseText, entry.Version.Text, OnlyChanges);
        if (rows is null)
        {
            DiffRows = [];
            DiffSummary = "Too large to compare here. Open it in a new tab to see it.";
            return;
        }
        DiffRows = rows;
        var against = CompareWithPrevious
            ? entry.Previous is null ? "the first version kept" : "the previous version"
            : "the current text";
        DiffSummary = added == 0 && removed == 0
            ? $"Same as {against}."
            : $"+{added:N0} −{removed:N0} line(s) compared with {against}";
    }

    /// <summary>Puts the version back into the tab as one edit (Ctrl+Z undoes it); the text it replaces is kept first.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RestoreAsync()
    {
        if (SelectedEntry is not { } entry) return;
        if (entry.Version.Text == _tab.Sql)
        {
            Status = "The tab already has this text.";
            return;
        }
        await _workspace.RecordAsync(_tab, LocalHistoryReason.BeforeRestore);
        _tab.SetText(entry.Version.Text, markClean: false);
        _workspace.SelectedDocument = _tab;
        Status = $"Restored the version from {entry.When} into {_tab.Title}. Ctrl+Z in the editor undoes it; the text it replaced is kept as \"Before restore\".";
        var keep = entry.Version;
        await LoadAsync();
        SelectedEntry = Entries.FirstOrDefault(e => Same(e, keep)) ?? SelectedEntry;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenInNewTab()
    {
        if (SelectedEntry is not { } entry) return;
        _workspace.OpenInNewTab(entry.Version.Text, $"{Trim(entry.ScriptTitle, 24)} ({entry.When})");
        Status = "Opened in a new tab.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyAsync()
    {
        if (SelectedEntry is not { } entry) return;
        await _dialogs.CopyTextAsync(entry.Version.Text);
        Status = "Copied.";
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Delete every kept version of every script, closed tabs included? Your open tabs and .sql files are not touched.",
                "Clear local history"))
            return;
        try
        {
            await _store.ClearAsync();
            Status = "Local history cleared.";
        }
        catch (Exception ex)
        {
            Status = "Could not clear the local history: " + ex.Message;
        }
        await LoadAsync();
    }

    private static bool Same(LocalHistoryEntry e, LocalHistoryVersion v) => e.Version.At == v.At && e.Version.Text == v.Text;

    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
