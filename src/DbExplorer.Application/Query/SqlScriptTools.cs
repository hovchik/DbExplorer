using System.Text;

namespace DbExplorer.Application.Query;

/// <summary>A statement's range in the script: <c>Start..End</c> (exclusive), trimmed of surrounding whitespace.</summary>
public readonly record struct SqlRange(int Start, int End)
{
    public int Length => End - Start;
    public string Of(string text) => text[Start..End];
}

/// <summary>Script-level helpers for the query editor: statement boundaries, comment toggling and formatting.</summary>
public static class SqlScriptTools
{
    private static readonly HashSet<string> BlockOpeners = new(StringComparer.OrdinalIgnoreCase) { "BEGIN", "CASE" };

    /// <summary>
    /// Statements of a script. Boundaries are ';', a GO line, or a blank line — but never inside parentheses,
    /// BEGIN…END / CASE…END blocks, strings or comments, so procedures and multi-line queries stay whole.
    /// </summary>
    public static IReadOnlyList<SqlRange> SplitStatements(string text, bool splitOnBlankLines = true)
    {
        var tokens = SqlLexer.Tokenize(text);
        var ranges = new List<SqlRange>();
        var depth = 0;
        var blocks = 0;
        var start = -1;
        var end = -1;

        void Close()
        {
            if (start >= 0) ranges.Add(new SqlRange(start, end));
            start = -1;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.Whitespace)
            {
                if (splitOnBlankLines && depth == 0 && blocks == 0 && CountNewLines(t.Text) >= 2) Close();
                continue;
            }

            if (t.Kind == SqlTokenKind.Semicolon && depth == 0 && blocks == 0)
            {
                if (start >= 0) end = t.End;
                Close();
                continue;
            }

            if (t.Is("GO") && IsAloneOnLine(text, t))
            {
                Close();
                depth = blocks = 0;
                continue;
            }

            if (t.Kind == SqlTokenKind.OpenParen) depth++;
            else if (t.Kind == SqlTokenKind.CloseParen) depth = Math.Max(0, depth - 1);
            else if (t.Kind == SqlTokenKind.Word && BlockOpeners.Contains(t.Text) && !IsBeginTransaction(tokens, i)) blocks++;
            else if (t.Is("END") && blocks > 0) blocks--;

            if (start < 0) start = t.Start;
            end = t.End;
        }
        Close();
        return ranges;
    }

    /// <summary>The statement containing (or, between statements, just before) the caret; null for an empty script.</summary>
    public static SqlRange? StatementAt(string text, int caret)
    {
        var statements = SplitStatements(text);
        if (statements.Count == 0) return null;
        foreach (var s in statements)
            if (caret >= s.Start && caret <= s.End) return s;
        return statements.LastOrDefault(s => s.End <= caret) is { Length: > 0 } before ? before : statements[0];
    }

    /// <summary>
    /// Comments out the given lines with "-- ", or uncomments them when every non-blank line already is.
    /// Returns the new text for the range of whole lines.
    /// </summary>
    public static string ToggleLineComments(string lines)
    {
        var split = lines.Split('\n');
        var nonBlank = split.Where(l => l.Trim().Length > 0).ToList();
        var uncomment = nonBlank.Count > 0 && nonBlank.All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal));
        var indent = nonBlank.Count == 0 ? 0 : nonBlank.Min(l => l.Length - l.TrimStart().Length);

        return string.Join('\n', split.Select(line =>
        {
            if (line.Trim().Length == 0) return line;
            if (!uncomment) return line[..indent] + "-- " + line[indent..];
            var at = line.IndexOf("--", StringComparison.Ordinal);
            var skip = at + 2 < line.Length && line[at + 2] == ' ' ? 3 : 2;
            return line[..at] + line[(at + skip)..];
        }));
    }

    private static readonly HashSet<string> NewLineBefore = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "HAVING", "VALUES", "SET", "UNION", "EXCEPT", "INTERSECT", "RETURNING", "LIMIT", "OFFSET",
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "INSERT", "UPDATE", "DELETE", "WITH"
    };

    private static readonly HashSet<string> JoinModifiers = new(StringComparer.OrdinalIgnoreCase) { "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "NATURAL" };

    /// <summary>
    /// Formats DML for reading: keywords upper-cased, one clause per line, one select/group/order item per line,
    /// AND/OR on their own lines. Strings, comments and quoted identifiers are kept byte for byte. Statements
    /// with BEGIN…END blocks (procedural code) are only keyword-cased, never re-flowed.
    /// </summary>
    public static string Format(string text, IReadOnlySet<string>? keywords = null)
    {
        keywords ??= SqlCompletionEngine.KeywordSet;
        var tokens = SqlLexer.Tokenize(text);
        if (tokens.Select((t, i) => t.Is("BEGIN") && !IsBeginTransaction(tokens, i)).Any(b => b) || tokens.Any(t => t.Is("CREATE") || t.Is("ALTER")))
            return string.Concat(tokens.Select(t => t.Kind == SqlTokenKind.Word && keywords.Contains(t.Text) ? t.Text.ToUpperInvariant() : t.Text));

        var sb = new StringBuilder();
        var depth = 0;
        var listClause = false;       // inside SELECT / GROUP BY / ORDER BY / SET list: break after commas
        var inJoin = false;           // ON starts a line only as a join condition (not in SET NOCOUNT ON)
        var pendingSpace = false;
        var lineStart = true;
        const string Indent = "    ";

        void NewLine(int extra = 0)
        {
            TrimEndSpaces(sb);
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(string.Concat(Enumerable.Repeat(Indent, depth + extra)));
            lineStart = true;
            pendingSpace = false;
        }

        void Emit(string s)
        {
            if (pendingSpace && !lineStart) sb.Append(' ');
            sb.Append(s);
            lineStart = false;
            pendingSpace = false;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var previous = PreviousSignificant(tokens, i);
            switch (t.Kind)
            {
                case SqlTokenKind.Whitespace:
                    pendingSpace = true;
                    if (CountNewLines(t.Text) >= 2 && depth == 0) { NewLine(); sb.Append('\n'); }
                    continue;
                case SqlTokenKind.Comment:
                    Emit(t.Text.TrimEnd());
                    if (t.Text.StartsWith("--", StringComparison.Ordinal)) NewLine(listClause ? 1 : 0);
                    continue;
                case SqlTokenKind.Comma:
                    sb.Append(',');
                    lineStart = false;
                    if (listClause && depth == 0) NewLine(1);
                    else pendingSpace = true;
                    continue;
                case SqlTokenKind.Dot:
                    TrimEndSpaces(sb);
                    sb.Append('.');
                    pendingSpace = false;
                    lineStart = false;
                    continue;
                case SqlTokenKind.OpenParen:
                    if (previous is { } p && (p.IsIdentifier || p.Kind == SqlTokenKind.Word)) pendingSpace = keywords.Contains(p.Text) && !IsFunctionLike(p.Text);
                    Emit("(");
                    depth++;
                    continue;
                case SqlTokenKind.CloseParen:
                    depth = Math.Max(0, depth - 1);
                    pendingSpace = false;
                    Emit(")");
                    continue;
                case SqlTokenKind.Semicolon:
                    TrimEndSpaces(sb);
                    sb.Append(';');
                    listClause = false;
                    NewLine();
                    continue;
            }

            var word = t.Kind == SqlTokenKind.Word && keywords.Contains(t.Text) ? t.Text.ToUpperInvariant() : t.Text;
            if (t.Kind == SqlTokenKind.Word && depth == 0)
            {
                var upper = t.Text.ToUpperInvariant();
                var joinContinuation = upper is "JOIN" or "OUTER" && previous is { } pj && JoinModifiers.Contains(pj.Text);
                var insertInto = upper == "INTO" || (upper == "FROM" && previous is { } pd && pd.Is("DELETE"));
                var tableHint = upper == "WITH" && NextSignificant(tokens, i) is { Kind: SqlTokenKind.OpenParen };
                if (NewLineBefore.Contains(upper) && !joinContinuation && !insertInto && !tableHint)
                {
                    inJoin = upper is "JOIN" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "OUTER";
                    NewLine();
                    listClause = upper is "SELECT" or "SET";
                    Emit(word);
                    if (listClause) NewLine(1);
                    continue;
                }
                if (upper is "BY" && previous is { } pb && (pb.Is("GROUP") || pb.Is("ORDER")))
                {
                    Emit(word);
                    listClause = true;
                    NewLine(1);
                    continue;
                }
                if (upper is "GROUP" or "ORDER" && NextSignificant(tokens, i) is { } nb && nb.Is("BY"))
                {
                    NewLine();
                    Emit(word);
                    continue;
                }
                if (upper is "AND" or "OR" && !IsBetweenAnd(tokens, i))
                {
                    NewLine(1);
                    Emit(word);
                    continue;
                }
                if (upper == "ON" && inJoin)
                {
                    NewLine(1);
                    Emit(word);
                    continue;
                }
                if (upper is "FROM" or "WHERE" or "HAVING" or "LIMIT" or "OFFSET" or "VALUES" or "RETURNING") listClause = false;
            }

            Emit(word);
        }

        TrimEndSpaces(sb);
        return sb.ToString().Trim('\n') + (text.EndsWith('\n') ? "\n" : "");
    }

    private static bool IsFunctionLike(string word) =>
        SqlCompletionEngine.KnownFunctionNames.Contains(word);

    /// <summary>"BETWEEN a AND b": that AND is part of the predicate, not a new condition.</summary>
    private static bool IsBetweenAnd(IReadOnlyList<SqlToken> tokens, int index)
    {
        for (var i = index - 1; i >= 0 && i >= index - 12; i--)
        {
            var t = tokens[i];
            if (t.Is("BETWEEN")) return true;
            if (t.Is("AND") || t.Is("OR") || t.Is("WHERE") || t.Is("ON") || t.Kind == SqlTokenKind.Semicolon) return false;
        }
        return false;
    }

    private static bool IsBeginTransaction(IReadOnlyList<SqlToken> tokens, int index) =>
        index >= 0 && tokens[index].Is("BEGIN") && NextSignificant(tokens, index) is { } next &&
        (next.Is("TRAN") || next.Is("TRANSACTION") || next.Is("DISTRIBUTED") || next.Kind == SqlTokenKind.Semicolon ||
         next.Is("WORK") || next.Is("ISOLATION"));

    private static SqlToken? PreviousSignificant(IReadOnlyList<SqlToken> tokens, int index)
    {
        for (var i = index - 1; i >= 0; i--) if (!tokens[i].IsTrivia) return tokens[i];
        return null;
    }

    private static SqlToken? NextSignificant(IReadOnlyList<SqlToken> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++) if (!tokens[i].IsTrivia) return tokens[i];
        return null;
    }

    private static bool IsAloneOnLine(string text, SqlToken t)
    {
        var lineStart = t.Start == 0 ? 0 : text.LastIndexOf('\n', t.Start - 1) + 1;
        var lineEnd = text.IndexOf('\n', t.End);
        if (lineEnd < 0) lineEnd = text.Length;
        return text[lineStart..t.Start].Trim().Length == 0 && text[t.End..lineEnd].Trim().Length == 0;
    }

    private static int CountNewLines(string s) => s.Count(ch => ch == '\n');

    private static void TrimEndSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] is ' ' or '\t') sb.Length--;
    }
}
