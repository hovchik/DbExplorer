using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Controls;

namespace DbExplorer.Desktop.ViewModels;

public enum NavigatorNodeKind
{
    ConnectionFolder,
    Connection,
    Database,
    Schema,
    Folder,
    Object,
    DetailFolder,
    Detail,
    Message
}

/// <summary>
/// One row of the database navigator. Children are produced on first expand (<see cref="Loader"/>), so a server with
/// thousands of tables costs nothing until the user opens a folder; a placeholder child keeps the expander visible.
/// </summary>
public sealed partial class NavigatorNode : ObservableObject
{
    private Func<NavigatorNode, Task<IReadOnlyList<NavigatorNode>>>? _loader;
    private Task? _loadTask;

    public NavigatorNode(NavigatorNodeKind kind, string title, string key)
    {
        Kind = kind;
        Title = title;
        Key = key;
    }

    public NavigatorNodeKind Kind { get; }
    public string Title { get; }

    /// <summary>A path that survives a rebuild ("db:shop/schema:public/folder:Tables"), used to keep the user's
    /// expanded nodes open when the catalog is refreshed.</summary>
    public string Key { get; }

    /// <summary>The faded text after the title: a count, a column type, an index's columns. It can change on a row
    /// that stays in the tree (a filter match count).</summary>
    [ObservableProperty] private string? _detail;

    /// <summary>Can change on a row that stays in the tree, like <see cref="Detail"/> (it may name a count).</summary>
    [ObservableProperty] private string? _toolTip;

    /// <summary>Outline icon path (see <see cref="Icons"/>).</summary>
    public string Icon { get; init; } = Icons.Folder;

    /// <summary>Theme brush the icon is drawn with (the Objects tab's colour per object type); null for the text colour.</summary>
    public string? ColorKey { get; init; }

    /// <summary>Greyed out: a saved connection other than the connected one, or a "nothing here" message.</summary>
    public bool IsDimmed { get; init; }

    /// <summary>Bold: the connected connection and the connection's own database.</summary>
    public bool IsEmphasized { get; init; }

    public ConnectionProfile? Profile { get; init; }

    /// <summary>On a connection row: the open connection it browses (null while it is closed). Rows below it read
    /// theirs through <see cref="Parent"/>.</summary>
    public DbExplorer.Application.Sessions.DatabaseSession? Session { get; init; }

    /// <summary>On an open connection row while several are open: the colour its Query tabs are marked with.</summary>
    public Avalonia.Media.IBrush? MarkBrush { get; init; }
    public string? Database { get; init; }
    public DbObject? Object { get; init; }

    /// <summary>True when <see cref="Object"/> belongs to the session's own catalog, so the Objects tab and its
    /// actions (Get data, Modify, Drop…) can show it. Objects of other databases on the server only get scripts.</summary>
    public bool InSessionCatalog { get; init; }

    /// <summary>The catalog the node's children are read from.</summary>
    public MetadataSnapshot? Snapshot { get; init; }

    /// <summary>Column, index or key name under a table (the text Copy name copies).</summary>
    public string? DetailName { get; init; }

    public NavigatorNode? Parent { get; private set; }

    public ObservableCollection<NavigatorNode> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    public bool IsObject => Kind == NavigatorNodeKind.Object && Object is not null;

    public Func<NavigatorNode, Task<IReadOnlyList<NavigatorNode>>>? Loader
    {
        get => _loader;
        init
        {
            _loader = value;
            if (value is not null) Children.Add(new NavigatorNode(NavigatorNodeKind.Message, "Loading…", "loading") { Icon = Icons.Info, IsDimmed = true });
        }
    }

    public bool IsLoaded => _loader is null;

