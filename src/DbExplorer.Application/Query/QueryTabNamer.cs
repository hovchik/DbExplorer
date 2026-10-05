namespace DbExplorer.Application.Query;

/// <summary>
/// A short tab name for a script that was run: the main table of its first real statement ("Customers" for a SELECT,
/// "UPDATE Orders" for a change), or the statement's verb when there is no table ("SELECT", "DECLARE").
/// </summary>
public static class QueryTabNamer
{
    public const int MaxLength = 40;

    /// <summary>Statements that only prepare a run; the name comes from the statement after them when there is one.</summary>
    private static readonly HashSet<string> Setup = new(StringComparer.OrdinalIgnoreCase)
    {
        "USE", "SET", "DECLARE", "GO", "PRINT", "BEGIN", "COMMIT", "ROLLBACK", "START"
    };

    private static readonly HashSet<string> ObjectKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "TABLE", "VIEW", "PROCEDURE", "PROC", "FUNCTION", "TRIGGER", "INDEX", "SCHEMA", "SEQUENCE", "TYPE",
        "DATABASE", "MATERIALIZED", "UNIQUE", "CLUSTERED", "NONCLUSTERED", "TEMPORARY", "TEMP", "OR", "REPLACE",
        "ALTER", "IF", "NOT", "EXISTS"
    };

    /// <summary>The name for <paramref name="sql"/>; null when it holds no statement.</summary>
    public static string? Suggest(string sql)
    {
        string? fallback = null;
        foreach (var range in SqlScriptTools.SplitStatements(sql))
        {
            var tokens = SqlLexer.Tokenize(range.Of(sql)).Where(t => !t.IsTrivia && t.Kind != SqlTokenKind.Semicolon).ToList();
            if (tokens.Count == 0 || tokens[0].Kind != SqlTokenKind.Word) continue;
            var verb = tokens[0].Text.ToUpperInvariant();
            if (Setup.Contains(verb))
            {
                fallback ??= verb;
                continue;
            }
            return Trim(NameOf(tokens, verb));
        }
        return fallback;
    }

    private static string NameOf(List<SqlToken> tokens, string verb)
    {
        switch (verb)
        {
            case "SELECT":
            case "WITH":
            case "VALUES":
            case "TABLE":
                return TopLevelFrom(tokens) ?? (verb == "WITH" ? "SELECT" : verb);
            case "INSERT":
            case "UPDATE":
            case "DELETE":
            case "MERGE":
            case "TRUNCATE":
            case "REPLACE":
            {
                var i = 1;
                while (i < tokens.Count && (tokens[i].Is("INTO") || tokens[i].Is("FROM") || tokens[i].Is("TABLE") || tokens[i].Is("ONLY") || tokens[i].Is("TOP")))
                {
                    i++;
                    if (i < tokens.Count && tokens[i].Kind == SqlTokenKind.OpenParen) i = SkipParens(tokens, i);
                }
                return Prefixed(verb, NameAt(tokens, i));
            }
            case "CREATE":
            case "ALTER":
            case "DROP":
            {
                var i = 1;
                while (i < tokens.Count && tokens[i].Kind == SqlTokenKind.Word && ObjectKinds.Contains(tokens[i].Text)) i++;
                return Prefixed(verb, NameAt(tokens, i));
            }
            case "EXEC":
            case "EXECUTE":
            case "CALL":
            {
                // EXEC @rc = dbo.Proc …
                var i = 1;
                if (i + 1 < tokens.Count && tokens[i].Kind == SqlTokenKind.Variable && tokens[i + 1].Text == "=") i += 2;
                return Prefixed("EXEC", NameAt(tokens, i));
            }
            default:
                return verb;
        }
    }

    private static string Prefixed(string verb, string? name) => name is null ? verb : $"{verb} {name}";

    /// <summary>The first table of the outermost FROM (a FROM inside parentheses belongs to a subquery or CTE).</summary>
    private static string? TopLevelFrom(List<SqlToken> tokens)
    {
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.OpenParen) depth++;
            else if (t.Kind == SqlTokenKind.CloseParen) depth--;
            else if (depth == 0 && t.Is("FROM")) return NameAt(tokens, i + 1);
        }
        return null;
    }

    /// <summary>The object name starting at <paramref name="i"/> ("dbo.[Order Lines]" → "Order Lines"); null when there is none.</summary>
    private static string? NameAt(List<SqlToken> tokens, int i)
    {
        if (i >= tokens.Count || !tokens[i].IsIdentifier) return null;
        var last = tokens[i];
        while (i + 2 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.Dot && tokens[i + 2].IsIdentifier)
        {
            i += 2;
            last = tokens[i];
        }
        var name = last.Identifier;
        return name.Length == 0 ? null : name;
    }

    private static int SkipParens(List<SqlToken> tokens, int i)
    {
        var depth = 0;
        for (; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SqlTokenKind.OpenParen) depth++;
            else if (tokens[i].Kind == SqlTokenKind.CloseParen && --depth == 0) return i + 1;
        }
        return i;
    }

    private static string Trim(string name) => name.Length <= MaxLength ? name : name[..(MaxLength - 1)] + "…";
}
