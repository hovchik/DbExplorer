using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Diagram;

public enum ErColumnMode
{
    KeysOnly,
    AllColumns,
    NoColumns
}

public sealed record ErColumn(string Name, string DataType, bool IsPrimaryKey, bool IsForeignKey, bool IsNullable);

public sealed record ErTable
{
    public required DbObject Object { get; init; }
    public required IReadOnlyList<ErColumn> Columns { get; init; }
    public int HiddenColumnCount { get; init; }
    public bool IsFocus { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    public string Title => Object.FullName;
    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>Child (referencing) table → parent (referenced) table.</summary>
public sealed record ErEdge(ErTable Child, ErTable Parent, DbForeignKey ForeignKey);

public sealed record ErDiagram(IReadOnlyList<ErTable> Tables, IReadOnlyList<ErEdge> Edges, double Width, double Height, int OmittedTables)
{
    public static readonly ErDiagram Empty = new([], [], 0, 0, 0);
}

/// <summary>
/// Builds an entity-relationship diagram from the cached foreign keys (no server round-trip).
/// Referenced (parent) tables are laid out to the left of the tables that reference them.
/// </summary>
public static class ErDiagramBuilder
{
    public const double TableWidth = 230;
    public const double HeaderHeight = 28;
    public const double RowHeight = 18;
    public const double Padding = 6;
    public const double ColumnGap = 90;
    public const double RowGap = 26;
    public const double Margin = 20;
    private const int MaxColumnsPerTable = 30;

    /// <summary>Tables within <paramref name="depth"/> foreign-key hops of <paramref name="focus"/>.</summary>
    public static ErDiagram AroundTable(MetadataSnapshot snapshot, DbObject focus, int depth, ErColumnMode mode, int maxTables = 60)
    {
        var graph = new FkGraph(snapshot, focus.Database);
        var focusKey = Key(focus);
        if (!graph.Tables.ContainsKey(focusKey)) return ErDiagram.Empty;
        var level = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [focusKey] = 0 };
        var hops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [focusKey] = 0 };
        var queue = new Queue<string>([focusKey]);
        var omitted = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (hops[current] >= depth) continue;

            // Parents one column to the left, children one to the right.
            var neighbours = graph.ParentsOf(current).Select(p => (p, level[current] - 1))
                .Concat(graph.ChildrenOf(current).Select(c => (c, level[current] + 1)));
            foreach (var (next, lvl) in neighbours)
            {
                if (level.ContainsKey(next)) continue;
                if (level.Count >= maxTables) { omitted++; continue; }
                level[next] = lvl;
                hops[next] = hops[current] + 1;
                queue.Enqueue(next);
            }
        }

