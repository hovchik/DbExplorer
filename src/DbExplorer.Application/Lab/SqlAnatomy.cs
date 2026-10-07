using DbExplorer.Application.Query;

namespace DbExplorer.Application.Lab;

/// <summary>One table source of a FROM clause and how it is joined to the ones before it.</summary>
/// <param name="Join">"" for the first source, otherwise the join as written upper-cased ("INNER JOIN", "LEFT JOIN", ",", "CROSS APPLY", …).</param>
/// <param name="Source">The table source text (name, alias, hints; a derived table in parentheses).</param>
/// <param name="Condition">The ON condition, or "USING (…)" text; null for comma / CROSS joins.</param>
/// <param name="Name">The (possibly qualified) object name, or null for derived tables and functions.</param>
/// <param name="Alias">The alias, or null.</param>
public sealed record FromItem(string Join, string Source, string? Condition, string? Name, string? Alias)
{
    /// <summary>How the item is referred to: its alias, else the last part of its name.</summary>
    public string? Reference => Alias ?? Name?.Split('.')[^1].Trim('[', ']', '"');

    /// <summary>A join that can drop rows of the sources before it (inner, cross, right; not left / full / outer apply).</summary>
    public bool CanEliminate => Join.Length > 0 && !Join.Contains("LEFT", StringComparison.Ordinal) &&
                                !Join.Contains("FULL", StringComparison.Ordinal) && Join != "OUTER APPLY";

    /// <summary>The item as it appears in a FROM clause after earlier items: "INNER JOIN x ON …", ", y", or the first source.</summary>
    public string ToSql(bool first) => first
        ? Source
        : Join == "," ? ", " + Source
        : $"{Join} {Source}" + (Condition is null ? "" : Condition.StartsWith("USING", StringComparison.OrdinalIgnoreCase) ? " " + Condition : " ON " + Condition);
}

/// <summary>The clauses of a single SELECT statement, as text slices of the original.</summary>
public sealed record SelectAnatomy(
    string Prefix,
    string Modifiers,
    string SelectList,
    IReadOnlyList<FromItem> From,
    string? Where,
    string? GroupBy,
    string? Having,
    string? OrderBy,
    string? Tail,
    bool HasSetOperation)
{
    public string FromClause(int count) =>
        string.Join(" ", From.Take(count).Select((f, i) => f.ToSql(i == 0)));

    public bool LimitsRows => Modifiers.Contains("TOP", StringComparison.OrdinalIgnoreCase) ||
                              (Tail?.Length ?? 0) > 0;
}

public enum DmlKind
{
    Other,
    Select,
    Insert,
    Update,
    Delete,
    Merge
}

/// <summary>The parts of an INSERT / UPDATE / DELETE needed to preview it: target, and the rows it would touch.</summary>
/// <param name="Target">The target as written (a table name or, in SQL Server's UPDATE … FROM, an alias).</param>
/// <param name="TargetAlias">Alias given right after the target (PostgreSQL / plain SQL Server).</param>
/// <param name="From">SQL Server UPDATE/DELETE … FROM clause, or PostgreSQL UPDATE … FROM / DELETE … USING list.</param>
/// <param name="Top">SQL Server TOP (n) text, or "".</param>
/// <param name="ReturningAt">Where a RETURNING / OUTPUT clause starts (-1 when none).</param>
/// <param name="InsertOutputAt">INSERT: where an OUTPUT clause would go (after the column list); -1 otherwise.</param>
public sealed record DmlAnatomy(
    DmlKind Kind, string Prefix, string Target, string? TargetAlias, string? From, string? Where, string Top,
    int ReturningAt, int InsertOutputAt);

