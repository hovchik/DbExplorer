using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Controls;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>What the main window does for a navigator object, through the Objects tab's own commands.</summary>
public enum NavigatorObjectAction
{
    Open,
    GetData,
    Execute,
    Debug,
    ShowInDiagram,
    Modify,
    DropTable,
    FindUsages,
    ImportData,
    GenerateTestData
}

public enum NavigatorConnectionAction
{
    Connect,
    Disconnect,
    /// <summary>Make an open connection the active one (the main tabs show it).</summary>
    Activate,
    Edit,
    Duplicate,
    Delete,
    RefreshMetadata
}

/// <summary>
/// The database navigator on the left of the main window (after DBeaver's Database Navigator): saved connections,
/// then for every open one (several can be open at once; the active one is bold) its databases, schemas, object folders (Tables, Views, Functions…), objects, and
/// under each table its columns, keys and indexes, foreign keys and the tables that reference it. Everything is read
/// from the cached catalog when a node is opened. Double-click or Enter opens an object; the right-click menu runs the
/// Objects tab's actions on it or opens a script in a Query tab.
/// </summary>
public partial class NavigatorViewModel : ViewModelBase, ISessionAware
{
    /// <summary>Matches shown for a filter; the summary says when there are more.</summary>
    public const int MaxMatches = 500;

    private readonly SessionService sessions;
    private readonly OpenConnections? _open;

    /// <param name="open">Every open connection; without it (tests) only the attached one is open.</param>
    public NavigatorViewModel(SessionService sessions, OpenConnections? open = null)
    {
        this.sessions = sessions;
        _open = open;
        if (_open is not null) _open.Changed += (_, _) => OnOpenChanged();
    }

    /// <summary>The active connection: bold in the tree, and the one the Objects tab shows.</summary>
    private DatabaseSession? _session;
    private ObservableCollection<ConnectionProfile>? _profiles;

    /// <summary>Catalogs of other databases on each open connection's server, read when the user opened them, so the
    /// filter finds their objects too.</summary>
    private readonly Dictionary<DatabaseSession, Dictionary<string, MetadataSnapshot>> _otherCatalogs = [];

    private Dictionary<string, MetadataSnapshot> OtherCatalogs(DatabaseSession session)
    {
        if (!_otherCatalogs.TryGetValue(session, out var catalogs))
            _otherCatalogs[session] = catalogs = new Dictionary<string, MetadataSnapshot>(StringComparer.OrdinalIgnoreCase);
        return catalogs;
    }

    /// <summary>Connections opened since the last rebuild: their row and their own database open on the next build.</summary>
    private readonly HashSet<DatabaseSession> _expandOnBuild = [];
    private readonly HashSet<DatabaseSession> _seen = [];
    private readonly HashSet<DatabaseSession> _subscribed = [];

    /// <summary>The open connections, in the order they were opened.</summary>
    public IReadOnlyList<DatabaseSession> OpenSessions
    {
        get
        {
            if (_open is null) return _session is null ? [] : [_session];
            return _session is not null && !_open.All.Contains(_session) ? [.. _open.All, _session] : _open.All;
        }
    }

    private DatabaseSession? SessionFor(Guid profileId) => OpenSessions.FirstOrDefault(s => s.Profile.Id == profileId);

