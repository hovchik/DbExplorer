using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

public enum CompletionKind
{
    Column,
    Alias,
    Keyword,
    Table,
    View,
    Routine,
    Schema,
    Function,
    Join,
    Snippet,
    Variable,
    Other
}

/// <param name="CaretOffset">Where the caret goes inside <paramref name="InsertText"/> after inserting (e.g. between
/// the parentheses of a function); null puts it at the end.</param>
public sealed record CompletionItem(string Label, string InsertText, CompletionKind Kind, string? Detail = null, int? CaretOffset = null)
{
    public string KindLabel => Kind.ToString().ToLowerInvariant();
}

/// <summary>Items to show plus the text range [ReplaceStart, caret) that picking one replaces.</summary>
public sealed record CompletionResult(int ReplaceStart, IReadOnlyList<CompletionItem> Items)
{
    public static readonly CompletionResult Empty = new(0, []);
}

/// <summary>Where the caret is in the statement, which decides what is worth suggesting.</summary>
public enum SqlContext
{
    StatementStart,
    TableName,
    AfterTableSource,
    JoinCondition,
    Expression,
    InsertColumns,
    RoutineName,
    AliasName
}

/// <summary>
/// Context-aware completion over a metadata snapshot, like a SQL IDE:
/// <list type="bullet">
/// <item>after FROM/JOIN/UPDATE/INTO only tables, views, table functions, schemas and CTEs;</item>
/// <item>after JOIN, tables related by a foreign key come first, with a ready-made <c>ON</c> condition;</item>
/// <item>after ON, whole join conditions from foreign keys;</item>
/// <item>in SELECT/WHERE/GROUP BY/ORDER BY/SET, columns of the tables in the statement (qualified when ambiguous),
/// aliases, built-in functions with their signature, @variables and clause keywords;</item>
/// <item><c>alias.</c>, <c>schema.</c> and <c>table.</c> qualifiers, INSERT column lists, EXEC routines,
/// <c>*</c> expansion (Ctrl+Space after <c>*</c>) and statement snippets.</item>
/// </list>
/// Matching accepts prefixes, word starts (<c>Id</c> → CustomerId) and acronyms (<c>ol</c> → OrderLines).
/// </summary>
public sealed class SqlCompletionEngine
{
    public static readonly IReadOnlyList<string> Keywords =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE", "IS", "NULL",
        "ORDER BY", "GROUP BY", "HAVING", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN",
        "CROSS JOIN", "CROSS APPLY", "OUTER APPLY", "ON", "AS", "DISTINCT", "TOP", "LIMIT", "OFFSET",
        "UNION", "UNION ALL", "EXCEPT", "INTERSECT", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM",
        "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE INDEX", "CREATE PROCEDURE", "CREATE FUNCTION",
        "CREATE VIEW", "BEGIN", "END", "IF", "ELSE", "DECLARE", "CASE", "WHEN", "THEN", "OVER", "PARTITION BY",
        "WITH", "NOLOCK", "ASC", "DESC", "RETURNING", "ILIKE", "TRUNCATE TABLE", "EXEC", "FETCH NEXT", "ROWS ONLY",
        "IS NULL", "IS NOT NULL", "NOT IN", "NOT EXISTS", "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "DEFAULT",
        "COMMIT", "ROLLBACK", "BEGIN TRANSACTION", "PRINT", "MERGE", "OUTPUT", "ALL", "ANY", "TRUE", "FALSE"
    ];

    private sealed record FunctionInfo(string Name, string Signature, string? Provider = null);

    private static readonly IReadOnlyList<FunctionInfo> Functions =
    [
        new("COUNT", "COUNT(expression | *)"), new("SUM", "SUM(expression)"), new("AVG", "AVG(expression)"),
        new("MIN", "MIN(expression)"), new("MAX", "MAX(expression)"), new("COALESCE", "COALESCE(value, value, …)"),
        new("NULLIF", "NULLIF(a, b)"), new("CAST", "CAST(expression AS type)"), new("ABS", "ABS(number)"),
        new("ROUND", "ROUND(number, digits)"), new("FLOOR", "FLOOR(number)"), new("CEILING", "CEILING(number)"),
        new("UPPER", "UPPER(text)"), new("LOWER", "LOWER(text)"), new("SUBSTRING", "SUBSTRING(text, start, length)"),
        new("TRIM", "TRIM(text)"), new("LTRIM", "LTRIM(text)"), new("RTRIM", "RTRIM(text)"), new("REPLACE", "REPLACE(text, find, with)"),
        new("CONCAT", "CONCAT(value, value, …)"), new("STRING_AGG", "STRING_AGG(expression, separator)"),
        new("ROW_NUMBER", "ROW_NUMBER() OVER (PARTITION BY … ORDER BY …)"), new("RANK", "RANK() OVER (…)"),
        new("DENSE_RANK", "DENSE_RANK() OVER (…)"), new("NTILE", "NTILE(n) OVER (…)"), new("LAG", "LAG(expression, offset, default) OVER (…)"),
        new("LEAD", "LEAD(expression, offset, default) OVER (…)"), new("FIRST_VALUE", "FIRST_VALUE(expression) OVER (…)"),
        new("LAST_VALUE", "LAST_VALUE(expression) OVER (…)"), new("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP"),
        // SQL Server
        new("GETDATE", "GETDATE() → datetime", "SqlServer"), new("SYSDATETIME", "SYSDATETIME() → datetime2", "SqlServer"),
        new("GETUTCDATE", "GETUTCDATE()", "SqlServer"), new("DATEADD", "DATEADD(part, number, date)", "SqlServer"),
        new("DATEDIFF", "DATEDIFF(part, start, end)", "SqlServer"), new("DATEPART", "DATEPART(part, date)", "SqlServer"),
        new("DATENAME", "DATENAME(part, date)", "SqlServer"), new("EOMONTH", "EOMONTH(date, months)", "SqlServer"),
        new("FORMAT", "FORMAT(value, format, culture)", "SqlServer"), new("ISNULL", "ISNULL(value, replacement)", "SqlServer"),
        new("CONVERT", "CONVERT(type, expression, style)", "SqlServer"), new("TRY_CAST", "TRY_CAST(expression AS type)", "SqlServer"),
        new("TRY_CONVERT", "TRY_CONVERT(type, expression, style)", "SqlServer"), new("LEN", "LEN(text)", "SqlServer"),
        new("CHARINDEX", "CHARINDEX(find, text, start)", "SqlServer"), new("LEFT", "LEFT(text, count)", "SqlServer"),
        new("RIGHT", "RIGHT(text, count)", "SqlServer"), new("IIF", "IIF(condition, whenTrue, whenFalse)", "SqlServer"),
        new("NEWID", "NEWID() → uniqueidentifier", "SqlServer"), new("SCOPE_IDENTITY", "SCOPE_IDENTITY()", "SqlServer"),
        new("OBJECT_ID", "OBJECT_ID('schema.name')", "SqlServer"), new("STUFF", "STUFF(text, start, length, insert)", "SqlServer"),
        new("JSON_VALUE", "JSON_VALUE(json, '$.path')", "SqlServer"), new("OPENJSON", "OPENJSON(json)", "SqlServer"),
        // PostgreSQL
        new("NOW", "now() → timestamptz", "Postgres"), new("DATE_TRUNC", "date_trunc('part', timestamp)", "Postgres"),
        new("EXTRACT", "EXTRACT(field FROM source)", "Postgres"), new("AGE", "age(end, start)", "Postgres"),
        new("TO_CHAR", "to_char(value, 'format')", "Postgres"), new("TO_DATE", "to_date(text, 'format')", "Postgres"),
        new("TO_TIMESTAMP", "to_timestamp(text, 'format')", "Postgres"), new("LENGTH", "length(text)", "Postgres"),
        new("POSITION", "position(find IN text)", "Postgres"), new("SPLIT_PART", "split_part(text, delimiter, n)", "Postgres"),
        new("REGEXP_REPLACE", "regexp_replace(text, pattern, replacement, flags)", "Postgres"),
        new("ARRAY_AGG", "array_agg(expression)", "Postgres"), new("UNNEST", "unnest(array)", "Postgres"),
        new("JSON_AGG", "json_agg(expression)", "Postgres"), new("JSONB_BUILD_OBJECT", "jsonb_build_object(key, value, …)", "Postgres"),
        new("GENERATE_SERIES", "generate_series(start, stop, step)", "Postgres"), new("GREATEST", "greatest(value, …)", "Postgres"),
        new("LEAST", "least(value, …)", "Postgres"), new("GEN_RANDOM_UUID", "gen_random_uuid() → uuid", "Postgres")
    ];

    public static readonly IReadOnlySet<string> KnownFunctionNames =
        Functions.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every keyword word (multi-word keywords split) plus function names, for upper-casing when formatting.</summary>
    public static readonly IReadOnlySet<string> KeywordSet = Keywords
        .SelectMany(k => k.Split(' '))
        .Concat(["BY", "INTO", "TABLE", "PROCEDURE", "FUNCTION", "VIEW", "INDEX", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS",
                 "APPLY", "TRAN", "TRANSACTION", "EXECUTE", "RETURN", "RETURNS", "WHILE", "USING", "NATURAL", "NEXT", "ROWS", "ONLY",
                 "UNIQUE", "CONSTRAINT", "CHECK", "ADD", "COLUMN", "DROP", "ALTER", "CREATE", "DELETE", "INSERT", "INTERVAL", "LATERAL"])
        .Concat(Functions.Select(f => f.Name))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "GROUP", "ORDER", "HAVING",
        "UNION", "EXCEPT", "INTERSECT", "WITH", "SET", "VALUES", "SELECT", "LIMIT", "OFFSET", "AS", "APPLY",
        "NATURAL", "USING", "WINDOW", "FOR", "OPTION", "PIVOT", "UNPIVOT", "TABLESAMPLE", "WHEN", "THEN", "OUTPUT",
        "RETURNING", "DEFAULT", "FETCH"
    };

    private static readonly HashSet<string> TableSourceKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "FROM", "JOIN", "UPDATE", "INTO", "APPLY", "TABLE", "USING", "MERGE" };

    private static readonly HashSet<string> ExpressionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WHERE", "HAVING", "ON", "AND", "OR", "NOT", "WHEN", "THEN", "ELSE", "CASE", "BY", "SET", "RETURNING",
        "DISTINCT", "TOP", "VALUES", "OUTPUT", "IN", "LIKE", "ILIKE", "BETWEEN", "IS", "EXISTS", "PRINT", "RETURN", "IF", "WHILE"
    };

    private const string Ident = @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)";


    /// <summary>WITH name [(columns)] AS ( … ) and , name AS ( … ).</summary>
    private static readonly Regex CteDefinition = new(
        $@"(?:\bWITH\b(?:\s+RECURSIVE\b)?|\)\s*,)\s*(?<name>{Ident})\s*(?:\((?<cols>[^)]*)\))?\s+AS\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Words that must be quoted when used as identifiers (keywords only, not function names).</summary>
    private static readonly IReadOnlySet<string> ReservedWords = Keywords.SelectMany(k => k.Split(' '))
        .Concat(["BY", "INTO", "TABLE", "INNER", "OUTER", "CROSS", "USER", "KEY", "CHECK", "COLUMN", "CONSTRAINT", "UNIQUE"])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A (db.)(schema.)table reference after FROM/JOIN/UPDATE/INTO or a comma, with an optional alias.
    /// The alias may not be a keyword: otherwise "SELECT a.x, a.y FROM t a" would read FROM as the alias of "a.y"
    /// and swallow the real FROM clause.</summary>
    private static readonly Regex TableReference = new(
        $@"(?:\b(?:FROM|JOIN|UPDATE|INTO|APPLY)\b|,)\s*(?<name>{Ident}(?:\s*\.\s*{Ident}){{0,2}})" +
        $@"(?:\s+(?:AS\s+)?(?!(?:{string.Join("|", NotAnAlias.Concat(ReservedWords).Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape))})\b)(?<alias>{Ident}))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex SimpleIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly Func<string, string> _quote;
    private readonly string? _providerKey;
    private readonly IReadOnlyList<DbObject> _objects;
    private readonly ILookup<string, DbObject> _objectsByName;
    private readonly ILookup<string, DbObject> _objectsBySchema;
    private readonly IReadOnlyList<string> _schemas;
    private readonly IReadOnlyList<string> _allColumnNames;
    private readonly IReadOnlyList<FunctionInfo> _functions;
    private readonly MetadataSnapshot _snapshot;

    public SqlCompletionEngine(MetadataSnapshot snapshot, Func<string, string> quote, string? providerKey = null)
    {
        _snapshot = snapshot;
        _quote = quote;
        _providerKey = providerKey;
        _objects = snapshot.Objects
            .Where(o => o.Type is not DbObjectType.Trigger)
            .DistinctBy(o => (o.Schema.ToUpperInvariant(), o.Name.ToUpperInvariant()))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _objectsByName = _objects.ToLookup(o => o.Name, StringComparer.OrdinalIgnoreCase);
        _objectsBySchema = _objects.ToLookup(o => o.Schema, StringComparer.OrdinalIgnoreCase);
        _schemas = _objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        _allColumnNames = snapshot.Columns.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        _functions = Functions.Where(f => f.Provider is null || providerKey is null || f.Provider == providerKey).ToList();
    }

    private bool IsSqlServer => _providerKey is null or "SqlServer";
    private bool IsPostgres => _providerKey is null or "Postgres";

    // ----- Completion -----

    /// <param name="explicitRequest">True for Ctrl+Space: an empty prefix still lists suggestions.</param>
    public CompletionResult Complete(string text, int caret, bool explicitRequest = false, int max = 25)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var prefix = text[start..caret];

        if (InsideStringOrComment(text, caret)) return CompletionResult.Empty;

        var (statement, statementStart) = CurrentStatement(text, caret);
        var references = ParseReferences(statement);
        var ctes = ParseCtes(statement);

        if (explicitRequest && TryExpandStar(text, start, references, out var star)) return star;

        var qualifier = ReadQualifier(text, start);
        List<(CompletionItem Item, int Group)> items;
        if (qualifier is not null)
        {
            items = QualifiedItems(qualifier, references, prefix).Select(i => (i, 0)).ToList();
        }
        else
        {
            if (prefix.Length == 0 && !explicitRequest) return CompletionResult.Empty;
            var context = DetectContext(statement, Math.Max(0, start - statementStart), out var clause, out var insertTable);
            if (context == SqlContext.AliasName && !explicitRequest) return CompletionResult.Empty;
            items = ContextItems(context, clause, insertTable, references, ctes, prefix, text);
        }

        var list = items
            .Select(x => (x.Item, x.Group, Quality: MatchQuality(x.Item.Label, prefix)))
            .Where(x => x.Quality > 0)
            .Select((x, index) => (x.Item, x.Group, x.Quality, index))
            .OrderBy(x => x.Quality >= 3 ? 0 : 1)       // prefix matches before word-start / acronym matches
            .ThenBy(x => x.Group)
            .ThenBy(x => x.index)
            .Select(x => x.Item)
            .DistinctBy(i => (i.Kind, i.InsertText.ToUpperInvariant()))
            .Take(max)
            .ToList();
        return new CompletionResult(start, list);
    }

    /// <summary>What kind of thing belongs at <paramref name="position"/> of the statement.</summary>
    public SqlContext DetectContext(string statement, int position, out string clause, out DbObject? insertTable)
    {
        clause = "";
        insertTable = null;
        var tokens = SqlLexer.Tokenize(statement[..Math.Clamp(position, 0, statement.Length)]).Where(t => !t.IsTrivia).ToList();
        if (tokens.Count == 0) return SqlContext.StatementStart;

        var depth = 0;
        var sinceKeyword = new List<SqlToken>();   // significant tokens between the clause keyword and the caret, at caret depth
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.CloseParen) { depth++; continue; }
            if (t.Kind == SqlTokenKind.OpenParen)
            {
                if (depth > 0) { depth--; continue; }
                // Directly inside "( … ": INSERT INTO t ( → column list; otherwise an expression (function args, IN list).
                var before = i >= 1 ? tokens[i - 1] : default;
                var beforeThat = i >= 2 ? tokens[i - 2] : default;
                if (i >= 1 && before.IsIdentifier && (i >= 2 && beforeThat.Is("INTO") || beforeThat.Kind == SqlTokenKind.Dot && IsInsertTarget(tokens, i - 1)))
                {
                    insertTable = ResolveQualified(tokens, i - 1);
                    clause = "INSERT";
                    return SqlContext.InsertColumns;
                }
                clause = "(";
                return SqlContext.Expression;
            }
            if (depth > 0) continue;
            if (t.Kind == SqlTokenKind.Semicolon) break;

            if (t.Kind == SqlTokenKind.Word)
            {
                var word = t.Text.ToUpperInvariant();
                if (word is "EXEC" or "EXECUTE" or "CALL")
                {
                    clause = word;
                    return sinceKeyword.Count == 0 ? SqlContext.RoutineName : SqlContext.Expression;
                }
                if (TableSourceKeywords.Contains(word) || (word == "INSERT" && sinceKeyword.Count == 0))
                {
                    clause = word;
                    return TableSourcePosition(sinceKeyword, word);
                }
                if (word == "AS")
                {
                    clause = "AS";
                    return sinceKeyword.Count == 0 ? SqlContext.AliasName : SqlContext.Expression;
                }
                if (word == "ON")
                {
                    clause = "ON";
                    return sinceKeyword.Count == 0 ? SqlContext.JoinCondition : SqlContext.Expression;
                }
                if (word is "AND" or "OR" && sinceKeyword.Count == 0 && ClauseBefore(tokens, i) == "ON")
                {
                    clause = "ON";
                    return SqlContext.JoinCondition;
                }
                if (word == "DELETE" && sinceKeyword.Count == 0)
                {
                    clause = "DELETE";
                    return SqlContext.Expression;
                }
                if (ExpressionKeywords.Contains(word))
                {
                    clause = word == "BY" && i >= 1 ? tokens[i - 1].Text.ToUpperInvariant() + " BY" : word;
                    if (word is "AND" or "OR" or "NOT" or "WHEN" or "THEN" or "ELSE" or "IN" or "LIKE" or "ILIKE" or "BETWEEN" or "IS" or "EXISTS")
                        clause = ClauseBefore(tokens, i) ?? word;
                    return SqlContext.Expression;
                }
            }
            sinceKeyword.Insert(0, t);
        }

        return SqlContext.StatementStart;
    }

    /// <summary>After FROM/JOIN/…: a table name goes here, unless one was already written (then keywords or an alias).</summary>
    private static SqlContext TableSourcePosition(IReadOnlyList<SqlToken> sinceKeyword, string keyword)
    {
        if (sinceKeyword.Count == 0 || sinceKeyword[^1].Kind is SqlTokenKind.Comma or SqlTokenKind.Dot)
            return SqlContext.TableName;
        // FROM a, b | FROM t alias | FROM schema.t
        return SqlContext.AfterTableSource;
    }

    private static bool IsInsertTarget(IReadOnlyList<SqlToken> tokens, int nameIndex)
    {
        for (var i = nameIndex; i >= 0; i--)
        {
            if (tokens[i].Is("INTO")) return true;
            if (!(tokens[i].IsIdentifier || tokens[i].Kind == SqlTokenKind.Dot)) return false;
        }
        return false;
    }

    /// <summary>The clause keyword (WHERE/ON/HAVING/SELECT/SET…) a condition belongs to.</summary>
    private static string? ClauseBefore(IReadOnlyList<SqlToken> tokens, int index)
    {
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.CloseParen) depth++;
            else if (t.Kind == SqlTokenKind.OpenParen) { if (depth == 0) return "("; depth--; }
            else if (depth == 0 && t.Kind == SqlTokenKind.Word &&
                     t.Text.ToUpperInvariant() is "WHERE" or "ON" or "HAVING" or "SELECT" or "SET" or "WHEN" or "CASE" or "BY" or "FROM" or "JOIN")
                return t.Text.ToUpperInvariant() == "BY" && i >= 1 ? tokens[i - 1].Text.ToUpperInvariant() + " BY" : t.Text.ToUpperInvariant();
        }
        return null;
    }

    private DbObject? ResolveQualified(IReadOnlyList<SqlToken> tokens, int nameIndex)
    {
        var name = tokens[nameIndex].Identifier;
        var schema = nameIndex >= 2 && tokens[nameIndex - 1].Kind == SqlTokenKind.Dot && tokens[nameIndex - 2].IsIdentifier
            ? tokens[nameIndex - 2].Identifier
            : null;
        return Resolve(schema, name);
    }

    private List<(CompletionItem Item, int Group)> ContextItems(
        SqlContext context, string clause, DbObject? insertTable, IReadOnlyList<TableReferenceInfo> references,
        IReadOnlyList<string> ctes, string prefix, string fullText)
    {
        var items = new List<(CompletionItem, int)>();
        void Add(IEnumerable<CompletionItem> source, int group) => items.AddRange(source.Select(i => (i, group)));

        switch (context)
        {
            case SqlContext.StatementStart:
                Add(Snippets(), 0);
                Add(KeywordItems(["SELECT", "INSERT INTO", "UPDATE", "DELETE FROM", "WITH", "EXEC", "DECLARE", "SET", "IF", "BEGIN",
                                  "BEGIN TRANSACTION", "COMMIT", "ROLLBACK", "CREATE TABLE", "CREATE VIEW", "CREATE PROCEDURE",
                                  "CREATE FUNCTION", "CREATE INDEX", "ALTER TABLE", "DROP TABLE", "TRUNCATE TABLE", "MERGE", "PRINT"]), 1);
                Add(AllKeywords(), 3);
                break;

            case SqlContext.TableName:
                Add(ctes.Select(c => new CompletionItem(c, QuoteIfNeeded(c), CompletionKind.Table, "CTE")), 0);
                if (clause == "JOIN") Add(RelatedTables(references), 1);
                Add(_objects.Where(o => o.IsTableLike || o.Type is DbObjectType.TableFunction or DbObjectType.Function or DbObjectType.Synonym)
                    .OrderBy(o => o.IsTableLike ? 0 : 1)
                    .Select(o => ObjectItem(o, qualified: true)), 2);
                Add(_schemas.Select(s => new CompletionItem(s, QuoteIfNeeded(s), CompletionKind.Schema)), 3);
                break;

            case SqlContext.AfterTableSource:
                Add(KeywordItems(clause == "JOIN"
                    ? ["ON", "AS", "WHERE", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "ORDER BY", "GROUP BY"]
                    : clause is "UPDATE" ? ["SET", "AS"]
                    : clause is "INTO" or "INSERT" ? ["VALUES", "SELECT", "DEFAULT VALUES"]
                    : ["WHERE", "AS", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "CROSS APPLY", "OUTER APPLY",
                       "ORDER BY", "GROUP BY", "UNION", "UNION ALL", "LIMIT", "WITH (NOLOCK)"]), 0);
                break;

            case SqlContext.JoinCondition:
                Add(JoinConditions(references), 0);
                Add(ColumnsInScope(references), 1);
                Add(AliasItems(references), 2);
                break;

            case SqlContext.InsertColumns:
                if (insertTable is not null)
                {
                    var columns = _snapshot.ColumnsOf(insertTable.Database, insertTable.Schema, insertTable.Name)
                        .Where(c => !c.IsComputed).OrderBy(c => c.Ordinal).ToList();
                    if (prefix.Length == 0 && columns.Count > 1)
                        Add([new CompletionItem("(all columns)", string.Join(", ", columns.Where(c => !c.IsIdentity).Select(c => QuoteIfNeeded(c.Name))),
                            CompletionKind.Snippet, $"{columns.Count(c => !c.IsIdentity)} insertable column(s) of {insertTable.FullName}")], 0);
                    Add(columns.Select(c => new CompletionItem(c.Name, QuoteIfNeeded(c.Name), CompletionKind.Column,
                        c.DataType + (c.IsIdentity ? " · identity" : "") + (c.IsNullable ? "" : " · required"))), 1);
                }
                break;

            case SqlContext.RoutineName:
                Add(_objects.Where(o => o.IsRoutine).OrderBy(o => o.Type == DbObjectType.Procedure ? 0 : 1)
                    .Select(o => ObjectItem(o, qualified: true)), 0);
                Add(_schemas.Select(s => new CompletionItem(s, QuoteIfNeeded(s), CompletionKind.Schema)), 1);
                break;

            case SqlContext.AliasName:
                break;

            default: // Expression
                Add(ColumnsInScope(references), 0);
                Add(AliasItems(references), 1);
                Add(VariableItems(fullText), 1);
                Add(KeywordItems(ClauseKeywords(clause)), 2);
                Add(FunctionItems(), 3);
                Add(AllKeywords(), 4);
                Add(_objects.OrderBy(o => o.IsTableLike ? 0 : 1).Select(o => ObjectItem(o, qualified: true)), 5);
                Add(_schemas.Select(s => new CompletionItem(s, QuoteIfNeeded(s), CompletionKind.Schema)), 6);
                if (prefix.Length > 0) Add(_allColumnNames.Select(c => new CompletionItem(c, QuoteIfNeeded(c), CompletionKind.Column)), 7);
                break;
        }
        return items;
    }

    private IReadOnlyList<string> ClauseKeywords(string clause) => clause switch
    {
        "SELECT" or "DISTINCT" or "TOP" => ["FROM", "AS", "DISTINCT", "CASE", IsSqlServer ? "TOP" : "LIMIT", "INTO"],
        "WHERE" or "HAVING" or "ON" => ["AND", "OR", "NOT", "IN", "NOT IN", "EXISTS", "NOT EXISTS", "BETWEEN", "LIKE", "IS NULL", "IS NOT NULL",
                                        "GROUP BY", "ORDER BY", IsPostgres ? "ILIKE" : "ESCAPE"],
        "GROUP BY" => ["HAVING", "ORDER BY", "ROLLUP", "CUBE"],
        "ORDER BY" => ["ASC", "DESC", IsSqlServer ? "OFFSET" : "LIMIT", IsSqlServer ? "FETCH NEXT" : "NULLS LAST"],
        "PARTITION BY" => ["ORDER BY"],
        "SET" => ["WHERE", "FROM", "OUTPUT", "RETURNING"],
        "CASE" or "WHEN" => ["WHEN", "THEN", "ELSE", "END"],
        "THEN" or "ELSE" => ["WHEN", "ELSE", "END"],
        "VALUES" => ["NULL", "DEFAULT"],
        _ => ["AND", "OR", "FROM", "WHERE", "AS"]
    };

    /// <summary>Columns of every table in the statement, qualified with the alias when more than one table is in play.</summary>
    private IEnumerable<CompletionItem> ColumnsInScope(IReadOnlyList<TableReferenceInfo> references)
    {
        var qualify = references.Count > 1;
        foreach (var r in references)
        {
            var qualifier = r.Alias ?? QuoteIfNeeded(r.Object.Name);
            foreach (var c in _snapshot.ColumnsOf(r.Object.Database, r.Object.Schema, r.Object.Name).OrderBy(c => c.Ordinal))
            {
                var insert = qualify ? $"{qualifier}.{QuoteIfNeeded(c.Name)}" : QuoteIfNeeded(c.Name);
                yield return new CompletionItem(c.Name, insert, CompletionKind.Column,
                    $"{(r.Alias is null ? r.Object.Name : $"{r.Alias} ({r.Object.Name})")} · {c.DataType}{(c.IsPrimaryKey ? " · PK" : "")}");
            }
        }
    }

    private static IEnumerable<CompletionItem> AliasItems(IReadOnlyList<TableReferenceInfo> references) =>
        references.Where(r => r.Alias is not null)
            .Select(r => new CompletionItem(r.Alias!, r.Alias!, CompletionKind.Alias, r.Object.FullName));

    private static IEnumerable<CompletionItem> VariableItems(string text) =>
        SqlLexer.Tokenize(text).Where(t => t.Kind == SqlTokenKind.Variable && t.Text.StartsWith('@') && !t.Text.StartsWith("@@"))
            .Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(v => new CompletionItem(v, v, CompletionKind.Variable, "variable"));

    private IEnumerable<CompletionItem> FunctionItems() =>
        _functions.Select(f =>
        {
            var name = _providerKey == "Postgres" ? f.Name.ToLowerInvariant() : f.Name;
            return f.Name == "CURRENT_TIMESTAMP"
                ? new CompletionItem(f.Name, f.Name, CompletionKind.Function, f.Signature)
                : new CompletionItem(f.Name, name + "()", CompletionKind.Function, f.Signature, name.Length + 1);
        });

    private static IEnumerable<CompletionItem> KeywordItems(IEnumerable<string> keywords) =>
        keywords.Select(k => new CompletionItem(k, k, CompletionKind.Keyword));

    private IEnumerable<CompletionItem> AllKeywords() =>
        KeywordItems(Keywords.Where(k => k switch
        {
            "TOP" or "NOLOCK" or "CROSS APPLY" or "OUTER APPLY" or "PRINT" or "OUTPUT" or "MERGE" or "EXEC" => IsSqlServer,
            "LIMIT" or "RETURNING" or "ILIKE" => IsPostgres,
            _ => true
        }));

    private IEnumerable<CompletionItem> Snippets()
    {
        CompletionItem Snippet(string label, string text, string detail)
        {
            var caret = text.IndexOf('|');
            return new CompletionItem(label, text.Replace("|", ""), CompletionKind.Snippet, detail, caret < 0 ? null : caret);
        }

        yield return Snippet("SELECT * FROM", "SELECT *\nFROM |", "query all columns");
        yield return IsSqlServer
            ? Snippet("SELECT TOP 100 * FROM", "SELECT TOP (100) *\nFROM |", "first rows")
            : Snippet("SELECT * FROM … LIMIT 100", "SELECT *\nFROM |\nLIMIT 100", "first rows");
        yield return Snippet("SELECT COUNT(*) FROM", "SELECT COUNT(*)\nFROM |", "count rows");
        yield return Snippet("INSERT INTO … VALUES", "INSERT INTO | ()\nVALUES ();", "insert a row");
        yield return Snippet("UPDATE … SET … WHERE", "UPDATE |\nSET \nWHERE ;", "update rows");
        yield return Snippet("DELETE FROM … WHERE", "DELETE FROM |\nWHERE ;", "delete rows");
        yield return Snippet("WITH cte AS (…)", "WITH cte AS (\n    SELECT |\n)\nSELECT *\nFROM cte;", "common table expression");
        yield return Snippet("CASE WHEN … END", "CASE WHEN | THEN  ELSE  END", "conditional expression");
        yield return IsSqlServer
            ? Snippet("BEGIN TRANSACTION … COMMIT", "BEGIN TRANSACTION;\n\n|\n\nCOMMIT TRANSACTION;", "explicit transaction")
            : Snippet("BEGIN … COMMIT", "BEGIN;\n\n|\n\nCOMMIT;", "explicit transaction");
        if (IsSqlServer)
            yield return Snippet("IF EXISTS (…)", "IF EXISTS (SELECT 1 FROM | WHERE )\nBEGIN\n    \nEND", "conditional block");
    }

    /// <summary>Complete "ON" conditions for the table joined last, from foreign keys in either direction.</summary>
    private IEnumerable<CompletionItem> JoinConditions(IReadOnlyList<TableReferenceInfo> references)
    {
        if (references.Count < 2) yield break;
        var joined = references[^1];
        foreach (var other in references.Take(references.Count - 1))
        {
            foreach (var condition in ForeignKeyConditions(joined, other))
                yield return new CompletionItem(condition, condition, CompletionKind.Join, "foreign key");
        }
    }

    private IEnumerable<string> ForeignKeyConditions(TableReferenceInfo a, TableReferenceInfo b)
    {
        string Q(TableReferenceInfo r) => r.Alias ?? QuoteIfNeeded(r.Object.Name);

        IEnumerable<string> From(TableReferenceInfo child, TableReferenceInfo parent) =>
            _snapshot.ForeignKeysOf(child.Object.Database, child.Object.Schema, child.Object.Name)
                .Where(fk => Same(fk.ReferencedSchema, parent.Object.Schema) && Same(fk.ReferencedTable, parent.Object.Name))
                .Select(fk => Condition(Q(child), fk.Columns, Q(parent), fk.ReferencedColumns))
                .Where(c => c is not null)!;

        return From(a, b).Concat(From(b, a));
    }

    private string? Condition(string childQualifier, string? childColumns, string parentQualifier, string? parentColumns)
    {
        if (string.IsNullOrWhiteSpace(childColumns) || string.IsNullOrWhiteSpace(parentColumns)) return null;
        var left = childColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var right = parentColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (left.Length != right.Length) return null;
        return string.Join(" AND ", left.Select((c, i) => $"{childQualifier}.{QuoteIfNeeded(c)} = {parentQualifier}.{QuoteIfNeeded(right[i])}"));
    }

    /// <summary>Tables linked by a foreign key to a table already in the statement, as "Table alias ON condition".</summary>
    private IEnumerable<CompletionItem> RelatedTables(IReadOnlyList<TableReferenceInfo> references)
    {
        var usedAliases = references.Select(r => r.Alias ?? r.Object.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in references)
        {
            var o = existing.Object;
            var parents = _snapshot.ForeignKeysOf(o.Database, o.Schema, o.Name)
                .Select(fk => Resolve(fk.ReferencedSchema, fk.ReferencedTable));
            var children = _snapshot.ReferencesTo(o.Database, o.Schema, o.Name)
                .Select(fk => Resolve(fk.Schema, fk.Table));

            foreach (var related in parents.Concat(children).Where(r => r is not null).Distinct())
            {
                var alias = MakeAlias(related!.Name, usedAliases);
                var candidate = new TableReferenceInfo(related, alias);
                var condition = ForeignKeyConditions(candidate, existing).FirstOrDefault();
                if (condition is null) continue;
                var name = $"{QuoteIfNeeded(related.Schema)}.{QuoteIfNeeded(related.Name)}";
                yield return new CompletionItem(related.Name, $"{name} {alias} ON {condition}", CompletionKind.Join,
                    $"{related.Schema} · ON {condition}");
            }
        }
    }

    /// <summary>Initials of the name's words (OrderLines → ol), made unique against the aliases in use.</summary>
    private static string MakeAlias(string name, ISet<string> used)
    {
        var initials = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetter(c)) continue;
            if (i == 0 || char.IsUpper(c) && !char.IsUpper(name[i - 1]) || name[i - 1] is '_' or ' ')
                initials.Append(char.ToLowerInvariant(c));
        }
        var alias = initials.Length == 0 ? "t" : initials.ToString();
        if (NotAnAlias.Contains(alias) || ReservedWords.Contains(alias)) alias += "1";
        var unique = alias;
        for (var n = 2; used.Contains(unique); n++) unique = alias + n.ToString(CultureInfo.InvariantCulture);
        used.Add(unique);
        return unique;
    }

    /// <summary>Ctrl+Space right after "*" (or "alias.*") offers the explicit column list.</summary>
    private bool TryExpandStar(string text, int wordStart, IReadOnlyList<TableReferenceInfo> references, out CompletionResult result)
    {
        result = CompletionResult.Empty;
        var i = wordStart;
        while (i > 0 && text[i - 1] == ' ') i--;
        if (i == 0 || text[i - 1] != '*') return false;
        var starStart = i - 1;
        var qualifier = ReadQualifier(text, starStart);

        IEnumerable<TableReferenceInfo> tables = references;
        if (qualifier is not null)
        {
            var q = qualifier[^1];
            tables = references.Where(r => Same(r.Alias, q) || Same(r.Object.Name, q)).ToList();
            while (starStart > 0 && (IsWordChar(text[starStart - 1]) || text[starStart - 1] is '.' or '[' or ']' or '"')) starStart--;
        }

        var list = tables.ToList();
        if (list.Count == 0) return false;
        var qualify = list.Count > 1 || qualifier is not null;
        var columns = list.SelectMany(r => _snapshot.ColumnsOf(r.Object.Database, r.Object.Schema, r.Object.Name)
            .OrderBy(c => c.Ordinal)
            .Select(c => (qualify ? (r.Alias ?? QuoteIfNeeded(r.Object.Name)) + "." : "") + QuoteIfNeeded(c.Name))).ToList();
        if (columns.Count == 0) return false;

        result = new CompletionResult(Math.Max(0, starStart),
            [new CompletionItem("Expand *", string.Join(", ", columns), CompletionKind.Snippet, $"{columns.Count} column(s)")]);
        return true;
    }

    private IEnumerable<CompletionItem> QualifiedItems(
        IReadOnlyList<string> qualifier, IReadOnlyList<TableReferenceInfo> references, string prefix)
    {
        var last = qualifier[^1];

        if (qualifier.Count == 1)
        {
            var aliased = references.FirstOrDefault(r => string.Equals(r.Alias, last, StringComparison.OrdinalIgnoreCase));
            if (aliased is not null) return ColumnItems(aliased.Object, prefix);
        }

        var schema = qualifier.Count >= 2 ? qualifier[^2] : null;
        var table = Resolve(schema, last);
        var items = new List<CompletionItem>();
        if (table is not null) items.AddRange(ColumnItems(table, prefix));

        if (qualifier.Count == 1 || qualifier.Count == 2)
        {
            items.AddRange(_objectsBySchema[last]
                .Where(o => MatchQuality(o.Name, prefix) > 0)
                .Select(o => ObjectItem(o, qualified: false)));
        }
        return items;
    }

    private IEnumerable<CompletionItem> ColumnItems(DbObject obj, string prefix) =>
        _snapshot.ColumnsOf(obj.Database, obj.Schema, obj.Name)
            .OrderBy(c => c.Ordinal)
            .Where(c => MatchQuality(c.Name, prefix) > 0)
            .Select(c => new CompletionItem(c.Name, QuoteIfNeeded(c.Name), CompletionKind.Column,
                c.DataType + (c.IsPrimaryKey ? " · PK" : "")));

    private CompletionItem ObjectItem(DbObject o, bool qualified)
    {
        var kind = o.Type switch
        {
            DbObjectType.Table or DbObjectType.ForeignTable => CompletionKind.Table,
            DbObjectType.View or DbObjectType.MaterializedView => CompletionKind.View,
            _ when o.IsRoutine => CompletionKind.Routine,
            _ => CompletionKind.Other
        };
        var insert = qualified ? $"{QuoteIfNeeded(o.Schema)}.{QuoteIfNeeded(o.Name)}" : QuoteIfNeeded(o.Name);
        var detail = o.Schema + (o.RowCount is long rows ? $" · ≈{rows:N0} rows" : "") + (kind == CompletionKind.Routine ? $" · {o.Type}" : "");
        return new CompletionItem(o.Name, insert, kind, detail);
    }

    // ----- Navigation and signature help -----

    /// <summary>The table/view/routine named at <paramref name="offset"/> (following aliases), for "go to definition".</summary>
    public DbObject? ResolveObjectAt(string text, int offset)
    {
        var token = SqlLexer.Tokenize(text).FirstOrDefault(t => t.IsIdentifier && offset >= t.Start && offset <= t.End);
        if (token.Text is null) return null;
        var (statement, _) = CurrentStatement(text, offset);
        var name = token.Identifier;
        var qualifier = ReadQualifier(text, token.Start);
        if (qualifier is null && ParseReferences(statement).FirstOrDefault(r => Same(r.Alias, name)) is { } aliased) return aliased.Object;
        return Resolve(qualifier?[^1], name);
    }

    /// <summary>The signature of the built-in function whose argument list contains <paramref name="caret"/>, and which
    /// argument (0-based) the caret is in; null outside a known function call.</summary>
    public (string Signature, int Argument)? SignatureAt(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var tokens = SqlLexer.Tokenize(text[..caret]).Where(t => !t.IsTrivia).ToList();
        var depth = 0;
        var argument = 0;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.CloseParen) depth++;
            else if (t.Kind == SqlTokenKind.Comma && depth == 0) argument++;
            else if (t.Kind == SqlTokenKind.Semicolon) return null;
            else if (t.Kind == SqlTokenKind.OpenParen)
            {
                if (depth > 0) { depth--; continue; }
                if (i == 0 || tokens[i - 1].Kind != SqlTokenKind.Word) return null;
                var fn = _functions.FirstOrDefault(f => Same(f.Name, tokens[i - 1].Text));
                return fn is null ? null : (fn.Signature, argument);
            }
        }
        return null;
    }

    // ----- Hover -----

    /// <summary>A short description of the identifier at <paramref name="offset"/> (object, column, alias or function), or null.</summary>
    public string? Describe(string text, int offset)
    {
        var token = SqlLexer.Tokenize(text).FirstOrDefault(t => t.IsIdentifier && offset >= t.Start && offset < t.End);
        if (token == default) return null;

        var (statement, _) = CurrentStatement(text, offset);
        var references = ParseReferences(statement);
        var name = token.Identifier;
        var qualifier = ReadQualifier(text, token.Start);

        if (qualifier is not null)
        {
            var owner = references.FirstOrDefault(r => Same(r.Alias, qualifier[^1]))?.Object
                        ?? Resolve(qualifier.Count >= 2 ? qualifier[^2] : null, qualifier[^1]);
            if (owner is not null && ColumnOf(owner, name) is { } col) return DescribeColumn(owner, col);
            if (_objectsBySchema[qualifier[^1]].FirstOrDefault(o => Same(o.Name, name)) is { } inSchema) return DescribeObject(inSchema);
            return null;
        }

        if (references.FirstOrDefault(r => Same(r.Alias, name)) is { } aliased)
            return $"alias {name} → {DescribeObject(aliased.Object)}";
        if (KnownFunctionNames.Contains(name) && _functions.FirstOrDefault(f => Same(f.Name, name)) is { } fn)
            return fn.Signature;
        foreach (var r in references)
            if (ColumnOf(r.Object, name) is { } col) return DescribeColumn(r.Object, col);
        return Resolve(null, name) is { } obj ? DescribeObject(obj) : null;
    }

    private DbColumn? ColumnOf(DbObject obj, string name) =>
        _snapshot.ColumnsOf(obj.Database, obj.Schema, obj.Name).FirstOrDefault(c => Same(c.Name, name));

    private static string DescribeColumn(DbObject owner, DbColumn c) =>
        $"{owner.FullName}.{c.Name}  {c.DataType}{(c.IsNullable ? " NULL" : " NOT NULL")}" +
        (c.IsPrimaryKey ? " · primary key" : "") + (c.IsIdentity ? " · identity" : "") + (c.IsComputed ? " · computed" : "");

    private string DescribeObject(DbObject o)
    {
        var sb = new StringBuilder($"{o.Type} {o.FullName}");
        if (o.RowCount is long rows) sb.Append(CultureInfo.CurrentCulture, $" · ≈{rows:N0} rows");
        var columns = _snapshot.ColumnsOf(o.Database, o.Schema, o.Name).OrderBy(c => c.Ordinal).ToList();
        foreach (var c in columns.Take(20))
            sb.Append("\n  ").Append(c.Name).Append("  ").Append(c.DataType).Append(c.IsPrimaryKey ? "  PK" : "");
        if (columns.Count > 20) sb.Append(CultureInfo.CurrentCulture, $"\n  … {columns.Count - 20} more");
        var fks = _snapshot.ForeignKeysOf(o.Database, o.Schema, o.Name).ToList();
        foreach (var fk in fks.Take(5)) sb.Append($"\n  → {fk.ReferencedSchema}.{fk.ReferencedTable} ({fk.Columns})");
        return sb.ToString();
    }

    // ----- Parsing -----

    /// <summary>The (db.)schema.table references and their aliases found in <paramref name="sql"/> that resolve to real objects.</summary>
    public IReadOnlyList<TableReferenceInfo> ParseReferences(string sql)
    {
        var result = new List<TableReferenceInfo>();
        foreach (Match m in TableReference.Matches(MaskStringsAndComments(sql)))
        {
            var parts = SplitQualified(m.Groups["name"].Value);
            var table = parts[^1];
            var schema = parts.Count >= 2 ? parts[^2] : null;
            var obj = Resolve(schema, table);
            if (obj is null) continue;

            var alias = m.Groups["alias"].Success ? Unquote(m.Groups["alias"].Value) : null;
            if (alias is not null && (NotAnAlias.Contains(alias) || ReservedWords.Contains(alias))) alias = null;
            result.Add(new TableReferenceInfo(obj, alias));
        }
        return result;
    }

    /// <summary>Names defined by WITH … AS ( … ) in the statement.</summary>
    public static IReadOnlyList<string> ParseCtes(string sql) =>
        CteDefinition.Matches(MaskStringsAndComments(sql)).Select(m => Unquote(m.Groups["name"].Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Blanks out strings and comments (same length) so regexes never match inside them.</summary>
    private static string MaskStringsAndComments(string sql)
    {
        var chars = sql.ToCharArray();
        foreach (var t in SqlLexer.Tokenize(sql))
            if (t.Kind is SqlTokenKind.String or SqlTokenKind.Comment)
                for (var i = t.Start; i < t.End; i++) if (chars[i] != '\n') chars[i] = ' ';
        return new string(chars);
    }

    private static bool InsideStringOrComment(string text, int caret)
    {
        foreach (var t in SqlLexer.Tokenize(text))
        {
            if (t.Start >= caret) break;
            if (t.Kind is not (SqlTokenKind.String or SqlTokenKind.Comment)) continue;
            if (caret < t.End || (caret == t.End && Unterminated(t))) return true;
        }
        return false;
    }

    /// <summary>A line comment always runs to the caret on its line; strings and block comments may be unclosed while typing.</summary>
    private static bool Unterminated(SqlToken t)
    {
        var s = t.Text;
        if (t.Kind == SqlTokenKind.Comment) return s.StartsWith("--", StringComparison.Ordinal) || s.Length < 4 || !s.EndsWith("*/", StringComparison.Ordinal);
        if (s.StartsWith('$'))
        {
            var tag = s[..(s.IndexOf('$', 1) + 1)];
            return s.Length < tag.Length * 2 || !s.EndsWith(tag, StringComparison.Ordinal);
        }
        var open = s.IndexOf('\'');
        return s.Length < open + 2 || s[^1] != '\'';
    }

    private DbObject? Resolve(string? schema, string name)
    {
        var candidates = _objectsByName[name];
        return schema is null
            ? candidates.OrderBy(o => o.IsTableLike ? 0 : 1).FirstOrDefault()
            : candidates.FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
    }

    private string QuoteIfNeeded(string identifier) =>
        SimpleIdentifier.IsMatch(identifier) && !ReservedWords.Contains(identifier) ? identifier : _quote(identifier);

    /// <summary>3 = prefix, 2 = starts a word inside the name (CustomerId ← "Id"), 1 = acronym (OrderLines ← "ol"), 0 = no match.</summary>
    public static int MatchQuality(string candidate, string prefix)
    {
        if (prefix.Length == 0) return 3;
        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 3;
        if (prefix.Length < 2) return 0;

        var starts = WordStarts(candidate);
        if (starts.Any(s => string.Compare(candidate, s, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0)) return 2;
        if (prefix.Length <= starts.Count && prefix.Select((ch, i) => char.ToLowerInvariant(candidate[starts[i]]) == char.ToLowerInvariant(ch)).All(b => b))
            return 1;
        return 0;
    }

    private static List<int> WordStarts(string s)
    {
        var starts = new List<int>();
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsLetterOrDigit(s[i])) continue;
            if (i == 0 || !char.IsLetterOrDigit(s[i - 1]) || (char.IsUpper(s[i]) && char.IsLower(s[i - 1])))
                starts.Add(i);
        }
        return starts;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    /// <summary>Reads "a.b." immediately before the word start, e.g. <c>dbo.Orders.</c> → [dbo, Orders].</summary>
    private static IReadOnlyList<string>? ReadQualifier(string text, int wordStart)
    {
        var parts = new List<string>();
        var i = wordStart;
        while (i > 0 && text[i - 1] == '.')
        {
            var end = i - 1;
            var j = end;
            if (j > 0 && text[j - 1] is ']' or '"')
            {
                var close = text[j - 1];
                var open = close == ']' ? '[' : '"';
                var k = j - 2;
                while (k >= 0 && text[k] != open) k--;
                if (k < 0) break;
                parts.Insert(0, text[(k + 1)..(j - 1)]);
                i = k;
            }
            else
            {
                while (j > 0 && IsWordChar(text[j - 1])) j--;
                if (j == end) break;
                parts.Insert(0, text[j..end]);
                i = j;
            }
        }
        return parts.Count == 0 ? null : parts;
    }

    /// <summary>The statement around the caret (split on ';' and GO, not on blank lines) and where it starts.</summary>
    private static (string Statement, int Start) CurrentStatement(string text, int caret)
    {
        var start = 0;
        var end = text.Length;
        var ranges = SqlScriptTools.SplitStatements(text, splitOnBlankLines: false);
        for (var i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            var terminated = r.Length > 0 && text[r.End - 1] == ';';
            // "FROM |": only whitespace since an unterminated statement means the caret still belongs to it.
            var continues = !terminated && r.End <= caret && text[r.End..caret].Trim().Length == 0 &&
                            (i + 1 >= ranges.Count || ranges[i + 1].Start >= caret);
            if (continues)
            {
                start = r.Start;
                end = Math.Max(r.End, caret);
                break;
            }
            if (r.End < caret || (r.End == caret && terminated)) { start = r.End; continue; }
            if (r.Start > caret) { end = r.Start; break; }
            start = r.Start;
            end = r.End;
            break;
        }
        return (text[start..end], start);
    }

    private static List<string> SplitQualified(string name) =>
        Regex.Matches(name, Ident).Select(m => Unquote(m.Value)).ToList();

    private static string Unquote(string identifier) =>
        identifier.Length >= 2 && (identifier[0] == '[' || identifier[0] == '"') ? identifier[1..^1] : identifier;
}

public sealed record TableReferenceInfo(DbObject Object, string? Alias);