/// <summary>
/// A small structural parser for one SQL statement: clause boundaries at parenthesis depth 0, FROM items and their
/// joins, AND-conjuncts of a condition. Built on <see cref="SqlLexer"/>, so strings, comments and quoted names never
/// confuse it; anything it does not understand makes it return null rather than guess.
/// </summary>
public static class SqlAnatomy
{
    private static readonly HashSet<string> SelectEnders = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "HAVING", "ORDER", "LIMIT", "OFFSET", "FETCH", "UNION", "INTERSECT", "EXCEPT", "FOR", "OPTION", "WINDOW"
    };

    private sealed record Tok(SqlToken Token, int Depth, int CaseDepth);

    /// <summary>Significant tokens with their parenthesis depth (and CASE … END nesting at that depth).</summary>
    private static List<Tok> Significant(string sql)
    {
        var list = new List<Tok>();
        var depth = 0;
        var cases = new Stack<int>();
        foreach (var t in SqlLexer.Tokenize(sql))
        {
            if (t.IsTrivia) continue;
            if (t.Kind == SqlTokenKind.CloseParen) depth = Math.Max(0, depth - 1);
            if (t.Is("END") && cases.Count > 0 && cases.Peek() == depth) cases.Pop();
            list.Add(new Tok(t, depth, cases.Count(c => c == depth)));
            if (t.Is("CASE")) cases.Push(depth);
            if (t.Kind == SqlTokenKind.OpenParen) depth++;
        }
        return list;
    }

    private static bool IsTop(Tok t) => t.Depth == 0 && t.CaseDepth == 0;

    /// <summary>The statement without a trailing semicolon and surrounding blanks.</summary>
    public static string Trim(string sql) => sql.Trim().TrimEnd(';').TrimEnd();

    public static DmlKind KindOf(string sql)
    {
        var tokens = Significant(sql);
        var i = SkipWith(tokens);
        if (i >= tokens.Count) return DmlKind.Other;
        var t = tokens[i].Token;
        return t.Is("SELECT") ? DmlKind.Select
            : t.Is("INSERT") ? DmlKind.Insert
            : t.Is("UPDATE") ? DmlKind.Update
            : t.Is("DELETE") ? DmlKind.Delete
            : t.Is("MERGE") ? DmlKind.Merge
            : t.Kind == SqlTokenKind.OpenParen && tokens.Count > i + 1 && tokens[i + 1].Token.Is("SELECT") ? DmlKind.Select
            : DmlKind.Other;
    }

    /// <summary>
    /// The transaction keyword(s) a statement starts with ("COMMIT", "BEGIN TRANSACTION", "START TRANSACTION", …) when it
    /// starts, commits or rolls back a transaction; null otherwise. Any ROLLBACK counts (also TO SAVEPOINT), and a T-SQL
    /// BEGIN … END block or BEGIN TRY does not. Comments and strings are skipped by the lexer.
    /// </summary>
    public static string? TransactionControl(string sql)
    {
        var tokens = SqlLexer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        if (tokens.Count == 0 || tokens[0].Kind != SqlTokenKind.Word) return null;
        var first = tokens[0].Text.ToUpperInvariant();
        var next = tokens.Count > 1 ? tokens[1] : (SqlToken?)null;
        var nextWord = next is { Kind: SqlTokenKind.Word } n ? n.Text.ToUpperInvariant() : null;
        string With(string? word) => word is null ? first : first + " " + word;
        return first switch
        {
            "COMMIT" or "ROLLBACK" or "ABORT" => With(nextWord is "TRAN" or "TRANSACTION" or "WORK" or "PREPARED" ? nextWord : null),
            // END / END TRANSACTION / END WORK (PostgreSQL); an END of a block never starts a statement of its own.
            "END" when next is null or { Kind: SqlTokenKind.Semicolon } || nextWord is "TRAN" or "TRANSACTION" or "WORK" => With(nextWord),
            "BEGIN" when next is null or { Kind: SqlTokenKind.Semicolon } ||
                         nextWord is "TRAN" or "TRANSACTION" or "WORK" or "ISOLATION" or "DISTRIBUTED" or "READ" or "NOT" or "DEFERRABLE" => With(nextWord),
            "START" when nextWord is "TRANSACTION" => With(nextWord),
            "PREPARE" when nextWord is "TRANSACTION" => With(nextWord),
            _ => null
        };
    }

    /// <summary>Index of the first token after a leading WITH … AS ( … ), … list.</summary>
    private static int SkipWith(List<Tok> tokens)
    {
        if (tokens.Count == 0 || !tokens[0].Token.Is("WITH")) return 0;
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!IsTop(t)) continue;
            if (t.Token.Kind == SqlTokenKind.Word && t.Token.Text.ToUpperInvariant() is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE")
            {
                // "WITH x AS (SELECT …)" — the SELECT inside is at depth 1, so a depth-0 one is the main statement.
                return i;
            }
        }
        return tokens.Count;
    }

    public static SelectAnatomy? ParseSelect(string sql)
    {
        sql = Trim(sql);
        var tokens = Significant(sql);
        var start = SkipWith(tokens);
        if (start >= tokens.Count || !tokens[start].Token.Is("SELECT")) return null;
        if (tokens.Any(t => t.Depth == 0 && t.Token.Kind == SqlTokenKind.Semicolon)) return null; // several statements

        var prefix = sql[..tokens[start].Token.Start];
        var hasSetOperation = tokens.Skip(start).Any(t => IsTop(t) && (t.Token.Is("UNION") || t.Token.Is("INTERSECT") || t.Token.Is("EXCEPT")));

        // SELECT [DISTINCT | ALL] [TOP (n) [PERCENT] [WITH TIES]] list
        var i = start + 1;
        var modifiersStart = i;
        while (i < tokens.Count)
        {
            var t = tokens[i].Token;
            if (t.Is("DISTINCT") || t.Is("ALL")) { i++; if (i < tokens.Count && tokens[i].Token.Is("ON")) i = SkipParens(tokens, i + 1); continue; }
            if (t.Is("TOP"))
            {
                i++;
                if (i < tokens.Count && tokens[i].Token.Kind == SqlTokenKind.OpenParen) i = SkipParens(tokens, i);
                else i++;
                if (i < tokens.Count && tokens[i].Token.Is("PERCENT")) i++;
                if (i + 1 < tokens.Count && tokens[i].Token.Is("WITH") && tokens[i + 1].Token.Is("TIES")) i += 2;
                continue;
            }
            break;
        }
        var modifiers = i > modifiersStart ? Slice(sql, tokens, modifiersStart, i) : "";

        var from = FindTop(tokens, i, t => t.Is("FROM"));
        if (from < 0) return null;
        if (FindTop(tokens, i, t => t.Is("INTO"), from) >= 0) return null; // SELECT … INTO creates a table
        var selectList = Slice(sql, tokens, i, from);

        var fromEnd = FindTop(tokens, from + 1, t => t.Kind == SqlTokenKind.Word && SelectEnders.Contains(t.Text));
        if (fromEnd < 0) fromEnd = tokens.Count;
        if (hasSetOperation) return new SelectAnatomy(prefix, modifiers, selectList, [], null, null, null, null, null, true);

        var items = ParseFromItems(sql, tokens, from + 1, fromEnd);
        if (items is null) return null;

        var position = fromEnd;
        string? where = null, groupBy = null, having = null, orderBy = null, tail = null;
        while (position < tokens.Count)
        {
            var t = tokens[position].Token;
            var keywordLength = (t.Is("GROUP") || t.Is("ORDER")) && position + 1 < tokens.Count && tokens[position + 1].Token.Is("BY") ? 2 : 1;
            var next = FindTop(tokens, position + keywordLength,
                x => x.Kind == SqlTokenKind.Word && x.Text.ToUpperInvariant() is "WHERE" or "GROUP" or "HAVING" or "ORDER" or "LIMIT" or "OFFSET" or "FETCH" or "FOR" or "OPTION" or "WINDOW");
            if (next < 0) next = tokens.Count;
            var body = Slice(sql, tokens, position + keywordLength, next);
            if (t.Is("WHERE")) where = body;
            else if (t.Is("GROUP")) groupBy = body;
            else if (t.Is("HAVING")) having = body;
            else if (t.Is("ORDER")) orderBy = body;
            else if (t.Is("WINDOW")) return null;
            else
            {
                // LIMIT / OFFSET / FETCH / FOR UPDATE / OPTION (…): everything to the end.
                tail = Slice(sql, tokens, position, tokens.Count);
                break;
            }
            position = next;
        }

        return new SelectAnatomy(prefix, modifiers, selectList, items, where, groupBy, having, orderBy, tail, false);
    }

    private static IReadOnlyList<FromItem>? ParseFromItems(string sql, List<Tok> tokens, int start, int end)
    {
        var items = new List<FromItem>();
        var join = "";
        var i = start;
        while (i < end)
        {
            // The source runs to ON / USING / the next join keyword / comma at depth 0.
            var sourceStart = i;
            var j = i;
            while (j < end && !(IsTop(tokens[j]) && (IsJoinStart(tokens, j) || tokens[j].Token.Is("ON") || tokens[j].Token.Is("USING") ||
                                                      tokens[j].Token.Kind == SqlTokenKind.Comma)))
                j++;
            if (j == sourceStart) return null;
            var source = Slice(sql, tokens, sourceStart, j);
            var (name, alias) = NameAndAlias(tokens, sourceStart, j);

            string? condition = null;
            if (j < end && (tokens[j].Token.Is("ON") || tokens[j].Token.Is("USING")))
            {
                var isUsing = tokens[j].Token.Is("USING");
                var k = j + 1;
                while (k < end && !(IsTop(tokens[k]) && (IsJoinStart(tokens, k) || tokens[k].Token.Kind == SqlTokenKind.Comma))) k++;
                condition = (isUsing ? "USING " : "") + Slice(sql, tokens, j + 1, k);
                j = k;
            }
            items.Add(new FromItem(join, source, condition, name, alias));

            if (j >= end) break;
            if (tokens[j].Token.Kind == SqlTokenKind.Comma) { join = ","; i = j + 1; continue; }
            var (keyword, length) = JoinKeyword(tokens, j);
            join = keyword;
            i = j + length;
        }
        return items;
    }

    private static bool IsJoinStart(List<Tok> tokens, int i) => JoinKeyword(tokens, i).Length > 0;

    /// <summary>[NATURAL] [INNER | LEFT | RIGHT | FULL] [OUTER] JOIN [LATERAL], CROSS JOIN, CROSS / OUTER APPLY.</summary>
    private static (string Keyword, int Length) JoinKeyword(List<Tok> tokens, int i)
    {
        var words = new List<string>();
        var j = i;
        while (j < tokens.Count && j < i + 4 && tokens[j].Token.Kind == SqlTokenKind.Word)
        {
            var w = tokens[j].Token.Text.ToUpperInvariant();
            if (w is "NATURAL" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "OUTER" or "CROSS")
            {
                words.Add(w);
                j++;
                continue;
            }
            if (w is "JOIN")
            {
                words.Add(w);
                j++;
                if (j < tokens.Count && tokens[j].Token.Is("LATERAL")) { words.Add("LATERAL"); j++; }
                return (string.Join(" ", words) == "JOIN" ? "INNER JOIN" : string.Join(" ", words), j - i);
            }
            if (w is "APPLY" && words.Count == 1 && words[0] is "CROSS" or "OUTER")
                return (words[0] + " APPLY", j - i + 1);
            break;
        }
        return ("", 0);
    }

    /// <summary>The object name and alias of a table source; a derived table or function has no name.</summary>
    private static (string? Name, string? Alias) NameAndAlias(List<Tok> tokens, int start, int end)
    {
        var parts = new List<string>();
        var i = start;
        if (tokens[i].Token.Kind == SqlTokenKind.OpenParen)
        {
            i = SkipParens(tokens, i);
            return (null, AliasAt(tokens, i, end));
        }

        while (i < end && tokens[i].Token.IsIdentifier)
        {
            parts.Add(tokens[i].Token.Text);
            if (i + 1 < end && tokens[i + 1].Token.Kind == SqlTokenKind.Dot) { i += 2; continue; }
            i++;
            break;
        }
        if (parts.Count == 0) return (null, null);
        if (i < end && tokens[i].Token.Kind == SqlTokenKind.OpenParen) // table-valued function
            return (null, AliasAt(tokens, SkipParens(tokens, i), end));
        return (string.Join(".", parts), AliasAt(tokens, i, end));
    }

    private static string? AliasAt(List<Tok> tokens, int i, int end)
    {
        if (i < end && tokens[i].Token.Is("AS")) i++;
        if (i >= end || !tokens[i].Token.IsIdentifier) return null;
        var t = tokens[i].Token;
        if (t.Kind == SqlTokenKind.Word && t.Text.ToUpperInvariant() is "WITH" or "TABLESAMPLE" or "FOR" or "ONLY") return null;
        return t.Identifier;
    }

    /// <summary>Index just past the parenthesis group that opens at <paramref name="open"/>.</summary>
    private static int SkipParens(List<Tok> tokens, int open)
    {
        if (open >= tokens.Count || tokens[open].Token.Kind != SqlTokenKind.OpenParen) return open;
        var depth = tokens[open].Depth;
        for (var i = open + 1; i < tokens.Count; i++)
            if (tokens[i].Token.Kind == SqlTokenKind.CloseParen && tokens[i].Depth == depth) return i + 1;
        return tokens.Count;
    }

    private static int FindTop(List<Tok> tokens, int from, Func<SqlToken, bool> match, int before = int.MaxValue)
    {
        for (var i = from; i < tokens.Count && i < before; i++)
            if (IsTop(tokens[i]) && match(tokens[i].Token)) return i;
        return -1;
    }

    private static string Slice(string sql, List<Tok> tokens, int start, int end)
    {
        if (start >= end || start >= tokens.Count) return "";
        var last = tokens[Math.Min(end, tokens.Count) - 1].Token;
        return sql[tokens[start].Token.Start..last.End];
    }

    /// <summary>
    /// The top-level AND operands of a condition ("a = 1 AND (b = 2 OR c = 3)" → ["a = 1", "(b = 2 OR c = 3)"]); the AND
    /// of a BETWEEN and those inside CASE or parentheses stay with their operand.
    /// </summary>
    public static IReadOnlyList<string> SplitConjuncts(string condition)
    {
        var tokens = Significant(condition);
        var parts = new List<string>();
        var start = 0;
        var pendingBetween = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!IsTop(tokens[i])) continue;
            var t = tokens[i].Token;
            if (t.Is("BETWEEN")) { pendingBetween++; continue; }
            if (!t.Is("AND")) continue;
            if (pendingBetween > 0) { pendingBetween--; continue; }
            if (i > start) parts.Add(Slice(condition, tokens, start, i));
            start = i + 1;
        }
        if (start < tokens.Count) parts.Add(Slice(condition, tokens, start, tokens.Count));
        return parts;
    }

    /// <summary>"alias.column" references in an expression, as (qualifier, column) without quotes.</summary>
    public static IReadOnlyList<(string Qualifier, string Column)> QualifiedColumns(string expression)
    {
        var tokens = SqlLexer.Tokenize(expression).Where(t => !t.IsTrivia).ToList();
        var result = new List<(string, string)>();
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (!tokens[i].IsIdentifier || tokens[i + 1].Kind != SqlTokenKind.Dot || !tokens[i + 2].IsIdentifier) continue;
            // a.b.c: the qualifier is the part right before the column.
            if (i + 4 < tokens.Count && tokens[i + 3].Kind == SqlTokenKind.Dot && tokens[i + 4].IsIdentifier) continue;
            if (i + 3 < tokens.Count && tokens[i + 3].Kind == SqlTokenKind.OpenParen) continue; // schema.function(
            result.Add((tokens[i].Identifier, tokens[i + 2].Identifier));
        }
        return result.Distinct().ToList();
    }

    /// <summary>Qualifiers used in an expression ("o" for o.Id), to tell which FROM item a predicate is about.</summary>
    public static IReadOnlyList<string> Qualifiers(string expression) =>
        QualifiedColumns(expression).Select(c => c.Qualifier).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Splits a comma-separated list at depth 0 (GROUP BY a, f(b, c) → ["a", "f(b, c)"]).</summary>
    public static IReadOnlyList<string> SplitList(string list)
    {
        var tokens = Significant(list);
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!IsTop(tokens[i]) || tokens[i].Token.Kind != SqlTokenKind.Comma) continue;
            if (i > start) parts.Add(Slice(list, tokens, start, i));
            start = i + 1;
        }
        if (start < tokens.Count) parts.Add(Slice(list, tokens, start, tokens.Count));
        return parts;
    }

    public static DmlAnatomy? ParseDml(string sql)
    {
        sql = Trim(sql);
        var tokens = Significant(sql);
        var start = SkipWith(tokens);
        if (start >= tokens.Count) return null;
        if (tokens.Any(t => t.Depth == 0 && t.Token.Kind == SqlTokenKind.Semicolon)) return null;
        var prefix = sql[..tokens[start].Token.Start];
        var kind = KindOf(sql);
        var returning = FindTop(tokens, start, t => t.Is("RETURNING") || t.Is("OUTPUT"));
        var returningAt = returning < 0 ? -1 : tokens[returning].Token.Start;

        int i;
        string top;
        switch (kind)
        {
            case DmlKind.Update:
            {
                (i, top) = ReadTop(sql, tokens, start + 1);
                if (i < tokens.Count && tokens[i].Token.Is("ONLY")) i++;
                var targetStart = i;
                var set = FindTop(tokens, i, t => t.Is("SET"));
                if (set < 0) return null;
                var (target, alias) = TargetAndAlias(sql, tokens, targetStart, set);
                if (target is null) return null;
                var fromIndex = FindTop(tokens, set + 1, t => t.Is("FROM"));
                var whereIndex = FindTop(tokens, set + 1, t => t.Is("WHERE"));
                var optionIndex = FindTop(tokens, set + 1, t => t.Is("OPTION") || t.Is("RETURNING"));
                if (FindTop(tokens, set + 1, t => t.Is("CURRENT")) >= 0) return null; // WHERE CURRENT OF cursor
                var end = optionIndex < 0 ? tokens.Count : optionIndex;
                var from = fromIndex < 0 ? null : Slice(sql, tokens, fromIndex + 1, whereIndex > fromIndex ? whereIndex : end);
                var where = whereIndex < 0 ? null : Slice(sql, tokens, whereIndex + 1, end);
                return new DmlAnatomy(kind, prefix, target, alias, from, where, top, returningAt, -1);
            }
            case DmlKind.Delete:
            {
                (i, top) = ReadTop(sql, tokens, start + 1);
                if (i < tokens.Count && tokens[i].Token.Is("FROM")) i++;
                if (i < tokens.Count && tokens[i].Token.Is("ONLY")) i++;
                var targetStart = i;
                var targetEnd = FindTop(tokens, i, t => t.Is("FROM") || t.Is("USING") || t.Is("WHERE") || t.Is("OUTPUT") ||
                                                         t.Is("RETURNING") || t.Is("OPTION"));
                if (targetEnd < 0) targetEnd = tokens.Count;
                var (target, alias) = TargetAndAlias(sql, tokens, targetStart, targetEnd);
                if (target is null) return null;
                var fromIndex = FindTop(tokens, targetEnd, t => t.Is("FROM") || t.Is("USING"));
                var whereIndex = FindTop(tokens, targetEnd, t => t.Is("WHERE"));
                if (FindTop(tokens, targetEnd, t => t.Is("CURRENT")) >= 0) return null;
                var optionIndex = FindTop(tokens, targetEnd, t => t.Is("OPTION") || t.Is("RETURNING"));
                var end = optionIndex < 0 ? tokens.Count : optionIndex;
                // SQL Server: DELETE t OUTPUT … FROM …: the OUTPUT clause sits between target and FROM.
                var from = fromIndex < 0 ? null : Slice(sql, tokens, fromIndex + 1, whereIndex > fromIndex ? whereIndex : end);
                var where = whereIndex < 0 ? null : Slice(sql, tokens, whereIndex + 1, end);
                return new DmlAnatomy(kind, prefix, target, alias, from, where, top, returningAt, -1);
            }
            case DmlKind.Insert:
            {
                (i, top) = ReadTop(sql, tokens, start + 1);
                if (i < tokens.Count && tokens[i].Token.Is("INTO")) i++;
                var targetStart = i;
                while (i < tokens.Count && (tokens[i].Token.IsIdentifier || tokens[i].Token.Kind == SqlTokenKind.Dot) &&
                       !(tokens[i].Token.IsIdentifier && i > targetStart && tokens[i - 1].Token.IsIdentifier) &&
                       !(tokens[i].Token.Kind == SqlTokenKind.Word && tokens[i].Token.Text.ToUpperInvariant() is
                           "VALUES" or "SELECT" or "DEFAULT" or "OUTPUT" or "WITH" or "OVERRIDING" or "AS" or "EXEC" or "EXECUTE")) i++;
                if (i == targetStart) return null;
                var target = Slice(sql, tokens, targetStart, i);
                string? alias = null;
                if (i + 1 < tokens.Count && tokens[i].Token.Is("AS") && tokens[i + 1].Token.IsIdentifier) { alias = tokens[i + 1].Token.Identifier; i += 2; }
                if (i < tokens.Count && tokens[i].Token.Kind == SqlTokenKind.OpenParen &&
                    !(i + 1 < tokens.Count && (tokens[i + 1].Token.Is("SELECT") || tokens[i + 1].Token.Is("WITH"))))
                    i = SkipParens(tokens, i); // column list
                var outputAt = i < tokens.Count ? tokens[i].Token.Start : sql.Length;
                return new DmlAnatomy(kind, prefix, target, alias, null, null, top, returningAt, outputAt);
            }
            case DmlKind.Merge:
            {
                i = start + 1;
                if (i < tokens.Count && tokens[i].Token.Is("TOP")) (i, _) = ReadTop(sql, tokens, i);
                if (i < tokens.Count && tokens[i].Token.Is("INTO")) i++;
                var targetStart = i;
                var usingIndex = FindTop(tokens, i, t => t.Is("USING"));
                if (usingIndex < 0) return null;
                var (target, alias) = TargetAndAlias(sql, tokens, targetStart, usingIndex);
                return target is null ? null : new DmlAnatomy(kind, prefix, target, alias, null, null, "", returningAt, -1);
            }
            default:
                return null;
        }
    }

    private static (int Next, string Top) ReadTop(string sql, List<Tok> tokens, int i)
    {
        if (i >= tokens.Count || !tokens[i].Token.Is("TOP")) return (i, "");
        var start = i;
        i++;
        i = i < tokens.Count && tokens[i].Token.Kind == SqlTokenKind.OpenParen ? SkipParens(tokens, i) : i + 1;
        if (i < tokens.Count && tokens[i].Token.Is("PERCENT")) i++;
        return (i, Slice(sql, tokens, start, i));
    }

    /// <summary>"dbo.Orders o" / "orders AS o" / "dbo.Orders WITH (ROWLOCK)" → (name, alias).</summary>
    private static (string? Target, string? Alias) TargetAndAlias(string sql, List<Tok> tokens, int start, int end)
    {
        var i = start;
        while (i < end && (tokens[i].Token.IsIdentifier || tokens[i].Token.Kind == SqlTokenKind.Dot))
        {
            if (tokens[i].Token.IsIdentifier && i > start && tokens[i - 1].Token.IsIdentifier) break; // next word = alias
            i++;
        }
        if (i == start) return (null, null);
        var target = Slice(sql, tokens, start, i);
        return (target, AliasAt(tokens, i, end));
    }
}
