using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DbExplorer.Application.Query;

/// <summary>A statement that changes or removes more than it probably should, found before running a script.</summary>
public sealed record UnsafeStatement(int Line, string Description);

/// <summary>A span that can be folded in the editor (BEGIN…END, a multi-line parenthesis or block comment).</summary>
public readonly record struct FoldRegion(int Start, int End, string Title);

/// <summary>Analyses behind editor features: safety checks, parameters, bracket matching and folding.</summary>
public static class SqlEditorAnalysis
{
    // ----- Safety -----

    /// <summary>
    /// UPDATE/DELETE without WHERE (every row changes), TRUNCATE and DROP — the statements IDEs ask about before
    /// running. Statements inside procedure/function bodies being created are not flagged.
    /// </summary>
    public static IReadOnlyList<UnsafeStatement> FindUnsafeStatements(string script)
    {
        var result = new List<UnsafeStatement>();
        foreach (var range in SqlScriptTools.SplitStatements(script))
        {
            var tokens = SqlLexer.Tokenize(range.Of(script)).Where(t => !t.IsTrivia).ToList();
            if (tokens.Count == 0) continue;
            if (tokens.Any(t => t.Is("CREATE") || t.Is("ALTER")) && tokens.Any(t => t.Is("PROCEDURE") || t.Is("PROC") || t.Is("FUNCTION") || t.Is("TRIGGER")))
                continue;

            var line = SqlLineOf(script, range.Start + tokens[0].Start);
            var verbIndex = tokens.FindIndex(t => t.Is("UPDATE") || t.Is("DELETE") || t.Is("TRUNCATE") || t.Is("DROP"));
            if (verbIndex < 0) continue;
            var verb = tokens[verbIndex].Text.ToUpperInvariant();
            // Only the statement's own verb: "... ON DELETE CASCADE" or a CTE body do not count.
            if (verbIndex > 0 && !(tokens[0].Is("WITH") || tokens[verbIndex - 1].Kind == SqlTokenKind.CloseParen)) continue;

            var target = TargetName(tokens, verbIndex + 1);
            switch (verb)
            {
                case "TRUNCATE":
                    result.Add(new UnsafeStatement(line, $"TRUNCATE {target} removes every row"));
                    break;
                case "DROP":
                    result.Add(new UnsafeStatement(line, $"DROP {target}"));
                    break;
                default:
                    if (!HasTopLevel(tokens, verbIndex, "WHERE") && !HasTopLevel(tokens, verbIndex, "CURRENT"))
                        result.Add(new UnsafeStatement(line, $"{verb} {target} has no WHERE clause: it changes every row"));
                    break;
            }
        }
        return result;
    }

