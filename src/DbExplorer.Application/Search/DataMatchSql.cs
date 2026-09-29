using System.Text;
using DbExplorer.Application.Diagram;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Search;

/// <summary>A generated read-only query over data search matches.</summary>
/// <param name="Tables">Tables the query reads, in join order.</param>
public sealed record DataMatchQuery(string Title, string Sql, IReadOnlyList<DbObject> Tables);

/// <summary>
/// Builds SELECTs that bring back the rows a data search found: one table's rows (located by primary key, or by the
/// matched value when the table has none), the rows of related tables that belong to them, and a combined query that
/// LEFT JOINs the related tables along their foreign keys so a value found in several tables reads as one result.
/// </summary>
public static class DataMatchSql
{
    private const string SqlServerProviderKey = "SqlServer";

    /// <summary>WHERE condition matching the found row: primary key when known, else the matched column's value.</summary>
    public static string RowPredicate(DataMatch match, string providerKey, Func<string, string> quote, string? alias = null)
    {
        var prefix = alias is null ? "" : alias + ".";
        var parts = match.KeyValues.Count > 0
            ? match.KeyValues.Select(kv => Equal(prefix + quote(kv.Key), kv.Value, providerKey))
            : [Equal(prefix + quote(match.Column), match.Value, providerKey)];
        return string.Join(" AND ", parts);
    }

    /// <summary>The found rows of one table (all columns), at most <paramref name="limit"/>.</summary>
    public static string SelectRows(DbObject table, IEnumerable<DataMatch> matches, string providerKey, Func<string, string> quote, int limit)
    {
        var where = Disjunction(matches.Select(m => RowPredicate(m, providerKey, quote)));
        return Limited($"* FROM {Name(table, quote)} WHERE {where}", providerKey, limit);
    }

    /// <summary>
    /// One query per group of related matched tables in <paramref name="diagram"/>: the matched table highest in the
    /// hierarchy is the root; the other matched tables and the tables linking them are LEFT JOINed along the diagram's
    /// foreign keys, and so is every table those rows reference (lookups, which never multiply rows). Rows are kept when
    /// any of the found rows takes part. Columns are named "Table.Column". A group of a single table gets no query.
    /// </summary>
    public static IReadOnlyList<DataMatchQuery> Combined(
        MetadataSnapshot snapshot, ErDiagram diagram, IReadOnlyList<DataMatch> matches, string providerKey, Func<string, string> quote, int limit)
    {
        var edges = diagram.Edges.Where(e => !ReferenceEquals(e.Child, e.Parent)).ToList();
        var visited = new HashSet<ErTable>(ReferenceEqualityComparer.Instance);
        var queries = new List<DataMatchQuery>();

        // Roots: matched tables, parents (left-most) first, so each group starts from the top of its hierarchy.
        foreach (var root in diagram.Tables.Where(t => t.IsFocus).OrderBy(t => t.X).ThenBy(t => t.Y))
        {
            if (!visited.Add(root)) continue;

            var aliases = new Dictionary<ErTable, string>(ReferenceEqualityComparer.Instance) { [root] = "t0" };
            var order = new List<ErTable> { root };
            var joins = new StringBuilder();
            var queue = new Queue<ErTable>([root]);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var edge in edges)
                {
                    ErTable next;
                    if (ReferenceEquals(edge.Child, current)) next = edge.Parent;                          // lookup: always
                    else if (ReferenceEquals(edge.Parent, current) && (edge.Child.IsFocus || edge.Child.IsLink)) next = edge.Child;
                    else continue;
                    if (!visited.Add(next)) continue;

                    var alias = "t" + aliases.Count;
                    aliases[next] = alias;
                    order.Add(next);
                    // Only matched and linking tables are expanded further; a plain lookup table is a leaf.
                    if (next.IsFocus || next.IsLink) queue.Enqueue(next);

                    var on = JoinColumns(edge.ForeignKey, aliases[edge.Child], aliases[edge.Parent], quote);
                    joins.Append("\n  LEFT JOIN ").Append(Name(next.Object, quote)).Append(' ').Append(alias).Append(" ON ").Append(on);
                }
            }

            // Lookup-only leaves may be reached from other groups too.
            foreach (var t in order.Where(t => !t.IsFocus && !t.IsLink)) visited.Remove(t);
            if (order.Count < 2) continue;

            var select = order.SelectMany(t => snapshot.ColumnsOf(t.Object.Database, t.Object.Schema, t.Object.Name)
                    .OrderBy(c => c.Ordinal)
                    .Select(c => $"{aliases[t]}.{quote(c.Name)} AS {quote($"{t.Object.Name}.{c.Name}")}"))
                .ToList();
            if (select.Count == 0) select = order.Select(t => aliases[t] + ".*").ToList();

