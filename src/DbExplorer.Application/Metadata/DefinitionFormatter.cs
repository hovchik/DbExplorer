using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Metadata;

/// <summary>
/// Lays out an object's definition for reading in the Objects tab: tidy indentation and blank lines, upper-case
/// keywords, one routine parameter per line, view queries one clause per line, long one-line trigger and sequence
/// definitions split over lines. Display only: the result is never sent to the server. Strings, comments and quoted
/// identifiers are kept as they are, and procedural bodies are never re-flowed, only keyword-cased.
/// </summary>
public static partial class DefinitionFormatter
{
    private const string Indent = "    ";

    /// <summary>Words upper-cased in definitions on top of the editor's keyword list.</summary>
    private static readonly IReadOnlySet<string> Keywords = SqlCompletionEngine.KeywordSet
        .Concat([
            "ALTER", "REPLACE", "PROC", "TRIGGER", "MATERIALIZED", "SEQUENCE", "LANGUAGE", "OUT", "INOUT", "VARIADIC",
            "READONLY", "BEFORE", "AFTER", "INSTEAD", "OF", "FOR", "EACH", "ROW", "STATEMENT", "REFERENCING", "NEW",
            "OLD", "SCHEMABINDING", "ENCRYPTION", "RECOMPILE", "NOCOUNT", "XACT_ABORT", "CALLER", "OWNER", "CHECK",
            "OPTION", "TRY", "CATCH", "THROW", "RAISERROR", "GOTO", "BREAK", "CONTINUE", "WAITFOR", "ELSIF", "LOOP",
            "PERFORM", "RAISE", "NOTICE", "EXCEPTION", "FOUND", "STRICT", "IMMUTABLE", "STABLE", "VOLATILE", "SECURITY",
            "DEFINER", "INVOKER", "PARALLEL", "SAFE", "UNSAFE", "RESTRICTED", "COST", "CALLED", "INPUT", "SETOF",
            "START", "INCREMENT", "MINVALUE", "MAXVALUE", "CYCLE", "NO", "CACHE", "OWNED", "CONSTRAINT", "UNIQUE",
            "KEY", "CASCADE", "LATERAL", "FILTER", "WITHIN", "ROWS", "RANGE", "PRECEDING", "FOLLOWING", "UNBOUNDED",
            "CURRENT", "ROW", "ELSE", "ELSEIF", "END", "DO", "INSTEAD", "QUERY", "GET", "DIAGNOSTICS", "OPEN", "CLOSE",
            "CURSOR", "DEALLOCATE", "FETCH", "LOCAL", "STATIC", "FORWARD_ONLY", "READ_ONLY", "EXECUTE", "EXEC"
        ])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Format(string definition, DbObjectType type)
    {
        if (string.IsNullOrWhiteSpace(definition)) return definition;

        var text = Tidy(definition);
        text = type switch
        {
            DbObjectType.View or DbObjectType.MaterializedView => FormatView(text),
            DbObjectType.Procedure or DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction
                => FormatRoutine(text),
            DbObjectType.Trigger => BreakSingleLineStatements(text, TriggerClauses),
            DbObjectType.Sequence => BreakSingleLineStatements(text, SequenceOptions),
            _ => text
        };
        return UpperKeywords(text);
    }

