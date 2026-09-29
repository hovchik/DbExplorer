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
/// matched value when the table has none), and a combined query that LEFT JOINs the related tables along their foreign
/// keys so a value found in several tables reads as one result.
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
        return Limited($"* FROM {quote(table.Schema)}.{quote(table.Name)} WHERE {where}", providerKey, limit);
    }

    /// <summary>
    /// One query per group of related matched tables in <paramref name="diagram"/> (tables connected through foreign
    /// keys, bridge tables included): the matched table highest in the hierarchy is the root, every other table is
    /// LEFT JOINed along the diagram's foreign keys, and rows are kept when any of the found rows takes part.
    /// Columns are named "Table.Column". Groups with a single table get no combined query.
    /// </summary>
    public static IReadOnlyList<DataMatchQuery> Combined(
        MetadataSnapshot snapshot, ErDiagram diagram, IReadOnlyList<DataMatch> matches, string providerKey, Func<string, string> quote, int limit)
    {
        var tables = diagram.Tables.ToDictionary(t => Key(t.Object), StringComparer.OrdinalIgnoreCase);
        var edges = diagram.Edges.Where(e => !ReferenceEquals(e.Child, e.Parent) && Key(e.Child.Object) != Key(e.Parent.Object)).ToList();
        var matchesByTable = matches.ToLookup(m => $"{m.Schema}.{m.Table}", StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queries = new List<DataMatchQuery>();

        // Roots: matched tables, parents (left-most) first, so each group starts from the top of its hierarchy.
        foreach (var root in diagram.Tables.Where(t => t.IsFocus).OrderBy(t => t.X).ThenBy(t => t.Y))
        {
            if (!visited.Add(Key(root.Object))) continue;

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Key(root.Object)] = "t0" };
            var order = new List<ErTable> { root };
            var joins = new StringBuilder();
            var queue = new Queue<ErTable>([root]);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var currentKey = Key(current.Object);
                foreach (var edge in edges)
                {
                    var childKey = Key(edge.Child.Object);
                    var parentKey = Key(edge.Parent.Object);
                    ErTable next;
                    if (string.Equals(childKey, currentKey, StringComparison.OrdinalIgnoreCase)) next = tables[parentKey];
                    else if (string.Equals(parentKey, currentKey, StringComparison.OrdinalIgnoreCase)) next = tables[childKey];
                    else continue;
                    if (!visited.Add(Key(next.Object))) continue;

                    var alias = "t" + aliases.Count;
                    aliases[Key(next.Object)] = alias;
                    order.Add(next);
                    queue.Enqueue(next);

                    var childColumns = ErDiagramBuilder.SplitColumns(edge.ForeignKey.Columns).ToList();
                    var parentColumns = ErDiagramBuilder.SplitColumns(edge.ForeignKey.ReferencedColumns).ToList();
                    var on = childColumns.Zip(parentColumns, (c, p) =>
                        $"{aliases[childKey]}.{quote(c)} = {aliases[parentKey]}.{quote(p)}");
                    joins.Append("\n  LEFT JOIN ").Append(quote(next.Object.Schema)).Append('.').Append(quote(next.Object.Name))
                         .Append(' ').Append(alias).Append(" ON ").AppendJoin(" AND ", on);
                }
            }

            var matched = order.Where(t => t.IsFocus).ToList();
            if (order.Count < 2 || matched.Count < 2) continue;

            var select = order.SelectMany(t => snapshot.ColumnsOf(t.Object.Database, t.Object.Schema, t.Object.Name)
                    .OrderBy(c => c.Ordinal)
                    .Select(c => $"{aliases[Key(t.Object)]}.{quote(c.Name)} AS {quote($"{t.Object.Name}.{c.Name}")}"))
                .ToList();
            if (select.Count == 0) select = order.Select(t => aliases[Key(t.Object)] + ".*").ToList();

            var where = Disjunction(matched.SelectMany(t => matchesByTable[Key(t.Object)]
                .Select(m => RowPredicate(m, providerKey, quote, aliases[Key(t.Object)]))));

            var body = $"{string.Join(",\n       ", select)}\n  FROM {quote(root.Object.Schema)}.{quote(root.Object.Name)} t0{joins}\n WHERE {where}";
            var title = "Combined: " + string.Join(" + ", matched.Select(t => t.Object.Name));
            queries.Add(new DataMatchQuery(title, Limited(body, providerKey, limit), order.Select(t => t.Object).ToList()));
        }

        return queries;
    }

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

    private static string Key(DbObject o) => $"{o.Schema}.{o.Name}";
}
