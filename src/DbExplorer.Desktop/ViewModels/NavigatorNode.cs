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

    /// <summary>The faded text after the title: a count, a column type, an index's columns.</summary>
    public string? Detail { get; init; }

    public string? ToolTip { get; init; }

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

    public IEnumerable<NavigatorNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants())
                yield return node;
    }

    public override string ToString() => Title;
}
