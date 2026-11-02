using DbExplorer.Application.Copy;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Metadata;

/// <summary>The object folders a schema shows in the database navigator, in display order.</summary>
public enum NavigatorFolder
{
    Tables,
    Views,
    MaterializedViews,
    ForeignTables,
    Procedures,
    Functions,
    Triggers,
    Sequences,
    Synonyms,
    Other
}

/// <summary>The detail folders under a table-like object in the navigator.</summary>
public enum NavigatorDetail
{
    Columns,
    Indexes,
    ForeignKeys,
    References
}

/// <summary>One schema of a database as the navigator lists it, with how many objects it holds.</summary>
public sealed record NavigatorSchema(string Name, int ObjectCount);

/// <summary>One database as the navigator lists it. <see cref="InCatalog"/> is false for a database the server lists
/// but the session's catalog does not cover yet (it is read when the user opens it).</summary>
public sealed record NavigatorDatabase(string Name, bool InCatalog, bool IsConnectionDatabase);

/// <summary>A child row under a table: a column, an index or a foreign key, with its one-line description.</summary>
public sealed record NavigatorDetailItem(string Name, string Description, bool IsKey = false);

/// <summary>
/// The pure part of the database navigator (the tree on the left of the main window, after DBeaver's Database
/// Navigator): which databases, schemas, folders, objects and details exist under a node, and which objects match a
/// filter. Everything reads the cached catalog, never the server, so expanding a node is instant.
/// </summary>
public static class NavigatorTree
{
    public static string FolderTitle(NavigatorFolder folder) => folder switch
    {
        NavigatorFolder.MaterializedViews => "Materialized views",
        NavigatorFolder.ForeignTables => "Foreign tables",
        _ => folder.ToString()
    };

    public static string DetailTitle(NavigatorDetail detail) => detail switch
    {
        NavigatorDetail.Indexes => "Indexes",
        NavigatorDetail.ForeignKeys => "Foreign keys",
        NavigatorDetail.References => "Referenced by",
        _ => detail.ToString()
    };

    /// <summary>The object type a folder's icon is coloured as (the Objects tab colours).</summary>
    public static DbObjectType FolderType(NavigatorFolder folder) => folder switch
    {
        NavigatorFolder.Tables => DbObjectType.Table,
        NavigatorFolder.Views => DbObjectType.View,
        NavigatorFolder.MaterializedViews => DbObjectType.MaterializedView,
        NavigatorFolder.ForeignTables => DbObjectType.ForeignTable,
        NavigatorFolder.Procedures => DbObjectType.Procedure,
        NavigatorFolder.Functions => DbObjectType.Function,
        NavigatorFolder.Triggers => DbObjectType.Trigger,
        NavigatorFolder.Sequences => DbObjectType.Sequence,
        NavigatorFolder.Synonyms => DbObjectType.Synonym,
        _ => DbObjectType.Other
    };

    public static NavigatorFolder FolderOf(DbObjectType type) => type switch
    {
        DbObjectType.Table => NavigatorFolder.Tables,
        DbObjectType.View => NavigatorFolder.Views,
        DbObjectType.MaterializedView => NavigatorFolder.MaterializedViews,
        DbObjectType.ForeignTable => NavigatorFolder.ForeignTables,
        DbObjectType.Procedure => NavigatorFolder.Procedures,
        DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction => NavigatorFolder.Functions,
        DbObjectType.Trigger => NavigatorFolder.Triggers,
        DbObjectType.Sequence => NavigatorFolder.Sequences,
        DbObjectType.Synonym => NavigatorFolder.Synonyms,
        _ => NavigatorFolder.Other
    };

