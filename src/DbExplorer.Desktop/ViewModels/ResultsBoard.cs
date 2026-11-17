namespace DbExplorer.Desktop.ViewModels;

/// <summary>Where a result on the Side by side board came from: shown above its grid, so two results from different
/// tabs, connections or databases tell apart at a glance.</summary>
/// <param name="Tab">The Query tab's title; null for a result from elsewhere (a table's data, a routine).</param>
/// <param name="Connection">The saved connection's name.</param>
/// <param name="Database">The database the query ran in, when known.</param>
/// <param name="RanAt">When the rows were read (local time).</param>
/// <param name="Query">The SQL behind the rows, when known; shown in the tooltip.</param>
public sealed record BoardSource(string? Tab, string? Connection, string? Database, DateTime RanAt, string? Query = null);

/// <summary>One result on the board: a read-only snapshot of the rows and where they came from.</summary>
public sealed class BoardResult(ResultSetView view, BoardSource source)
{
    public ResultSetView View { get; } = view;

    public BoardSource Source { get; } = source;

    /// <summary>The result the snapshot was taken from, so the same one is not added twice.</summary>
    internal ResultSetView? Origin { get; init; }

    /// <summary>The bold line: the Query tab's title, then the result's own title when it says more ("Result 2").</summary>
    public string Title
    {
        get
        {
            var tab = Source.Tab?.Trim();
            if (string.IsNullOrEmpty(tab)) return View.Title;
            return string.Equals(tab, View.Title, StringComparison.Ordinal) ? tab : $"{tab} · {View.Title}";
        }
    }

    /// <summary>The line under the title: connection · database · time · rows.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Source.Connection)) parts.Add(Source.Connection!);
            if (!string.IsNullOrWhiteSpace(Source.Database) &&
                !string.Equals(Source.Database, Source.Connection, StringComparison.OrdinalIgnoreCase)) parts.Add(Source.Database!);
            parts.Add(Source.RanAt.ToString("HH:mm:ss"));
            parts.Add(View.IsTruncated ? $"first {View.Rows.Count:N0} of {View.TotalRowsText} rows" : $"{View.Rows.Count:N0} row(s)");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The header's tooltip: everything known about the result, the SQL last.</summary>
    public string ToolTip
    {
        get
        {
            var lines = new List<string> { Title, Detail };
            if (!string.IsNullOrWhiteSpace(View.Connection)) lines.Add(View.Connection!);
            if (!string.IsNullOrWhiteSpace(Source.Query))
            {
                var sql = Source.Query!.Trim();
                lines.Add("");
                lines.Add(sql.Length > 600 ? sql[..600] + "…" : sql);
            }
            return string.Join("\n", lines);
        }
    }
}

/// <summary>
/// The results the user put on the Side by side board, in the order they are shown (left to right, or top to bottom).
/// Each is a read-only snapshot: the board never runs anything, and running the query again in its tab leaves the
/// board alone. Nothing is merged or compared: the results are simply shown next to each other.
/// </summary>
public sealed class ResultsBoard
{
    /// <summary>More than this many grids next to each other are too narrow to read, and each holds its rows in memory.</summary>
    public const int MaxResults = 6;

    private readonly List<BoardResult> _items = [];

    public IReadOnlyList<BoardResult> Items => _items;

    public int Count => _items.Count;

    /// <summary>Why <paramref name="view"/> cannot go on the board, or null when it can.</summary>
    public string? WhyNot(ResultSetView view)
    {
        if (!view.IsGrid) return "Only result grids can be shown side by side, not plans.";
        if (view.Rows.Any(r => r.HasChanges)) return "Commit or discard the changes in this result before showing it side by side.";
        if (_items.Any(i => ReferenceEquals(i.Origin, view)))
            return "This result is already on the Side by side board.";
        if (_items.Count >= MaxResults) return $"At most {MaxResults} results fit side by side. Remove one from the board first.";
        return null;
    }

    /// <summary>Adds a read-only snapshot of <paramref name="view"/> at the end; null when it cannot be added (see
    /// <see cref="WhyNot"/>).</summary>
    public BoardResult? Add(ResultSetView view, BoardSource source)
    {
        if (WhyNot(view) is not null) return null;
        var snapshot = ResultPins.Snapshot(view, source.Query,
            "Results on the Side by side board are snapshots: they are not run again. Use the Query tab to fetch more or export everything.");
        var item = new BoardResult(snapshot, source) { Origin = view };
        _items.Add(item);
        return item;
    }

    public bool Remove(BoardResult item)
    {
        var i = IndexOf(item);
        if (i < 0) return false;
        _items.RemoveAt(i);
        return true;
    }

    /// <summary>Moves <paramref name="item"/> by <paramref name="delta"/> places (-1 left/up, +1 right/down); false at an end.</summary>
    public bool Move(BoardResult item, int delta)
    {
        var i = IndexOf(item);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return false;
        _items.RemoveAt(i);
        _items.Insert(j, item);
        return true;
    }

    public bool CanMove(BoardResult item, int delta)
    {
        var i = IndexOf(item);
        return i >= 0 && i + delta >= 0 && i + delta < _items.Count;
    }

    public void Clear() => _items.Clear();

    public int IndexOf(BoardResult item)
    {
        for (var i = 0; i < _items.Count; i++)
            if (ReferenceEquals(_items[i], item)) return i;
        return -1;
    }
}
