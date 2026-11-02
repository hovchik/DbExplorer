namespace DbExplorer.Application.Query;

/// <summary>A query tab as it was when it was closed, so Reopen closed tab (Ctrl+Shift+T) can bring it back.</summary>
public sealed class ClosedQueryTab
{
    /// <summary>Title, text, file, dirty flag, databases and id of the tab. The id stays the same on reopen, so the
    /// tab keeps its local history.</summary>
    public QueryTabState Tab { get; set; } = new();

    /// <summary>Where the tab stood in the strip; it goes back there (or to the end when there are fewer tabs now).</summary>
    public int Index { get; set; }

    public DateTimeOffset ClosedAt { get; set; }
}

/// <summary>
/// The recently closed query tabs, newest first, like a browser's "reopen closed tab". Pure list logic: the
/// workspace saves it through <see cref="ScriptStore"/> so it survives a restart.
/// </summary>
public sealed class ClosedTabs
{
    public const int MaxTabs = 20;

    private readonly List<ClosedQueryTab> _items = [];

    public ClosedTabs(IEnumerable<ClosedQueryTab>? saved = null)
    {
        if (saved is null) return;
        foreach (var item in saved.Where(i => i?.Tab is not null && IsWorthKeeping(i.Tab)))
        {
            if (_items.Count >= MaxTabs) break;
            if (item.Tab.Id is { } id && _items.Any(i => i.Tab.Id == id)) continue;
            _items.Add(item);
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ClosedQueryTab> Items => _items;

    public int Count => _items.Count;

    /// <summary>An empty scratch tab is not worth reopening; a tab with text or a file is.</summary>
    public static bool IsWorthKeeping(QueryTabState tab) => tab.FilePath is not null || !string.IsNullOrWhiteSpace(tab.Sql);

    /// <summary>Remembers a closed tab; false when it was not worth keeping. A tab closed again replaces its older entry,
    /// and the oldest entries drop off past <see cref="MaxTabs"/>.</summary>
    public bool Add(QueryTabState tab, int index, DateTimeOffset closedAt)
    {
        if (!IsWorthKeeping(tab)) return false;
        if (tab.Id is { } id) _items.RemoveAll(i => i.Tab.Id == id);
        if (tab.FilePath is { } path) _items.RemoveAll(i => SamePath(i.Tab.FilePath, path));
        _items.Insert(0, new ClosedQueryTab { Tab = tab, Index = Math.Max(0, index), ClosedAt = closedAt });
        if (_items.Count > MaxTabs) _items.RemoveRange(MaxTabs, _items.Count - MaxTabs);
        return true;
    }

    /// <summary>Takes the most recently closed tab off the list; null when there is none.</summary>
    public ClosedQueryTab? TakeLatest() => _items.Count == 0 ? null : TakeAt(0);

    /// <summary>Takes <paramref name="item"/> off the list; null when it is no longer there.</summary>
    public ClosedQueryTab? Take(ClosedQueryTab item)
    {
        var at = _items.IndexOf(item);
        return at < 0 ? null : TakeAt(at);
    }

    public void Clear() => _items.Clear();

    private ClosedQueryTab TakeAt(int at)
    {
        var item = _items[at];
        _items.RemoveAt(at);
        return item;
    }

    /// <summary>Where a reopened tab goes among <paramref name="count"/> open tabs.</summary>
    public static int InsertIndex(int closedIndex, int count) => Math.Clamp(closedIndex, 0, Math.Max(0, count));

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
