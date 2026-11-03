namespace DbExplorer.Application.Query;

/// <summary>What a statement of a script gives back when it runs, as far as its text tells.</summary>
public enum StatementOutput
{
    /// <summary>No rows: DDL, settings, transaction control, DML without RETURNING / OUTPUT.</summary>
    None,

    /// <summary>One result set: a query, SHOW, EXPLAIN, DML with RETURNING / OUTPUT.</summary>
    OneResult,

    /// <summary>Any number of result sets: EXEC, CALL, a block, several T-SQL statements without separators.</summary>
    Unknown
}

/// <summary>A statement of the script that ran, where it is in that script, and what it gives back.</summary>
public sealed record PlacedStatement(int Start, int End, StatementOutput Output);

/// <summary>
/// Tells which statement of a script produced each of its result sets, so a Query tab can show small results right
/// under the statement in the editor. Providers return the result sets of a whole script in order without saying where
/// each came from, so this counts the statements that return rows. When the count cannot match (two statements that may
/// return any number of results, or more results than statements), nothing is placed rather than guessing.
/// </summary>
public static class InlineResultPlacement
{
    private static readonly HashSet<string> QueryStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "VALUES", "TABLE", "SHOW", "EXPLAIN", "DESCRIBE", "DESC"
    };

    private static readonly HashSet<string> DmlStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "UPSERT"
    };

    /// <summary>Statements that never return rows on their own.</summary>
    private static readonly HashSet<string> QuietStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "DECLARE", "SET", "USE", "COMMIT", "ROLLBACK", "START", "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT",
        "REVOKE", "DENY", "PRINT", "SAVEPOINT", "RELEASE", "ANALYZE", "VACUUM", "COMMENT", "LOCK", "RESET", "DISCARD",
        "REFRESH", "REINDEX", "CLUSTER", "LISTEN", "NOTIFY", "UNLISTEN", "RAISERROR", "THROW", "SAVE", "RENAME", "OPTIMIZE"
    };

    private static readonly HashSet<string> SetOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNION", "EXCEPT", "INTERSECT", "MINUS"
    };

    /// <summary>
    /// For each of <paramref name="resultCount"/> result sets of <paramref name="script"/>, the statement that produced it;
    /// null when that cannot be told. Statements are split at ';', GO and blank lines (as the editor shows them), and at
    /// ';' and GO only; the split that explains the results best wins.
    /// </summary>
    public static IReadOnlyList<PlacedStatement>? Place(string script, int resultCount)
    {
        if (resultCount == 0) return [];
        // Of the two ways to split that add up, the one with fewer statements of unknown output: a blank line inside a
        // statement leaves a piece like "FROM t" behind, and T-SQL without semicolons reads as one block of queries.
        IReadOnlyList<PlacedStatement>? best = null;
        var bestUnknown = int.MaxValue;
        foreach (var blankLines in new[] { true, false })
        {
            var statements = Statements(script, blankLines);
            if (Assign(statements.Select(s => s.Output).ToList(), resultCount) is not { } owners) continue;
            var unknown = statements.Count(s => s.Output == StatementOutput.Unknown);
            if (unknown >= bestUnknown) continue;
            best = owners.Select(i => statements[i]).ToList();
            bestUnknown = unknown;
        }
        return best;
    }

    /// <summary>The script's statements (comment-only pieces left out) with what each gives back.</summary>
    public static IReadOnlyList<PlacedStatement> Statements(string script, bool splitOnBlankLines = true)
    {
        var statements = new List<PlacedStatement>();
        foreach (var range in SqlScriptTools.SplitStatements(script, splitOnBlankLines))
        {
            var tokens = SqlLexer.Tokenize(range.Of(script)).Where(t => !t.IsTrivia).ToList();
            if (tokens.Count == 0 || tokens.All(t => t.Kind == SqlTokenKind.Semicolon)) continue;
            statements.Add(new PlacedStatement(range.Start, range.End, Classify(tokens)));
        }
        return statements;
    }

    /// <summary>
    /// The index of the statement behind each result set, in order, or null when the outputs cannot add up to
    /// <paramref name="resultCount"/>. One statement of unknown output takes whatever the others leave over.
    /// </summary>
    public static IReadOnlyList<int>? Assign(IReadOnlyList<StatementOutput> outputs, int resultCount)
    {
        if (resultCount == 0) return [];
        var known = outputs.Count(o => o == StatementOutput.OneResult);
        var unknown = outputs.Count(o => o == StatementOutput.Unknown);
        if (unknown > 1) return null;
        var rest = resultCount - known;
        if (rest < 0 || (unknown == 0 && rest != 0)) return null;

        var owners = new List<int>(resultCount);
        for (var i = 0; i < outputs.Count; i++)
        {
            if (outputs[i] == StatementOutput.OneResult) owners.Add(i);
            else if (outputs[i] == StatementOutput.Unknown) owners.AddRange(Enumerable.Repeat(i, rest));
        }
        return owners;
    }

    private static StatementOutput Classify(IReadOnlyList<SqlToken> tokens)
    {
        var first = tokens[0];
        if (first.Kind == SqlTokenKind.OpenParen) return StatementOutput.OneResult; // (SELECT …) UNION (SELECT …)
        if (first.Kind != SqlTokenKind.Word) return StatementOutput.Unknown;
        var word = first.Text;

        // Words at the top level (outside parentheses), where a statement's own clauses are.
        var top = new List<SqlToken>();
        var depth = 0;
        foreach (var t in tokens)
        {
            if (t.Kind == SqlTokenKind.OpenParen) depth++;
            else if (t.Kind == SqlTokenKind.CloseParen) depth = Math.Max(0, depth - 1);
            else if (depth == 0 && t.Kind == SqlTokenKind.Word) top.Add(t);
        }
        bool Has(string w) => top.Any(t => t.Is(w));
        var selects = top.Count(t => t.Is("SELECT")) - top.Count(t => SetOperators.Contains(t.Text));

        if (QueryStarts.Contains(word))
        {
            // WITH … INSERT/UPDATE/DELETE: a data-changing statement with a CTE in front.
            if (first.Is("WITH") && selects == 0 && top.Any(t => DmlStarts.Contains(t.Text))) return DmlOutput(top);
            if (first.Is("SELECT") || first.Is("WITH"))
            {
                if (selects > 1) return StatementOutput.Unknown; // T-SQL: "SELECT 1 SELECT 2" is two statements
                if (Has("INTO") && !Has("OUTFILE") && !Has("DUMPFILE")) return StatementOutput.None; // SELECT … INTO a table or variables
            }
            return StatementOutput.OneResult;
        }
        if (DmlStarts.Contains(word))
        {
            if (top.Count(t => t.Is("SELECT")) > 1 || top.Any(t => t.Is("EXEC") || t.Is("EXECUTE"))) return StatementOutput.Unknown;
            return DmlOutput(top);
        }
        if (first.Is("BEGIN"))
            return tokens.Count <= 3 && tokens.Skip(1).All(t => t.Is("TRANSACTION") || t.Is("TRAN") || t.Is("WORK") || t.Kind == SqlTokenKind.Semicolon)
                ? StatementOutput.None
                : StatementOutput.Unknown;
        if (QuietStarts.Contains(word))
        {
            // CREATE PROCEDURE … AS SELECT is a definition, but "SET x = 1 SELECT x" (T-SQL, no separator) returns rows.
            if (first.Is("CREATE") || first.Is("ALTER")) return StatementOutput.None;
            return Has("SELECT") || Has("EXEC") || Has("EXECUTE") ? StatementOutput.Unknown : StatementOutput.None;
        }
        return StatementOutput.Unknown;
    }

    private static StatementOutput DmlOutput(IReadOnlyList<SqlToken> top)
    {
        if (top.Any(t => t.Is("RETURNING"))) return StatementOutput.OneResult;
        var output = top.ToList().FindIndex(t => t.Is("OUTPUT"));
        if (output < 0) return StatementOutput.None;
        // OUTPUT … INTO @table returns nothing to the client.
        return top.Skip(output + 1).Any(t => t.Is("INTO")) ? StatementOutput.None : StatementOutput.OneResult;
    }
}
