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

            if (t.Kind == SqlTokenKind.Semicolon && (depth == 0 && blocks == 0 || t.Text != ";"))
            {
                // A DELIMITER-set delimiter ($$, //) always ends the statement and is not part of it: the server only knows ';'.
                if (start >= 0 && t.Text == ";") end = t.End;
                Close();
                depth = t.Text == ";" ? depth : 0;
                blocks = t.Text == ";" ? blocks : 0;
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
            else if (t.Is("END") && blocks > 0 && !EndsUncountedBlock(tokens, i)) blocks--;

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

    /// <summary>Formats a script with the default style; see <see cref="SqlFormatter"/>.</summary>
    public static string Format(string text, IReadOnlySet<string>? keywords = null) => SqlFormatter.Format(text, null, keywords);

    /// <summary>END IF / END LOOP / END WHILE / END REPEAT (MySQL, PL/pgSQL): their openers are not counted as blocks.</summary>
    private static bool EndsUncountedBlock(IReadOnlyList<SqlToken> tokens, int index) =>
        tokens.Skip(index + 1).Where(t => !t.IsTrivia).Take(2).ToList() is [var word, ..] rest &&
        (word.Is("IF") || word.Is("LOOP") || word.Is("WHILE") || word.Is("REPEAT")) &&
        // T-SQL has no END IF: "END IF @x = 1" is a block's END and the next statement.
        (rest.Count == 1 || rest[1].Kind == SqlTokenKind.Semicolon || word.Is("LOOP") && rest[1].Kind == SqlTokenKind.Word);

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
}
