namespace DbExplorer.Application.Query;

public enum SqlTokenKind
{
    Word,
    QuotedIdentifier,
    String,
    Number,
    Variable,
    Comment,
    Operator,
    OpenParen,
    CloseParen,
    Comma,
    Dot,
    Semicolon,
    Whitespace
}

/// <summary>A slice of the script: <c>Text</c> is <c>Start..Start+Length</c> of the source.</summary>
public readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length, string Text)
{
    public int End => Start + Length;
    public bool IsTrivia => Kind is SqlTokenKind.Whitespace or SqlTokenKind.Comment;
    public bool IsIdentifier => Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier;

    /// <summary>The identifier without [brackets] / "quotes" / `backticks`.</summary>
    public string Identifier => Kind == SqlTokenKind.QuotedIdentifier && Text.Length >= 2 ? Text[1..^1] : Text;

    public bool Is(string word) => Kind == SqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A forgiving SQL tokenizer for SQL Server, PostgreSQL and MySQL scripts: strings ('..', N'..', E'..', $tag$..$tag$),
/// quoted identifiers ([..], "..", `..`), comments (--, nested /* */, MySQL's "# "), numbers, @variables and punctuation.
/// The mysql client's DELIMITER lines are comments, and the delimiter they set ends statements like a semicolon.
/// Unterminated strings/comments run to the end of the text, so it never throws on half-typed input.
/// </summary>
public static class SqlLexer
{
    public static IReadOnlyList<SqlToken> Tokenize(string text)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        string? delimiter = null;
        // A custom delimiter ends a word too: END$$ is END then $$.
        bool NotDelimiter(int p) => delimiter is null || string.CompareOrdinal(text, p, delimiter, 0, delimiter.Length) != 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            SqlTokenKind kind;

            if (AtLineStart(text, i) && DelimiterCommand(text, i) is { } command)
            {
                // Never valid SQL in any engine, so this cannot misread another dialect's script.
                i = command.End;
                delimiter = command.Delimiter == ";" ? null : command.Delimiter;
                kind = SqlTokenKind.Comment;
            }
            else if (delimiter is not null && string.CompareOrdinal(text, i, delimiter, 0, delimiter.Length) == 0)
            {
                i += delimiter.Length;
                kind = SqlTokenKind.Semicolon;
            }
            else if (c == '#' && (next == '\0' || char.IsWhiteSpace(next)))
            {
                // MySQL comment; a T-SQL temp table name (#t) never has a space after the #.
                while (i < text.Length && text[i] != '\n') i++;
                kind = SqlTokenKind.Comment;
            }
            else if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                kind = SqlTokenKind.Whitespace;
            }
            else if (c == '-' && next == '-')
            {
                while (i < text.Length && text[i] != '\n') i++;
                kind = SqlTokenKind.Comment;
            }
            else if (c == '/' && next == '*')
            {
                var depth = 0;
                while (i < text.Length)
                {
                    if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { depth++; i += 2; }
                    else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/') { depth--; i += 2; if (depth == 0) break; }
                    else i++;
                }
                kind = SqlTokenKind.Comment;
            }
            else if (c == '\'' || ((c is 'N' or 'n' or 'E' or 'e') && next == '\''))
            {
                if (c != '\'') i++;
                i = SkipQuoted(text, i, '\'', allowBackslash: c is 'E' or 'e');
                kind = SqlTokenKind.String;
            }
            else if (c == '$' && TryDollarQuote(text, i, out var end))
            {
                i = end;
                kind = SqlTokenKind.String;
            }
            else if (c == '[')
            {
                i = SkipQuoted(text, i, ']', allowBackslash: false, open: '[');
                kind = SqlTokenKind.QuotedIdentifier;
            }
            else if (c is '"' or '`')
            {
                i = SkipQuoted(text, i, c, allowBackslash: false);
                kind = SqlTokenKind.QuotedIdentifier;
            }
            else if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.') && NotDelimiter(i)) i++;
                kind = SqlTokenKind.Number;
            }
            else if (c is '@' or ':' && (char.IsLetter(next) || next is '_' or '@'))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '$' or '#') && NotDelimiter(i)) i++;
                kind = SqlTokenKind.Variable;
            }
            else if (char.IsLetter(c) || c is '_' or '#')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$' or '#' or '@') && NotDelimiter(i)) i++;
                kind = SqlTokenKind.Word;
            }
            else
            {
                i++;
                kind = c switch
                {
                    '(' => SqlTokenKind.OpenParen,
                    ')' => SqlTokenKind.CloseParen,
                    ',' => SqlTokenKind.Comma,
                    '.' => SqlTokenKind.Dot,
                    ';' => SqlTokenKind.Semicolon,
                    _ => SqlTokenKind.Operator
                };
                // Two-character operators: <= >= <> != || :: ->
                if (kind == SqlTokenKind.Operator && i < text.Length &&
                    (c, text[i]) is ('<', '=') or ('>', '=') or ('<', '>') or ('!', '=') or ('|', '|') or (':', ':') or ('-', '>'))
                    i++;
            }

            tokens.Add(new SqlToken(kind, start, i - start, text[start..i]));
        }
        return tokens;
    }

    private static bool AtLineStart(string text, int i)
    {
        for (var j = i - 1; j >= 0; j--)
        {
            if (text[j] == '\n') return true;
            if (!char.IsWhiteSpace(text[j])) return false;
        }
        return true;
    }

    /// <summary>"DELIMITER $$" on a line of its own: where the line ends and the new delimiter.</summary>
    private static (int End, string Delimiter)? DelimiterCommand(string text, int i)
    {
        const string word = "DELIMITER";
        if (string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return null;
        var j = i + word.Length;
        if (j >= text.Length || text[j] is not (' ' or '\t')) return null;
        while (j < text.Length && text[j] is ' ' or '\t') j++;
        var d = j;
        while (j < text.Length && !char.IsWhiteSpace(text[j])) j++;
        if (j == d) return null;
        var delimiter = text[d..j];
        while (j < text.Length && text[j] is ' ' or '\t' or '\r') j++;
        return j >= text.Length || text[j] == '\n' ? (j, delimiter) : null;
    }

    private static int SkipQuoted(string text, int i, char close, bool allowBackslash, char? open = null)
    {
        i++; // opening quote
        while (i < text.Length)
        {
            if (allowBackslash && text[i] == '\\') { i += 2; continue; }
            if (text[i] == close)
            {
                // Doubled closing character is an escape ('' ]] "").
                if (i + 1 < text.Length && text[i + 1] == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }

    /// <summary>PostgreSQL dollar quoting: $$ ... $$ or $tag$ ... $tag$.</summary>
    private static bool TryDollarQuote(string text, int i, out int end)
    {
        end = i;
        var j = i + 1;
        while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
        if (j >= text.Length || text[j] != '$') return false;
        if (j > i + 1 && char.IsDigit(text[i + 1])) return false; // $1 is a parameter
        var tag = text[i..(j + 1)];
        var close = text.IndexOf(tag, j + 1, StringComparison.Ordinal);
        end = close < 0 ? text.Length : close + tag.Length;
        return true;
    }
}