            var matched = order.Where(t => t.IsFocus).ToList();
            var where = Disjunction(matched.SelectMany(t => matches.Where(m => Same(m, t.Object))
                .Select(m => RowPredicate(m, providerKey, quote, aliases[t]))));

            var body = $"{string.Join(",\n       ", select)}\n  FROM {Name(root.Object, quote)} t0{joins}\n WHERE {where}";
            var title = "Combined: " + string.Join(" + ", order.Select(t => t.Object.Name));
            queries.Add(new DataMatchQuery(title, Limited(body, providerKey, limit), order.Select(t => t.Object).ToList()));
        }

        return queries;
    }

    /// <summary>
    /// For every foreign key touching a table with found rows: the rows on the other side that belong to those found
    /// rows — the parent rows they reference, and the child rows referencing them. <see cref="DataMatchQuery.Tables"/>
    /// starts with the table the rows come from, followed by the table the value was found in.
    /// </summary>
    public static IReadOnlyList<DataMatchQuery> Related(
        ErDiagram diagram, IReadOnlyList<DataMatch> matches, string providerKey, Func<string, string> quote, int limit)
    {
        var queries = new List<DataMatchQuery>();
        foreach (var edge in diagram.Edges)
        {
            // A self reference on a found table yields both its parent and its child rows.
            if (edge.Child.IsFocus) queries.Add(Build(edge, found: edge.Child, other: edge.Parent, foundIsChild: true));
            if (edge.Parent.IsFocus) queries.Add(Build(edge, found: edge.Parent, other: edge.Child, foundIsChild: false));
        }
        return queries;

        DataMatchQuery Build(ErEdge edge, ErTable found, ErTable other, bool foundIsChild)
        {
            var on = foundIsChild
                ? JoinColumns(edge.ForeignKey, childAlias: "f", parentAlias: "r", quote)
                : JoinColumns(edge.ForeignKey, childAlias: "r", parentAlias: "f", quote);
            var where = Disjunction(matches.Where(m => Same(m, found.Object)).Select(m => RowPredicate(m, providerKey, quote, "f")));
            var body = $"r.* FROM {Name(other.Object, quote)} r\n WHERE EXISTS (SELECT 1 FROM {Name(found.Object, quote)} f\n                WHERE {on}\n                  AND ({where}))";
            var columns = string.Join(", ", ErDiagramBuilder.SplitColumns(edge.ForeignKey.Columns));
            var title = foundIsChild
                ? $"{other.Object.Name} · referenced by {found.Object.Name}.{columns}"
                : $"{other.Object.Name} · referencing {found.Object.Name} by {columns}";
            return new DataMatchQuery(title, Limited(body, providerKey, limit), [other.Object, found.Object]);
        }
    }

    private static string JoinColumns(DbForeignKey fk, string childAlias, string parentAlias, Func<string, string> quote) =>
        string.Join(" AND ", ErDiagramBuilder.SplitColumns(fk.Columns).Zip(ErDiagramBuilder.SplitColumns(fk.ReferencedColumns),
            (c, p) => $"{childAlias}.{quote(c)} = {parentAlias}.{quote(p)}"));

    private static string Name(DbObject o, Func<string, string> quote) => $"{quote(o.Schema)}.{quote(o.Name)}";

    private static bool Same(DataMatch m, DbObject o) =>
        string.Equals(m.Database, o.Database, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(m.Schema, o.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(m.Table, o.Name, StringComparison.OrdinalIgnoreCase);

    public static string Literal(string? value, string providerKey) =>
        value is null ? "NULL"
        : (IsSqlServer(providerKey) ? "N'" : "'") + value.Replace("'", "''") + "'";

    private static string Equal(string column, string? value, string providerKey) =>
        value is null ? $"{column} IS NULL" : $"{column} = {Literal(value, providerKey)}";

    private static string Disjunction(IEnumerable<string> predicates)
    {
        var distinct = predicates.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count switch
        {
            0 => "1 = 0",
            1 => distinct[0],
            _ => string.Join("\n    OR ", distinct.Select(p => p.Contains(" AND ") ? $"({p})" : p))
        };
    }

    private static string Limited(string body, string providerKey, int limit) =>
        IsSqlServer(providerKey) ? $"SELECT TOP ({limit}) {body};" : $"SELECT {body}\n LIMIT {limit};";

    private static bool IsSqlServer(string providerKey) =>
        string.Equals(providerKey, SqlServerProviderKey, StringComparison.OrdinalIgnoreCase);

}
