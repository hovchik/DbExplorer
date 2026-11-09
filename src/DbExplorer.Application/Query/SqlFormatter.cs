using System.Text;

namespace DbExplorer.Application.Query;

/// <summary>What <see cref="SqlFormatter.FormatRange"/> replaces: <c>Start..Start+Length</c> of the document becomes <c>Text</c>.</summary>
public readonly record struct SqlTextEdit(int Start, int Length, string Text);

/// <summary>
/// The Query editor's SQL formatter (Ctrl+Shift+F), for SQL Server, PostgreSQL, MySQL / MariaDB and SQLite scripts.
/// Each statement is laid out by kind:
/// <list type="bullet">
/// <item>Queries and DML: one clause per line, one list item per line, AND / OR on their own lines, subqueries and CTE
/// bodies indented in their parentheses, multi-branch CASE expressions one WHEN per line.</item>
/// <item>CREATE TABLE: one column or constraint per line with the types lined up; CREATE VIEW: the query laid out.</item>
/// <item>Procedural code (procedures, functions, triggers, DO and BEGIN…END blocks, IF / WHILE / LOOP): re-indented by
/// block, keeping the author's line breaks; statements that end with ';' inside a block are laid out like queries,
/// and PL/pgSQL or SQL bodies in $$ quotes are formatted too.</item>
/// <item>Anything else (ALTER, DROP, GRANT, SET…): keywords cased and spacing tidied, line breaks kept.</item>
/// </list>
/// Only whitespace and keyword case ever change: strings, quoted identifiers and comments are kept byte for byte, and
/// a line comment always keeps a line break after it.
/// </summary>
public static class SqlFormatter
{
    /// <summary>Formats a whole script (or any fragment of one).</summary>
    public static string Format(string text, SqlFormatOptions? options = null, IReadOnlySet<string>? keywords = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        options = (options ?? SqlFormatOptions.Default).Normalized();
        keywords ??= Keywords;

        var source = text.Replace("\r\n", "\n");
        var sb = new StringBuilder();
        var position = 0;
        foreach (var range in SqlScriptTools.SplitStatements(source))
        {
            AppendGap(sb, source[position..range.Start], statementFollows: true);
            sb.Append(FormatStatement(range.Of(source), options, keywords));
            position = range.End;
        }
        AppendGap(sb, source[position..], statementFollows: false);

        var result = sb.ToString().Trim('\n');
        if (text.EndsWith('\n')) result += "\n";
        return text.Contains("\r\n") ? result.Replace("\n", "\r\n") : result;
    }

    /// <summary>
    /// Formats part of a document, such as the editor's selection: the text is formatted on its own, its later lines
    /// are indented like the line it starts on, and the whitespace around it is left alone.
    /// </summary>
    public static SqlTextEdit FormatRange(string document, int start, int length, SqlFormatOptions? options = null)
    {
        start = Math.Clamp(start, 0, document.Length);
        length = Math.Clamp(length, 0, document.Length - start);
        var end = start + length;
        while (start < end && char.IsWhiteSpace(document[start])) start++;
        while (end > start && char.IsWhiteSpace(document[end - 1])) end--;
        if (end == start) return new SqlTextEdit(start, 0, "");

        var lineStart = start == 0 ? 0 : document.LastIndexOf('\n', start - 1) + 1;
        var before = document[lineStart..start];
        var baseIndent = before.Trim().Length == 0 ? before : new string(before.TakeWhile(c => c is ' ' or '\t').ToArray());

        var formatted = Format(document[start..end], options).TrimEnd('\r', '\n');
        if (baseIndent.Length > 0) formatted = IndentLines(formatted, baseIndent, firstLine: false);
        return new SqlTextEdit(start, end - start, formatted);
    }

    /// <summary>
    /// The script joined into one line (for pasting into code or a chat): whitespace becomes single spaces, line
    /// comments become /* */ comments. GO lines and DELIMITER commands stay on their own lines.
    /// </summary>
    public static string Compact(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var tokens = SqlLexer.Tokenize(text.Replace("\r\n", "\n"));
        var sb = new StringBuilder();
        var space = false;
        SqlToken? previous = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.Whitespace) { space = true; continue; }