    /// <summary>
    /// Unix line ends, tabs in indentation as four spaces, no trailing spaces, no common left margin, at most one
    /// blank line in a row and none at either end.
    /// </summary>
    public static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => ExpandLeadingTabs(l).TrimEnd())
            .ToList();

        var margin = lines.Where(l => l.Length > 0).Select(l => l.Length - l.TrimStart(' ').Length).DefaultIfEmpty(0).Min();

        var sb = new StringBuilder();
        var blank = 0;
        foreach (var raw in lines)
        {
            var line = raw.Length >= margin ? raw[margin..] : raw.TrimStart(' ');
            if (line.Length == 0)
            {
                blank++;
                continue;
            }
            if (sb.Length > 0) sb.Append(blank > 0 ? "\n\n" : "\n");
            blank = 0;
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static string ExpandLeadingTabs(string line)
    {
        var i = 0;
        var sb = new StringBuilder();
        for (; i < line.Length && line[i] is ' ' or '\t'; i++)
            sb.Append(line[i] == '\t' ? Indent : " ");
        return i == 0 ? line : sb.Append(line, i, line.Length - i).ToString();
    }

    // ----- Views -----

    /// <summary>"CREATE VIEW v AS" on its own line, then the query one clause per line.</summary>
    private static string FormatView(string text)
    {
        var tokens = SqlLexer.Tokenize(text);
        var view = tokens.FindIndex(t => t.Is("VIEW"));
        if (view < 0) return text;

        var depth = 0;
        for (var i = view + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.OpenParen) depth++;
            else if (t.Kind == SqlTokenKind.CloseParen) depth--;
            else if (depth == 0 && t.Is("AS"))
            {
                var body = text[t.End..].Trim();
                if (body.Length == 0 || ContainsWord(body, "BEGIN")) return text;
                return text[..t.End].TrimEnd() + "\n" + SqlScriptTools.Format(body, Keywords).Trim('\n');
            }
        }
        return text;
    }

    // ----- Procedures and functions -----

    /// <summary>Puts each parameter of every CREATE PROCEDURE/FUNCTION header on its own line.</summary>
    private static string FormatRoutine(string text)
    {
        var tokens = SqlLexer.Tokenize(text);
        var edits = new List<(int Start, int End, string Text)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Is("CREATE") && !tokens[i].Is("ALTER")) continue;
            if (ParameterEdit(text, tokens, i) is { } edit) edits.Add(edit);
        }

        // Applied back to front so earlier offsets stay valid.
        foreach (var (start, end, replacement) in edits.OrderByDescending(e => e.Start))
            text = text[..start] + replacement + text[end..];

        return OutdentPostgresRoutineOptions(text);
    }

    private static (int Start, int End, string Text)? ParameterEdit(string text, IReadOnlyList<SqlToken> tokens, int create)
    {
        var i = Next(tokens, create);
        if (i < tokens.Count && tokens[i].Is("OR")) i = Next(tokens, Next(tokens, i));
        if (i >= tokens.Count || !(tokens[i].Is("PROCEDURE") || tokens[i].Is("PROC") || tokens[i].Is("FUNCTION"))) return null;

        // The routine name: identifiers joined by dots.
        var nameEnd = Next(tokens, i);
        if (nameEnd >= tokens.Count || !tokens[nameEnd].IsIdentifier) return null;
        while (Next(tokens, nameEnd) is var dot && dot < tokens.Count && tokens[dot].Kind == SqlTokenKind.Dot &&
               Next(tokens, dot) is var part && part < tokens.Count && tokens[part].IsIdentifier)
            nameEnd = part;

        var first = Next(tokens, nameEnd);
        if (first >= tokens.Count) return null;

        if (tokens[first].Kind == SqlTokenKind.OpenParen)
        {
            var close = MatchingParen(tokens, first);
            if (close < 0) return null;
            var parameters = SplitTopLevel(tokens, first + 1, close);
            if (parameters is null) return null;
            if (parameters.Count == 0) return (tokens[first].Start, tokens[close].End, "()");
            var oneLine = "(" + string.Join(", ", parameters) + ")";
            var replacement = parameters.Count == 1 && oneLine.Length <= 60
                ? oneLine
                : "(\n" + string.Join(",\n", parameters.Select(p => Indent + p)) + "\n)";
            return (tokens[first].Start, tokens[close].End, replacement);
        }

        // SQL Server procedures may list parameters without parentheses: "CREATE PROC p @a int, @b int AS".
        if (tokens[first].Kind != SqlTokenKind.Variable) return null;
        var depth = 0;
        for (var j = first; j < tokens.Count; j++)
        {
            var t = tokens[j];
            if (t.Kind == SqlTokenKind.OpenParen) depth++;
            else if (t.Kind == SqlTokenKind.CloseParen) depth--;
            else if (depth == 0 && (t.Is("AS") || t.Is("WITH") || t.Is("FOR")))
            {
                var parameters = SplitTopLevel(tokens, first, LastSignificantBefore(tokens, j) + 1);
                if (parameters is null || parameters.Count == 0) return null;
                return (tokens[nameEnd].End, t.Start, "\n" + string.Join(",\n", parameters.Select(p => Indent + p)) + "\n");
            }
        }
        return null;
    }

    /// <summary>The comma-separated items of tokens [from, to) with whitespace collapsed; null when a comment is
    /// in the way (moving it could change what it comments).</summary>
    private static List<string>? SplitTopLevel(IReadOnlyList<SqlToken> tokens, int from, int to)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        for (var i = from; i < to; i++)
        {
            var t = tokens[i];
            switch (t.Kind)
            {
                case SqlTokenKind.Comment:
                    return null;
                case SqlTokenKind.Whitespace:
                    current.Append(' ');
                    continue;
                case SqlTokenKind.OpenParen:
                    depth++;
                    break;
                case SqlTokenKind.CloseParen:
                    depth--;
                    break;
                case SqlTokenKind.Comma when depth == 0:
                    items.Add(Collapse(current.ToString()));
                    current.Clear();
                    continue;
            }
            current.Append(t.Text);
        }
        var last = Collapse(current.ToString());
        if (last.Length > 0 || items.Count > 0) items.Add(last);
        return items;
    }

    private static string Collapse(string s) => SpaceRun().Replace(s.Trim(), " ").Replace("( ", "(").Replace(" )", ")");

    /// <summary>pg_get_functiondef indents RETURNS, LANGUAGE … by one space; line them up with CREATE.</summary>
    private static string OutdentPostgresRoutineOptions(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains('$')) break; // the body starts; leave it alone
            if (PostgresOption().IsMatch(lines[i])) lines[i] = lines[i][1..];
        }
        return string.Join('\n', lines);
    }

    // ----- Triggers and sequences -----

    private static readonly IReadOnlySet<string> TriggerClauses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "BEFORE", "AFTER", "INSTEAD", "ON", "FROM", "REFERENCING", "FOR", "WHEN", "EXECUTE" };

    private static readonly IReadOnlySet<string> SequenceOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "AS", "START", "INCREMENT", "MINVALUE", "MAXVALUE", "NO", "CYCLE", "CACHE", "OWNED" };

    /// <summary>Statements written on one long line get each clause on its own indented line.</summary>
    private static string BreakSingleLineStatements(string text, IReadOnlySet<string> clauses)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines.Select(line => line.Length > 60 ? BreakLine(line, clauses) : line));
    }

    private static string BreakLine(string line, IReadOnlySet<string> clauses)
    {
        var tokens = SqlLexer.Tokenize(line);
        var sb = new StringBuilder();
        var depth = 0;
        SqlToken? previous = null;
        foreach (var t in tokens)
        {
            if (t.Kind == SqlTokenKind.OpenParen) depth++;
            else if (t.Kind == SqlTokenKind.CloseParen) depth--;

            var breaks = depth == 0 && t.Kind == SqlTokenKind.Word && previous is not null && clauses.Contains(t.Text)
                         && !previous.Value.Is("NO"); // "NO CYCLE" stays together
            if (breaks)
            {
                while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                sb.Append('\n').Append(Indent);
            }
            sb.Append(t.Text);
            if (!t.IsTrivia) previous = t;
        }
        return sb.ToString();
    }

    // ----- Keyword case -----

    /// <summary>Upper-cases keywords outside strings, comments and quoted names; also inside $$ bodies of SQL and
    /// PL/pgSQL routines.</summary>
    public static string UpperKeywords(string text)
    {
        var tokens = SqlLexer.Tokenize(text);
        var sqlBodies = IsSqlLanguage(tokens);
        var sb = new StringBuilder(text.Length);
        foreach (var t in tokens)
        {
            if (t.Kind == SqlTokenKind.Word && Keywords.Contains(t.Text)) sb.Append(t.Text.ToUpperInvariant());
            else if (sqlBodies && t.Kind == SqlTokenKind.String && DollarBody().Match(t.Text) is { Success: true } m)
                sb.Append(m.Groups["open"].Value).Append(UpperKeywords(m.Groups["body"].Value)).Append(m.Groups["close"].Value);
            else sb.Append(t.Text);
        }
        return sb.ToString();
    }

    /// <summary>True unless a LANGUAGE clause names something other than SQL or PL/pgSQL (e.g. plpython).</summary>
    private static bool IsSqlLanguage(IReadOnlyList<SqlToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Is("LANGUAGE")) continue;
            var next = Next(tokens, i);
            if (next >= tokens.Count) return false;
            var language = tokens[next].Identifier.Trim('\'');
            return language.Equals("sql", StringComparison.OrdinalIgnoreCase) ||
                   language.Equals("plpgsql", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // ----- Token helpers -----

    private static int Next(IReadOnlyList<SqlToken> tokens, int index)
    {
        var i = index + 1;
        while (i < tokens.Count && tokens[i].IsTrivia) i++;
        return i;
    }

    private static int LastSignificantBefore(IReadOnlyList<SqlToken> tokens, int index)
    {
        var i = index - 1;
        while (i >= 0 && tokens[i].IsTrivia) i--;
        return i;
    }

    private static int MatchingParen(IReadOnlyList<SqlToken> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SqlTokenKind.OpenParen) depth++;
            else if (tokens[i].Kind == SqlTokenKind.CloseParen && --depth == 0) return i;
        }
        return -1;
    }

    private static bool ContainsWord(string text, string word) => SqlLexer.Tokenize(text).Any(t => t.Is(word));

    private static int FindIndex(this IReadOnlyList<SqlToken> tokens, Func<SqlToken, bool> match)
    {
        for (var i = 0; i < tokens.Count; i++) if (match(tokens[i])) return i;
        return -1;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRun();

    [GeneratedRegex(@"^ (RETURNS|LANGUAGE|SECURITY|SET|IMMUTABLE|STABLE|VOLATILE|STRICT|CALLED|PARALLEL|COST|ROWS|WINDOW|LEAKPROOF|NOT|SUPPORT|TRANSFORM)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PostgresOption();

    [GeneratedRegex(@"^(?<open>\$[A-Za-z_]*\$)(?<body>.*)(?<close>\$[A-Za-z_]*\$)$", RegexOptions.Singleline)]
    private static partial Regex DollarBody();
}
