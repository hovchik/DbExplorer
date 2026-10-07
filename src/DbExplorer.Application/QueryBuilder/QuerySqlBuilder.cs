using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Copy;

namespace DbExplorer.Application.QueryBuilder;

/// <summary>
/// Writes the SELECT a <see cref="QueryDesign"/> describes. Tables are joined in the order they were placed, each one
/// on every condition it has with the tables before it; tables with no condition are cross joined (with a warning).
/// When any column is aggregated, every other output column is grouped by.
/// </summary>
public static partial class QuerySqlBuilder
{
    private const string Indent = "    ";

    public static BuiltQuery Build(QueryDesign design, SqlDialect dialect)
    {
        var warnings = new List<string>();
        if (design.Tables.Count == 0) return new BuiltQuery("", warnings);

        var tables = design.Tables.ToDictionary(t => t.Alias, StringComparer.OrdinalIgnoreCase);
        var columns = design.Columns.Where(c => tables.ContainsKey(c.TableAlias)).ToList();
        string Expr(QueryColumn c) => Expression(c, dialect);

        var sql = new StringBuilder("SELECT");
        if (design.Distinct) sql.Append(" DISTINCT");
        var limit = design.Limit is > 0 ? design.Limit.Value : 0;
        if (limit > 0 && dialect.ProviderKey == SqlDialect.SqlServerKey) sql.Append(" TOP (").Append(limit.ToString(CultureInfo.InvariantCulture)).Append(')');

        var output = columns.Where(c => c.Output).ToList();
        if (output.Count == 0) sql.Append(" *");
        else
        {
            sql.AppendLine();
            sql.Append(string.Join(",\n", output.Select(c => Indent + Expr(c) +
                (string.IsNullOrWhiteSpace(c.OutputAlias) ? "" : " AS " + Identifier(c.OutputAlias.Trim(), dialect)))));
        }

        sql.Append("\nFROM ");
        AppendFrom(sql, design, dialect, warnings);

        var where = columns.Where(c => c.Aggregate == ColumnAggregate.None)
            .Select(c => Condition(Expr(c), c.Filter)).OfType<string>().ToList();
        if (where.Count > 0) sql.Append("\nWHERE ").Append(string.Join("\n" + Indent + "AND ", where));

        var grouping = columns.Any(c => c.Aggregate != ColumnAggregate.None);
        if (grouping)
        {
            var groupBy = output.Where(c => c.Aggregate == ColumnAggregate.None).Select(Expr)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (groupBy.Count > 0) sql.Append("\nGROUP BY ").Append(string.Join(", ", groupBy));

            var having = columns.Where(c => c.Aggregate != ColumnAggregate.None)
                .Select(c => Condition(Expr(c), c.Filter)).OfType<string>().ToList();
            if (having.Count > 0) sql.Append("\nHAVING ").Append(string.Join("\n" + Indent + "AND ", having));

            foreach (var c in columns.Where(c => c.Sort != SortDirection.None && c.Aggregate == ColumnAggregate.None && !c.Output))
                warnings.Add($"Sorting by {c.TableAlias}.{c.Column} fails: it is neither grouped nor aggregated. Tick it as output or give it an aggregate.");
        }

        // An aliased output column sorts by its alias (ORDER BY units DESC), which both engines accept.
        var orderBy = columns.Where(c => c.Sort != SortDirection.None)
            .Select(c => (c.Output && !string.IsNullOrWhiteSpace(c.OutputAlias) ? Identifier(c.OutputAlias.Trim(), dialect) : Expr(c)) +
                         (c.Sort == SortDirection.Descending ? " DESC" : "")).ToList();
        if (orderBy.Count > 0) sql.Append("\nORDER BY ").Append(string.Join(", ", orderBy));
        if (design.Distinct && columns.Any(c => c.Sort != SortDirection.None && !c.Output))
            warnings.Add("With DISTINCT, every sorted column must also be an output column.");

        if (limit > 0 && dialect.ProviderKey != SqlDialect.SqlServerKey) sql.Append("\nLIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));
        sql.Append(';');
        return new BuiltQuery(sql.ToString(), warnings);
    }

    /// <summary>FROM first [JOIN next ON …]…: each table joins on its conditions with the tables placed before it.</summary>
    private static void AppendFrom(StringBuilder sql, QueryDesign design, SqlDialect dialect, List<string> warnings)
    {
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = design.Tables.ToList();
        var first = true;

        while (remaining.Count > 0)
        {
            // Prefer the first table (in placing order) that has a condition with what is already in the FROM.
            var next = first ? remaining[0] : remaining.FirstOrDefault(t => ConditionsBetween(design, placed, t.Alias).Any()) ?? remaining[0];
            remaining.Remove(next);
            var source = TableSource(next, dialect);

            if (first)
            {
                sql.Append(source);
                first = false;
            }
            else
            {
                var conditions = ConditionsBetween(design, placed, next.Alias).ToList();
                if (conditions.Count == 0)
                {
                    sql.Append('\n').Append(Indent).Append("CROSS JOIN ").Append(source);
                    warnings.Add($"{next.Name} ({next.Alias}) is not joined to the other tables, so every row pairs with every row (CROSS JOIN). Drag a column onto another table to join them.");
                }
                else
                {
                    // The kind reads from the left side of its condition: flip it when the new table is on the left.
                    var head = conditions[0];
                    var kind = string.Equals(head.RightAlias, next.Alias, StringComparison.OrdinalIgnoreCase) ? head.Kind : Flip(head.Kind);
                    sql.Append('\n').Append(Indent).Append(JoinKeyword(kind)).Append(' ').Append(source).Append(" ON ")
                        .Append(string.Join(" AND ", conditions.Select(c =>
                            $"{Identifier(c.LeftAlias, dialect)}.{Identifier(c.LeftColumn, dialect)} = {Identifier(c.RightAlias, dialect)}.{Identifier(c.RightColumn, dialect)}")));
                }
            }
            placed.Add(next.Alias);
        }
    }

    private static IEnumerable<JoinCondition> ConditionsBetween(QueryDesign design, HashSet<string> placed, string alias) =>
        design.Joins.Where(j =>
            string.Equals(j.LeftAlias, alias, StringComparison.OrdinalIgnoreCase) && placed.Contains(j.RightAlias) ||
            string.Equals(j.RightAlias, alias, StringComparison.OrdinalIgnoreCase) && placed.Contains(j.LeftAlias));

    public static JoinKind Flip(JoinKind kind) => kind switch
    {
        JoinKind.Left => JoinKind.Right,
        JoinKind.Right => JoinKind.Left,
        _ => kind
    };

    private static string JoinKeyword(JoinKind kind) => kind switch
    {
        JoinKind.Left => "LEFT JOIN",
        JoinKind.Right => "RIGHT JOIN",
        JoinKind.Full => "FULL JOIN",
        _ => "INNER JOIN"
    };

    private static string TableSource(QueryTable t, SqlDialect dialect) =>
        $"{Identifier(t.Schema, dialect)}.{Identifier(t.Name, dialect)} AS {Identifier(t.Alias, dialect)}";

    /// <summary>alias.column, alias.*, or the aggregate around it.</summary>
    public static string Expression(QueryColumn c, SqlDialect dialect)
    {
        var alias = Identifier(c.TableAlias, dialect);
        if (c.Column == "*")
            return c.Aggregate is ColumnAggregate.Count or ColumnAggregate.CountDistinct ? "COUNT(*)" : alias + ".*";

        var column = alias + "." + Identifier(c.Column, dialect);
        return c.Aggregate switch
        {
            ColumnAggregate.Count => $"COUNT({column})",
            ColumnAggregate.CountDistinct => $"COUNT(DISTINCT {column})",
            ColumnAggregate.Sum => $"SUM({column})",
            ColumnAggregate.Avg => $"AVG({column})",
            ColumnAggregate.Min => $"MIN({column})",
            ColumnAggregate.Max => $"MAX({column})",
            _ => column
        };
    }

    /// <summary>The name as is when the engine reads it that way unquoted; quoted otherwise (spaces, keywords, and on
    /// PostgreSQL any capital letter, which it would otherwise fold to lower case).</summary>
    public static string Identifier(string name, SqlDialect dialect)
    {
        var plain = PlainIdentifier().IsMatch(name) && !Reserved.Contains(name) &&
                    (dialect.ProviderKey == SqlDialect.SqlServerKey || name == name.ToLowerInvariant());
        return plain ? name : dialect.Quote(name);
    }

    /// <summary>
    /// The WHERE/HAVING condition a filter cell means for <paramref name="expression"/>, or null when it is empty.
    /// An operator or keyword at the start is kept (<c>&gt;= 10</c>, <c>LIKE 'a%'</c>, <c>IS NOT NULL</c>, <c>IN (1,2)</c>,
    /// <c>BETWEEN 1 AND 5</c>); a bare value means equals, or LIKE when it holds % or _. Bare words are quoted as text.
    /// </summary>
    public static string? Condition(string expression, string? filter)
    {
        var text = filter?.Trim() ?? "";
        if (text.Length == 0) return null;

        var op = OperatorPrefix().Match(text);
        if (op.Success)
        {
            var value = text[op.Length..].Trim();
            return $"{expression} {op.Groups["op"].Value} {Value(value)}";
        }

        var keyword = KeywordPrefix().Match(text);
        if (keyword.Success)
        {
            var word = Regex.Replace(keyword.Groups["kw"].Value.ToUpperInvariant(), @"\s+", " ");
            var rest = text[keyword.Length..].Trim();
            if (word.EndsWith("LIKE", StringComparison.Ordinal)) rest = Value(rest);
            return $"{expression} {word}{(rest.Length > 0 ? " " + rest : "")}";
        }

        return text.Contains('%') && !IsQuoted(text) ? $"{expression} LIKE {Value(text)}" : $"{expression} = {Value(text)}";
    }

    /// <summary>Numbers, quoted text, NULL/TRUE/FALSE, parameters, alias.column and function calls stay; anything else
    /// (a word, a date, an e-mail address) becomes text.</summary>
    private static string Value(string value)
    {
        if (value.Length == 0 || IsQuoted(value) || Number().IsMatch(value)) return value;
        if (value.ToUpperInvariant() is "NULL" or "TRUE" or "FALSE" or "CURRENT_DATE" or "CURRENT_TIMESTAMP") return value;
        if (value.Contains('(') || value[0] is '@' or ':' or '"' or '[' || ColumnReference().IsMatch(value)) return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    private static bool IsQuoted(string value) => value.Length >= 2 && value[0] == '\'' && value[^1] == '\'';

    [GeneratedRegex(@"^(?<op><>|!=|>=|<=|=|<|>)")]
    private static partial Regex OperatorPrefix();

    [GeneratedRegex(@"^(?<kw>NOT\s+LIKE|NOT\s+ILIKE|LIKE|ILIKE|NOT\s+IN|IN|NOT\s+BETWEEN|BETWEEN|IS)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeywordPrefix();

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^[A-Za-z_]\w*\.[A-Za-z_]\w*$")]
    private static partial Regex ColumnReference();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PlainIdentifier();

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL", "AND", "ANY", "AS", "ASC", "BETWEEN", "BY", "CASE", "CHECK", "COLUMN", "CONSTRAINT", "CREATE", "CROSS",
        "CURRENT", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP", "ELSE", "END", "EXISTS", "FOR", "FOREIGN", "FROM",
        "FULL", "GROUP", "HAVING", "IN", "INDEX", "INNER", "INSERT", "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE", "LIMIT",
        "NOT", "NULL", "OF", "ON", "OR", "ORDER", "OUTER", "PRIMARY", "REFERENCES", "RIGHT", "SELECT", "SET", "TABLE",
        "THEN", "TO", "TOP", "UNION", "UNIQUE", "UPDATE", "USER", "VALUES", "VIEW", "WHEN", "WHERE", "WITH"
    };
}
