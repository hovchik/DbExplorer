using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A result that can be shown next to this tab's own: another tab's (or this tab's) result, live.</summary>
/// <param name="Tab">The Query tab the result belongs to.</param>
/// <param name="Pinned">A pinned result of that tab; null for one of its latest run's results.</param>
/// <param name="RunIndex">Which of the latest run's results (0 = the first), when not pinned.</param>
public sealed record CompareChoice(QueryViewModel Tab, ResultSetView? Pinned, int RunIndex)
{
    /// <summary>The result this choice shows now: a new run of the tab replaces it with the new rows.</summary>
    public ResultSetView? Current =>
        Pinned is not null
            ? Tab.Pins.Contains(Pinned) ? Pinned : null
            : RunIndex < Tab.ResultSets.Count && Tab.ResultSets[RunIndex].IsGrid ? Tab.ResultSets[RunIndex] : null;

    /// <summary>"Accounts top 10 · Result 1", or just the tab's title when that is its only result.</summary>
    public string Title
    {
        get
        {
            var result = Current;
            if (result is null) return Tab.Title;
            var only = Pinned is null && Tab.ResultSets.Count == 1 && Tab.Pins.Count == 0;
            return only ? Tab.Title : $"{Tab.Title} · {result.Header}";
        }
    }

    /// <summary>Connection · database · rows · when.</summary>
    public string Detail => Tab.DescribeResult(Current);

    public bool SameChoice(CompareChoice? other) =>
        other is not null && ReferenceEquals(Tab, other.Tab) && ReferenceEquals(Pinned, other.Pinned) && (Pinned is not null || RunIndex == other.RunIndex);
}

/// <summary>
/// Compare: a result from another Query tab (or another result of this tab) shown in a pane next to this tab's own
/// results, so the two can be compared by eye. The pane follows its tab: when that query runs again, the pane shows the
/// new rows. Nothing is merged or run from here.
/// </summary>
public partial class QueryViewModel
{
    /// <summary>When the results on screen arrived (local time).</summary>
    private DateTime? _resultsAt;

    /// <summary>All open Query tabs (set by the workspace), for the list of results to compare with.</summary>
    public Func<IReadOnlyList<QueryViewModel>>? AllTabs { get; set; }

    /// <summary>The result shown next to this tab's own; null when the pane is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompare))]
    [NotifyPropertyChangedFor(nameof(CompareResult))]
    [NotifyPropertyChangedFor(nameof(CompareTitle))]
    [NotifyPropertyChangedFor(nameof(CompareDetail))]
    [NotifyPropertyChangedFor(nameof(CompareEmptyText))]
    [NotifyPropertyChangedFor(nameof(HasCompareResult))]
    private CompareChoice? _compared;

    /// <summary>The pane sits below the results instead of beside them (for wide results).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareLayoutTip))]
    private bool _compareBelow;

    /// <summary>Scrolling one of the two grids scrolls the other to the same row.</summary>
    [ObservableProperty] private bool _compareScrollTogether = true;

    public bool HasCompare => Compared is not null;

    public ResultSetView? CompareResult => Compared?.Current;

    public bool HasCompareResult => CompareResult is not null;

    public string CompareTitle => Compared?.Title ?? "";

    public string CompareDetail => Compared?.Detail ?? "";

    public string CompareEmptyText => Compared is null ? "" : $"{Compared.Tab.Title} has no such result now. Run its query, or pick another result.";

    public string CompareLayoutTip => CompareBelow ? "Show it beside the results" : "Show it below the results (for wide results)";

    /// <summary>The results that can be shown next to this tab's: every other tab's, then this tab's own other ones.</summary>
    public IReadOnlyList<CompareChoice> CompareChoices()
    {
        var tabs = AllTabs?.Invoke() ?? [this];
        var choices = new List<CompareChoice>();
        foreach (var tab in tabs.Where(t => !ReferenceEquals(t, this)).Append(this))
        {
            foreach (var pinned in tab.Pins.Items.Where(p => p.IsGrid)) choices.Add(new CompareChoice(tab, pinned, 0));
            for (var i = 0; i < tab.ResultSets.Count; i++)
                if (tab.ResultSets[i].IsGrid) choices.Add(new CompareChoice(tab, null, i));
        }
        // This tab's result on screen is already there.
        return choices.Where(c => !ReferenceEquals(c.Current, SelectedResult) || !ReferenceEquals(c.Tab, this)).ToList();
    }

    [RelayCommand]
    private void CompareWith(CompareChoice? choice)
    {
        if (choice?.Current is null) return;
        Compared = choice;
    }

    [RelayCommand]
    private void CloseCompare() => Compared = null;

    [RelayCommand]
    private void ToggleCompareLayout() => CompareBelow = !CompareBelow;

    partial void OnComparedChanged(CompareChoice? oldValue, CompareChoice? newValue)
    {
        if (oldValue is not null && !ReferenceEquals(oldValue.Tab, newValue?.Tab)) oldValue.Tab.PropertyChanged -= OnCompareTabChanged;
        if (newValue is not null && !ReferenceEquals(oldValue?.Tab, newValue.Tab)) newValue.Tab.PropertyChanged += OnCompareTabChanged;
    }

    /// <summary>The compared tab ran again, was renamed or pinned something: the pane shows what it has now.</summary>
    private void OnCompareTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ShownResults) or nameof(Title) or nameof(CurrentDatabase))) return;
        OnPropertyChanged(nameof(CompareResult));
        OnPropertyChanged(nameof(HasCompareResult));
        OnPropertyChanged(nameof(CompareTitle));
        OnPropertyChanged(nameof(CompareDetail));
    }

    /// <summary>A tab was closed: a pane showing its result closes too.</summary>
    public void OnTabClosed(QueryViewModel closed)
    {
        if (ReferenceEquals(Compared?.Tab, closed)) Compared = null;
    }

    /// <summary>"Shop · uirender_shop · 10 row(s) · 11:12:37" for a result of this tab.</summary>
    public string DescribeResult(ResultSetView? view)
    {
        var parts = new List<string>();
        var profile = _session?.Profile;
        if (profile is not null) parts.Add(profile.DisplayName);
        if ((NullIfEmpty(CurrentDatabase) ?? NullIfEmpty(profile?.Database)) is { } database &&
            !string.Equals(database, profile?.DisplayName, StringComparison.OrdinalIgnoreCase)) parts.Add(database);
        if (view is not null)
        {
            parts.Add(view.IsTruncated ? $"first {view.Rows.Count:N0} of {view.TotalRowsText} rows" : $"{view.Rows.Count:N0} row(s)");
            if ((view.PinnedAt ?? _resultsAt) is { } at) parts.Add(at.ToString("HH:mm:ss"));
        }
        return string.Join(" · ", parts);
    }
}