    /// <summary>The open connection a row belongs to (from its connection row), or null for a closed one.</summary>
    public static DatabaseSession? SessionOf(NavigatorNode? node)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n.Kind == NavigatorNodeKind.Connection) return n.Session;
        return null;
    }

    private bool IsStillOpen(DatabaseSession session) => OpenSessions.Contains(session);

    private void OnOpenChanged()
    {
        Track();
        OnPropertyChanged(nameof(IsConnected));
        Rebuild();
    }

    /// <summary>Listens to the catalogs of the open connections only, and forgets what belonged to closed ones.</summary>
    private void Track()
    {
        var open = OpenSessions;
        foreach (var gone in _subscribed.Where(s => !open.Contains(s)).ToList())
        {
            gone.SnapshotChanged -= OnCatalogChanged;
            gone.DatabasesChanged -= OnCatalogChanged;
            _subscribed.Remove(gone);
            _otherCatalogs.Remove(gone);
            _seen.Remove(gone);
            _expandOnBuild.Remove(gone);
        }
        foreach (var session in open)
        {
            if (_subscribed.Add(session))
            {
                session.SnapshotChanged += OnCatalogChanged;
                session.DatabasesChanged += OnCatalogChanged;
            }
            if (_seen.Add(session)) _expandOnBuild.Add(session);
        }
    }

    public ObservableCollection<NavigatorNode> Roots { get; } = [];

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _filterSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedIsConnection), nameof(SelectedIsClosedConnection), nameof(SelectedIsActiveConnection),
        nameof(SelectedIsOpenConnection), nameof(SelectedIsOpenInactiveConnection),
        nameof(SelectedIsObject), nameof(SelectedIsTableLike), nameof(SelectedIsTable), nameof(SelectedIsRoutine),
        nameof(SelectedHasCatalogActions), nameof(SelectedHasDatabase), nameof(SelectedHasName), nameof(SelectedCanDocument))]
    private NavigatorNode? _selectedNode;

    /// <summary>Raised for an object the Objects tab can show (it is in the session's catalog).</summary>
    /// <remarks>The session is the open connection the object belongs to (it may not be the active one).</remarks>
    public event Action<NavigatorObjectAction, DbObject, DatabaseSession?>? ObjectActionRequested;

    public event Action<NavigatorConnectionAction, ConnectionProfile>? ConnectionActionRequested;

    /// <summary>A script for a new Query tab: the SQL, the database to run it in, the tab title (null for a numbered
    /// tab) and the connection it is for (the row's, whichever connection is active).</summary>
    public event Action<string, string?, string?, ConnectionProfile?>? OpenSqlRequested;

    /// <summary>Text for the clipboard (the view owns the clipboard).</summary>
    public event Action<string>? CopyRequested;

    /// <summary>Export documentation of a database (null: the connection's own) and, from a schema row, one schema.</summary>
    public event Action<string?, string?, DatabaseSession?>? DocumentationRequested;

    /// <summary>Whether any connection is open.</summary>
    public bool IsConnected => OpenSessions.Count > 0;

    /// <summary>The saved connections, kept in step with the toolbar's list.</summary>
    public ObservableCollection<ConnectionProfile>? Profiles
    {
        get => _profiles;
        set
        {
            if (_profiles is not null) _profiles.CollectionChanged -= OnProfilesChanged;
            _profiles = value;
            if (_profiles is not null) _profiles.CollectionChanged += OnProfilesChanged;
            Rebuild();
        }
    }

    private void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    /// <summary>The active connection changed (or, without the list of open connections, the only one).</summary>
    public void Attach(DatabaseSession? session)
    {
        _session = session;
        Track();
        OnPropertyChanged(nameof(IsConnected));
        Rebuild();
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (sender is not DatabaseSession session || !IsStillOpen(session)) return;
        _otherCatalogs.Remove(session);
        Rebuild();
    }

    partial void OnFilterTextChanged(string value) => Rebuild();

    // ----- Building the tree -----

    /// <summary>Builds the roots again, re-opening the nodes that were open and selecting the same node, so a catalog
    /// refresh or a new connection does not throw away where the user was.</summary>
    public void Rebuild()
    {
        // A rebuild can start while the previous one is still re-opening nodes (a catalog refresh raises
        // SnapshotChanged and then DatabasesChanged): carry over what that one still had to open and select, and stop it.
        var expanded = Roots.SelectMany(r => r.SelfAndDescendants()).Where(n => n.IsExpanded).Select(n => n.Key).ToHashSet();
        if (_pendingExpanded is not null) expanded.UnionWith(_pendingExpanded);
        var selected = SelectedNode?.Key ?? _pendingSelected;
        var generation = ++_restoreGeneration;

        Roots.Clear();
        FilterSummary = "";
        if (FilterText.Trim().Length > 0) BuildFiltered(FilterText.Trim());
        else BuildConnections();

        foreach (var opened in _expandOnBuild)
        {
            var key = ConnectionKey(opened.Profile);
            expanded.Add(key);
            // Open the connection's own database too, the way DBeaver lands on it after connecting.
            expanded.Add(DatabaseKey(key, opened.Profile.Database));
        }
        _expandOnBuild.Clear();
        _pendingExpanded = expanded;
        _pendingSelected = selected;
        _ = RestoreAsync(expanded, selected, generation);
    }

    private HashSet<string>? _pendingExpanded;
    private string? _pendingSelected;
    private int _restoreGeneration;

    /// <summary>Whether a rebuild is still re-opening nodes (tests wait for it).</summary>
    public bool IsRestoring => _pendingExpanded is not null;

    private async Task RestoreAsync(HashSet<string> expanded, string? selected, int generation)
    {
        foreach (var root in Roots.ToList())
            if (!await RestoreAsync(root, expanded, generation)) return;
        if (generation != _restoreGeneration) return;
        if (selected is not null)
            SelectedNode = Roots.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Key == selected) ?? SelectedNode;
        _pendingExpanded = null;
        _pendingSelected = null;
    }

    /// <returns>False once a newer rebuild took over, so this one stops.</returns>
    private async Task<bool> RestoreAsync(NavigatorNode node, HashSet<string> expanded, int generation)
    {
        if (generation != _restoreGeneration) return false;
        if (!expanded.Contains(node.Key) && !node.IsExpanded) return true;
        await node.LoadAsync();
        if (generation != _restoreGeneration) return false;
        node.IsExpanded = true;
        foreach (var child in node.Children.ToList())
            if (!await RestoreAsync(child, expanded, generation)) return false;
        return true;
    }

    private void BuildConnections()
    {
        if (_profiles is null) return;
        var ordered = ConnectionFolders.Order(_profiles);
        var folders = new Dictionary<string, NavigatorNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in ordered)
        {
            var node = ConnectionNode(profile);
            var folder = ConnectionFolders.Normalize(profile.Folder);
            if (folder.Length == 0)
            {
                Roots.Add(node);
                continue;
            }
            if (!folders.TryGetValue(folder, out var folderNode))
            {
                folderNode = new NavigatorNode(NavigatorNodeKind.ConnectionFolder, folder, "folder:" + folder)
                {
                    Icon = Icons.Folder, ToolTip = "Connection folder " + folder
                };
                folders[folder] = folderNode;
                Roots.Add(folderNode);
                folderNode.IsExpanded = true;
            }
            folderNode.SetChildren([.. folderNode.Children, node]);
        }

        if (Roots.Count == 0)
            Roots.Add(NavigatorNode.Message("No saved connections. Add one with + in the toolbar."));
    }

    private NavigatorNode ConnectionNode(ConnectionProfile profile)
    {
        var open = OpenSessions;
        var session = SessionFor(profile.Id);
        var active = session is not null && ReferenceEquals(session, _session);
        var several = open.Count > 1;
        var engine = SqlDialect.EngineName(profile.ProviderKey);
        var tip = $"{profile.DisplayName} · {engine} · {profile.Host}" + (string.IsNullOrEmpty(profile.Database) ? "" : "/" + profile.Database);
        return new NavigatorNode(NavigatorNodeKind.Connection, profile.DisplayName, ConnectionKey(profile))
        {
            Icon = Icons.Server,
            ColorKey = session is not null ? "AppStatusConnectedBrush" : null,
            Profile = session?.Profile ?? profile,
            Session = session,
            IsEmphasized = active,
            MarkBrush = session is not null && several ? ConnectionMarks.Brush(IndexOfOpen(session)) : null,
            Detail = active && several ? "active" : null,
            ToolTip = session is null
                ? tip + (open.Count == 0
                    ? "\nDouble-click or press Enter to connect."
                    : "\nDouble-click or press Enter to connect. It opens next to the open connections and becomes the active one.")
                : active
                    ? tip + (several
                        ? "\nOpen and active: the Objects, Diagram and other tabs show it. Query tabs with its colour run on it."
                        : "\nConnected. Open it to browse databases and objects.")
                    : tip + "\nOpen. Right-click › Make active to show it in the Objects, Diagram and other tabs; Query tabs with its colour run on it.",
            Loader = session is not null ? LoadDatabasesAsync : null
        };
    }

    private int IndexOfOpen(DatabaseSession session)
    {
        var index = _open?.IndexOf(session.Profile.Id) ?? -1;
        if (index >= 0) return index;
        var all = OpenSessions;
        for (var i = 0; i < all.Count; i++)
            if (ReferenceEquals(all[i], session)) return i;
        return 0;
    }

    private async Task<IReadOnlyList<NavigatorNode>> LoadDatabasesAsync(NavigatorNode connection)
    {
        if (SessionOf(connection) is not { } session) return [];
        IReadOnlyList<string> server = [];
        try
        {
            server = await session.GetServerDatabasesAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("navigator database list", ex);
        }
        if (!IsStillOpen(session)) return [];

        var snapshot = session.Snapshot;
        return NavigatorTree.Databases(snapshot, server, session.Profile.Database)
            .Select(db => DatabaseNode(connection.Key, db, snapshot))
            .ToList();
    }

    private NavigatorNode DatabaseNode(string parentKey, NavigatorDatabase db, MetadataSnapshot snapshot)
    {
        var title = db.Name.Length > 0 ? db.Name : "(database)";
        var count = db.InCatalog ? NavigatorTree.ObjectsOf(snapshot, db.Name, db.IsConnectionDatabase).Count() : (int?)null;
        return new NavigatorNode(NavigatorNodeKind.Database, title, DatabaseKey(parentKey, db.Name))
        {
            Icon = Icons.Database,
            ColorKey = "SystemAccentColor",
            Database = db.Name,
            IsEmphasized = db.IsConnectionDatabase,
            Detail = count is { } n ? n.ToString("N0") : null,
            ToolTip = db.InCatalog
                ? $"{title}: {count:N0} objects in the cached catalog"
                : $"{title}: read from the server when opened",
            Loader = node => LoadDatabaseAsync(node, db)
        };
    }

    private async Task<IReadOnlyList<NavigatorNode>> LoadDatabaseAsync(NavigatorNode node, NavigatorDatabase db)
    {
        if (SessionOf(node) is not { } session) return [];
        MetadataSnapshot snapshot;
        bool inSession;
        if (db.InCatalog)
        {
            snapshot = session.Snapshot;
            inSession = true;
        }
        else
        {
            snapshot = await sessions.GetDatabaseSnapshotAsync(session, db.Name);
            if (!IsStillOpen(session)) return [];
            OtherCatalogs(session)[db.Name] = snapshot;
            inSession = false;
        }

        // A catalog read for that database alone may leave Database empty: match those rows too.
        var objects = NavigatorTree.ObjectsOf(snapshot, db.Name, db.IsConnectionDatabase || !inSession).ToList();
        return SchemaChildren(node.Key, db.Name, snapshot, objects, inSession, session.Profile.ProviderKey);
    }

    private IReadOnlyList<NavigatorNode> SchemaChildren(string parentKey, string database, MetadataSnapshot snapshot,
        IReadOnlyList<DbObject> objects, bool inSession, string providerKey, bool keepOrder = false)
    {
        var schemas = NavigatorTree.Schemas(objects);
        if (NavigatorTree.SkipSchemaLevel(providerKey, database, schemas))
            return FolderNodes(parentKey, database, snapshot, objects, inSession, keepOrder);

        return schemas.Select(schema =>
        {
            var key = $"{parentKey}/schema:{schema.Name}";
            var schemaObjects = objects.Where(o => string.Equals(o.Schema, schema.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            return new NavigatorNode(NavigatorNodeKind.Schema, schema.Name, key)
            {
                Icon = Icons.Schema,
                Database = database,
                Detail = schema.ObjectCount.ToString("N0"),
                ToolTip = $"Schema {schema.Name}: {schema.ObjectCount:N0} objects",
                Loader = _ => Task.FromResult(FolderNodes(key, database, snapshot, schemaObjects, inSession, keepOrder))
            };
        }).ToList();
    }

    private IReadOnlyList<NavigatorNode> FolderNodes(string parentKey, string database, MetadataSnapshot snapshot,
        IReadOnlyList<DbObject> objects, bool inSession, bool keepOrder = false) =>
        NavigatorTree.Folders(objects).Select(f =>
        {
            var key = $"{parentKey}/folder:{f.Folder}";
            return new NavigatorNode(NavigatorNodeKind.Folder, NavigatorTree.FolderTitle(f.Folder), key)
            {
                Icon = Icons.Folder,
                ColorKey = ObjectTypeColors.ResourceKey(NavigatorTree.FolderType(f.Folder)),
                Database = database,
                Detail = f.Count.ToString("N0"),
                Loader = _ => Task.FromResult<IReadOnlyList<NavigatorNode>>(NavigatorTree.ObjectsIn(objects, f.Folder, keepOrder)
                    .Select(o => ObjectNode(key, database, snapshot, o, inSession)).ToList())
            };
        }).ToList();

    private NavigatorNode ObjectNode(string parentKey, string database, MetadataSnapshot snapshot, DbObject obj, bool inSession)
    {
        // Objects of another database may come without their database name; actions need it to run in the right place.
        var target = inSession || obj.Database.Length > 0 ? obj : obj with { Database = database };
        var details = NavigatorTree.Details(snapshot, obj);
        var key = $"{parentKey}/obj:{obj.Schema}.{obj.Name}";
        var rows = obj.Type == DbObjectType.Table && obj.RowCount is { } n and >= 0 ? ShortCount(n) : null;
        return new NavigatorNode(NavigatorNodeKind.Object, obj.Name, key)
        {
            Icon = ObjectIcon(obj.Type),
            ColorKey = ObjectTypeColors.ResourceKey(obj.Type),
            Object = target,
            Database = database,
            InSessionCatalog = inSession,
            Snapshot = snapshot,
            Detail = rows,
            ToolTip = $"{obj.FullName} · {ObjectScripts.Humanize(obj.Type)}" +
                      (obj.RowCount is { } r and >= 0 ? $" · about {r:N0} rows" : "") +
                      (inSession ? "\nDouble-click to open it in the Objects tab" : "\nIn another database: right-click for scripts"),
            Loader = details.Count == 0 ? null : node => Task.FromResult(DetailFolders(node, snapshot, obj, details))
        };
    }

    private static IReadOnlyList<NavigatorNode> DetailFolders(NavigatorNode owner, MetadataSnapshot snapshot, DbObject obj,
        IReadOnlyList<(NavigatorDetail Detail, int Count)> details) =>
        details.Select(d =>
        {
            var key = $"{owner.Key}/detail:{d.Detail}";
            return new NavigatorNode(NavigatorNodeKind.DetailFolder, NavigatorTree.DetailTitle(d.Detail), key)
            {
                Icon = Icons.Folder,
                Object = owner.Object,
                Database = owner.Database,
                InSessionCatalog = owner.InSessionCatalog,
                Detail = d.Count.ToString("N0"),
                Loader = _ => Task.FromResult<IReadOnlyList<NavigatorNode>>(NavigatorTree.DetailItems(snapshot, obj, d.Detail)
                    .Select(item => new NavigatorNode(NavigatorNodeKind.Detail, item.Name, $"{key}/{item.Name}")
                    {
                        Icon = d.Detail switch
                        {
                            NavigatorDetail.Columns => item.IsKey ? Icons.Key : Icons.Column,
                            NavigatorDetail.Indexes => item.IsKey ? Icons.Key : Icons.Index,
                            _ => Icons.Link
                        },
                        ColorKey = item.IsKey ? "AppObjectSequenceBrush" : null,
                        Object = owner.Object,
                        Database = owner.Database,
                        InSessionCatalog = owner.InSessionCatalog,
                        DetailName = item.Name,
                        Detail = item.Description,
                        ToolTip = $"{item.Name} · {item.Description}"
                    }).ToList())
            };
        }).ToList();

    /// <summary>Filter mode: the matching objects of every catalog read so far on every open connection, under their
    /// connection, database, schema and folder, all open. Saved connections are matched by name as well.</summary>
    private void BuildFiltered(string filter)
    {
        var open = OpenSessions;
        var connectionMatches = (_profiles ?? []).Where(p => p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                                             && open.All(s => s.Profile.Id != p.Id)).ToList();
        foreach (var profile in ConnectionFolders.Order(connectionMatches)) Roots.Add(ConnectionNode(profile));

        if (open.Count == 0)
        {
            FilterSummary = connectionMatches.Count == 0 ? "No connection matches. Connect to search objects." : "";
            return;
        }

        var total = 0;
        var shown = 0;
        var noOtherCatalogs = true;
        var insertAt = 0;
        // The active connection first, then the others in the order they were opened.
        foreach (var session in open.OrderBy(s => ReferenceEquals(s, _session) ? 0 : 1))
        {
            var (connection, matches, kept, others) = FilterConnection(session, filter, MaxMatches - shown);
            total += matches;
            shown += kept;
            if (others) noOtherCatalogs = false;
            Roots.Insert(insertAt++, connection);
            connection.IsExpanded = true;
            _ = ExpandContainersAsync(connection);
        }

        FilterSummary = total == 0
            ? "No object matches" + (noOtherCatalogs ? open.Count > 1 ? " in the connections' catalogs" : " in the connection's catalog" : "")
            : total > shown ? $"First {shown:N0} of {total:N0} matches" : $"{total:N0} match{(total == 1 ? "" : "es")}";
    }

    /// <returns>The connection row with its matches under it, how many matched, how many are shown (at most
    /// <paramref name="limit"/>) and whether other databases' catalogs were searched too.</returns>
    private (NavigatorNode Connection, int Matches, int Shown, bool OtherCatalogs) FilterConnection(DatabaseSession session, string filter, int limit)
    {
        var several = OpenSessions.Count > 1;
        var connection = new NavigatorNode(NavigatorNodeKind.Connection, session.Profile.DisplayName, ConnectionKey(session.Profile))
        {
            Icon = Icons.Server, ColorKey = "AppStatusConnectedBrush", Profile = session.Profile, Session = session,
            IsEmphasized = ReferenceEquals(session, _session),
            MarkBrush = several ? ConnectionMarks.Brush(IndexOfOpen(session)) : null
        };
        var otherCatalogs = _otherCatalogs.TryGetValue(session, out var read) ? read : [];
        var catalogs = new List<(string Database, MetadataSnapshot Snapshot, bool InSession)>();
        foreach (var db in NavigatorTree.Databases(session.Snapshot, [], session.Profile.Database).Where(d => d.InCatalog))
            catalogs.Add((db.Name, session.Snapshot, true));
        foreach (var (name, snapshot) in otherCatalogs.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            if (!catalogs.Any(c => string.Equals(c.Database, name, StringComparison.OrdinalIgnoreCase)))
                catalogs.Add((name, snapshot, false));

        // Rank every catalog's matches together before the limit, so an exact match in one database is never pushed
        // out by a pile of partial matches in another; then group what is kept back under its database.
        var candidates = catalogs.SelectMany((c, index) =>
        {
            var own = c.InSession && string.Equals(c.Database, session.Profile.Database ?? "", StringComparison.OrdinalIgnoreCase);
            return NavigatorTree.ObjectsOf(c.Snapshot, c.Database, own || !c.InSession)
                .Select(o => (Catalog: index, Object: o, Rank: NavigatorTree.MatchRank(o, filter)))
                .Where(m => m.Rank >= 0);
        }).ToList();
        var total = candidates.Count;
        var kept = candidates
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Object.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Object.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Catalog)
            .Take(Math.Max(0, limit))
            .ToList();
        var shown = kept.Count;

        var databases = new List<NavigatorNode>();
        for (var index = 0; index < catalogs.Count; index++)
        {
            var (database, snapshot, inSession) = catalogs[index];
            var take = kept.Where(m => m.Catalog == index).Select(m => m.Object).ToList();
            if (take.Count == 0) continue;
            var own = inSession && string.Equals(database, session.Profile.Database ?? "", StringComparison.OrdinalIgnoreCase);
            var count = candidates.Count(m => m.Catalog == index);

            var dbKey = DatabaseKey(connection.Key, database);
            var dbNode = new NavigatorNode(NavigatorNodeKind.Database, database.Length > 0 ? database : "(database)", dbKey)
            {
                Icon = Icons.Database, ColorKey = "SystemAccentColor", Database = database, IsEmphasized = own,
                Detail = count.ToString("N0")
            };
            dbNode.SetChildren(SchemaChildren(dbKey, database, snapshot, take, inSession, session.Profile.ProviderKey, keepOrder: true));
            databases.Add(dbNode);
        }
        connection.SetChildren(databases.Count > 0 ? databases : [NavigatorNode.Message("No object matches")]);
        return (connection, total, shown, otherCatalogs.Count > 0);
    }

    /// <summary>Opens every database, schema and folder under <paramref name="node"/> (the filter's results).</summary>
    private static async Task ExpandContainersAsync(NavigatorNode node)
    {
        foreach (var child in node.Children.ToList())
        {
            if (child.Kind is not (NavigatorNodeKind.Database or NavigatorNodeKind.Schema or NavigatorNodeKind.Folder)) continue;
            await child.LoadAsync();
            child.IsExpanded = true;
            await ExpandContainersAsync(child);
        }
    }

    // ----- Selection-dependent menu items -----

    public bool SelectedIsConnection => SelectedNode?.Kind == NavigatorNodeKind.Connection;
    public bool SelectedIsActiveConnection => SelectedIsConnection && SelectedNode!.Session is { } s && ReferenceEquals(s, _session);

    /// <summary>A saved connection that is not open (Connect, Delete).</summary>
    public bool SelectedIsClosedConnection => SelectedIsConnection && SelectedNode!.Session is null;

    /// <summary>An open connection (Disconnect).</summary>
    public bool SelectedIsOpenConnection => SelectedIsConnection && SelectedNode!.Session is not null;

    /// <summary>An open connection other than the active one (Make active).</summary>
    public bool SelectedIsOpenInactiveConnection => SelectedIsOpenConnection && !SelectedIsActiveConnection;
    public bool SelectedIsObject => SelectedObject is not null;
    public bool SelectedIsTableLike => SelectedObject?.IsTableLike == true;
    public bool SelectedIsTable => SelectedObject?.Type == DbObjectType.Table;
    public bool SelectedIsRoutine => SelectedObject?.IsRoutine == true;

    /// <summary>The Objects tab's actions (Get data, Modify, Drop, Execute…) work on the session's own catalog.</summary>
    public bool SelectedHasCatalogActions => SelectedObject is not null && SelectedNode!.InSessionCatalog;

    public bool SelectedHasDatabase => SessionOf(SelectedNode) is not null &&
                                       SelectedNode is { Kind: not (NavigatorNodeKind.ConnectionFolder or NavigatorNodeKind.Message) };

    /// <summary>Export documentation: the connected connection, a database or a schema row.</summary>
    public bool SelectedCanDocument => SelectedHasDatabase &&
                                       SelectedNode!.Kind is NavigatorNodeKind.Connection or NavigatorNodeKind.Database or NavigatorNodeKind.Schema;

    public bool SelectedHasName => SelectedNode is { Kind: not NavigatorNodeKind.Message };

    /// <summary>The object of an object row, or of the table a column / index / key row belongs to.</summary>
    private DbObject? SelectedObject => SelectedNode is { Kind: NavigatorNodeKind.Object or NavigatorNodeKind.DetailFolder or NavigatorNodeKind.Detail } n
        ? n.Object
        : null;

    // ----- Actions -----

    /// <summary>Rows a double-click shows instead of opening or closing: objects and their columns, indexes and keys,
    /// and saved connections that are not open (a double-click connects them).</summary>
    public static bool OpensOnDoubleClick(NavigatorNode node) =>
        node.Kind is NavigatorNodeKind.Object or NavigatorNodeKind.Detail ||
        (node.Kind == NavigatorNodeKind.Connection && node.Session is null);

    /// <summary>Double-click / Enter: connect a saved connection, open an object (or a column's table).</summary>
    /// <returns>True when the row did something, so the tree does not also open or close it.</returns>
    public bool Activate(NavigatorNode? node)
    {
        if (node is null) return false;
        SelectedNode = node;
        switch (node.Kind)
        {
            case NavigatorNodeKind.Connection when node.Profile is { } profile && node.Session is null:
                ConnectionActionRequested?.Invoke(NavigatorConnectionAction.Connect, profile);
                return true;
            case NavigatorNodeKind.Object or NavigatorNodeKind.Detail when node.Object is { } obj:
                if (node.InSessionCatalog) ObjectActionRequested?.Invoke(NavigatorObjectAction.Open, obj, SessionOf(node));
                else SelectRows();
                return true;
            default:
                return false;
        }
    }

    /// <summary>The menu's Open: the object of any object, column, index or detail-folder row.</summary>
    [RelayCommand]
    private void Open()
    {
        if (SelectedNode is { Kind: NavigatorNodeKind.DetailFolder, Object: { } obj } folder)
        {
            if (folder.InSessionCatalog) ObjectActionRequested?.Invoke(NavigatorObjectAction.Open, obj, SessionOf(folder));
            return;
        }
        Activate(SelectedNode);
    }

    [RelayCommand] private void GetData() => RunObjectAction(NavigatorObjectAction.GetData);
    [RelayCommand] private void Execute() => RunObjectAction(NavigatorObjectAction.Execute);
    [RelayCommand] private void Debug() => RunObjectAction(NavigatorObjectAction.Debug);
    [RelayCommand] private void ShowInDiagram() => RunObjectAction(NavigatorObjectAction.ShowInDiagram);
    [RelayCommand] private void Modify() => RunObjectAction(NavigatorObjectAction.Modify);
    [RelayCommand] private void DropTable() => RunObjectAction(NavigatorObjectAction.DropTable);
    [RelayCommand] private void FindUsages() => RunObjectAction(NavigatorObjectAction.FindUsages);
    [RelayCommand] private void ImportData() => RunObjectAction(NavigatorObjectAction.ImportData);
    [RelayCommand] private void GenerateTestData() => RunObjectAction(NavigatorObjectAction.GenerateTestData);

    private void RunObjectAction(NavigatorObjectAction action)
    {
        if (SelectedObject is { } obj && SelectedNode!.InSessionCatalog) ObjectActionRequested?.Invoke(action, obj, SessionOf(SelectedNode));
    }

    /// <summary>"Read data in SQL console": a SELECT of the first rows in a new Query tab, run by the user.</summary>
    [RelayCommand]
    private void SelectRows() => OpenScript("Select", (key, obj, cols) => ObjectScripts.SelectScript(key, obj, cols), tableLikeOnly: true);

    [RelayCommand]
    private void ScriptInsert() => OpenScript("Insert", ObjectScripts.InsertTemplate, tableLikeOnly: false);

    [RelayCommand]
    private void ScriptUpdate() => OpenScript("Update", ObjectScripts.UpdateTemplate, tableLikeOnly: false);

    [RelayCommand]
    private void ScriptDelete() => OpenScript("Delete", ObjectScripts.DeleteTemplate, tableLikeOnly: false);

    private void OpenScript(string verb, Func<string, DbObject, IEnumerable<DbColumn>, string> script, bool tableLikeOnly)
    {
        if (SelectedNode is not { } node || SessionOf(node) is not { } session || SelectedObject is not { } obj) return;
        if (tableLikeOnly ? !obj.IsTableLike : obj.Type != DbObjectType.Table) return;
        var snapshot = node.Snapshot ?? FindOwner(node)?.Snapshot ?? session.Snapshot;
        var columns = snapshot.ColumnsOf(LookupDatabase(snapshot, obj), obj.Schema, obj.Name).ToList();
        OpenSqlRequested?.Invoke(script(session.Profile.ProviderKey, obj, columns), DatabaseOf(node), $"{verb} {obj.Name}", session.Profile);
    }

    /// <summary>A blank Query tab in the selected node's database.</summary>
    [RelayCommand]
    private void NewQuery()
    {
        if (SelectedNode is not { } node || SessionOf(node) is not { } session) return;
        OpenSqlRequested?.Invoke("", DatabaseOf(node), null, session.Profile);
    }

    [RelayCommand]
    private void ExportDocumentation()
    {
        if (!SelectedCanDocument) return;
        var node = SelectedNode!;
        DocumentationRequested?.Invoke(DatabaseOf(node), node.Kind == NavigatorNodeKind.Schema ? node.Title : null, SessionOf(node));
    }

    [RelayCommand]
    private void CopyName()
    {
        if (SelectedNode is not { } node) return;
        var text = node.Kind switch
        {
            NavigatorNodeKind.Object when node.Object is { } o => o.FullName,
            NavigatorNodeKind.Detail => node.DetailName ?? node.Title,
            _ => node.Title
        };
        CopyRequested?.Invoke(text);
    }

    /// <summary>The selected saved connection as a connection string or URL ("url"), never with its password.</summary>
    [RelayCommand]
    private void CopyConnectionString(string? kind)
    {
        if (SelectedNode is not { Kind: NavigatorNodeKind.Connection, Profile: { } profile }) return;
        var style = kind == "url" ? ConnectionStringStyle.Url : ConnectionStringStyle.KeyValue;
        CopyRequested?.Invoke(ConnectionStrings.Format(profile, style));
    }

    [RelayCommand]
    private void Connect() => RunConnectionAction(NavigatorConnectionAction.Connect);

    [RelayCommand]
    private void Disconnect() => RunConnectionAction(NavigatorConnectionAction.Disconnect);

    [RelayCommand]
    private void ActivateConnection() => RunConnectionAction(NavigatorConnectionAction.Activate);

    [RelayCommand]
    private void EditConnection() => RunConnectionAction(NavigatorConnectionAction.Edit);

    [RelayCommand]
    private void DuplicateConnection() => RunConnectionAction(NavigatorConnectionAction.Duplicate);

    [RelayCommand]
    private void DeleteConnection() => RunConnectionAction(NavigatorConnectionAction.Delete);

    private void RunConnectionAction(NavigatorConnectionAction action)
    {
        if (SelectedNode is { Kind: NavigatorNodeKind.Connection, Profile: { } profile }) ConnectionActionRequested?.Invoke(action, profile);
    }

    /// <summary>Re-reads the catalog: the whole connection's, or one other database's when that is selected.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        // The selected row's connection; with nothing (or a closed connection) selected, the active one.
        if ((SessionOf(SelectedNode) ?? _session) is not { } session) return;
        var node = SelectedNode;
        while (node is not null && node.Kind != NavigatorNodeKind.Database) node = node.Parent;
        if (node?.Database is { } database && SessionOf(node) is { } owner && ReferenceEquals(owner, session) &&
            _otherCatalogs.TryGetValue(session, out var others) && others.ContainsKey(database))
        {
            try
            {
                var fresh = await sessions.RefreshDatabaseSnapshotAsync(session, database);
                if (!IsStillOpen(session)) return; // disconnected meanwhile: not this tree's
                OtherCatalogs(session)[database] = fresh;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("navigator refresh", ex);
            }
            if (!IsStillOpen(session)) return;
            Rebuild();
            return;
        }
        ConnectionActionRequested?.Invoke(NavigatorConnectionAction.RefreshMetadata, session.Profile);
    }

    /// <summary>The ✕ in the navigator's header; Ctrl+B or the app bar's sidebar button brings it back.</summary>
    public event Action? HideRequested;

    [RelayCommand]
    private void Hide() => HideRequested?.Invoke();

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in Roots.SelectMany(r => r.SelfAndDescendants()).ToList())
            if (node.Kind != NavigatorNodeKind.ConnectionFolder) node.IsExpanded = false;
    }

    /// <summary>Selects the object in the tree (opening its database, schema and folder) when it is there.</summary>
    public async Task RevealAsync(DbObject obj)
    {
        if (_session is not { } session || FilterText.Length > 0) return;
        var connection = Roots.SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(n => n.Kind == NavigatorNodeKind.Connection && n.Profile?.Id == session.Profile.Id);
        if (connection is null) return;

        NavigatorNode? current = connection;
        var path = new Func<NavigatorNode, bool>[]
        {
            n => n.Kind == NavigatorNodeKind.Database && SameDatabase(n, obj, session),
            n => n.Kind == NavigatorNodeKind.Schema && string.Equals(n.Title, obj.Schema, StringComparison.OrdinalIgnoreCase),
            n => n.Kind == NavigatorNodeKind.Folder && n.Title == NavigatorTree.FolderTitle(NavigatorTree.FolderOf(obj.Type)),
            n => n.Kind == NavigatorNodeKind.Object && n.Object is { } o &&
                 string.Equals(o.Schema, obj.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(o.Name, obj.Name, StringComparison.OrdinalIgnoreCase)
        };
        foreach (var step in path)
        {
            await current.LoadAsync();
            current.IsExpanded = true;
            var next = current.Children.FirstOrDefault(step);
            if (next is null && step == path[1]) continue; // no schema level (MySQL)
            if (next is null) return;
            current = next;
        }
        SelectedNode = current;
    }

    private static bool SameDatabase(NavigatorNode node, DbObject obj, DatabaseSession session)
    {
        var database = obj.Database.Length > 0 ? obj.Database : session.Profile.Database;
        return string.Equals(node.Database, database, StringComparison.OrdinalIgnoreCase);
    }

    private static NavigatorNode? FindOwner(NavigatorNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n.Kind == NavigatorNodeKind.Object) return n;
        return null;
    }

    /// <summary>The database a row's scripts run in: its database node's name, or null for the connection's default.</summary>
    private string? DatabaseOf(NavigatorNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n.Kind == NavigatorNodeKind.Database || n.Database is not null)
                return string.IsNullOrEmpty(n.Database) ? null : n.Database;
            if (n.Kind == NavigatorNodeKind.Connection)
                return string.IsNullOrEmpty(n.Profile?.Database) ? null : n.Profile!.Database;
        }
        return null;
    }

    /// <summary>The Database value the catalog stores the object's columns under (empty for a single-database read).</summary>
    /// <remarks>The full database/schema/name/type identity wins, so in a server-wide catalog with Audit.dbo.Orders and
    /// Shop.dbo.Orders the script for Shop never borrows Audit's columns. A row without a database name (a catalog
    /// read for one database) is the fallback.</remarks>
    internal static string LookupDatabase(MetadataSnapshot snapshot, DbObject obj)
    {
        bool SameObject(DbObject o) => string.Equals(o.Schema, obj.Schema, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(o.Name, obj.Name, StringComparison.OrdinalIgnoreCase)
                                       && o.Type == obj.Type;
        if (snapshot.Objects.Any(o => SameObject(o) && string.Equals(o.Database, obj.Database, StringComparison.OrdinalIgnoreCase)))
            return obj.Database;
        return snapshot.Objects.Any(o => SameObject(o) && o.Database.Length == 0) ? "" : obj.Database;
    }

    private static string ConnectionKey(ConnectionProfile profile) => "conn:" + profile.Id;
    private static string DatabaseKey(string connectionKey, string? database) => $"{connectionKey}/db:{database}";

    public static string ObjectIcon(DbObjectType type) => type switch
    {
        DbObjectType.Table or DbObjectType.ForeignTable => Icons.Table,
        DbObjectType.View or DbObjectType.MaterializedView => Icons.View,
        DbObjectType.Procedure => Icons.Procedure,
        DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction => Icons.Function,
        DbObjectType.Trigger => Icons.Bolt,
        DbObjectType.Sequence => Icons.Hash,
        DbObjectType.Synonym => Icons.Link,
        _ => Icons.Info
    };

    /// <summary>"950", "12k", "3.4M": a table's estimated rows, short enough for the tree.</summary>
    public static string ShortCount(long n) => n switch
    {
        < 1_000 => n.ToString("N0"),
        < 10_000 => (n / 1_000d).ToString("0.#") + "k",
        < 1_000_000 => (n / 1_000) + "k",
        < 10_000_000 => (n / 1_000_000d).ToString("0.#") + "M",
        < 1_000_000_000 => (n / 1_000_000) + "M",
        _ => (n / 1_000_000_000d).ToString("0.#") + "B"
    };
}