        return Layout(snapshot, graph, level, focusKey, mode, omitted);
    }

    /// <summary>Every table in a schema (or all schemas when null), capped at <paramref name="maxTables"/>
    /// with the most-connected tables kept first.</summary>
    public static ErDiagram WholeSchema(MetadataSnapshot snapshot, string? database, string? schema, ErColumnMode mode, int maxTables = 80)
    {
        var graph = new FkGraph(snapshot, database);
        var candidates = graph.Tables.Values
            .Where(t => schema is null || string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase))
            .Select(Key)
            .ToList();
        var chosen = candidates
            .OrderByDescending(k => graph.ParentsOf(k).Count() + graph.ChildrenOf(k).Count())
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Take(maxTables)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Level = length of the longest chain of parents inside the chosen set (cycle-safe).
        var level = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in chosen) Depth(k, []);

        return Layout(snapshot, graph, level, focusKey: null, mode, candidates.Count - chosen.Count);

        int Depth(string key, HashSet<string> path)
        {
            if (level.TryGetValue(key, out var known)) return known;
            if (!path.Add(key)) return 0;
            var parents = graph.ParentsOf(key).Where(p => chosen.Contains(p) && p != key).ToList();
            var d = parents.Count == 0 ? 0 : parents.Max(p => Depth(p, path)) + 1;
            path.Remove(key);
            level[key] = d;
            return d;
        }
    }

    private static ErDiagram Layout(
        MetadataSnapshot snapshot, FkGraph graph, Dictionary<string, int> level, string? focusKey, ErColumnMode mode, int omitted)
    {
        if (level.Count == 0) return ErDiagram.Empty;

        var minLevel = level.Values.Min();
        var columns = level.GroupBy(kv => kv.Value - minLevel).OrderBy(g => g.Key)
            .Select(g => g.Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();

        // One barycenter pass: order each column by the average position of its already-placed neighbours.
        var position = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            for (var i = 0; i < column.Count; i++) position.TryAdd(column[i], i);
            column.Sort((a, b) =>
            {
                var byIsolation = IsIsolated(a).CompareTo(IsIsolated(b));
                return byIsolation != 0 ? byIsolation : Barycenter(a).CompareTo(Barycenter(b));
            });
            for (var i = 0; i < column.Count; i++) position[column[i]] = i;
        }

        var tables = new Dictionary<string, ErTable>(StringComparer.OrdinalIgnoreCase);
        double height = 0;
        for (var c = 0; c < columns.Count; c++)
        {
            var y = Margin;
            foreach (var key in columns[c])
            {
                var table = CreateTable(snapshot, graph, key, mode, string.Equals(key, focusKey, StringComparison.OrdinalIgnoreCase)) with
                {
                    X = Margin + c * (TableWidth + ColumnGap),
                    Y = y
                };
                tables[key] = table;
                y += table.Height + RowGap;
            }
            height = Math.Max(height, y);
        }

        var edges = graph.ForeignKeys
            .Where(fk => tables.ContainsKey(ChildKey(fk)) && tables.ContainsKey(ParentKey(fk)))
            .Select(fk => new ErEdge(tables[ChildKey(fk)], tables[ParentKey(fk)], fk))
            .ToList();

        var width = Margin * 2 + columns.Count * TableWidth + (columns.Count - 1) * ColumnGap;
        return new ErDiagram(tables.Values.ToList(), edges, width, height - RowGap + Margin, omitted);

        bool IsIsolated(string key) =>
            !graph.ParentsOf(key).Concat(graph.ChildrenOf(key)).Any(n => level.ContainsKey(n) && !string.Equals(n, key, StringComparison.OrdinalIgnoreCase));

        double Barycenter(string key)
        {
            var placed = graph.ParentsOf(key).Concat(graph.ChildrenOf(key))
                .Where(n => level.ContainsKey(n) && level[n] != level[key] && position.ContainsKey(n))
                .Select(n => position[n]).ToList();
            return placed.Count == 0 ? position.GetValueOrDefault(key) : placed.Average();
        }
    }

    private static ErTable CreateTable(MetadataSnapshot snapshot, FkGraph graph, string key, ErColumnMode mode, bool isFocus)
    {
        var obj = graph.Tables[key];
        var fkColumns = snapshot.ForeignKeysOf(obj.Database, obj.Schema, obj.Name)
            .SelectMany(fk => SplitColumns(fk.Columns))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var all = snapshot.ColumnsOf(obj.Database, obj.Schema, obj.Name)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ErColumn(c.Name, c.DataType, c.IsPrimaryKey, fkColumns.Contains(c.Name), c.IsNullable))
            .ToList();

        var shown = mode switch
        {
            ErColumnMode.NoColumns => [],
            ErColumnMode.KeysOnly => all.Where(c => c.IsPrimaryKey || c.IsForeignKey).ToList(),
            _ => all
        };
        var hidden = all.Count - Math.Min(shown.Count, MaxColumnsPerTable);
        if (shown.Count > MaxColumnsPerTable) shown = shown.Take(MaxColumnsPerTable).ToList();

        var rows = shown.Count + (hidden > 0 && mode != ErColumnMode.NoColumns ? 1 : 0);
        return new ErTable
        {
            Object = obj,
            Columns = shown,
            HiddenColumnCount = mode == ErColumnMode.NoColumns ? 0 : hidden,
            IsFocus = isFocus,
            Width = TableWidth,
            Height = HeaderHeight + (rows == 0 ? 0 : rows * RowHeight + Padding * 2)
        };
    }

    /// <summary>Mermaid <c>erDiagram</c> text, pasteable into GitHub/GitLab markdown, Notion, Confluence, etc.</summary>
    public static string ToMermaid(ErDiagram diagram)
    {
        var sb = new StringBuilder("erDiagram\n");
        foreach (var t in diagram.Tables.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("    ").Append(MermaidId(t.Object)).Append("[\"").Append(t.Title.Replace("\"", "'")).Append("\"]");
            if (t.Columns.Count == 0) { sb.Append('\n'); continue; }
            sb.Append(" {\n");
            foreach (var c in t.Columns)
            {
                var keys = string.Join(", ", new[] { c.IsPrimaryKey ? "PK" : null, c.IsForeignKey ? "FK" : null }.Where(k => k is not null));
                sb.Append("        ").Append(MermaidType(c.DataType)).Append(' ').Append(MermaidWord(c.Name));
                if (keys.Length > 0) sb.Append(' ').Append(keys);
                sb.Append('\n');
            }
            sb.Append("    }\n");
        }

        foreach (var e in diagram.Edges.OrderBy(e => e.ForeignKey.Name, StringComparer.OrdinalIgnoreCase))
        {
            var fkColumns = SplitColumns(e.ForeignKey.Columns);
            var optional = e.Child.Columns.Any(c => c.IsNullable && fkColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
            sb.Append("    ").Append(MermaidId(e.Parent.Object))
              .Append(optional ? " |o--o{ " : " ||--o{ ")
              .Append(MermaidId(e.Child.Object))
              .Append(" : \"").Append(e.ForeignKey.Name.Replace("\"", "'")).Append("\"\n");
        }
        return sb.ToString();
    }

    private static string MermaidId(DbObject o) => MermaidWord($"{o.Schema}_{o.Name}");

    private static string MermaidWord(string s)
    {
        var w = Regex.Replace(s, @"[^A-Za-z0-9_]", "_");
        return w.Length > 0 && char.IsLetter(w[0]) ? w : "t_" + w;
    }

    private static string MermaidType(string type)
    {
        var t = Regex.Replace(type.Replace(",", "-").Replace(' ', '_'), @"[^A-Za-z0-9_\-\(\)\[\]]", "");
        return t.Length > 0 && char.IsLetter(t[0]) ? t : "type";
    }

    public static IEnumerable<string> SplitColumns(string? columns) =>
        (columns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Key(DbObject o) => $"{o.Schema}.{o.Name}";
    private static string ChildKey(DbForeignKey fk) => $"{fk.Schema}.{fk.Table}";
    private static string ParentKey(DbForeignKey fk) => $"{fk.ReferencedSchema}.{fk.ReferencedTable}";

    /// <summary>Tables and foreign keys of one database, keyed case-insensitively by schema.name.</summary>
    private sealed class FkGraph
    {
        public Dictionary<string, DbObject> Tables { get; }
        public List<DbForeignKey> ForeignKeys { get; }
        private readonly ILookup<string, string> _parents;
        private readonly ILookup<string, string> _children;

        public FkGraph(MetadataSnapshot snapshot, string? database)
        {
            bool InDb(string db) => string.IsNullOrEmpty(database) || string.Equals(db, database, StringComparison.OrdinalIgnoreCase);

            Tables = snapshot.Objects
                .Where(o => o.Type is DbObjectType.Table or DbObjectType.ForeignTable && InDb(o.Database))
                .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            ForeignKeys = snapshot.ForeignKeys
                .Where(fk => InDb(fk.Database) && Tables.ContainsKey(ChildKey(fk)) && Tables.ContainsKey(ParentKey(fk)))
                .ToList();
            _parents = ForeignKeys.ToLookup(ChildKey, ParentKey, StringComparer.OrdinalIgnoreCase);
            _children = ForeignKeys.ToLookup(ParentKey, ChildKey, StringComparer.OrdinalIgnoreCase);
        }

        public IEnumerable<string> ParentsOf(string key) => _parents[key].Distinct(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> ChildrenOf(string key) => _children[key].Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
