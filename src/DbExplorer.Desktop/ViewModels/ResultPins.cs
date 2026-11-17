namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// The results a Query tab's user pinned. A pinned result is a read-only snapshot that stays when the query runs again,
/// so two runs can be compared side by side. Pinned results are shown first (left), like pinned browser tabs, and the
/// latest run's results follow.
/// </summary>
public sealed class ResultPins
{
    /// <summary>Each pin holds its rows in memory, so there is a limit.</summary>
    public const int MaxPinned = 20;

    private readonly List<ResultSetView> _items = [];

    public IReadOnlyList<ResultSetView> Items => _items;

    public int Count => _items.Count;

    public bool Contains(ResultSetView? view) => view is not null && _items.Any(p => ReferenceEquals(p, view));

    /// <summary>Pinned results first, then the run's results.</summary>
    public IReadOnlyList<ResultSetView> Arrange(IReadOnlyList<ResultSetView> runResults) => [.. _items, .. runResults];

    /// <summary>Why <paramref name="view"/> cannot be pinned, or null when it can.</summary>
    public string? WhyNot(ResultSetView view)
    {
        if (view.IsPinned) return "This result is already pinned.";
        if (view.Rows.Any(r => r.HasChanges)) return "Commit or discard the changes in this result before pinning it.";
        if (_items.Count >= MaxPinned) return $"At most {MaxPinned} results can be pinned in one tab. Close one first.";
        return null;
    }

    /// <summary>Pins a copy of <paramref name="view"/> (read-only, see <see cref="Snapshot"/>) and returns it; null when it
    /// cannot be pinned (see <see cref="WhyNot"/>).</summary>
    public ResultSetView? Pin(ResultSetView view, string? query, DateTime at)
    {
        if (WhyNot(view) is not null) return null;
        var pinned = Snapshot(view, query,
            "A pinned result is a snapshot: it is not run again. Use the latest run's result to fetch more or export everything.")
            with { IsPinned = true, PinnedAt = at };
        _items.Add(pinned);
        return pinned;
    }

    /// <summary>A read-only copy of <paramref name="view"/> that later runs, edits and connection changes leave alone:
    /// saving edits, following foreign keys and fetching more are off (it may later be shown for another connection), and
    /// the rows are copies, so editing the original grid does not change it. Used by pins and the Side by side board.</summary>
    /// <param name="cutOffReason">Shown above the grid when the result was cut off at the row limit.</param>
    public static ResultSetView Snapshot(ResultSetView view, string? query, string cutOffReason) => view with
    {
        Query = query ?? view.Query,
        CommitEdits = null,
        OpenReference = null,
        Rerun = null,
        RerunUnavailable = view.IsTruncated ? cutOffReason : null,
        Rows = view.Rows.Where(r => !r.IsDeleted).Select(r => new ResultRow(r.Values)).ToArray()
    };

    /// <summary>Gives a pinned result a new title; returns the renamed result (null when it is not pinned here).</summary>
    public ResultSetView? Rename(ResultSetView view, string title)
    {
        var i = IndexOf(view);
        if (i < 0 || string.IsNullOrWhiteSpace(title)) return null;
        var renamed = view with { Title = title.Trim() };
        _items[i] = renamed;
        return renamed;
    }

    public bool Remove(ResultSetView view)
    {
        var i = IndexOf(view);
        if (i < 0) return false;
        _items.RemoveAt(i);
        return true;
    }

    public void Clear() => _items.Clear();

    /// <summary>
    /// The tab to select after the shown results changed: <paramref name="preferred"/> when it is shown; with
    /// <paramref name="keepPosition"/> (auto refresh, pins changed) the selected pinned tab or the same position among the
    /// run's results; otherwise the run's first result (a new run shows its own results), otherwise the first pinned one.
    /// </summary>
    public ResultSetView? ChooseSelection(ResultSetView? preferred, ResultSetView? previous,
        IReadOnlyList<ResultSetView> oldRunResults, IReadOnlyList<ResultSetView> newRunResults, bool keepPosition)
    {
        if (preferred is not null && (Contains(preferred) || newRunResults.Any(r => ReferenceEquals(r, preferred)))) return preferred;
        if (keepPosition && previous is not null)
        {
            if (Contains(previous)) return previous;
            var i = IndexIn(oldRunResults, previous);
            if (i >= 0 && i < newRunResults.Count) return newRunResults[i];
        }
        return newRunResults.FirstOrDefault() ?? _items.FirstOrDefault();
    }

    private int IndexOf(ResultSetView view) => IndexIn(_items, view);

    private static int IndexIn(IReadOnlyList<ResultSetView> list, ResultSetView view)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], view)) return i;
        return -1;
    }
}
