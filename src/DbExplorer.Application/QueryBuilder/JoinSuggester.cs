using System.Text.RegularExpressions;
using DbExplorer.Application.Diagram;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.QueryBuilder;

/// <summary>A join the builder proposes for a newly placed table, and where it came from.</summary>
public sealed record SuggestedJoin(IReadOnlyList<JoinCondition> Conditions, string Reason, bool FromForeignKey);

/// <summary>
/// Joins a table placed in the builder to the tables already there: foreign keys first (either direction, accepted
/// virtual ones included), and only when there is none, a column named like the other table's key
/// (Orders.CustomerId → Customers.CustomerId, orders.customer_id → customers.id).
/// </summary>
public static partial class JoinSuggester
{
    public static IReadOnlyList<SuggestedJoin> Suggest(MetadataSnapshot snapshot, string database, IReadOnlyList<QueryTable> placed, QueryTable added)
    {
        var others = placed.Where(t => !string.Equals(t.Alias, added.Alias, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count == 0) return [];

        var keys = snapshot.ForeignKeysOf(database, added.Schema, added.Name)
            .Concat(snapshot.ReferencesTo(database, added.Schema, added.Name))
            .Distinct()
            .ToList();

        var result = new List<SuggestedJoin>();
        foreach (var fk in keys)
        {
            var childColumns = ErDiagramBuilder.SplitColumns(fk.Columns).ToList();
            var parentColumns = ErDiagramBuilder.SplitColumns(fk.ReferencedColumns).ToList();
            if (childColumns.Count == 0 || childColumns.Count != parentColumns.Count) continue;

            // Each key joins to one table only (the first placed copy), so a table placed twice is not joined twice.
            foreach (var other in others)
            {
                List<JoinCondition>? conditions = null;
                if (Is(added, fk.Schema, fk.Table) && Is(other, fk.ReferencedSchema, fk.ReferencedTable))
                    conditions = childColumns.Select((c, i) => new JoinCondition(other.Alias, parentColumns[i], added.Alias, c)).ToList();
                else if (Is(added, fk.ReferencedSchema, fk.ReferencedTable) && Is(other, fk.Schema, fk.Table))
                    conditions = childColumns.Select((c, i) => new JoinCondition(other.Alias, c, added.Alias, parentColumns[i])).ToList();
                if (conditions is null) continue;

                // A self reference adds both directions of the same key; keep the first.
                if (result.Any(r => SameConditions(r.Conditions, conditions))) break;
                result.Add(new SuggestedJoin(conditions, (fk.IsVirtual ? "accepted relationship " : "foreign key ") + fk.Name, true));
                break;
            }
        }

        if (result.Count > 0) return result;
        foreach (var other in others)
        {
            if (Guess(snapshot, database, other, added) is { } a) { result.Add(a); continue; }
            if (Guess(snapshot, database, added, other) is { } b) result.Add(b with { Conditions = b.Conditions.Select(Reverse).ToList() });
        }
        return result;
    }

    /// <summary>child.{parent}Id / {parent}_id → parent's single key column, when the types are compatible.</summary>
    private static SuggestedJoin? Guess(MetadataSnapshot snapshot, string database, QueryTable parent, QueryTable child)
    {
        if (Is(parent, child.Schema, child.Name)) return null;
        var keys = snapshot.ColumnsOf(database, parent.Schema, parent.Name).Where(c => c.IsPrimaryKey).ToList();
        if (keys.Count != 1) return null;
        var key = keys[0];

        var singular = Singular(parent.Name);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { singular + "Id", singular + "_id", parent.Name + "Id", parent.Name + "_id" };
        if (!string.Equals(key.Name, "id", StringComparison.OrdinalIgnoreCase)) names.Add(key.Name);

        var match = snapshot.ColumnsOf(database, child.Schema, child.Name)
            .FirstOrDefault(c => names.Contains(c.Name));
        if (match is null || !string.Equals(BaseType(match), BaseType(key), StringComparison.OrdinalIgnoreCase)) return null;
        return new SuggestedJoin([new JoinCondition(parent.Alias, key.Name, child.Alias, match.Name)],
            $"guessed from the name {child.Name}.{match.Name}", false);
    }

    private static string BaseType(DbColumn c) => string.IsNullOrEmpty(c.BaseType) ? c.DataType : c.BaseType;

    private static JoinCondition Reverse(JoinCondition c) =>
        new(c.RightAlias, c.RightColumn, c.LeftAlias, c.LeftColumn, QuerySqlBuilder.Flip(c.Kind));

    private static bool SameConditions(IReadOnlyList<JoinCondition> a, IReadOnlyList<JoinCondition> b) =>
        a.Count == b.Count && a.All(x => b.Any(y => x == y || x == Reverse(y)));

    private static bool Is(QueryTable t, string schema, string name) =>
        string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase);

    private static string Singular(string name) =>
        name.EndsWith("ies", StringComparison.OrdinalIgnoreCase) ? name[..^3] + "y"
        : name.EndsWith("sses", StringComparison.OrdinalIgnoreCase) ? name[..^2]
        : name.EndsWith('s') && !name.EndsWith("ss", StringComparison.OrdinalIgnoreCase) ? name[..^1]
        : name;

    /// <summary>A short alias from the name's word initials (OrderLines → ol, order_items → oi, Customers → c),
    /// numbered when taken (c2), never a keyword.</summary>
    public static string AliasFor(string tableName, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var words = WordStart().Matches(tableName).Select(m => char.ToLowerInvariant(m.Value[0])).ToList();
        var stem = words.Count == 0 ? "t" : new string(words.Take(4).ToArray());
        if (!char.IsAsciiLetter(stem[0])) stem = "t" + stem;
        if (!used.Contains(stem) && !Keywords.Contains(stem)) return stem;
        for (var i = 2; ; i++)
            if (!used.Contains(stem + i)) return stem + i;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        { "as", "at", "by", "do", "go", "if", "in", "is", "of", "on", "or", "to", "and", "asc", "end", "for", "key", "not", "set", "top", "all", "any", "add", "use" };

    /// <summary>Starts of words: after _ or space or a digit run, and capitals that follow a lower-case letter.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z0-9]|(?<=[a-z0-9])[A-Z]")]
    private static partial Regex WordStart();
}