    public void SetChildren(IEnumerable<NavigatorNode> children)
    {
        _loader = null;
        Children.Clear();
        foreach (var child in children)
        {
            child.Parent = this;
            Children.Add(child);
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = LoadAsync();
    }

    /// <summary>Reads the children if they have not been read yet; a second caller while that read runs (the tree
    /// opening the node and a "reveal" walking through it) waits for the same read. Errors become a message row, so a
    /// database the login cannot open says so in the tree instead of failing silently.</summary>
    public Task LoadAsync()
    {
        if (_loader is not { } loader) return _loadTask ?? Task.CompletedTask;
        return _loadTask ??= LoadCoreAsync(loader);
    }

    private async Task LoadCoreAsync(Func<NavigatorNode, Task<IReadOnlyList<NavigatorNode>>> loader)
    {
        try
        {
            var children = await loader(this);
            SetChildren(children.Count > 0 ? children : [Message("Empty")]);
        }
        catch (Exception ex)
        {
            SetChildren([Message("Could not read: " + ex.Message)]);
        }
    }

    public static NavigatorNode Message(string text) =>
        new(NavigatorNodeKind.Message, text, "message:" + text) { Icon = Icons.Info, IsDimmed = true, ToolTip = text };

    /// <summary>Whether <paramref name="other"/> shows the same thing as this row (everything but the count and tooltip), so the
    /// tree can keep this row and what is open under it instead of replacing it.</summary>
    public bool SameAs(NavigatorNode other) =>
        Kind == other.Kind && Key == other.Key && Title == other.Title && Icon == other.Icon && ColorKey == other.ColorKey &&
        IsDimmed == other.IsDimmed && IsEmphasized == other.IsEmphasized &&
        ReferenceEquals(Profile, other.Profile) && ReferenceEquals(Session, other.Session) && ReferenceEquals(MarkBrush, other.MarkBrush) &&
        Database == other.Database && Equals(Object, other.Object) && InSessionCatalog == other.InSessionCatalog &&
        ReferenceEquals(Snapshot, other.Snapshot) && DetailName == other.DetailName;

    /// <summary>
    /// Makes <paramref name="target"/> show <paramref name="fresh"/> with as few changes as possible: a row that is
    /// <see cref="SameAs"/> one already there is kept (its count updated, its children merged the same way when the
    /// fresh row has them), others are inserted or removed. Rows the tree keeps keep their item containers, which is
    /// what makes typing in the filter cheap: one more letter usually changes a handful of rows, not hundreds.
    /// New rows are added top to bottom until <paramref name="budget"/> runs out; the rest is left in the budget for
    /// <see cref="MergeBudget.Continue"/>, so the tree never makes hundreds of rows in one go while the user types.
    /// </summary>
    public static void Merge(IList<NavigatorNode> target, IReadOnlyList<NavigatorNode> fresh, NavigatorNode? parent, MergeBudget budget)
    {
        var existing = new Dictionary<string, NavigatorNode>();
        foreach (var node in target) existing.TryAdd(node.Key, node);
        var wanted = new List<NavigatorNode>(fresh.Count);
        var keep = new HashSet<NavigatorNode>(ReferenceEqualityComparer.Instance);
        var mergeKept = new List<(NavigatorNode Old, NavigatorNode Fresh)>();
        foreach (var node in fresh)
        {
            if (existing.TryGetValue(node.Key, out var old) && old.SameAs(node) && keep.Add(old))
            {
                old.Detail = node.Detail;
                old.ToolTip = node.ToolTip;
                if (node.IsLoaded) mergeKept.Add((old, node));
                wanted.Add(old);
            }
            else
            {
                node.Parent = parent;
                wanted.Add(node);
            }
        }

        // Removing rows is cheap; do it all now so nothing stale stays on screen.
        if (keep.Count == 0) target.Clear();
        else
            for (var i = target.Count - 1; i >= 0; i--)
                if (!keep.Contains(target[i])) target.RemoveAt(i);
        foreach (var (old, node) in mergeKept)
        {
            if (old.IsLoaded) Merge(old.Children, node.Children.ToList(), old, budget);
            else
            {
                old._loader = null;
                old.Children.Clear();
                Place(old.Children, node.Children.ToList(), 0, budget);
            }
            if (node.IsExpanded && !old.IsExpanded) old.IsExpanded = true;
        }
        Place(target, wanted, 0, budget);
    }

    /// <summary>Puts <paramref name="wanted"/> in <paramref name="target"/> from <paramref name="start"/> on (kept rows
    /// are moved, new ones inserted), stopping when the budget is spent and leaving the rest to it.</summary>
    private static void Place(IList<NavigatorNode> target, List<NavigatorNode> wanted, int start, MergeBudget budget)
    {
        for (var i = start; i < wanted.Count; i++)
        {
            var node = wanted[i];
            if (i < target.Count && ReferenceEquals(target[i], node)) continue;
            var at = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], node)) { at = j; break; }
            if (at >= 0)
            {
                target.RemoveAt(at);
                target.Insert(i, node);
                continue;
            }
            if (budget.Left <= 0)
            {
                var from = i;
                budget.Defer(() => Place(target, wanted, from, budget));
                return;
            }
            budget.Left--;
            if (node.IsLoaded && node.IsExpanded && node.Children.Count > 0)
            {
                // An open row goes in empty and its rows follow under the same budget, top to bottom.
                var children = node.Children.ToList();
                node.Children.Clear();
                target.Insert(i, node);
                Place(node.Children, children, 0, budget);
            }
            else target.Insert(i, node);
        }
    }

    public IEnumerable<NavigatorNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants())
                yield return node;
    }

    public override string ToString() => Title;
}

/// <summary>How many new rows <see cref="NavigatorNode.Merge"/> may still add now, and what it left for later.</summary>
public sealed class MergeBudget(int rows)
{
    private List<Action> _deferred = [];

    public int Left { get; set; } = rows;

    public bool IsDone => _deferred.Count == 0;

    internal void Defer(Action rest) => _deferred.Add(rest);

    /// <summary>Adds up to <paramref name="rows"/> more of the rows left over, in tree order.</summary>
    public void Continue(int rows)
    {
        Left = rows;
        while (_deferred.Count > 0 && Left > 0)
        {
            var queue = _deferred;
            _deferred = [];
            var index = 0;
            while (index < queue.Count && Left > 0) queue[index++]();
            // What these deferred again comes first: it is higher in the tree than what is still waiting.
            _deferred.AddRange(queue.Skip(index));
        }
    }
}

