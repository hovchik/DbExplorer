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
/// A forgiving SQL tokenizer for SQL Server and PostgreSQL scripts: strings ('..', N'..', E'..', $tag$..$tag$),
/// quoted identifiers ([..], "..", `..`), comments (--, nested /* */), numbers, @variables and punctuation.
/// Unterminated strings/comments run to the end of the text, so it never throws on half-typed input.
/// </summary>
public static class SqlLexer
{
    public static IReadOnlyList<SqlToken> Tokenize(string text)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            SqlTokenKind kind;

            if (char.IsWhiteSpace(c))
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
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.')) i++;
                kind = SqlTokenKind.Number;
            }
            else if (c is '@' or ':' && (char.IsLetter(next) || next is '_' or '@'))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '$' or '#')) i++;
                kind = SqlTokenKind.Variable;
            }
            else if (char.IsLetter(c) || c is '_' or '#')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$' or '#' or '@')) i++;
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