    /// <summary>
    /// The databases to list: every database the server reports plus every one the catalog has objects from,
    /// alphabetically. A catalog read for one database only (objects with an empty Database) is listed under the
    /// connection's database name.
    /// </summary>
    public static IReadOnlyList<NavigatorDatabase> Databases(MetadataSnapshot snapshot, IEnumerable<string> serverDatabases,
        string? connectionDatabase)
    {
        var inCatalog = new HashSet<string>(snapshot.Databases, StringComparer.OrdinalIgnoreCase);
        var hasUnnamed = snapshot.Objects.Any(o => string.IsNullOrEmpty(o.Database));
        var own = string.IsNullOrWhiteSpace(connectionDatabase) ? null : connectionDatabase;
        if (hasUnnamed) inCatalog.Add(own ?? "");

        var names = new HashSet<string>(inCatalog, StringComparer.OrdinalIgnoreCase);
        foreach (var name in serverDatabases)
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        if (own is not null) names.Add(own);

        return names
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new NavigatorDatabase(n, inCatalog.Contains(n),
                own is not null && string.Equals(n, own, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>The objects of one database; <paramref name="database"/> also matches catalog rows with no database
    /// name when it is the connection's own database.</summary>
    public static IEnumerable<DbObject> ObjectsOf(MetadataSnapshot snapshot, string database, bool isConnectionDatabase) =>
        snapshot.Objects.Where(o => SameDatabase(o.Database, database, isConnectionDatabase));

    /// <summary>The schemas the objects are in, with counts: the users' schemas alphabetically, then the engine's own.</summary>
    public static IReadOnlyList<NavigatorSchema> Schemas(IEnumerable<DbObject> objects) =>
        objects
            .GroupBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NavigatorSchema(g.Key, g.Count()))
            .OrderBy(s => SchemaRank(s.Name))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Whether the schema level is left out because it says nothing: MySQL (a schema is the database) or a
    /// database whose only schema has the database's own name.</summary>
    public static bool SkipSchemaLevel(string providerKey, string database, IReadOnlyList<NavigatorSchema> schemas) =>
        SqlDialect.SchemaIsDatabase(providerKey) ||
        (schemas.Count == 1 && string.Equals(schemas[0].Name, database, StringComparison.OrdinalIgnoreCase));

    /// <summary>The non-empty folders of one schema with their object counts, in display order.</summary>
    public static IReadOnlyList<(NavigatorFolder Folder, int Count)> Folders(IEnumerable<DbObject> schemaObjects) =>
        schemaObjects
            .GroupBy(o => FolderOf(o.Type))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count()))
            .ToList();