    private static bool HasTopLevel(IReadOnlyList<SqlToken> tokens, int from, string word)
    {
        var depth = 0;
        for (var i = from; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SqlTokenKind.OpenParen) depth++;
            else if (tokens[i].Kind == SqlTokenKind.CloseParen) depth--;
            else if (depth == 0 && tokens[i].Is(word)) return true;
        }
        return false;
    }

    private static string TargetName(IReadOnlyList<SqlToken> tokens, int from)
    {
        var sb = new StringBuilder();
        for (var i = from; i < tokens.Count && sb.Length < 120; i++)
        {
            var t = tokens[i];
            if (t.Is("FROM") || t.Is("TABLE") || t.Is("TOP") || t.Is("IF") || t.Is("EXISTS") || t.Is("ONLY")) { if (sb.Length == 0) continue; break; }
            if (t.Kind == SqlTokenKind.OpenParen && sb.Length == 0) { while (i < tokens.Count && tokens[i].Kind != SqlTokenKind.CloseParen) i++; continue; }
            if (t.IsIdentifier || t.Kind == SqlTokenKind.Dot) sb.Append(t.Text);
            else break;
            if (i + 1 < tokens.Count && tokens[i + 1].Kind != SqlTokenKind.Dot && t.Kind != SqlTokenKind.Dot) break;
        }
        return sb.Length == 0 ? "(table)" : sb.ToString();
    }

    private static int SqlLineOf(string text, int offset) => text.AsSpan(0, Math.Clamp(offset, 0, text.Length)).Count('\n') + 1;

    // ----- Parameters -----

    /// <summary>
    /// Parameters the script uses but does not define: <c>:name</c> placeholders, and <c>@name</c> variables that are
    /// not DECLAREd (SQL Server). Scripts that create routines are skipped (their @parameters are declared by the routine).
    /// </summary>
    public static IReadOnlyList<string> FindParameters(string script)
    {
        var tokens = SqlLexer.Tokenize(script).Where(t => !t.IsTrivia).ToList();
        if (tokens.Any(t => t.Is("PROCEDURE") || t.Is("PROC") || t.Is("FUNCTION") || t.Is("TRIGGER"))) return [];

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Is("DECLARE")) continue;
            // DECLARE @a int, @b varchar(10) = 'x', ...
            var depth = 0;
            for (var j = i + 1; j < tokens.Count; j++)
            {
                var t = tokens[j];
                if (t.Kind == SqlTokenKind.OpenParen) depth++;
                else if (t.Kind == SqlTokenKind.CloseParen) depth--;
                else if (t.Kind == SqlTokenKind.Semicolon || (depth == 0 && t.Kind == SqlTokenKind.Word && IsStatementStart(t))) break;
                else if (t.Kind == SqlTokenKind.Variable && (j == i + 1 || tokens[j - 1].Kind == SqlTokenKind.Comma)) declared.Add(t.Text);
            }
        }
        // Table variables / cursors named in a SELECT ... INTO @x or FETCH ... INTO @x are declared elsewhere anyway.

        return tokens
            .Where(t => t.Kind == SqlTokenKind.Variable && !t.Text.StartsWith("@@", StringComparison.Ordinal) && !declared.Contains(t.Text))
            .Select(t => t.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsStatementStart(SqlToken t) =>
        t.Text.ToUpperInvariant() is "SELECT" or "SET" or "INSERT" or "UPDATE" or "DELETE" or "IF" or "WHILE" or "EXEC" or "EXECUTE" or "PRINT" or "BEGIN";

    private static readonly Regex Literal = new(@"^(-?\d+(\.\d+)?|NULL|TRUE|FALSE|'.*'|N'.*')$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What a user-typed parameter value becomes in SQL: numbers, NULL, TRUE/FALSE and quoted text as typed; anything else quoted.</summary>
    public static string ParameterLiteral(string value, string? providerKey)
    {
        var v = value.Trim();
        if (v.ToUpperInvariant() is "NULL" or "TRUE" or "FALSE") return v.ToUpperInvariant();
        if (Literal.IsMatch(v)) return v;
        return (providerKey == "SqlServer" ? "N'" : "'") + v.Replace("'", "''") + "'";
    }

    /// <summary>Replaces each parameter token (outside strings and comments) with its value.</summary>
    public static string SubstituteParameters(string script, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(script.Length);
        foreach (var t in SqlLexer.Tokenize(script))
            sb.Append(t.Kind == SqlTokenKind.Variable && values.TryGetValue(t.Text, out var v) ? v : t.Text);
        return sb.ToString();
    }

    // ----- Brackets and folding -----

    /// <summary>The parenthesis pair touching <paramref name="caret"/> (the character after it, else the one before), or null.</summary>
    public static (int Open, int Close)? MatchingParentheses(string text, int caret)
    {
        var parens = SqlLexer.Tokenize(text).Where(t => t.Kind is SqlTokenKind.OpenParen or SqlTokenKind.CloseParen).ToList();
        var index = parens.FindIndex(t => t.Start == caret);
        if (index < 0) index = parens.FindIndex(t => t.Start == caret - 1);
        if (index < 0) return null;

        var open = parens[index].Kind == SqlTokenKind.OpenParen;
        var depth = 0;
        for (var i = index; open ? i < parens.Count : i >= 0; i += open ? 1 : -1)
        {
            depth += parens[i].Kind == SqlTokenKind.OpenParen ? 1 : -1;
            if (depth == 0)
                return open ? (parens[index].Start, parens[i].Start) : (parens[i].Start, parens[index].Start);
        }
        return null;
    }

    /// <summary>Regions spanning several lines: BEGIN…END blocks, parentheses (subqueries, column lists) and block comments.</summary>
    public static IReadOnlyList<FoldRegion> FoldRegions(string text)
    {
        var tokens = SqlLexer.Tokenize(text);
        var regions = new List<FoldRegion>();
        var begins = new Stack<SqlToken>();
        var parens = new Stack<SqlToken>();
        var cases = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.Comment && t.Text.StartsWith("/*", StringComparison.Ordinal) && t.Text.Contains('\n'))
                regions.Add(new FoldRegion(t.Start, t.End, "/* … */"));
            else if (t.Kind == SqlTokenKind.OpenParen) parens.Push(t);
            else if (t.Kind == SqlTokenKind.CloseParen && parens.Count > 0)
            {
                var open = parens.Pop();
                if (text.AsSpan(open.Start, t.End - open.Start).Contains('\n')) regions.Add(new FoldRegion(open.Start, t.End, "(…)"));
            }
            else if (t.Is("CASE")) cases++;
            else if (t.Is("BEGIN") && !IsBeginTransaction(tokens, i)) begins.Push(t);
            else if (t.Is("END"))
            {
                if (cases > 0) cases--;
                else if (begins.Count > 0)
                {
                    var begin = begins.Pop();
                    if (text.AsSpan(begin.Start, t.End - begin.Start).Contains('\n')) regions.Add(new FoldRegion(begin.Start, t.End, "BEGIN … END"));
                }
            }
        }
        return regions.OrderBy(r => r.Start).ToList();
    }

    private static bool IsBeginTransaction(IReadOnlyList<SqlToken> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++)
        {
            if (tokens[i].IsTrivia) continue;
            return tokens[i].Is("TRAN") || tokens[i].Is("TRANSACTION") || tokens[i].Kind == SqlTokenKind.Semicolon ||
                   tokens[i].Is("WORK") || tokens[i].Is("ISOLATION") || tokens[i].Is("DISTRIBUTED");
        }
        return true;
    }

    /// <summary>The identifier at or just before <paramref name="caret"/> (for highlighting its other occurrences).</summary>
    public static SqlToken? IdentifierAt(string text, int caret) =>
        SqlLexer.Tokenize(text).FirstOrDefault(t => t.IsIdentifier && caret >= t.Start && caret <= t.End) is { Text: not null } token &&
        !SqlCompletionEngine.KeywordSet.Contains(token.Text)
            ? token
            : null;
}