            var ownLine = t.Is("GO") && IsAloneOnLine(tokens, i) || t.Kind == SqlTokenKind.Comment && IsDelimiterCommand(t.Text);
            if (ownLine)
            {
                TrimEnd(sb);
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t.Text.Trim()).Append('\n');
                space = false;
                previous = null;
                continue;
            }

            var piece = t.Text;
            if (t.Kind == SqlTokenKind.Comment && !piece.StartsWith("/*", StringComparison.Ordinal))
                piece = "/* " + piece.TrimStart('-', '#').Trim().Replace("*/", "* /") + " */";
            else if (t.Kind == SqlTokenKind.Comment)
                piece = string.Join(' ', piece.Split('\n').Select(l => l.Trim()));

            var noSpace = sb.Length == 0 || sb[^1] == '\n' || previous is { Kind: SqlTokenKind.OpenParen or SqlTokenKind.Dot } ||
                          t.Kind is SqlTokenKind.CloseParen or SqlTokenKind.Comma or SqlTokenKind.Semicolon or SqlTokenKind.Dot;
            if (space && !noSpace) sb.Append(' ');
            sb.Append(piece);
            space = t.Kind is SqlTokenKind.Comma or SqlTokenKind.Semicolon;
            previous = t;
        }
        TrimEnd(sb);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Where the caret should go after formatting: the same number of non-blank characters in, so it stays on the
    /// same token (formatting only changes whitespace).
    /// </summary>
    public static int MapOffset(string before, string after, int offset)
    {
        offset = Math.Clamp(offset, 0, before.Length);
        var count = 0;
        for (var i = 0; i < offset; i++) if (!char.IsWhiteSpace(before[i])) count++;
        if (count == 0) return 0;
        for (var i = 0; i < after.Length; i++)
            if (!char.IsWhiteSpace(after[i]) && --count == 0) return i + 1;
        return after.Length;
    }

    /// <summary>Prefixes every line of <paramref name="text"/> with <paramref name="indent"/>, except lines that start
    /// inside a multi-line string or comment (which would change it).</summary>
    public static string IndentLines(string text, string indent, bool firstLine = true)
    {
        if (indent.Length == 0) return text;
        var sb = new StringBuilder();
        if (firstLine && text.Length > 0 && text[0] != '\n') sb.Append(indent);
        foreach (var t in SqlLexer.Tokenize(text))
        {
            if (t.Kind != SqlTokenKind.Whitespace) { sb.Append(t.Text); continue; }
            for (var k = 0; k < t.Text.Length; k++)
            {
                sb.Append(t.Text[k]);
                // Blank lines stay empty.
                if (t.Text[k] == '\n' && (k + 1 < t.Text.Length ? t.Text[k + 1] != '\n' : t.End < text.Length)) sb.Append(indent);
            }
        }
        return sb.ToString();
    }

    /// <summary>Words cased as keywords: the editor's keywords and functions, plus procedural and DDL words that are
    /// rarely used as names (KEY, TYPE, OPEN, COMMENT… are left out on purpose: they are common column names).</summary>
    public static readonly IReadOnlySet<string> Keywords = SqlCompletionEngine.KeywordSet
        .Concat([
            "CONFLICT", "DO", "NOTHING", "FOR", "NOCOUNT", "XACT_ABORT", "ANSI_NULLS", "QUOTED_IDENTIFIER", "TRY", "CATCH",
            "RAISERROR", "THROW", "LANGUAGE", "ELSIF", "ELSEIF", "LOOP", "RAISE", "NOTICE", "EXCEPTION", "OTHERS", "MATCHED",
            "GENERATED", "ALWAYS", "IDENTITY", "DECLARE", "PERFORM", "REPEAT", "UNTIL", "LEAVE", "ITERATE", "CURSOR",
            "DEALLOCATE", "PRINT", "GOTO", "BREAK", "CONTINUE", "WAITFOR", "OUTPUT", "INOUT", "RETURNS", "TRIGGER", "BEFORE",
            "AFTER", "INSTEAD", "EACH", "IMMUTABLE", "STABLE", "VOLATILE", "SECURITY", "DEFINER", "INVOKER", "SETOF", "CASCADE",
            "RESTRICT", "TEMPORARY", "UNLOGGED", "IF", "ELSE", "END", "BEGIN", "WHILE", "RECURSIVE", "MATERIALIZED",
            "CONCURRENTLY", "GRANT", "REVOKE", "PRIVILEGES", "NOLOCK", "ROWLOCK", "UPDLOCK", "READPAST", "HOLDLOCK", "TIES",
            "PERCENT", "PARTITION", "OVER", "WITHIN", "ROLLUP", "CUBE", "GROUPING", "SETS", "ILIKE", "SIMILAR", "ESCAPE",
            "COLLATE", "AUTO_INCREMENT", "UNSIGNED", "IGNORE", "DUPLICATE", "PRIMARY", "FOREIGN", "REFERENCES", "CLUSTERED",
            "NONCLUSTERED", "INCLUDE", "RENAME", "DEFAULT", "NULL", "NOT", "TRUE", "FALSE", "PROCEDURE", "PROC", "EXEC"
        ])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ----- Between statements -----

    /// <summary>What lies between two statements: whitespace, GO lines, a MySQL DELIMITER-set delimiter. Kept, with at most one blank line.</summary>
    private static void AppendGap(StringBuilder sb, string gap, bool statementFollows)
    {
        var lines = gap.Split('\n');
        var lastContent = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (i == 0)
            {
                if (sb.Length > 0 && gap.Length > 0 && char.IsWhiteSpace(gap[0])) sb.Append(' ');
            }
            else if (sb.Length > 0)
            {
                sb.Append(i - lastContent >= 2 ? "\n\n" : "\n");
            }
            sb.Append(line);
            lastContent = i;
        }
        if (!statementFollows || sb.Length == 0) return;
        var trailing = lines.Length - 1 - lastContent;
        sb.Append(trailing >= 2 ? "\n\n" : "\n");
    }

    // ----- One statement -----

    private enum StatementKind { Query, CreateTable, Procedural }

    internal static string FormatStatement(string statement, SqlFormatOptions options, IReadOnlySet<string> keywords)
    {
        var tokens = SqlLexer.Tokenize(statement);
        var (kind, restart) = Classify(tokens);
        switch (kind)
        {
            case StatementKind.Query:
                var query = new QueryWriter(tokens, options, keywords) { RestartAt = restart };
                query.Walk(0, tokens.Count, QueryMode.Query, 0);
                return query.ToString();
            case StatementKind.CreateTable when CreateTableWriter.TryFormat(tokens, options, keywords) is { } table:
                return table;
            default:
                return new BlockWriter(statement, tokens, options, keywords).Write();
        }
    }

    private static readonly HashSet<string> QueryStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "MERGE", "VALUES", "REPLACE", "EXPLAIN", "UPSERT"
    };

    private static readonly HashSet<string> RoutineWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "FUNCTION", "PROCEDURE", "PROC", "TRIGGER", "EVENT", "PACKAGE", "BODY"
    };

    private static readonly HashSet<string> ObjectWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "TABLE", "VIEW", "FUNCTION", "PROCEDURE", "PROC", "TRIGGER", "EVENT", "PACKAGE", "INDEX", "SCHEMA", "SEQUENCE",
        "TYPE", "DATABASE", "ROLE", "USER", "LOGIN", "EXTENSION", "DOMAIN", "SYNONYM", "POLICY", "RULE"
    };

    /// <summary>The statement's kind, and for CREATE VIEW / CREATE TABLE … AS the token after the AS that starts the query.</summary>
    private static (StatementKind Kind, int Restart) Classify(IReadOnlyList<SqlToken> tokens)
    {
        var first = NextSignificant(tokens, -1);
        if (first < 0) return (StatementKind.Procedural, -1);
        if (HasBlock(tokens)) return (StatementKind.Procedural, -1);

        var t = tokens[first];
        if (t.Kind == SqlTokenKind.OpenParen || QueryStarts.Contains(t.Text) && t.Kind == SqlTokenKind.Word)
            return (StatementKind.Query, -1);
        if (!t.Is("CREATE") && !t.Is("ALTER")) return (StatementKind.Procedural, -1);

        var objectWord = -1;
        for (int i = NextSignificant(tokens, first), n = 0; i >= 0 && n < 14; i = NextSignificant(tokens, i), n++)
            if (tokens[i].Kind == SqlTokenKind.Word && ObjectWords.Contains(tokens[i].Text)) { objectWord = i; break; }
        if (objectWord < 0 || RoutineWords.Contains(tokens[objectWord].Text)) return (StatementKind.Procedural, -1);

        if (tokens[objectWord].Is("VIEW"))
            return TopLevelAs(tokens, objectWord) is { } body ? (StatementKind.Query, body) : (StatementKind.Procedural, -1);
        if (tokens[objectWord].Is("TABLE") && t.Is("CREATE"))
        {
            if (TopLevelAs(tokens, objectWord) is { } body && tokens[body] is var q && (q.Is("SELECT") || q.Is("WITH") || q.Kind == SqlTokenKind.OpenParen))
                return (StatementKind.Query, body);
            return (StatementKind.CreateTable, -1);
        }
        return (StatementKind.Procedural, -1);
    }

    /// <summary>The first token after the first AS outside parentheses that follows <paramref name="from"/>.</summary>
    private static int? TopLevelAs(IReadOnlyList<SqlToken> tokens, int from)
    {
        var depth = 0;
        for (var i = from + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.OpenParen) depth++;
            else if (t.Kind == SqlTokenKind.CloseParen) depth--;
            else if (depth == 0 && t.Is("AS"))
            {
                var next = NextSignificant(tokens, i);
                return next < 0 ? null : next;
            }
        }
        return null;
    }

    /// <summary>A BEGIN…END block, a $$-quoted routine body, or a procedural statement (IF, WHILE, DECLARE…).</summary>
    private static bool HasBlock(IReadOnlyList<SqlToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].Is("BEGIN") && !IsBeginTransaction(tokens, i)) return true;
        return false;
    }

    internal static bool IsBeginTransaction(IReadOnlyList<SqlToken> tokens, int index)
    {
        var next = NextSignificant(tokens, index);
        if (next < 0) return true;
        var n = tokens[next];
        return n.Is("TRAN") || n.Is("TRANSACTION") || n.Is("DISTRIBUTED") || n.Kind == SqlTokenKind.Semicolon ||
               n.Is("WORK") || n.Is("ISOLATION") || n.Is("DEFERRED") || n.Is("IMMEDIATE") || n.Is("EXCLUSIVE") ||
               n.Is("READ") || n.Is("NOT");
    }

    // ----- Shared helpers -----

    internal static int NextSignificant(IReadOnlyList<SqlToken> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++) if (!tokens[i].IsTrivia) return i;
        return -1;
    }

    internal static int PreviousSignificant(IReadOnlyList<SqlToken> tokens, int index)
    {
        for (var i = index - 1; i >= 0; i--) if (!tokens[i].IsTrivia) return i;
        return -1;
    }

    internal static int MatchingParen(IReadOnlyList<SqlToken> tokens, int open, int to)
    {
        var depth = 0;
        for (var i = open; i < to; i++)
        {
            if (tokens[i].Kind == SqlTokenKind.OpenParen) depth++;
            else if (tokens[i].Kind == SqlTokenKind.CloseParen && --depth == 0) return i;
        }
        return -1;
    }

    private static bool IsAloneOnLine(IReadOnlyList<SqlToken> tokens, int i) =>
        (i == 0 || tokens[i - 1].Kind == SqlTokenKind.Whitespace && tokens[i - 1].Text.Contains('\n')) &&
        (i + 1 >= tokens.Count || tokens[i + 1].Kind == SqlTokenKind.Whitespace && tokens[i + 1].Text.Contains('\n'));

    internal static bool IsDelimiterCommand(string comment) =>
        comment.StartsWith("DELIMITER", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLineComment(string comment) => !comment.StartsWith("/*", StringComparison.Ordinal);

    internal static string Case(string word, SqlKeywordCase keywordCase) => keywordCase switch
    {
        SqlKeywordCase.Upper => word.ToUpperInvariant(),
        SqlKeywordCase.Lower => word.ToLowerInvariant(),
        _ => word
    };

    /// <summary>The token as written out: keywords cased, except where they are a name (next to a dot).</summary>
    internal static string Cased(IReadOnlyList<SqlToken> tokens, int i, SqlFormatOptions options, IReadOnlySet<string> keywords)
    {
        var t = tokens[i];
        if (t.Kind != SqlTokenKind.Word || !keywords.Contains(t.Text)) return t.Text;
        var p = PreviousSignificant(tokens, i);
        var n = NextSignificant(tokens, i);
        if (p >= 0 && tokens[p].Kind == SqlTokenKind.Dot || n >= 0 && tokens[n].Kind == SqlTokenKind.Dot) return t.Text;
        return Case(t.Text, options.KeywordCase);
    }

    private static void TrimEnd(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] is ' ' or '\t') sb.Length--;
    }

    /// <summary>Operators written with a space on both sides.</summary>
    private static readonly HashSet<string> SpacedOperators = new(StringComparer.Ordinal)
    {
        "=", "<>", "!=", "<=", ">=", "<", ">", "||", ":=", "=>", "<=>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^="
    };

    /// <summary>Operators written without spaces.</summary>
    private static readonly HashSet<string> TightOperators = new(StringComparer.Ordinal) { "::" };

    /// <summary>Data types whose size in parentheses follows without a space: VARCHAR(20), not VARCHAR (20).</summary>
    private static readonly HashSet<string> SizedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "VARCHAR", "NVARCHAR", "CHAR", "NCHAR", "CHARACTER", "VARYING", "VARCHAR2", "NVARCHAR2", "DECIMAL", "DEC", "NUMERIC",
        "NUMBER", "FLOAT", "REAL", "DOUBLE", "BINARY", "VARBINARY", "BIT", "VARBIT", "TIME", "TIMESTAMP", "DATETIME",
        "DATETIME2", "DATETIMEOFFSET", "INT", "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "MEDIUMINT", "RAW", "INTERVAL",
        "ENUM", "SET", "TEXT", "BLOB", "MONEY", "STRING", "INT2", "INT4", "INT8", "FLOAT4", "FLOAT8"
    };

    /// <summary>Words after which a name and "(" are a table and its columns, not a function call.</summary>
    private static readonly HashSet<string> NameIntroducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "INTO", "TABLE", "VIEW", "REFERENCES", "ON", "WITH", "RECURSIVE", "UPDATE", "EXISTS"
    };

    /// <summary>Whether "(" at <paramref name="i"/> gets a space before it; null leaves it as written.</summary>
    internal static bool? SpaceBeforeParen(IReadOnlyList<SqlToken> tokens, int i, IReadOnlySet<string> keywords)
    {
        var p = PreviousSignificant(tokens, i);
        if (p < 0) return false;
        var pt = tokens[p];
        switch (pt.Kind)
        {
            case SqlTokenKind.Word when keywords.Contains(pt.Text) && !SqlCompletionEngine.KnownFunctionNames.Contains(pt.Text) &&
                                       !SizedTypes.Contains(pt.Text) && !IsName(tokens, p):
                return true;
            case SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier:
                var start = p;
                while (PreviousSignificant(tokens, start) is var d and >= 0 && tokens[d].Kind == SqlTokenKind.Dot &&
                       PreviousSignificant(tokens, d) is var q and >= 0 && tokens[q].IsIdentifier)
                    start = q;
                var before = PreviousSignificant(tokens, start);
                return before >= 0 && tokens[before].Kind == SqlTokenKind.Word && NameIntroducers.Contains(tokens[before].Text);
            case SqlTokenKind.Number or SqlTokenKind.String:
                return true;
            case SqlTokenKind.OpenParen:
                return false;
            default:
                return null;
        }
    }

    private static bool IsName(IReadOnlyList<SqlToken> tokens, int i)
    {
        var p = PreviousSignificant(tokens, i);
        return p >= 0 && tokens[p].Kind == SqlTokenKind.Dot;
    }

    /// <summary>
    /// Adjacent operator characters as one operator (":=", "=>", "->>"). Returns the operator and the index of its last token.
    /// </summary>
    internal static (string Text, int Last) OperatorAt(IReadOnlyList<SqlToken> tokens, int i, int to)
    {
        var text = tokens[i].Text;
        var last = i;
        // $1 is a parameter, not part of the operator before it.
        while (last + 1 < to && tokens[last + 1].Kind == SqlTokenKind.Operator && tokens[last + 1].Start == tokens[last].End &&
               tokens[last + 1].Text != "$" && tokens[last].Text != "$")
        {
            last++;
            text += tokens[last].Text;
        }
        return (text, last);
    }

    // ----- Queries and DML -----

    internal enum QueryMode
    {
        /// <summary>A query level: the statement itself or a subquery in parentheses. Clauses start lines.</summary>
        Query,

        /// <summary>A multi-line CASE expression: WHEN / ELSE start lines.</summary>
        Case,

        /// <summary>Other parentheses (function arguments, IN lists, column lists): no line breaks, except for subqueries in them.</summary>
        Paren
    }

    private sealed class QueryWriter
    {
        private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "HAVING", "VALUES", "SET", "UNION", "EXCEPT", "INTERSECT", "MINUS", "RETURNING", "LIMIT",
            "OFFSET", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "NATURAL", "INSERT", "UPDATE", "DELETE",
            "WINDOW", "QUALIFY", "INTO", "OUTPUT", "OPTION", "USING", "MERGE", "REPLACE"
        };

        private static readonly HashSet<string> JoinWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "NATURAL", "JOIN", "STRAIGHT_JOIN"
        };

        private static readonly HashSet<string> SelectModifiers = new(StringComparer.OrdinalIgnoreCase)
        {
            "DISTINCT", "ALL", "DISTINCTROW", "SQL_CALC_FOUND_ROWS", "SQL_NO_CACHE", "SQL_CACHE", "HIGH_PRIORITY",
            "STRAIGHT_JOIN", "SQL_SMALL_RESULT", "SQL_BIG_RESULT", "TOP", "PERCENT", "TIES"
        };

        /// <summary>Words that end a SELECT / GROUP BY / ORDER BY / SET list when looking for its commas.</summary>
        private static readonly HashSet<string> ListEnders = new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "EXCEPT", "INTERSECT", "MINUS", "INTO",
            "WINDOW", "RETURNING", "OUTPUT", "OPTION", "FETCH", "FOR", "QUALIFY", "SELECT", "VALUES", "ON", "WHEN", "USING"
        };

        private readonly IReadOnlyList<SqlToken> _t;
        private readonly SqlFormatOptions _o;
        private readonly IReadOnlySet<string> _kw;
        private readonly string _unit;
        private readonly StringBuilder _sb = new();
        private bool _lineStart = true;
        private bool _pendingSpace;
        private int _lineIndent;
        private SqlTokenKind _last = SqlTokenKind.Whitespace;
        private int _commentFix = -1;

        public QueryWriter(IReadOnlyList<SqlToken> tokens, SqlFormatOptions options, IReadOnlySet<string> keywords)
        {
            _t = tokens;
            _o = options;
            _kw = keywords;
            _unit = options.IndentUnit;
        }

        /// <summary>For CREATE VIEW … AS: the token that starts the query, which starts a new line and a new query level.</summary>
        public int RestartAt { get; init; } = -1;

        public override string ToString()
        {
            TrimEnd(_sb);
            return _sb.ToString().TrimEnd('\n');
        }

        private string Indent(int level) => string.Concat(Enumerable.Repeat(_unit, Math.Max(0, level)));

        private void NewLine(int indent)
        {
            TrimEnd(_sb);
            _lineIndent = indent;
            _pendingSpace = false;
            if (_sb.Length == 0) { _lineStart = true; return; }
            if (!_lineStart) _sb.Append('\n');
            _sb.Append(Indent(indent));
            _lineStart = true;
        }

        private void Emit(string text, SqlTokenKind kind)
        {
            if (!_lineStart && (_pendingSpace || ForcedSpace(_last, kind))) _sb.Append(' ');
            _sb.Append(text);
            _lineStart = false;
            _pendingSpace = false;
            _last = kind;
            _commentFix = -1;
        }

        private static bool ForcedSpace(SqlTokenKind last, SqlTokenKind next) =>
            last is SqlTokenKind.CloseParen or SqlTokenKind.String or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Number &&
            next is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Variable or SqlTokenKind.Number;

        /// <summary>Back to the end of the previous line, when the current one is still empty.</summary>
        private void JoinPreviousLine()
        {
            if (!_lineStart || _sb.Length == 0) return;
            TrimEnd(_sb);
            if (_sb.Length > 0 && _sb[^1] == '\n') _sb.Length--;
            TrimEnd(_sb);
            _lineStart = false;
        }

        private void Comma()
        {
            if (_lineStart && _commentFix >= 0)
            {
                // The previous line ends with a line comment: the comma goes before it.
                _sb.Insert(_commentFix, ',');
                _commentFix = -1;
                _last = SqlTokenKind.Comma;
                return;
            }
            JoinPreviousLine();
            TrimEnd(_sb);
            _sb.Append(',');
            _lineStart = false;
            _pendingSpace = false;
            _last = SqlTokenKind.Comma;
        }

        private void Comment(int i, int indent)
        {
            var text = _t[i].Text.TrimEnd();
            var line = IsLineComment(text);
            var ownLine = _sb.Length == 0 || i == 0 || _t[i - 1].Kind == SqlTokenKind.Whitespace && _t[i - 1].Text.Contains('\n');
            var fix = -1;
            if (ownLine)
            {
                NewLine(indent);
                _sb.Append(text);
            }
            else
            {
                JoinPreviousLine();
                TrimEnd(_sb);
                fix = _sb.Length;
                _sb.Append(' ').Append(text);
            }
            _lineStart = false;
            var newLineAfter = line || i + 1 < _t.Count && _t[i + 1].Kind == SqlTokenKind.Whitespace && _t[i + 1].Text.Contains('\n');
            if (newLineAfter)
            {
                NewLine(indent);
                _commentFix = line && !ownLine ? fix : -1;
            }
            else
            {
                _pendingSpace = true;
            }
        }

        private void Operator(ref int i, int to)
        {
            var (op, last) = OperatorAt(_t, i, to);
            var spaceAfter = last + 1 < to && _t[last + 1].Kind == SqlTokenKind.Whitespace;
            i = last;
            if (TightOperators.Contains(op))
            {
                TrimEnd(_sb);
                _sb.Append(op);
                _lineStart = false;
                _pendingSpace = false;
                _last = SqlTokenKind.Dot;
                return;
            }
            if (SpacedOperators.Contains(op))
            {
                _pendingSpace = true;
                Emit(op, SqlTokenKind.Operator);
                _pendingSpace = true;
                return;
            }
            if (op.Length > 1 && op[^1] is '-' or '+' && SpacedOperators.Contains(op[..^1]))
            {
                // "a=-1": the comparison is spaced, the sign stays on its number.
                _pendingSpace = true;
                Emit(op[..^1], SqlTokenKind.Operator);
                _pendingSpace = true;
                Emit(op[^1..], SqlTokenKind.Operator);
                return;
            }
            Emit(op, SqlTokenKind.Operator);
            _pendingSpace = spaceAfter;
        }

        /// <summary>One token with no layout decisions (inside short CASE / subqueries and other parentheses).</summary>
        private void Plain(ref int i, int to)
        {
            var t = _t[i];
            switch (t.Kind)
            {
                case SqlTokenKind.Whitespace:
                    if (_last is not (SqlTokenKind.OpenParen or SqlTokenKind.Dot)) _pendingSpace = true;
                    return;
                case SqlTokenKind.Comment:
                    Comment(i, _lineIndent + 1);
                    return;
                case SqlTokenKind.Comma:
                    Comma();
                    _pendingSpace = true;
                    return;
                case SqlTokenKind.Dot:
                    TrimEnd(_sb);
                    _sb.Append('.');
                    _lineStart = false;
                    _pendingSpace = false;
                    _last = SqlTokenKind.Dot;
                    _commentFix = -1;
                    return;
                case SqlTokenKind.OpenParen:
                    if (SpaceBeforeParen(_t, i, _kw) is { } space) _pendingSpace = space;
                    Emit("(", SqlTokenKind.OpenParen);
                    return;
                case SqlTokenKind.CloseParen:
                    _pendingSpace = false;
                    Emit(")", SqlTokenKind.CloseParen);
                    return;
                case SqlTokenKind.Semicolon:
                    TrimEnd(_sb);
                    _sb.Append(t.Text);
                    _last = SqlTokenKind.Semicolon;
                    _lineStart = false;
                    return;
                case SqlTokenKind.Operator:
                    Operator(ref i, to);
                    return;
                default:
                    Emit(Cased(_t, i, _o, _kw), t.Kind);
                    return;
            }
        }

        private void Inline(int from, int to)
        {
            for (var i = from; i < to; i++) Plain(ref i, to);
        }

        /// <summary>The one-line length of tokens <c>from..to</c>, or null when it holds a comment (never joined).</summary>
        private int? FlatLength(int from, int to)
        {
            var length = 0;
            for (var i = from; i < to; i++)
            {
                var t = _t[i];
                if (t.Kind == SqlTokenKind.Comment || t.Text.Contains('\n') && t.Kind != SqlTokenKind.Whitespace) return null;
                length += t.Kind == SqlTokenKind.Whitespace ? 1 : t.Length;
            }
            return length;
        }

        private bool HasSubquery(int from, int to)
        {
            for (var i = from; i < to; i++)
                if (_t[i].Kind == SqlTokenKind.OpenParen && NextSignificant(_t, i) is var n and >= 0 && n < to && IsQueryStart(_t[n])) return true;
            return false;
        }

        private static bool IsQueryStart(SqlToken t) => t.Is("SELECT") || t.Is("WITH");

        /// <summary>The END that closes the CASE at <paramref name="i"/>, or -1.</summary>
        private int CaseEnd(int i, int to)
        {
            var depth = 0;
            for (var k = i; k < to; k++)
            {
                if (_t[k].Is("CASE")) depth++;
                else if (_t[k].Is("END") && --depth == 0) return k;
            }
            return -1;
        }

        private int CountWhens(int from, int to)
        {
            var nested = 0;
            var count = 0;
            for (var k = from + 1; k < to; k++)
            {
                if (_t[k].Is("CASE")) nested++;
                else if (_t[k].Is("END")) nested--;
                else if (nested == 0 && _t[k].Is("WHEN")) count++;
            }
            return count;
        }

        /// <summary>Whether the list that starts after <paramref name="from"/> has a comma before the clause that ends it.</summary>
        private (bool HasComma, int End) ListExtent(int from, int to)
        {
            var depth = 0;
            var cases = 0;
            for (var k = from + 1; k < to; k++)
            {
                var t = _t[k];
                if (t.Kind == SqlTokenKind.OpenParen) depth++;
                else if (t.Kind == SqlTokenKind.CloseParen) { if (--depth < 0) return (false, k); }
                else if (depth > 0) continue;
                else if (t.Is("CASE")) cases++;
                else if (t.Is("END") && cases > 0) cases--;
                else if (cases > 0) continue;
                else if (t.Kind == SqlTokenKind.Comma) return (true, k);
                else if (t.Kind == SqlTokenKind.Semicolon || t.Kind == SqlTokenKind.Word && ListEnders.Contains(t.Text) && !SelectModifiers.Contains(t.Text) ||
                         k == RestartAt) return (false, k);
            }
            return (false, to);
        }

        /// <summary>A list with one item that fits on the clause's line (SELECT *, ORDER BY name).</summary>
        private bool SingleShortItem(int keyword, int to)
        {
            var (hasComma, end) = ListExtent(keyword, to);
            if (hasComma || !_o.CompactShortExpressions) return false;
            for (var k = keyword + 1; k < end; k++)
                if (_t[k].Is("CASE")) return false;
            return !HasSubquery(keyword + 1, end) && FlatLength(keyword + 1, end) is <= SqlFormatOptions.CompactWidth;
        }

        private bool IsBetweenAnd(int index)
        {
            for (var k = index - 1; k >= 0 && k >= index - 16; k--)
            {
                var t = _t[k];
                if (t.Is("BETWEEN")) return true;
                if (t.Is("AND") || t.Is("OR") || t.Is("WHERE") || t.Is("ON") || t.Is("WHEN") || t.Kind == SqlTokenKind.Semicolon) return false;
            }
            return false;
        }

        public void Walk(int from, int to, QueryMode mode, int indent)
        {
            var list = false;               // inside a list whose items each get a line
            var itemIndent = indent + 1;
            var pendingItem = false;        // the list's first item has not been placed yet
            var topArgument = false;        // TOP's argument follows
            var leadingComma = false;       // a comma waits to start the next item's line
            var inJoin = false;             // ON starts a line only after a join
            var merge = false;
            var shift = 0;                  // MERGE: the action after WHEN … THEN is indented
            var firstWord = true;
            if (mode == QueryMode.Query && from > 0) NewLine(indent);

            void BeforeItem()
            {
                if (pendingItem)
                {
                    pendingItem = false;
                    NewLine(itemIndent);
                }
                if (leadingComma)
                {
                    leadingComma = false;
                    NewLine(itemIndent);
                    Emit(",", SqlTokenKind.Comma);
                    _pendingSpace = true;
                }
            }

            void Clause(int i, string word, bool listClause, int clauseIndent)
            {
                pendingItem = false;
                leadingComma = false;
                NewLine(clauseIndent);
                Emit(word, SqlTokenKind.Word);
                list = false;
                if (!listClause) return;
                itemIndent = clauseIndent + 1;
                if (!_o.OneItemPerLine || SingleShortItem(i, to)) return;
                list = true;
                pendingItem = true;
            }

            for (var i = from; i < to; i++)
            {
                var t = _t[i];
                if (i == RestartAt && mode == QueryMode.Query)
                {
                    // CREATE VIEW v AS ↵ the query.
                    list = pendingItem = leadingComma = inJoin = false;
                    firstWord = true;
                    NewLine(indent);
                }

                if (t.Kind == SqlTokenKind.Whitespace)
                {
                    if (_last is not (SqlTokenKind.OpenParen or SqlTokenKind.Dot)) _pendingSpace = true;
                    continue;
                }
                if (t.Kind == SqlTokenKind.Comment)
                {
                    Comment(i, list ? itemIndent : _lineIndent);
                    continue;
                }

                if (t.Kind == SqlTokenKind.Comma && mode == QueryMode.Query && list)
                {
                    if (_o.Commas == SqlCommaStyle.StartOfLine) leadingComma = true;
                    else
                    {
                        Comma();
                        NewLine(itemIndent);
                    }
                    continue;
                }

                // TOP 10 / TOP (10) / DISTINCT ON (…) stay on the SELECT line.
                if (pendingItem && (topArgument || t.Kind == SqlTokenKind.Word && SelectModifiers.Contains(t.Text) || t.Is("ON") && PreviousSignificant(_t, i) is var pd and >= 0 && _t[pd].Is("DISTINCT")))
                {
                    var wasTop = topArgument;
                    topArgument = t.Is("TOP") || t.Is("ON");
                    if (t.Kind == SqlTokenKind.OpenParen && MatchingParen(_t, i, to) is var close and >= 0)
                    {
                        Inline(i, close + 1);
                        i = close;
                    }
                    else Plain(ref i, to);
                    if (wasTop && !topArgument) topArgument = false;
                    continue;
                }
                topArgument = false;

                BeforeItem();

                switch (t.Kind)
                {
                    case SqlTokenKind.OpenParen:
                    {
                        var close = MatchingParen(_t, i, to);
                        if (close < 0) { Inline(i, to); return; }
                        var inner = NextSignificant(_t, i);
                        var subquery = inner >= 0 && inner < close && IsQueryStart(_t[inner]);
                        // A CTE's body always gets its own lines: it is the query's main part, not a detail.
                        var cteBody = PreviousSignificant(_t, i) is var a and >= 0 && _t[a].Is("AS");
                        if (subquery && (cteBody || !(_o.CompactShortExpressions && !HasSubquery(i + 1, close) && FlatLength(i + 1, close) is <= SqlFormatOptions.CompactWidth)))
                        {
                            if (SpaceBeforeParen(_t, i, _kw) is { } space) _pendingSpace = space;
                            Emit("(", SqlTokenKind.OpenParen);
                            var open = _lineIndent;
                            Walk(i + 1, close, QueryMode.Query, open + 1);
                            NewLine(open);
                            Emit(")", SqlTokenKind.CloseParen);
                        }
                        else if (subquery)
                        {
                            Inline(i, close + 1);
                        }
                        else
                        {
                            if (SpaceBeforeParen(_t, i, _kw) is { } space) _pendingSpace = space;
                            Emit("(", SqlTokenKind.OpenParen);
                            Walk(i + 1, close, QueryMode.Paren, _lineIndent);
                            _pendingSpace = false;
                            Emit(")", SqlTokenKind.CloseParen);
                        }
                        i = close;
                        continue;
                    }
                    case SqlTokenKind.Word when t.Is("CASE"):
                    {
                        var end = CaseEnd(i, to);
                        if (end < 0) break;
                        if (_o.CompactShortExpressions && CountWhens(i, end) <= 1 && !HasSubquery(i, end) && FlatLength(i, end + 1) is <= SqlFormatOptions.CompactWidth)
                        {
                            Inline(i, end + 1);
                        }
                        else
                        {
                            var open = _lineIndent;
                            Emit(Cased(_t, i, _o, _kw), SqlTokenKind.Word);
                            Walk(i + 1, end, QueryMode.Case, open);
                            NewLine(open);
                            Emit(Cased(_t, end, _o, _kw), SqlTokenKind.Word);
                        }
                        i = end;
                        continue;
                    }
                    case SqlTokenKind.Word when mode == QueryMode.Case && (t.Is("WHEN") || t.Is("ELSE")):
                        NewLine(indent + 1);
                        Emit(Cased(_t, i, _o, _kw), SqlTokenKind.Word);
                        continue;
                    case SqlTokenKind.Word when mode == QueryMode.Query:
                        if (QueryWord(i, to, indent, ref list, ref itemIndent, ref pendingItem, ref inJoin, ref merge, ref shift, ref firstWord, Clause))
                            continue;
                        break;
                }

                firstWord = false;
                Plain(ref i, to);
            }
        }

        private delegate void ClauseAction(int i, string word, bool listClause, int clauseIndent);

        /// <summary>Layout of a word at a query level; false when it is written like any other word.</summary>
        private bool QueryWord(int i, int to, int indent, ref bool list, ref int itemIndent, ref bool pendingItem, ref bool inJoin,
                               ref bool merge, ref int shift, ref bool firstWord, ClauseAction clause)
        {
            var t = _t[i];
            var upper = t.Text.ToUpperInvariant();
            var word = Cased(_t, i, _o, _kw);
            var previous = PreviousSignificant(_t, i) is var p and >= 0 ? _t[p] : default;
            var next = NextSignificant(_t, i) is var n and >= 0 ? _t[n] : default;
            var first = firstWord;
            firstWord = false;
            if (IsName(_t, i) || next.Kind == SqlTokenKind.Dot) return false;
            var ci = indent + shift;

            if (first && upper == "MERGE") merge = true;
            if (first && upper == "WITH")
            {
                // WITH a AS (…),↵b AS (…)↵SELECT …: the CTEs are a list at the statement's level.
                clause(i, word, false, ci);
                list = true;
                itemIndent = ci;
                pendingItem = false;
                return true;
            }
            if (merge && upper == "WHEN")
            {
                shift = 0;
                clause(i, word, false, indent);
                return true;
            }
            if (merge && upper == "THEN")
            {
                shift = 1;
                return false;
            }

            if (upper is "BY" && previous.Kind == SqlTokenKind.Word && (previous.Is("GROUP") || previous.Is("ORDER")) )
            {
                Emit(word, SqlTokenKind.Word);
                list = false;
                pendingItem = false;
                itemIndent = _lineIndent + 1;
                if (_o.OneItemPerLine && !SingleShortItem(i, to))
                {
                    list = true;
                    pendingItem = true;
                }
                return true;
            }
            if (upper is "GROUP" or "ORDER" && next.Is("BY"))
            {
                clause(i, word, false, ci);
                return true;
            }

            if (upper is "AND" or "OR")
            {
                // INSERT OR REPLACE / UPDATE OR IGNORE (SQLite) are not conditions.
                if (!_o.BreakAndOr || IsBetweenAnd(i) || previous.Is("INSERT") || previous.Is("UPDATE") || previous.Is("REPLACE")) return false;
                list = false;
                pendingItem = false;
                NewLine(ci + 1);
                Emit(word, SqlTokenKind.Word);
                return true;
            }
            if (upper == "ON")
            {
                if (next.Is("CONFLICT") || next.Is("DUPLICATE"))
                {
                    clause(i, word, false, ci);
                    return true;
                }
                if (!inJoin) return false;
                inJoin = false;
                NewLine(ci + 1);
                Emit(word, SqlTokenKind.Word);
                return true;
            }

            if (!ClauseWords.Contains(upper)) return false;

            // Not a clause here: LEFT(…) the function, IS DISTINCT FROM, DELETE FROM, INSERT INTO, FOR UPDATE, ON DELETE…
            if (upper is "LEFT" or "RIGHT" or "REPLACE" && next.Kind == SqlTokenKind.OpenParen) return false;
            if (upper == "REPLACE" && !first) return false;
            if (upper is "JOIN" or "OUTER" && previous.Kind == SqlTokenKind.Word && JoinWords.Contains(previous.Text) && !previous.Is("JOIN")) return false;
            if (upper == "FROM" && (previous.Is("DELETE") || previous.Is("DISTINCT"))) return false;
            if (upper == "INTO" && (previous.Is("INSERT") || previous.Is("REPLACE") || previous.Is("MERGE") || previous.Is("IGNORE") || previous.Is("OR"))) return false;
            if (upper is "UPDATE" or "DELETE" or "INSERT" && (previous.Is("FOR") || previous.Is("DO") || previous.Is("ON") || previous.Is("KEY") || previous.Is("THEN") && !merge || previous.Is("INSTEAD") || previous.Is("OF") || previous.Is("OR"))) return false;
            if (upper is "SET" && previous.Is("DO")) { clause(i, word, true, ci); return true; }
            if (upper == "USING" && !merge && next.Kind == SqlTokenKind.OpenParen) return false;
            if (upper is "OPTION" && next.Kind != SqlTokenKind.OpenParen) return false;
            if (upper is "OUTPUT" && !(next.IsIdentifier)) return false;
            if (merge && upper is "UPDATE" or "INSERT" or "DELETE" && previous.Is("THEN"))
            {
                clause(i, word, false, indent + 1);
                return true;
            }

            inJoin = JoinWords.Contains(upper) || upper == "USING" && merge;
            var listClause = upper is "SELECT" or "SET";
            if (upper == "VALUES")
            {
                var (rows, _) = ListExtent(i, to);
                listClause = rows;
            }
            clause(i, word, listClause, ci);
            return true;
        }
    }

    // ----- CREATE TABLE -----

    private static class CreateTableWriter
    {
        private static readonly HashSet<string> ConstraintStarts = new(StringComparer.OrdinalIgnoreCase)
        {
            "CONSTRAINT", "PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "INDEX", "KEY", "EXCLUDE", "FULLTEXT", "SPATIAL", "PERIOD", "LIKE"
        };

        /// <summary>CREATE TABLE name (↵ one column per line ↵) options; null when it holds comments or no column list.</summary>
        public static string? TryFormat(IReadOnlyList<SqlToken> tokens, SqlFormatOptions options, IReadOnlySet<string> keywords)
        {
            var table = -1;
            for (var i = 0; i < tokens.Count; i++) if (tokens[i].Is("TABLE")) { table = i; break; }
            if (table < 0) return null;
            var open = -1;
            for (var i = table + 1; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == SqlTokenKind.OpenParen) { open = i; break; }
                if (tokens[i].Kind == SqlTokenKind.Semicolon) return null;
            }
            if (open < 0) return null;
            var close = MatchingParen(tokens, open, tokens.Count);
            if (close < 0) return null;
            if (tokens.Any(t => t.Kind == SqlTokenKind.Comment)) return null;

            // The items between the parentheses, split at top-level commas.
            var items = new List<(int From, int To)>();
            var depth = 0;
            var itemStart = open + 1;
            for (var i = open + 1; i < close; i++)
            {
                if (tokens[i].Kind == SqlTokenKind.OpenParen) depth++;
                else if (tokens[i].Kind == SqlTokenKind.CloseParen) depth--;
                else if (depth == 0 && tokens[i].Kind == SqlTokenKind.Comma)
                {
                    items.Add((itemStart, i));
                    itemStart = i + 1;
                }
            }
            items.Add((itemStart, close));
            if (items.Any(it => NextSignificant(tokens, it.From - 1) is var f && (f < 0 || f >= it.To))) return null;

            string Flat(int from, int to)
            {
                var sb = new StringBuilder();
                var space = false;
                for (var i = from; i < to; i++)
                {
                    var t = tokens[i];
                    if (t.Kind == SqlTokenKind.Whitespace) { space = true; continue; }
                    var prev = sb.Length > 0 ? sb[^1] : ' ';
                    var piece = t.Kind == SqlTokenKind.Word ? Cased(tokens, i, options, keywords) : t.Text;
                    var noSpace = sb.Length == 0 || prev == '(' || prev == '.' ||
                                  t.Kind is SqlTokenKind.CloseParen or SqlTokenKind.Comma or SqlTokenKind.Dot or SqlTokenKind.Semicolon;
                    if (t.Kind == SqlTokenKind.OpenParen && SpaceBeforeParen(tokens, i, keywords) is { } s) { space = s; noSpace = !s; }
                    if (t.Kind == SqlTokenKind.Operator && TightOperators.Contains(t.Text)) noSpace = true;
                    if (prev == ':' && sb.Length > 1 && sb[^2] == ':') noSpace = true;
                    if (t.Kind == SqlTokenKind.Comma) space = false;
                    if (space && !noSpace) sb.Append(' ');
                    else if (!noSpace && t.Kind != SqlTokenKind.OpenParen && prev == ',') sb.Append(' ');
                    sb.Append(piece);
                    space = t.Kind == SqlTokenKind.Comma;
                }
                return sb.ToString();
            }

            var unit = options.IndentUnit;
            var rows = items.Select(it =>
            {
                var first = NextSignificant(tokens, it.From - 1);
                var isColumn = tokens[first].IsIdentifier && !ConstraintStarts.Contains(tokens[first].Text);
                var name = isColumn ? Flat(first, first + 1) : null;
                var rest = isColumn ? Flat(first + 1, it.To) : Flat(first, it.To);
                return (Name: name, Body: rest);
            }).ToList();
            var width = rows.Where(r => r.Name is not null).Select(r => r.Name!.Length).DefaultIfEmpty(0).Max();
            width = Math.Min(width, 30);

            var result = new StringBuilder(Flat(0, open).TrimEnd());
            result.Append(" (");
            for (var r = 0; r < rows.Count; r++)
            {
                var (name, rest) = rows[r];
                var body = name is null ? rest : rest.Length == 0 ? name : name.PadRight(Math.Max(width, name.Length)) + " " + rest;
                result.Append('\n').Append(unit);
                if (options.Commas == SqlCommaStyle.StartOfLine && r > 0) result.Append(", ");
                result.Append(body);
                if (options.Commas == SqlCommaStyle.EndOfLine && r < rows.Count - 1) result.Append(',');
            }
            result.Append("\n)");
            var tail = Flat(close + 1, tokens.Count);
            if (tail.Length > 0) result.Append(tail[0] is ';' or ',' ? "" : " ").Append(tail);
            return result.ToString();
        }
    }

    // ----- Procedural code -----

    /// <summary>
    /// Re-indents procedural code by its blocks while keeping the author's line breaks (a few are added so blocks
    /// written on one line open up), and lays out ';'-terminated queries inside blocks like any other query.
    /// </summary>
    private sealed class BlockWriter
    {
        private enum Frame { Begin, Declare, If, Loop, CaseStatement, CaseExpression, Exception, Repeat }

        private static readonly HashSet<string> QueryWords = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "INSERT", "UPDATE", "DELETE", "WITH", "MERGE" };

        /// <summary>Words after which an IF has no THEN (T-SQL): the scan for THEN stops there.</summary>
        private static readonly HashSet<string> IfScanStops = new(StringComparer.OrdinalIgnoreCase)
        {
            "BEGIN", "END", "CASE", "SELECT", "INSERT", "UPDATE", "DELETE", "SET", "EXEC", "EXECUTE", "PRINT", "RETURN",
            "RAISERROR", "THROW", "DECLARE", "GOTO", "BREAK", "CONTINUE", "WHILE"
        };

        private static readonly HashSet<string> EndSuffixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "IF", "LOOP", "CASE", "WHILE", "REPEAT", "TRY", "CATCH"
        };

        private readonly string _source;
        private readonly IReadOnlyList<SqlToken> _t;
        private readonly SqlFormatOptions _o;
        private readonly IReadOnlySet<string> _kw;
        private readonly string _unit;
        private readonly StringBuilder _sb = new();
        private readonly List<Frame> _frames = [];
        private readonly string? _language;
        private readonly bool _routine;

        private bool _lineStart = true;
        private bool _space;
        private bool _statementStart = true;
        private int _anchor;               // original indentation of the current statement's first line
        private int _lineOriginalIndent;   // of the line being written; -1 for a line break this writer added
        private int _lineDepth;
        private bool _pendingIf;
        private bool _pendingDo;
        private int _skipFrameWord = -1;   // the IF of END IF, the LOOP of END LOOP…
        private int _lastEnd = -1;         // the last END that closed a block (not a CASE expression)

        private readonly bool _plpgsqlBody;

        public BlockWriter(string source, IReadOnlyList<SqlToken> tokens, SqlFormatOptions options, IReadOnlySet<string> keywords, bool plpgsqlBody = false)
        {
            _plpgsqlBody = plpgsqlBody;
            _source = source;
            _t = tokens;
            _o = options;
            _kw = keywords;
            _unit = options.IndentUnit;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Is("LANGUAGE") && NextSignificant(tokens, i) is var l and >= 0) _language = tokens[l].Identifier.Trim('\'');
                if (tokens[i].Is("FUNCTION") || tokens[i].Is("PROCEDURE") || tokens[i].Is("DO") && NextSignificant(tokens, i) is var b and >= 0 && tokens[b].Kind == SqlTokenKind.String && tokens[b].Text.StartsWith('$'))
                    _routine = true;
            }
            _lineOriginalIndent = OriginalIndent(0);
            _anchor = _lineOriginalIndent;
        }

        private int Depth => _frames.Sum(f => f is Frame.CaseStatement or Frame.Exception ? 2 : 1);
        private Frame? Top => _frames.Count == 0 ? null : _frames[^1];

        private int OriginalIndent(int position)
        {
            var lineStart = position == 0 ? 0 : _source.LastIndexOf('\n', position - 1) + 1;
            var width = 0;
            for (var k = lineStart; k < _source.Length && _source[k] is ' ' or '\t'; k++) width += _source[k] == '\t' ? 4 : 1;
            return width;
        }

        public string Write()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                var t = _t[i];
                if (t.Kind == SqlTokenKind.Whitespace)
                {
                    var newLines = t.Text.Count(c => c == '\n');
                    if (newLines == 0) { _space = true; continue; }
                    EndLine(newLines >= 2);
                    _lineOriginalIndent = OriginalIndent(t.End);
                    continue;
                }

                if (!_lineStart && BreakBefore(i)) NewLineHere();

                if (_lineStart && _statementStart && t.Kind == SqlTokenKind.Word && QueryWords.Contains(t.Text) && QueryEnd(i) is var end and >= 0)
                {
                    StartLine(i);
                    WriteQuery(i, end);
                    i = end;
                    _statementStart = true;
                    if (HasMoreOnLine(i)) NewLineHere();
                    continue;
                }

                if (_lineStart) StartLine(i);
                WriteToken(i);
                Track(i);
                if (BreakAfter(i) && HasMoreOnLine(i)) NewLineHere();
            }
            TrimEnd(_sb);
            return _sb.ToString().TrimEnd('\n');
        }

        private void EndLine(bool blank)
        {
            TrimEnd(_sb);
            if (_sb.Length == 0) return;
            if (!_lineStart) _sb.Append('\n');
            if (blank && !(_sb.Length >= 2 && _sb[^1] == '\n' && _sb[^2] == '\n')) _sb.Append('\n');
            _lineStart = true;
            _space = false;
        }

        private void NewLineHere()
        {
            EndLine(false);
            _lineOriginalIndent = -1;
        }

        private bool HasMoreOnLine(int i)
        {
            for (var k = i + 1; k < _t.Count; k++)
            {
                if (_t[k].Kind == SqlTokenKind.Whitespace) { if (_t[k].Text.Contains('\n')) return false; continue; }
                return _t[k].Kind != SqlTokenKind.Comment && !(_t[k].Kind == SqlTokenKind.Semicolon);
            }
            return false;
        }

        /// <summary>Indentation of a line starting at token <paramref name="i"/>.</summary>
        private void StartLine(int i)
        {
            var t = _t[i];
            var depth = Depth;
            var top = Top;
            var outdent =
                t.Is("END") && top is not null && _frames.Count > 0 ? (top is Frame.CaseStatement or Frame.Exception ? 2 : 1) :
                (t.Is("ELSE") || t.Is("ELSIF") || t.Is("ELSEIF")) && top is Frame.If ? 1 :
                (t.Is("WHEN") || t.Is("ELSE")) && top is Frame.CaseStatement or Frame.Exception ? 1 :
                t.Is("EXCEPTION") && top is Frame.Begin ? 1 :
                t.Is("UNTIL") && top is Frame.Repeat ? 1 :
                t.Is("BEGIN") && top is Frame.Declare ? 1 : 0;
            depth = Math.Max(0, depth - outdent);

            var statementStart = _statementStart || outdent > 0 || _lineOriginalIndent < 0 || _lineOriginalIndent <= _anchor;
            var extra = 0;
            if (statementStart)
            {
                _anchor = Math.Max(0, _lineOriginalIndent);
                _statementStart = false;
            }
            else
            {
                // A continuation line is indented at least one level under its statement's first line.
                extra = Math.Max(_lineOriginalIndent - _anchor, _o.UseTabs ? 4 : _unit.Length);
            }

            _lineDepth = depth;
            _sb.Append(string.Concat(Enumerable.Repeat(_unit, depth)));
            if (extra > 0) _sb.Append(_o.UseTabs ? string.Concat(Enumerable.Repeat("\t", Math.Max(1, extra / 4))) : new string(' ', extra));
            _lineStart = false;
            _space = false;
        }

        private void WriteToken(int i)
        {
            var t = _t[i];
            var text = t.Kind == SqlTokenKind.Comment ? t.Text.TrimEnd() : Cased(_t, i, _o, _kw);
            if (t.Kind == SqlTokenKind.String && _routine && t.Text.StartsWith('$') && FormatBody(t.Text) is { } body) text = body;
            if (_space && _sb.Length > 0 && _sb[^1] is not ('\n' or ' ' or '\t')) _sb.Append(' ');
            _sb.Append(text);
            _space = false;
        }

        /// <summary>A $tag$ routine body laid out at the depth of the line it is on (PL/pgSQL and SQL only).</summary>
        private string? FormatBody(string text)
        {
            var tagEnd = text.IndexOf('$', 1);
            if (tagEnd < 0) return null;
            var tag = text[..(tagEnd + 1)];
            if (text.Length < tag.Length * 2 || !text.EndsWith(tag, StringComparison.Ordinal)) return null;
            var inner = text[tag.Length..^tag.Length];
            var language = _language?.ToLowerInvariant();
            if (language is not (null or "plpgsql" or "sql")) return null;
            var block = SqlLexer.Tokenize(inner).Any(t => t.Is("BEGIN") || t.Is("DECLARE"));
            if (!block && !inner.Contains('\n') && inner.Trim().Length <= SqlFormatOptions.CompactWidth) return null;

            var formatted = language == "sql" ? Format(inner.Trim(), _o, _kw) : new BlockWriter(inner.Trim(), SqlLexer.Tokenize(inner.Trim()), _o, _kw, plpgsqlBody: true).Write();
            if (formatted.Trim().Length == 0) return null;
            var indent = string.Concat(Enumerable.Repeat(_unit, _lineDepth));
            return tag + "\n" + IndentLines(formatted.TrimEnd('\n'), indent) + "\n" + indent + tag;
        }

        /// <summary>The ';' that ends a query starting at <paramref name="i"/> inside procedural code, or -1.</summary>
        private int QueryEnd(int i)
        {
            var depth = 0;
            for (var k = i + 1; k < _t.Count; k++)
            {
                var t = _t[k];
                if (t.Kind == SqlTokenKind.OpenParen) depth++;
                else if (t.Kind == SqlTokenKind.CloseParen) depth--;
                else if (t.Kind == SqlTokenKind.Semicolon) return depth == 0 && t.Text == ";" ? k : -1;
                else if (depth == 0 && (t.Is("BEGIN") || t.Is("END") || t.Is("THEN") || t.Is("LOOP") || t.Is("ELSE"))) return -1;
                else if (t.Kind == SqlTokenKind.String && t.Text.StartsWith('$')) return -1;
            }
            return -1;
        }

        private void WriteQuery(int from, int to)
        {
            var text = _source[_t[from].Start.._t[to].End];
            var formatted = FormatStatement(text, _o, _kw);
            var indent = _sb.ToString(_sb.Length - LineLength(), LineLength());
            _sb.Append(IndentLines(formatted, indent, firstLine: false));
            _lineStart = false;
        }

        private int LineLength()
        {
            var k = _sb.Length;
            while (k > 0 && _sb[k - 1] != '\n') k--;
            return _sb.Length - k;
        }

        private bool StatementStartAt(int i)
        {
            var p = PreviousSignificant(_t, i);
            if (p < 0) return true;
            var pt = _t[p];
            return pt.Kind == SqlTokenKind.Semicolon || pt.Is("BEGIN") || pt.Is("THEN") || pt.Is("ELSE") || pt.Is("LOOP") ||
                   pt.Is("DO") || pt.Is("AS") || pt.Is("DECLARE") || pt.Is("EXCEPTION") || pt.Is("REPEAT");
        }

        private bool IsBlockBegin(int i) => _t[i].Is("BEGIN") && !IsBeginTransaction(_t, i) && !(NextSignificant(_t, i) is var n and >= 0 && _t[n].Is("DISTRIBUTED"));

        private bool BreakBefore(int i)
        {
            var t = _t[i];
            var top = Top;
            if (i == _skipFrameWord) return false;
            if (t.Is("END") && top is not null and not Frame.CaseExpression) return true;
            if ((t.Is("ELSE") || t.Is("ELSIF") || t.Is("ELSEIF")) && top is Frame.If) return true;
            if ((t.Is("WHEN") || t.Is("ELSE")) && top is Frame.CaseStatement or Frame.Exception) return true;
            if (t.Is("EXCEPTION") && top is Frame.Begin) return true;
            if (IsBlockBegin(i) && PreviousSignificant(_t, i) is var p and >= 0 && (_t[p].Is("AS") || _t[p].Kind == SqlTokenKind.Semicolon)) return true;
            return false;
        }

        private bool BreakAfter(int i)
        {
            var t = _t[i];
            if (t.Kind == SqlTokenKind.Semicolon) return true;
            if (i == _skipFrameWord) return true;
            if (t.Is("END") && _lastEnd == i && !(NextSignificant(_t, i) is var after and >= 0 && (after == _skipFrameWord || _t[after].Kind == SqlTokenKind.Semicolon)))
                return true;
            if (_frames.Count == 0) return false;
            var top = Top;
            if (IsBlockBegin(i)) return !(NextSignificant(_t, i) is var n and >= 0 && (_t[n].Is("TRY") || _t[n].Is("CATCH")));
            if ((t.Is("TRY") || t.Is("CATCH")) && PreviousSignificant(_t, i) is var b and >= 0 && _t[b].Is("BEGIN")) return true;
            if (t.Is("THEN") && top is Frame.If or Frame.CaseStatement or Frame.Exception) return true;
            if (t.Is("ELSE") && top is Frame.If or Frame.CaseStatement) return !(NextSignificant(_t, i) is var e and >= 0 && _t[e].Is("IF"));
            if (t.Is("LOOP") && top is Frame.Loop) return true;
            if (t.Is("DO") && top is Frame.Loop) return true;
            if (t.Is("REPEAT") && top is Frame.Repeat) return true;
            if (t.Is("DECLARE") && top is Frame.Declare) return true;
            if (t.Is("EXCEPTION") && top is Frame.Exception) return true;
            return false;
        }

        /// <summary>Block structure after writing token <paramref name="i"/>.</summary>
        private void Track(int i)
        {
            var t = _t[i];
            if (t.Kind == SqlTokenKind.Semicolon) { _statementStart = true; _pendingIf = _pendingDo = false; return; }
            if (t.Kind != SqlTokenKind.Word || i == _skipFrameWord) return;
            var atStart = StatementStartAt(i);

            if (IsBlockBegin(i))
            {
                if (Top is Frame.Declare) _frames.RemoveAt(_frames.Count - 1);
                _frames.Add(Frame.Begin);
                _statementStart = true;
            }
            else if (t.Is("END"))
            {
                if (Top is not Frame.CaseExpression and not null) _lastEnd = i;
                if (Top is Frame.CaseExpression)
                {
                    _frames.RemoveAt(_frames.Count - 1);
                    return;
                }
                if (_frames.Count > 0) _frames.RemoveAt(_frames.Count - 1);
                _statementStart = true;
                if (NextSignificant(_t, i) is var n and >= 0 && _t[n].Kind == SqlTokenKind.Word && EndSuffixes.Contains(_t[n].Text) &&
                    (_t[n].Is("TRY") || _t[n].Is("CATCH") || NextSignificant(_t, n) is var x && (x < 0 || _t[x].Kind == SqlTokenKind.Semicolon || _t[n].Is("LOOP") && _t[x].Kind == SqlTokenKind.Word)))
                    _skipFrameWord = n;
            }
            else if (t.Is("CASE"))
            {
                _frames.Add(atStart ? Frame.CaseStatement : Frame.CaseExpression);
            }
            else if (t.Is("IF") && atStart && HasThen(i))
            {
                _pendingIf = true;
            }
            else if (t.Is("THEN") && _pendingIf)
            {
                _pendingIf = false;
                _frames.Add(Frame.If);
                _statementStart = true;
            }
            else if (t.Is("THEN") || t.Is("ELSE") && Top is Frame.If or Frame.CaseStatement)
            {
                if (Top is Frame.If or Frame.CaseStatement or Frame.Exception) _statementStart = true;
            }
            else if ((t.Is("WHILE") || t.Is("FOR")) && atStart)
            {
                _pendingDo = true;
            }
            else if (t.Is("LOOP"))
            {
                _pendingDo = false;
                _frames.Add(Frame.Loop);
                _statementStart = true;
            }
            else if (t.Is("DO") && _pendingDo && !(NextSignificant(_t, i) is var s and >= 0 && _t[s].Kind == SqlTokenKind.String))
            {
                _pendingDo = false;
                _frames.Add(Frame.Loop);
                _statementStart = true;
            }
            else if (t.Is("REPEAT") && atStart)
            {
                _frames.Add(Frame.Repeat);
                _statementStart = true;
            }
            // PL/pgSQL's DECLARE section (to BEGIN); in T-SQL and MySQL DECLARE is one statement.
            else if (t.Is("DECLARE") && (FollowedByLineBreak(i) || _plpgsqlBody && PreviousSignificant(_t, i) < 0) && Top is not Frame.Declare)
            {
                _frames.Add(Frame.Declare);
                _statementStart = true;
            }
            else if (t.Is("EXCEPTION") && Top is Frame.Begin && atStart)
            {
                _frames[^1] = Frame.Exception;
                _statementStart = true;
            }
            else if (t.Is("AS") && FollowedByLineBreak(i))
            {
                _statementStart = true;
            }
        }

        private bool FollowedByLineBreak(int i)
        {
            for (var k = i + 1; k < _t.Count; k++)
            {
                if (_t[k].Kind == SqlTokenKind.Whitespace) { if (_t[k].Text.Contains('\n')) return true; continue; }
                return _t[k].Kind == SqlTokenKind.Comment;
            }
            return true;
        }

        private bool HasThen(int i)
        {
            var depth = 0;
            for (var k = i + 1; k < _t.Count; k++)
            {
                var t = _t[k];
                if (t.Kind == SqlTokenKind.OpenParen) depth++;
                else if (t.Kind == SqlTokenKind.CloseParen) depth--;
                else if (depth > 0) continue;
                else if (t.Kind == SqlTokenKind.Semicolon) return false;
                else if (t.Is("THEN")) return true;
                else if (t.Kind == SqlTokenKind.Word && IfScanStops.Contains(t.Text)) return false;
            }
            return false;
        }
    }
}