    /// <summary>The objects of one folder, alphabetically, or in the order given when <paramref name="keepOrder"/>
    /// is set (filter results, which arrive best match first).</summary>
    public static IReadOnlyList<DbObject> ObjectsIn(IEnumerable<DbObject> schemaObjects, NavigatorFolder folder, bool keepOrder = false)
    {
        var inFolder = schemaObjects.Where(o => FolderOf(o.Type) == folder);
        return (keepOrder ? inFolder : inFolder.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The detail folders of a table-like object that have something in them, with their counts.</summary>
    public static IReadOnlyList<(NavigatorDetail Detail, int Count)> Details(MetadataSnapshot snapshot, DbObject obj)
    {
        if (!obj.IsTableLike) return [];
        var result = new List<(NavigatorDetail, int)>();
        Add(NavigatorDetail.Columns, snapshot.ColumnsOf(obj.Database, obj.Schema, obj.Name).Count());
        Add(NavigatorDetail.Indexes, snapshot.IndexesOf(obj.Database, obj.Schema, obj.Name).Count());
        Add(NavigatorDetail.ForeignKeys, snapshot.ForeignKeysOf(obj.Database, obj.Schema, obj.Name).Count());
        Add(NavigatorDetail.References, IncomingReferences(snapshot, obj).Count());
        return result;

        void Add(NavigatorDetail detail, int count)
        {
            if (count > 0) result.Add((detail, count));
        }
    }

    public static IReadOnlyList<NavigatorDetailItem> DetailItems(MetadataSnapshot snapshot, DbObject obj, NavigatorDetail detail) =>
        detail switch
        {
            NavigatorDetail.Columns => snapshot.ColumnsOf(obj.Database, obj.Schema, obj.Name)
                .OrderBy(c => c.Ordinal)
                .Select(c => new NavigatorDetailItem(c.Name, ColumnDescription(c), c.IsPrimaryKey))
                .ToList(),
            NavigatorDetail.Indexes => snapshot.IndexesOf(obj.Database, obj.Schema, obj.Name)
                .OrderByDescending(i => i.IsPrimaryKey).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(i => new NavigatorDetailItem(i.Name,
                    (i.IsPrimaryKey ? "primary key" : i.IsUnique ? "unique" : "index") + " (" + i.Columns + ")", i.IsPrimaryKey))
                .ToList(),
            NavigatorDetail.ForeignKeys => snapshot.ForeignKeysOf(obj.Database, obj.Schema, obj.Name)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new NavigatorDetailItem(f.Name,
                    $"({f.Columns}) → {f.ReferencedSchema}.{f.ReferencedTable} ({f.ReferencedColumns})" + (f.IsVirtual ? " · virtual" : "")))
                .ToList(),
            NavigatorDetail.References => IncomingReferences(snapshot, obj)
                .OrderBy(f => f.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Table, StringComparer.OrdinalIgnoreCase)
                .Select(f => new NavigatorDetailItem($"{f.Schema}.{f.Table}", $"({f.Columns}) via {f.Name}"))
                .ToList(),
            _ => []
        };

    /// <summary>"integer · not null", "text · null", "int · PK".</summary>
    public static string ColumnDescription(DbColumn column)
    {
        var parts = new List<string> { column.DataType };
        if (column.IsPrimaryKey) parts.Add("PK");
        else parts.Add(column.IsNullable ? "null" : "not null");
        if (column.IsComputed) parts.Add("computed");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Objects of one database whose name (or schema.name) contains <paramref name="filter"/>, at most
    /// <paramref name="limit"/>, best matches first (exact, then prefix, then anywhere). An empty filter matches nothing.
    /// </summary>
    public static IReadOnlyList<DbObject> Matches(IEnumerable<DbObject> objects, string filter, int limit = 500)
    {
        var text = filter.Trim();
        if (text.Length == 0) return [];
        return objects
            .Select(o => (Object: o, Rank: MatchRank(o, text)))
            .Where(m => m.Rank >= 0)
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Object.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Object.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(m => m.Object)
            .ToList();
    }

    /// <summary>How well an object matches a filter: 0 exact name, 1 name prefix, 2 name contains, 3 schema.name
    /// contains, -1 no match. Lower is better.</summary>
    public static int MatchRank(DbObject obj, string text)
    {
        if (string.Equals(obj.Name, text, StringComparison.OrdinalIgnoreCase)) return 0;
        if (obj.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return 1;
        if (obj.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return 2;
        if (obj.FullName.Contains(text, StringComparison.OrdinalIgnoreCase)) return 3;
        return -1;
    }

    /// <summary>Foreign keys of other tables pointing at <paramref name="obj"/> (a self-reference is listed under
    /// Foreign keys only).</summary>
    private static IEnumerable<DbForeignKey> IncomingReferences(MetadataSnapshot snapshot, DbObject obj) =>
        snapshot.ReferencesTo(obj.Database, obj.Schema, obj.Name)
            .Where(f => !(string.Equals(f.Schema, obj.Schema, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(f.Table, obj.Name, StringComparison.OrdinalIgnoreCase)));

    private static bool SameDatabase(string objectDatabase, string database, bool isConnectionDatabase) =>
        string.Equals(objectDatabase, database, StringComparison.OrdinalIgnoreCase) ||
        (isConnectionDatabase && string.IsNullOrEmpty(objectDatabase));

    /// <summary>Users' schemas first; the engines' own (pg_catalog, information_schema, sys) last.</summary>
    private static int SchemaRank(string schema) => schema.ToLowerInvariant() switch
    {
        "pg_catalog" or "information_schema" or "sys" or "pg_toast" or "guest" => 1,
        _ => 0
    };
}