/// <summary>Execution plans: the script that asks the engine for a plan, and plain-language hints read from the plan.</summary>
public static class QueryPlanTools
{
    /// <summary>
    /// The script that returns the plan of <paramref name="sql"/>. Estimated plans do not run the statements. With
    /// <paramref name="analyze"/> the statements do run (to measure them) inside a transaction that is rolled back.
    /// </summary>
    public static string BuildExplainScript(string sql, string providerKey, bool analyze)
    {
        if (providerKey == "SqlServer")
        {
            return analyze
                ? $"SET STATISTICS PROFILE ON;\nGO\nBEGIN TRANSACTION;\nGO\n{sql}\nGO\nIF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\nGO\nSET STATISTICS PROFILE OFF;"
                : $"SET SHOWPLAN_ALL ON;\nGO\n{sql}\nGO\nSET SHOWPLAN_ALL OFF;";
        }

        var statements = SqlScriptTools.SplitStatements(sql)
            .Select(r => r.Of(sql).TrimEnd().TrimEnd(';').Trim())
            .Where(s => s.Length > 0)
            .ToList();
        var options = analyze ? "ANALYZE, BUFFERS, FORMAT TEXT" : "FORMAT TEXT";
        var body = string.Join("\n", statements.Select(s => $"EXPLAIN ({options})\n{s};"));
        return analyze ? $"BEGIN;\n{body}\nROLLBACK;" : body;
    }

    private static readonly Regex PostgresScan = new(@"Seq Scan on (?<table>\S+).*?rows=(?<rows>\d+)", RegexOptions.CultureInvariant);

    /// <summary>Things worth a look in a plan: large scans, lookups, spills, warnings and the overall cost/time.</summary>
    public static IReadOnlyList<string> Insights(IReadOnlyList<(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows)> plans, string providerKey)
    {
        var insights = new List<string>();
        foreach (var (columns, rows) in plans)
        {
            if (providerKey == "SqlServer")
            {
                var op = IndexOf(columns, "PhysicalOp");
                var estimate = IndexOf(columns, "EstimateRows");
                var cost = IndexOf(columns, "TotalSubtreeCost");
                var warnings = IndexOf(columns, "Warnings");
                var text = IndexOf(columns, "StmtText");
                if (op < 0) continue;

                if (cost >= 0 && rows.Count > 0 && rows[0][cost] is { } total)
                    insights.Add($"Estimated cost {Convert.ToDouble(total, CultureInfo.InvariantCulture):0.###}");
                foreach (var row in rows)
                {
                    var physical = row[op]?.ToString() ?? "";
                    var rowsEstimate = estimate >= 0 && row[estimate] is { } e ? Convert.ToDouble(e, CultureInfo.InvariantCulture) : 0;
                    var what = text >= 0 ? (row[text]?.ToString() ?? "").Trim() : physical;
                    if (physical is "Table Scan" or "Clustered Index Scan" or "Index Scan" && rowsEstimate >= 10_000)
                        insights.Add($"{physical} over ≈{rowsEstimate:N0} rows: {Shorten(what)} — an index on the filtered/joined columns may help");
                    else if (physical is "Key Lookup" or "RID Lookup")
                        insights.Add($"{physical}: {Shorten(what)} — a covering index (INCLUDE the looked-up columns) avoids it");
                    else if (physical == "Sort" && rowsEstimate >= 100_000)
                        insights.Add($"Sort of ≈{rowsEstimate:N0} rows — an index in that order avoids it");
                    if (warnings >= 0 && row[warnings] is { } w && w.ToString() is { Length: > 0 } warning)
                        insights.Add($"Warning: {warning}");
                }
            }
            else
            {
                if (rows.Count > 0 && rows[0].FirstOrDefault()?.ToString() is { Length: > 0 } top)
                    insights.Add("Top node: " + Shorten(top.Trim()));
                foreach (var line in rows.Select(r => r.FirstOrDefault()?.ToString() ?? ""))
                {
                    if (PostgresScan.Match(line) is { Success: true } m && long.Parse(m.Groups["rows"].Value, CultureInfo.InvariantCulture) >= 10_000)
                        insights.Add($"Sequential scan on {m.Groups["table"].Value} (≈{long.Parse(m.Groups["rows"].Value, CultureInfo.InvariantCulture):N0} rows) — an index on the filtered/joined columns may help");
                    else if (line.Contains("external merge", StringComparison.Ordinal))
                        insights.Add("A sort spilled to disk (external merge) — raise work_mem or sort on an indexed column");
                    else if (line.TrimStart().StartsWith("Execution Time:", StringComparison.Ordinal) || line.TrimStart().StartsWith("Planning Time:", StringComparison.Ordinal))
                        insights.Add(line.Trim());
                }
            }
        }
        return insights.Distinct().ToList();
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Shorten(string s) => s.Length <= 120 ? s : s[..120] + "…";
}
