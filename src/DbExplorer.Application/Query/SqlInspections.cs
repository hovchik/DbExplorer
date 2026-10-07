using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

public enum InspectionSeverity
{
    /// <summary>Probably a mistake, but the query may still run (an ambiguous column).</summary>
    Warning,

    /// <summary>The server will reject the statement (unknown table or column, a column missing from GROUP BY).</summary>
    Error
}

/// <summary>One edit that fixes a problem: <c>Start..Start+Length</c> of the script becomes <see cref="Replacement"/>.</summary>
public sealed record SqlQuickFix(string Title, int Start, int Length, string Replacement)
{
    public string ApplyTo(string text) => string.Concat(text.AsSpan(0, Start), Replacement, text.AsSpan(Start + Length));
}

/// <summary>A problem found in the script while typing, with the span to underline and the fixes offered for it.</summary>
public sealed record SqlInspection(int Start, int Length, InspectionSeverity Severity, string Message, IReadOnlyList<SqlQuickFix> Fixes)
{
    public int End => Start + Length;

    /// <summary>The caret is on the underlined text or right after it.</summary>
    public bool Touches(int offset) => offset >= Start && offset <= End;
}

/// <summary>
/// Live checks of the query editor's script against the cached catalog, like the inspections of DataGrip or the red
/// squiggles of SSMS: unknown tables, columns and aliases, ambiguous column names and columns missing from GROUP BY,
/// each with one-click fixes. Nothing is sent to the server.
/// <para>
/// The checks lean towards silence: whenever a statement uses something the catalog cannot describe (a CTE, a derived
/// table, a table function, a temp table, a schema that was not loaded) the checks that would need it are skipped,
/// so a warning is worth reading. Statements that create procedures, functions or triggers are not checked.
/// </para>
/// </summary>
public sealed class SqlInspector
{
    private const int MaxInspections = 200;

    /// <summary>Words that are not in <see cref="SqlCompletionEngine.KeywordSet"/> but are SQL syntax, type names,
    /// date parts or built-in values, so they are never taken for a column.</summary>
    private static readonly HashSet<string> NotColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "TIME", "ZONE", "LOCAL", "WITHOUT", "COLLATE", "ESCAPE", "SIMILAR", "TO", "FILTER", "WITHIN", "RECURSIVE",
        "NULLS", "FIRST", "LAST", "TIES", "PERCENT", "ROLLUP", "CUBE", "GROUPING", "SETS", "PRECEDING", "FOLLOWING",
        "UNBOUNDED", "CURRENT", "ROW", "RANGE", "GROUPS", "EXCLUDE", "OTHERS", "NO", "MATCHED", "SOURCE", "TARGET", "SOME",
        "SYMMETRIC", "OF", "NOWAIT", "SKIP", "LOCKED", "SHARE", "XML", "PATH", "JSON", "AUTO", "RAW", "ROOT", "OPTION",
        "RECOMPILE", "MAXDOP", "HOLDLOCK", "UPDLOCK", "ROWLOCK", "READPAST", "TABLOCK", "TABLOCKX", "NOEXPAND", "FORCESEEK",
        "FOR", "DO", "NOTHING", "CONFLICT", "TIMESTAMP", "DATE", "CURRENT_DATE", "CURRENT_TIME", "LOCALTIME",
        "LOCALTIMESTAMP", "CURRENT_USER", "SESSION_USER", "SYSTEM_USER", "USER", "CURRENT_SCHEMA", "CURRENT_CATALOG",
        "BOTH", "LEADING", "TRAILING", "ARRAY", "UNKNOWN", "VARYING", "PRECISION", "DOUBLE", "CHARACTER", "IDENTITY",
        "MINUS", "PIVOT", "UNPIVOT", "TABLESAMPLE", "SYSTEM", "BERNOULLI", "REPEATABLE", "ISNULL", "NOTNULL", "OVERLAPS",
        "VALUE", "SOME", "MATERIALIZED", "PARTITION", "OVER", "INTERVAL", "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND",
        "EXCLUDED", "INSERTED", "DELETED", "NEW", "OLD", "ROWCOUNT", "SQL_VARIANT", "MAX"
    };

    /// <summary>Functions whose first argument is a type or a date part, not a column: CONVERT(int, …), DATEADD(day, …).</summary>
    private static readonly HashSet<string> FirstArgumentIsKeyword = new(StringComparer.OrdinalIgnoreCase)
        { "CONVERT", "TRY_CONVERT", "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATETRUNC", "DATE_BUCKET", "EXTRACT" };

    private static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "COUNT_BIG", "SUM", "AVG", "MIN", "MAX", "STRING_AGG", "ARRAY_AGG", "JSON_AGG", "JSONB_AGG", "JSON_OBJECT_AGG",
        "JSONB_OBJECT_AGG", "XMLAGG", "STDEV", "STDEVP", "STDDEV", "STDDEV_POP", "STDDEV_SAMP", "VAR", "VARP", "VARIANCE",
        "VAR_POP", "VAR_SAMP", "GROUPING", "GROUPING_ID", "CHECKSUM_AGG", "BOOL_AND", "BOOL_OR", "EVERY", "BIT_AND", "BIT_OR",
        "PERCENTILE_CONT", "PERCENTILE_DISC", "MODE", "APPROX_COUNT_DISTINCT", "LISTAGG"
    };

    /// <summary>Words after a table name that are never its alias.</summary>
    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "GROUP", "ORDER", "HAVING", "UNION",
        "EXCEPT", "INTERSECT", "WITH", "SET", "VALUES", "SELECT", "LIMIT", "OFFSET", "AS", "APPLY", "NATURAL", "USING",
        "WINDOW", "FOR", "OPTION", "PIVOT", "UNPIVOT", "TABLESAMPLE", "WHEN", "THEN", "OUTPUT", "RETURNING", "DEFAULT",
        "FETCH", "LATERAL", "ONLY"
    };

    /// <summary>Statements that name tables in ways the checks do not follow (GRANT … FROM a role, COPY … FROM STDIN).</summary>
    private static readonly HashSet<string> SkippedStatements = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALTER", "DROP", "GRANT", "REVOKE", "DENY", "COPY", "FETCH", "OPEN", "CLOSE", "DEALLOCATE", "MOVE", "BACKUP",
        "RESTORE", "TRUNCATE", "CALL", "VACUUM", "ANALYZE", "REINDEX", "CLUSTER", "COMMENT", "SECURITY", "LOCK", "DBCC"
    };

    private static readonly Regex SimpleIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly MetadataSnapshot _snapshot;
    private readonly Func<string, string> _quote;
    private readonly string? _providerKey;
    private readonly ILookup<string, DbObject> _objectsByName;
    private readonly IReadOnlyList<DbObject> _tableLike;
    private readonly HashSet<string> _schemas;
    private readonly HashSet<string> _databases;
    private readonly ConcurrentDictionary<DbObject, IReadOnlyList<DbColumn>> _columns = new();

    public SqlInspector(MetadataSnapshot snapshot, Func<string, string> quote, string? providerKey = null)
    {
        _snapshot = snapshot;
        _quote = quote;
        _providerKey = providerKey;
        var objects = snapshot.Objects.Where(o => o.Type is not DbObjectType.Trigger).ToList();
        _objectsByName = objects.ToLookup(o => o.Name, StringComparer.OrdinalIgnoreCase);
        _tableLike = objects.Where(o => o.IsTableLike || o.Type == DbObjectType.Synonym).ToList();
        _schemas = objects.Select(o => o.Schema).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _databases = objects.Select(o => o.Database).Where(d => d.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Problems in <paramref name="text"/>, in script order.</summary>
    public IReadOnlyList<SqlInspection> Inspect(string text)
    {
        var result = new List<SqlInspection>();
        if (_objectsByName.Count == 0 || string.IsNullOrWhiteSpace(text)) return result;

        var all = SqlLexer.Tokenize(text);
        var created = CreatedNames(all);
        var significant = all.Where(t => !t.IsTrivia).ToList();
        var next = 0;
        foreach (var range in SqlScriptTools.SplitStatements(text))
        {
            var tokens = new List<SqlToken>();
            while (next < significant.Count && significant[next].Start < range.Start) next++;
            while (next < significant.Count && significant[next].End <= range.End) tokens.Add(significant[next++]);
            if (tokens.Count == 0 || IsSkipped(tokens)) continue;

            new StatementCheck(this, text, tokens, created, result).Run();
            if (result.Count >= MaxInspections) break;
        }
        return result
            .DistinctBy(i => (i.Start, i.Length, i.Message))
            .OrderBy(i => i.Start)
            .Take(MaxInspections)
            .ToList();
    }

    /// <summary>The problem under (or right before) the caret, most severe first.</summary>
    public static SqlInspection? At(IReadOnlyList<SqlInspection> inspections, int offset) =>
        inspections.Where(i => i.Touches(offset)).OrderByDescending(i => i.Severity).ThenBy(i => i.Length).FirstOrDefault();

    private static bool IsSkipped(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens[0].Kind == SqlTokenKind.Word && SkippedStatements.Contains(tokens[0].Text)) return true;
        // Routine bodies use parameters, variables and trigger pseudo-tables the catalog knows nothing about.
        return tokens.Any(t => t.Is("CREATE") || t.Is("ALTER")) &&
               tokens.Any(t => t.Is("PROCEDURE") || t.Is("PROC") || t.Is("FUNCTION") || t.Is("TRIGGER") || t.Is("TYPE"));
    }

    /// <summary>Tables the script itself creates (CREATE TABLE/VIEW, SELECT … INTO), which the catalog does not have yet.</summary>
    private static HashSet<string> CreatedNames(IReadOnlyList<SqlToken> all)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = all.Where(t => !t.IsTrivia).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            var creates = tokens[i].Is("CREATE") ||
                          (tokens[i].Is("INTO") && i > 0 && !tokens[i - 1].Is("INSERT") && !tokens[i - 1].Is("MERGE"));
            if (!creates) continue;
            var j = i + 1;
            if (tokens[i].Is("CREATE"))
            {
                while (j < tokens.Count && tokens[j].Kind == SqlTokenKind.Word &&
                       !(tokens[j].Is("TABLE") || tokens[j].Is("VIEW"))) j++;
                if (j >= tokens.Count || j - i > 5) continue;
                j++;
                while (j < tokens.Count && (tokens[j].Is("IF") || tokens[j].Is("NOT") || tokens[j].Is("EXISTS"))) j++;
            }
            // The last part of a (schema.)name.
            var last = -1;
            while (j < tokens.Count && tokens[j].IsIdentifier)
            {
                last = j;
                if (j + 2 < tokens.Count && tokens[j + 1].Kind == SqlTokenKind.Dot) j += 2;
                else break;
            }
            if (last >= 0) names.Add(tokens[last].Identifier);
        }
        return names;
    }

    // ----- Catalog helpers -----

    private DbObject? Resolve(IReadOnlyList<string> parts)
    {
        var name = parts[^1];
        var schema = parts.Count >= 2 ? parts[^2] : null;
        var database = parts.Count >= 3 ? parts[^3] : null;
        var candidates = database is null ? _objectsByName[name] : _objectsByName[name].Where(o => Same(o.Database, database));
        return schema is null
            ? candidates.OrderBy(o => o.IsTableLike ? 0 : 1).FirstOrDefault()
            : candidates.FirstOrDefault(o => Same(o.Schema, schema));
    }

    private IReadOnlyList<DbColumn> ColumnsOf(DbObject o) =>
        _columns.GetOrAdd(o, key => _snapshot.ColumnsOf(key.Database, key.Schema, key.Name).OrderBy(c => c.Ordinal).ToList());

    private bool HasColumn(DbObject o, string name) => ColumnsOf(o).Any(c => Same(c.Name, name));

    private string QuoteIfNeeded(string identifier) =>
        SimpleIdentifier.IsMatch(identifier) && !SqlCompletionEngine.KeywordSet.Contains(identifier) ? identifier : _quote(identifier);

    /// <summary>The schemas an unqualified name is looked up in by default (dbo, public).</summary>
    private bool IsDefaultSchema(string schema) => _providerKey switch
    {
        "SqlServer" => Same(schema, "dbo"),
        "Postgres" => Same(schema, "public"),
        _ => Same(schema, "dbo") || Same(schema, "public")
    };

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Names close to <paramref name="typed"/>: a typo or two away, or starting with what was typed.</summary>
    internal static IReadOnlyList<string> Closest(string typed, IEnumerable<string> candidates, int max = 3)
    {
        var limit = typed.Length <= 3 ? 1 : typed.Length <= 6 ? 2 : 3;
        return candidates
            .Where(c => !Same(c, typed))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (Name: c, Distance: Distance(typed, c),
                Prefix: typed.Length >= 3 && c.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.Distance <= limit || x.Prefix)
            .ToList() is var close && close.Count > 0
            ? close
            .Where(x => x.Prefix || x.Distance <= close.Min(c => c.Distance))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Name)
            .ToList()
            : [];
    }

    /// <summary>Case-insensitive edit distance (insert, delete, replace, swap two neighbours).</summary>
    internal static int Distance(string a, string b)
    {
        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    // ----- One statement -----

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;
        public List<Source> Sources { get; } = [];

        /// <summary>Some row source cannot be described from the catalog (CTE, derived table, function, unknown table).</summary>
        public bool Opaque { get; set; }

        /// <summary>JOIN … USING / NATURAL JOIN merge same-named columns, so they are not ambiguous.</summary>
        public bool MergesColumns { get; set; }

        public string Clause { get; set; } = "";
        public int SelectIndex { get; set; } = -1;
        public int GroupIndex { get; set; } = -1;
    }

    private sealed class Source
    {
        public required DbObject? Object { get; init; }
        public required string Name { get; init; }
        public required IReadOnlyList<string> Parts { get; init; }
        public string? Alias { get; set; }
        public required int Start { get; init; }
        public required int End { get; init; }
        public required Scope Scope { get; init; }
        public bool IsInsertTarget { get; init; }
        public bool IsUpdateTarget { get; init; }
        public int ColumnListParen { get; init; } = -1;

        /// <summary>CTE, @table variable or table function: fine, just not described by the catalog.</summary>
        public bool IsOpaque { get; init; }

        public string Qualifier => Alias ?? Name;
        public bool Answers(string qualifier) => Same(Alias, qualifier) || Same(Name, qualifier);
    }

    private readonly record struct ColumnRef(string? Qualifier, string Name, int Start, int End, int NameIndex);

    private sealed class StatementCheck(
        SqlInspector owner, string text, List<SqlToken> t, HashSet<string> created, List<SqlInspection> result)
    {
        private readonly int _n = t.Count;
        private readonly Scope[] _scopeOf = new Scope[t.Count];
        private readonly string[] _clauseOf = new string[t.Count];
        private readonly int[] _depthOf = new int[t.Count];
        private readonly bool[] _isName = new bool[t.Count];
        private readonly int[] _match = MatchParentheses(t);
        private readonly List<Scope> _scopes = [];
        private readonly List<Source> _sources = [];
        private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
        private Scope _current = null!;

        public void Run()
        {
            Walk();
            CollectKnownNames();
            CheckTables();
            CheckQualifiedColumns();
            CheckTargetColumns();
            CheckUnqualifiedColumns();
            foreach (var scope in _scopes.Where(s => s.SelectIndex >= 0)) CheckGroupBy(scope);
        }

        private static int[] MatchParentheses(List<SqlToken> tokens)
        {
            var match = Enumerable.Repeat(-1, tokens.Count).ToArray();
            var open = new Stack<int>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == SqlTokenKind.OpenParen) open.Push(i);
                else if (tokens[i].Kind == SqlTokenKind.CloseParen && open.Count > 0)
                {
                    var o = open.Pop();
                    match[o] = i;
                    match[i] = o;
                }
            }
            return match;
        }

        private Scope NewScope(Scope? parent)
        {
            var scope = new Scope(parent);
            _scopes.Add(scope);
            return scope;
        }

        private bool Is(int i, string word) => i >= 0 && i < _n && t[i].Is(word);
        private bool KindAt(int i, SqlTokenKind kind) => i >= 0 && i < _n && t[i].Kind == kind;
        private bool IsOperator(int i, string op) => KindAt(i, SqlTokenKind.Operator) && t[i].Text == op;

        private static bool IsReserved(SqlToken token) =>
            token.Kind == SqlTokenKind.Word && SqlCompletionEngine.KeywordSet.Contains(token.Text);

        private static bool IsKeyword(SqlToken token) =>
            token.Kind == SqlTokenKind.Word && (SqlCompletionEngine.KeywordSet.Contains(token.Text) || NotColumns.Contains(token.Text));

        // ----- Pass 1: scopes, clauses and row sources -----

        private void Walk()
        {
            _current = NewScope(null);
            var frames = new Stack<(bool IsScope, Scope Outer, int Depth)>();
            var depth = 0;
            var cases = 0;

            for (var i = 0; i < _n; i++)
            {
                var token = t[i];
                if (frames.Count == 0 && StartsNewRoot(i, cases))
                {
                    _current = NewScope(null);
                    depth = 0;
                }

                if (token.Kind == SqlTokenKind.OpenParen)
                {
                    Mark(i, depth);
                    if (Is(i + 1, "SELECT") || Is(i + 1, "WITH"))
                    {
                        frames.Push((true, _current, depth));
                        // A CTE body or a derived table cannot see the outer query's tables.
                        var independent = Is(i - 1, "AS") || Is(i - 1, "FROM") || Is(i - 1, "JOIN") ||
                                          (KindAt(i - 1, SqlTokenKind.Comma) && _current.Clause == "FROM");
                        _current = NewScope(independent ? null : _current);
                        depth = 0;
                    }
                    else
                    {
                        frames.Push((false, _current, depth));
                        depth++;
                    }
                    continue;
                }
                if (token.Kind == SqlTokenKind.CloseParen)
                {
                    if (frames.Count > 0)
                    {
                        var frame = frames.Pop();
                        _current = frame.Outer;
                        depth = frame.Depth;
                    }
                    Mark(i, depth);
                    continue;
                }

                if (token.Is("CASE")) cases++;
                else if (token.Is("END") && cases > 0) cases--;

                if (depth == 0 && token.Kind == SqlTokenKind.Word) OnClauseWord(i);
                else if (depth == 0 && token.Kind == SqlTokenKind.Comma && _current.Clause == "FROM") ParseSource(i + 1);
                Mark(i, depth);
            }
        }

        private void Mark(int i, int depth)
        {
            _scopeOf[i] = _current;
            _clauseOf[i] = _current.Clause;
            _depthOf[i] = depth;
        }

        /// <summary>A new statement inside the same range: after ';', or a statement keyword at the top level
        /// (T-SQL batches often have no semicolons between the statements of a BEGIN … END block).</summary>
        private bool StartsNewRoot(int i, int cases)
        {
            if (i == 0) return false;
            if (t[i - 1].Kind == SqlTokenKind.Semicolon) return true;
            var token = t[i];
            if (token.Kind != SqlTokenKind.Word) return false;
            var previous = t[i - 1];
            switch (token.Text.ToUpperInvariant())
            {
                case "BEGIN" or "IF" or "WHILE" or "RETURN" or "PRINT" or "DECLARE" or "EXEC" or "EXECUTE":
                    return true;
                case "END" or "ELSE":
                    return cases == 0;
                case "SELECT":
                    return !(previous.Is("UNION") || previous.Is("ALL") || previous.Is("EXCEPT") || previous.Is("INTERSECT") ||
                             previous.Is("DISTINCT") || previous.Is("MINUS"));
                case "UPDATE":
                    return !(previous.Is("FOR") || previous.Is("DO") || previous.Is("THEN") || previous.Is("KEY") || previous.Is("ON") ||
                             Is(i + 1, "SET"));
                case "INSERT" or "DELETE":
                    return !(previous.Is("THEN") || previous.Is("ON") || previous.Is("INSTEAD") || previous.Is("FOR") ||
                             previous.Is("OF") || previous.Kind == SqlTokenKind.Comma);
                case "WITH":
                    return i + 1 < _n && (t[i + 1].Kind == SqlTokenKind.QuotedIdentifier || Is(i + 1, "RECURSIVE") ||
                                          (t[i + 1].Kind == SqlTokenKind.Word && !IsKeyword(t[i + 1])));
                case "MERGE":
                    return true;
                default:
                    return false;
            }
        }

        private void OnClauseWord(int i)
        {
            var scope = _current;
            switch (t[i].Text.ToUpperInvariant())
            {
                case "UNION" or "EXCEPT" or "INTERSECT" or "MINUS":
                    _current = NewScope(scope.Parent);
                    return;
                case "SELECT":
                    scope.Clause = "SELECT";
                    if (scope.SelectIndex < 0) scope.SelectIndex = i;
                    return;
                case "FROM":
                    if (Is(i - 1, "DISTINCT")) return; // IS [NOT] DISTINCT FROM
                    scope.Clause = "FROM";
                    ParseSource(i + 1);
                    return;
                case "JOIN" or "APPLY":
                    scope.Clause = "FROM";
                    ParseSource(i + 1);
                    return;
                case "NATURAL":
                    scope.MergesColumns = true;
                    return;
                case "USING":
                    if (KindAt(i + 1, SqlTokenKind.OpenParen)) scope.MergesColumns = true;
                    else ParseSource(i + 1); // DELETE … USING other (PostgreSQL), MERGE … USING source
                    scope.Clause = "FROM";
                    return;
                case "ON":
                    if (Is(i - 1, "DISTINCT")) return; // SELECT DISTINCT ON (…)
                    if (Is(i + 1, "CONFLICT")) scope.Clause = "CONFLICT";
                    else if (scope.Clause is "FROM" or "ON") scope.Clause = "ON";
                    else scope.Clause = "OTHER";
                    return;
                case "WHERE":
                    scope.Clause = "WHERE";
                    return;
                case "GROUP":
                    if (Is(i - 1, "WITHIN")) return;
                    scope.Clause = "GROUP";
                    if (scope.GroupIndex < 0) scope.GroupIndex = i;
                    return;
                case "HAVING":
                    scope.Clause = "HAVING";
                    return;
                case "ORDER":
                    scope.Clause = "ORDER";
                    return;
                case "LIMIT" or "OFFSET" or "FETCH" or "FOR" or "OPTION":
                    scope.Clause = "LIMIT";
                    return;
                case "WINDOW" or "VALUES" or "RETURNING" or "OUTPUT":
                    scope.Clause = t[i].Text.ToUpperInvariant();
                    return;
                case "SET":
                    scope.Clause = scope.Clause == "UPDATE" ? "SET" : "OTHER";
                    return;
                case "INTO":
                    var insert = Is(i - 1, "INSERT") || Is(i - 1, "MERGE");
                    scope.Clause = "INTO";
                    if (insert) ParseSource(i + 1, insertTarget: true);
                    return;
                case "INSERT":
                    scope.Clause = "INSERT";
                    if (!Is(i + 1, "INTO")) ParseSource(i + 1, insertTarget: true);
                    return;
                case "UPDATE":
                    if (Is(i + 1, "SET") || Is(i - 1, "FOR") || Is(i - 1, "DO") || Is(i - 1, "THEN") || Is(i - 1, "KEY"))
                    {
                        scope.Clause = "OTHER";
                        return;
                    }
                    scope.Clause = "UPDATE";
                    ParseSource(i + 1, updateTarget: true);
                    return;
                case "DELETE":
                    scope.Clause = "DELETE";
                    // DELETE alias FROM … (SQL Server): the name is an alias, checked through the FROM clause.
                    if (i + 1 < _n && t[i + 1].IsIdentifier && !Is(i + 1, "FROM")) _isName[i + 1] = true;
                    return;
                case "MERGE":
                    scope.Opaque = true;
                    scope.Clause = "OTHER";
                    return;
                case "BEGIN" or "IF" or "WHILE" or "RETURN" or "PRINT" or "DECLARE" or "EXEC" or "EXECUTE" or "ELSE" or "END" or "WITH":
                    if (scope.Clause is "" or "OTHER") scope.Clause = "OTHER";
                    return;
            }
        }

        /// <summary>A row source after FROM, JOIN, a FROM-list comma, UPDATE or INSERT INTO: its name, alias, and whether
        /// the catalog describes it.</summary>
        private void ParseSource(int j, bool insertTarget = false, bool updateTarget = false)
        {
            var scope = _current;
            while (Is(j, "LATERAL") || Is(j, "ONLY")) j++;
            if (j >= _n) return;

            if (t[j].Kind == SqlTokenKind.OpenParen)
            {
                scope.Opaque = true; // derived table, VALUES list or nested join
                return;
            }

            var start = j;
            var parts = new List<string>();
            if (t[j].Kind == SqlTokenKind.Variable)
            {
                parts.Add(t[j].Text);
                _isName[j] = true;
                j++;
            }
            else
            {
                // A keyword here is half-typed SQL or syntax the checks do not follow: describe nothing.
                if (!t[j].IsIdentifier || IsReserved(t[j]))
                {
                    scope.Opaque = true;
                    return;
                }
                while (true)
                {
                    parts.Add(t[j].Identifier);
                    _isName[j] = true;
                    if (KindAt(j + 1, SqlTokenKind.Dot) && j + 2 < _n && t[j + 2].IsIdentifier)
                    {
                        _isName[j + 1] = true;
                        j += 2;
                        continue;
                    }
                    j++;
                    break;
                }
                // "FROM sales." while typing, or db..table: leave it to completion.
                if (KindAt(j, SqlTokenKind.Dot))
                {
                    scope.Opaque = true;
                    return;
                }
            }
            var end = t[j - 1].End;

            var function = !insertTarget && KindAt(j, SqlTokenKind.OpenParen);
            var variable = t[start].Kind == SqlTokenKind.Variable;
            var obj = variable || function || parts.Count > 3 ? null : owner.Resolve(parts);
            var columnList = insertTarget && KindAt(j, SqlTokenKind.OpenParen) ? j : -1;

            var source = new Source
            {
                Object = obj, Name = parts[^1], Parts = parts, Start = t[start].Start, End = end, Scope = scope,
                IsInsertTarget = insertTarget, IsUpdateTarget = updateTarget, ColumnListParen = columnList,
                IsOpaque = variable || function || t[start].Text.StartsWith('#')
            };

            // Alias: [AS] name, not a keyword. A function's alias follows its closing parenthesis.
            var a = function && _match[j] > j ? _match[j] + 1 : j;
            if (Is(a, "AS")) a++;
            if (columnList < 0 && a < _n && t[a].IsIdentifier && !NotAnAlias.Contains(t[a].Text) && !IsReserved(t[a]))
            {
                source.Alias = t[a].Identifier;
                _isName[a] = true;
            }

            if (obj is null) scope.Opaque = true;
            scope.Sources.Add(source);
            _sources.Add(source);
        }

        // ----- Names the statement defines -----

        private void CollectKnownNames()
        {
            foreach (var s in _sources)
            {
                _known.Add(s.Name);
                if (s.Alias is not null) _known.Add(s.Alias);
            }
            foreach (var cte in SqlCompletionEngine.ParseCtes(text[t[0].Start..t[^1].End])) _known.Add(cte);
            foreach (var special in new[] { "EXCLUDED", "INSERTED", "DELETED", "NEW", "OLD" }) _known.Add(special);

            for (var i = 0; i < _n; i++)
            {
                // ) [AS] alias — derived tables and table functions; AS name — select aliases, CTE column lists.
                if (t[i].Is("AS") && i + 1 < _n && t[i + 1].IsIdentifier) _known.Add(t[i + 1].Identifier);
                else if (t[i].Kind == SqlTokenKind.CloseParen && i + 1 < _n && t[i + 1].IsIdentifier && !IsKeyword(t[i + 1]))
                    _known.Add(t[i + 1].Identifier);
                // SQL Server: SELECT Total = SUM(x)
                else if (_clauseOf[i] == "SELECT" && _depthOf[i] == 0 && t[i].IsIdentifier && IsOperator(i + 1, "=") &&
                         (Is(i - 1, "SELECT") || KindAt(i - 1, SqlTokenKind.Comma)))
                    _known.Add(t[i].Identifier);
                else if (t[i].IsIdentifier && !IsKeyword(t[i]) && i > 0 && EndsExpression(i - 1) && _clauseOf[i] == "SELECT")
                    _known.Add(t[i].Identifier);
            }
            // WITH name (col, col) AS: the column names are known too.
            for (var i = 0; i + 1 < _n; i++)
            {
                if (t[i].Kind != SqlTokenKind.OpenParen || _match[i] < 0 || !Is(_match[i] + 1, "AS") || !KindAt(_match[i] + 2, SqlTokenKind.OpenParen)) continue;
                for (var k = i + 1; k < _match[i]; k++)
                    if (t[k].IsIdentifier) _known.Add(t[k].Identifier);
            }
        }

        /// <summary>The token closes an expression, so an identifier right after it is an alias (SELECT a.x total).</summary>
        private bool EndsExpression(int i)
        {
            var p = t[i];
            switch (p.Kind)
            {
                case SqlTokenKind.Number:
                    return !Is(i - 1, "TOP");
                case SqlTokenKind.String or SqlTokenKind.QuotedIdentifier:
                    return true;
                case SqlTokenKind.CloseParen:
                    return !(_match[i] > 0 && Is(_match[i] - 1, "TOP"));
                case SqlTokenKind.Word:
                    return p.Is("END") || p.Is("NULL") || p.Is("TRUE") || p.Is("FALSE") || !IsKeyword(p);
                default:
                    return false;
            }
        }

        // ----- Tables -----

        private void CheckTables()
        {
            foreach (var s in _sources)
            {
                if (s.Object is not null || s.IsOpaque || !IsCheckableTableName(s)) continue;

                var typedSchema = s.Parts.Count >= 2 ? s.Parts[^2] : null;
                var candidates = owner._tableLike.Where(o => s.Parts.Count < 3 || Same(o.Database, s.Parts[^3])).ToList();
                // Same schema first: "sales.Ord" means a sales table.
                var sameSchema = typedSchema is null ? [] : candidates.Where(o => Same(o.Schema, typedSchema)).ToList();
                var names = Closest(s.Name, sameSchema.Select(o => o.Name));
                if (names.Count == 0) names = Closest(s.Name, candidates.Select(o => o.Name));
                var fixes = new List<SqlQuickFix>();
                foreach (var name in names)
                {
                    var match = candidates.Where(o => Same(o.Name, name))
                        .OrderBy(o => typedSchema is not null && Same(o.Schema, typedSchema) ? 0 : 1)
                        .ThenBy(o => owner.IsDefaultSchema(o.Schema) ? 0 : 1)
                        .First();
                    var replacement = typedSchema is null && owner.IsDefaultSchema(match.Schema)
                        ? owner.QuoteIfNeeded(match.Name)
                        : owner.QuoteIfNeeded(match.Schema) + "." + owner.QuoteIfNeeded(match.Name);
                    if (s.Parts.Count >= 3) replacement = owner.QuoteIfNeeded(s.Parts[^3]) + "." + replacement;
                    fixes.Add(new SqlQuickFix($"Change to {replacement}", s.Start, s.End - s.Start, replacement));
                }

                var written = text[s.Start..s.End];
                result.Add(new SqlInspection(s.Start, s.End - s.Start, InspectionSeverity.Error,
                    $"Table {written} not found" + (names.Count > 0 ? $". Did you mean {string.Join(", ", names)}?" : "."), fixes));
            }
        }

        /// <summary>Unresolved names are only reported when the catalog should know them: not temp tables, CTEs, aliases,
        /// system catalogs, tables the script creates, or schemas and databases that were not loaded.</summary>
        private bool IsCheckableTableName(Source s)
        {
            if (s.Parts.Count > 3) return false;
            if (s.Parts.Count == 3 && !owner._databases.Contains(s.Parts[0])) return false;
            if (s.Parts.Count >= 2 && !owner._schemas.Contains(s.Parts[^2])) return false;
            var name = s.Name;
            if (name.StartsWith('#') || name.StartsWith('@') || created.Contains(name)) return false;
            if (s.Parts.Count == 1)
            {
                if (name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sys", StringComparison.OrdinalIgnoreCase) ||
                    Same(name, "dual") || Same(name, "information_schema")) return false;
                // A CTE, or UPDATE alias SET … FROM Orders alias.
                if (_known.Contains(name) && _sources.Any(o => !ReferenceEquals(o, s) && Same(o.Alias, name))) return false;
                if (SqlCompletionEngine.ParseCtes(text[t[0].Start..t[^1].End]).Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        // ----- alias.column -----

        private void CheckQualifiedColumns()
        {
            for (var i = 0; i + 2 < _n; i++)
            {
                if (!t[i].IsIdentifier || t[i + 1].Kind != SqlTokenKind.Dot || _isName[i]) continue;
                if (KindAt(i - 1, SqlTokenKind.Dot) || KindAt(i + 3, SqlTokenKind.Dot) || KindAt(i + 3, SqlTokenKind.OpenParen)) continue;
                if (IsOperator(i - 1, "::") || !(_clauseOf[i] is "SELECT" or "WHERE" or "ON" or "GROUP" or "HAVING" or "ORDER" or "SET")) continue;
                var member = t[i + 2];
                var star = member.Kind == SqlTokenKind.Operator && member.Text == "*";
                if (!member.IsIdentifier && !star) continue;

                var qualifier = t[i].Identifier;
                var bound = SourcesFor(qualifier, _scopeOf[i]);
                if (bound.Count == 0)
                {
                    if (_sources.Any(s => s.Answers(qualifier)) || _known.Contains(qualifier) || owner._schemas.Contains(qualifier) ||
                        owner._databases.Contains(qualifier)) continue;
                    // Nothing to resolve against yet: the FROM clause is still being written.
                    if (VisibleSources(_scopeOf[i]).Count == 0) continue;
                    ReportUnknownQualifier(i, qualifier, star ? null : member.Identifier);
                    continue;
                }
                if (star) continue;

                var source = bound[0];
                if (source.Object is not { } obj || owner.ColumnsOf(obj).Count == 0 || owner.HasColumn(obj, member.Identifier)) continue;
                ReportUnknownColumn(i + 2, member.Identifier, obj, prefix: "");
            }
        }

        /// <summary>The row sources <paramref name="qualifier"/> names, from the innermost query outwards.</summary>
        private List<Source> SourcesFor(string qualifier, Scope? scope)
        {
            for (; scope is not null; scope = scope.Parent)
            {
                var found = scope.Sources.Where(s => s.Answers(qualifier)).ToList();
                if (found.Count > 0) return found;
            }
            return [];
        }

        private static List<Source> VisibleSources(Scope? scope)
        {
            var visible = new List<Source>();
            for (; scope is not null; scope = scope.Parent) visible.AddRange(scope.Sources);
            return visible;
        }

        private void ReportUnknownQualifier(int i, string qualifier, string? column)
        {
            var visible = VisibleSources(_scopeOf[i]);
            var fixes = new List<SqlQuickFix>();
            // Prefer the tables that have the column; otherwise the aliases that look alike.
            var having = column is null ? [] : visible.Where(s => s.Object is { } o && owner.HasColumn(o, column)).Select(s => s.Qualifier).ToList();
            var choices = having.Count > 0 ? having : Closest(qualifier, visible.Select(s => s.Qualifier)).ToList();
            foreach (var q in choices.Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
                fixes.Add(new SqlQuickFix($"Change to {q}", t[i].Start, t[i].Length, owner.QuoteIfNeeded(q)));
            result.Add(new SqlInspection(t[i].Start, t[i].Length, InspectionSeverity.Error,
                $"{qualifier} is not a table or alias of this query", fixes));
        }

        private void ReportUnknownColumn(int i, string name, DbObject obj, string prefix)
        {
            var suggestions = Closest(name, owner.ColumnsOf(obj).Select(c => c.Name));
            var fixes = suggestions
                .Select(c => new SqlQuickFix($"Change to {prefix}{owner.QuoteIfNeeded(c)}", t[i].Start, t[i].Length, prefix + owner.QuoteIfNeeded(c)))
                .ToList();
            result.Add(new SqlInspection(t[i].Start, t[i].Length, InspectionSeverity.Error,
                $"Column {name} not found in {obj.FullName}" + (suggestions.Count > 0 ? $". Did you mean {string.Join(", ", suggestions)}?" : "."), fixes));
        }

        // ----- INSERT column lists and UPDATE … SET targets -----

        private void CheckTargetColumns()
        {
            foreach (var s in _sources.Where(s => s.IsInsertTarget && s.ColumnListParen >= 0 && s.Object is not null))
            {
                if (owner.ColumnsOf(s.Object!).Count == 0) continue;
                var close = _match[s.ColumnListParen];
                if (close < 0) continue;
                for (var k = s.ColumnListParen + 1; k < close; k++)
                {
                    if (!t[k].IsIdentifier || KindAt(k + 1, SqlTokenKind.Dot) || KindAt(k - 1, SqlTokenKind.Dot)) continue;
                    _isName[k] = true;
                    if (!owner.HasColumn(s.Object!, t[k].Identifier)) ReportUnknownColumn(k, t[k].Identifier, s.Object!, prefix: "");
                }
            }

            for (var i = 0; i < _n; i++)
            {
                if (_clauseOf[i] != "SET" || _depthOf[i] != 0 || !t[i].IsIdentifier || !IsOperator(i + 1, "=")) continue;
                if (!(Is(i - 1, "SET") || KindAt(i - 1, SqlTokenKind.Comma))) continue;
                _isName[i] = true;
                var target = UpdateTarget(_scopeOf[i]);
                if (target?.Object is not { } obj || owner.ColumnsOf(obj).Count == 0 || owner.HasColumn(obj, t[i].Identifier)) continue;
                ReportUnknownColumn(i, t[i].Identifier, obj, prefix: "");
            }
        }

        /// <summary>The table an UPDATE changes; for UPDATE alias SET … FROM Orders alias, the aliased table.</summary>
        private Source? UpdateTarget(Scope scope)
        {
            var target = scope.Sources.FirstOrDefault(s => s.IsUpdateTarget);
            if (target is null) return null;
            if (target.Object is null && target.Parts.Count == 1)
                return scope.Sources.FirstOrDefault(s => !s.IsUpdateTarget && Same(s.Alias, target.Name));
            return target;
        }

        /// <summary>The sources an unqualified column can come from: an UPDATE target that is repeated in its own FROM
        /// clause, or that only names an alias, counts once.</summary>
        private static List<Source> ColumnSources(Scope scope) =>
            scope.Sources.Where(s => !s.IsInsertTarget && !(s.IsUpdateTarget && scope.Sources.Any(o => !ReferenceEquals(o, s) &&
                (Same(o.Alias, s.Name) || (o.Object is not null && ReferenceEquals(o.Object, s.Object) && o.Alias is null && s.Alias is null))))).ToList();

        // ----- Unqualified columns: unknown or ambiguous -----

        private void CheckUnqualifiedColumns()
        {
            for (var i = 0; i < _n; i++)
            {
                if (!(_clauseOf[i] is "SELECT" or "WHERE" or "ON" or "GROUP" or "HAVING" or "SET") || !IsColumnCandidate(i)) continue;
                var name = t[i].Identifier;
                var scope = _scopeOf[i];

                var here = ColumnSources(scope);
                var owners = here.Where(s => s.Object is { } o && owner.HasColumn(o, name)).ToList();
                if (owners.Count >= 2 && !scope.MergesColumns)
                {
                    ReportAmbiguous(i, name, owners);
                    continue;
                }
                if (owners.Count == 1 || _known.Contains(name)) continue;
                if (!FullyKnown(scope)) continue;

                var visible = Visible(scope);
                if (visible.Any(s => owner.HasColumn(s.Object!, name))) continue;

                var columns = visible.SelectMany(s => owner.ColumnsOf(s.Object!).Select(c => (Source: s, Column: c.Name))).ToList();
                var suggestions = Closest(name, columns.Select(c => c.Column));
                var fixes = new List<SqlQuickFix>();
                foreach (var suggestion in suggestions)
                {
                    var from = columns.Where(c => Same(c.Column, suggestion)).Select(c => c.Source).Distinct().ToList();
                    var replacement = (from.Count > 1 ? owner.QuoteIfNeeded(from[0].Qualifier) + "." : "") + owner.QuoteIfNeeded(suggestion);
                    fixes.Add(new SqlQuickFix($"Change to {replacement}", t[i].Start, t[i].Length, replacement));
                }
                var tables = string.Join(", ", visible.Select(s => s.Object!.FullName).Distinct(StringComparer.OrdinalIgnoreCase));
                result.Add(new SqlInspection(t[i].Start, t[i].Length, InspectionSeverity.Error,
                    $"Column {name} not found in {tables}" + (suggestions.Count > 0 ? $". Did you mean {string.Join(", ", suggestions)}?" : "."), fixes));
            }
        }

        private static List<Source> Visible(Scope scope)
        {
            var visible = new List<Source>();
            for (Scope? s = scope; s is not null; s = s.Parent) visible.AddRange(ColumnSources(s));
            return visible;
        }

        /// <summary>Every table this query and the queries around it read is in the catalog with its columns.</summary>
        private bool FullyKnown(Scope scope)
        {
            var any = false;
            for (var s = scope; s is not null; s = s.Parent)
            {
                if (s.Opaque) return false;
                foreach (var source in ColumnSources(s))
                {
                    if (source.Object is not { } o || source.IsOpaque || owner.ColumnsOf(o).Count == 0) return false;
                    any = true;
                }
            }
            return any;
        }

        private void ReportAmbiguous(int i, string name, List<Source> owners)
        {
            var fixes = owners
                .Select(s => owner.QuoteIfNeeded(s.Qualifier) + "." + t[i].Text)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(q => new SqlQuickFix($"Change to {q}", t[i].Start, t[i].Length, q))
                .ToList();
            var tables = string.Join(" and ", owners.Select(s => s.Alias is null ? s.Object!.FullName : $"{s.Alias} ({s.Object!.FullName})"));
            result.Add(new SqlInspection(t[i].Start, t[i].Length, InspectionSeverity.Warning,
                $"Ambiguous column {name}: it is in {tables}", fixes));
        }

        /// <summary>A bare name in an expression that can only be a column (not a keyword, function, type, alias or part of a
        /// dotted name).</summary>
        private bool IsColumnCandidate(int i)
        {
            var token = t[i];
            if (!token.IsIdentifier || _isName[i] || IsKeyword(token)) return false;
            if (KindAt(i + 1, SqlTokenKind.Dot) || KindAt(i + 1, SqlTokenKind.OpenParen) || KindAt(i + 1, SqlTokenKind.String)) return false;
            if (i == 0) return true;
            var p = t[i - 1];
            if (p.Kind == SqlTokenKind.Dot || p.Is("AS") || p.Is("COLLATE") || IsOperator(i - 1, "::")) return false;
            if (EndsExpression(i - 1)) return false;
            if (p.Kind == SqlTokenKind.OpenParen && i >= 2 && t[i - 2].Kind == SqlTokenKind.Word && FirstArgumentIsKeyword.Contains(t[i - 2].Text)) return false;
            // SQL Server: SELECT Total = SUM(x) names the result column.
            if (_clauseOf[i] == "SELECT" && _depthOf[i] == 0 && IsOperator(i + 1, "=") && (p.Is("SELECT") || p.Kind == SqlTokenKind.Comma)) return false;
            return true;
        }

        // ----- GROUP BY -----

        private void CheckGroupBy(Scope scope)
        {
            var items = SelectItems(scope);
            if (items.Count == 0) return;
            var hasGroupBy = scope.GroupIndex >= 0;
            if (!hasGroupBy && !items.Any(item => HasAggregate(item))) return;

            var groupItems = hasGroupBy ? GroupItems(scope) : [];
            if (groupItems.Any(g => g.Count == 1 && t[g[0]].Kind == SqlTokenKind.Number)) return; // GROUP BY 1, 2
            if (groupItems.Any(g => g.Any(k => t[k].Is("ALL")))) return;
            var groupRefs = groupItems.SelectMany(g => ColumnRefs(g)).ToList();
            var groupTexts = groupItems.Select(Normalized).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groupNames = groupItems.Where(g => g.Count == 1 && t[g[0]].IsIdentifier).Select(g => t[g[0]].Identifier)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = new List<ColumnRef>();
            foreach (var raw in items)
            {
                var (item, alias) = WithoutAlias(raw);
                if (item.Count == 0 || item.Any(k => IsOperator(k, "*") && item.Count == 1)) continue;
                if (groupTexts.Contains(Normalized(item)) || (alias is not null && groupNames.Contains(alias))) continue;
                foreach (var r in ColumnRefs(item, skipAggregates: true))
                    if (!IsGrouped(r, groupRefs, scope)) missing.Add(r);
            }
            if (missing.Count == 0) return;

            var texts = missing.Select(RefText).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var r in missing)
            {
                var refText = RefText(r);
                var fixes = new List<SqlQuickFix>();
                if (hasGroupBy)
                {
                    var end = LastIndex(scope, k => _clauseOf[k] == "GROUP");
                    fixes.Add(new SqlQuickFix($"Add {refText} to GROUP BY", t[end].End, 0, ", " + refText));
                    if (texts.Count > 1)
                        fixes.Add(new SqlQuickFix($"Add all {texts.Count} missing columns to GROUP BY", t[end].End, 0, ", " + string.Join(", ", texts)));
                    result.Add(new SqlInspection(r.Start, r.End - r.Start, InspectionSeverity.Error,
                        $"{refText} is neither in GROUP BY nor inside an aggregate such as MAX()", fixes));
                }
                else
                {
                    var end = LastIndex(scope, k => _clauseOf[k] is "SELECT" or "FROM" or "ON" or "WHERE");
                    var clause = "GROUP BY " + string.Join(", ", texts);
                    fixes.Add(new SqlQuickFix($"Add {clause}", t[end].End, 0, Separator(scope.SelectIndex, end) + clause));
                    result.Add(new SqlInspection(r.Start, r.End - r.Start, InspectionSeverity.Error,
                        $"{refText} needs a GROUP BY: the query also uses an aggregate", fixes));
                }
            }
        }

        /// <summary>A new line indented like the SELECT when the query spans lines; otherwise a space.</summary>
        private string Separator(int selectIndex, int end)
        {
            var from = t[selectIndex].Start;
            if (text.IndexOf('\n', from, t[end].End - from) < 0) return " ";
            var lineStart = text.LastIndexOf('\n', Math.Max(0, from - 1)) + 1;
            var indent = text[lineStart..from];
            return "\n" + (indent.All(char.IsWhiteSpace) ? indent : "");
        }

        private int LastIndex(Scope scope, Func<int, bool> clause)
        {
            var last = -1;
            for (var k = 0; k < _n; k++)
                if (ReferenceEquals(_scopeOf[k], scope) && t[k].Kind != SqlTokenKind.Semicolon && clause(k)) last = k;
            return last;
        }

        private List<List<int>> SelectItems(Scope scope)
        {
            var items = new List<List<int>>();
            var item = new List<int>();
            var start = scope.SelectIndex + 1;
            while (start < _n && (Is(start, "DISTINCT") || Is(start, "ALL"))) start++;
            if (Is(start, "TOP"))
            {
                start++;
                if (KindAt(start, SqlTokenKind.OpenParen) && _match[start] > start) start = _match[start] + 1;
                else start++;
                while (Is(start, "PERCENT") || Is(start, "WITH") || Is(start, "TIES")) start++;
            }
            if (Is(start, "ON") && KindAt(start + 1, SqlTokenKind.OpenParen) && _match[start + 1] > start) start = _match[start + 1] + 1; // DISTINCT ON (…)

            for (var k = start; k < _n; k++)
            {
                if (!ReferenceEquals(_scopeOf[k], scope)) continue; // inside a subquery
                if (_clauseOf[k] != "SELECT") break;
                if (t[k].Kind == SqlTokenKind.Comma && _depthOf[k] == 0)
                {
                    items.Add(item);
                    item = [];
                    continue;
                }
                item.Add(k);
            }
            if (item.Count > 0) items.Add(item);
            return items;
        }

        private List<List<int>> GroupItems(Scope scope)
        {
            var items = new List<List<int>>();
            var item = new List<int>();
            for (var k = scope.GroupIndex + 1; k < _n; k++)
            {
                if (!ReferenceEquals(_scopeOf[k], scope)) continue;
                if (_clauseOf[k] != "GROUP" || t[k].Kind == SqlTokenKind.Semicolon) break;
                if (t[k].Is("BY") && k == scope.GroupIndex + 1) continue;
                if (t[k].Kind == SqlTokenKind.Comma && _depthOf[k] == 0)
                {
                    items.Add(item);
                    item = [];
                    continue;
                }
                item.Add(k);
            }
            if (item.Count > 0) items.Add(item);
            return items;
        }

        /// <summary>The select item without its alias: <c>expr AS name</c>, <c>expr name</c>, <c>name = expr</c>.</summary>
        private (List<int> Item, string? Alias) WithoutAlias(List<int> item)
        {
            if (item.Count >= 2 && t[item[0]].IsIdentifier && IsOperator(item[1], "=")) return (item.Skip(2).ToList(), t[item[0]].Identifier);
            var asIndex = item.FindIndex(k => t[k].Is("AS") && _depthOf[k] == 0);
            if (asIndex >= 0) return (item.Take(asIndex).ToList(), asIndex + 1 < item.Count ? t[item[asIndex + 1]].Identifier : null);
            if (item.Count >= 2 && t[item[^1]].IsIdentifier && !IsKeyword(t[item[^1]]) && EndsExpression(item[^2]) && item[^2] == item[^1] - 1)
                return (item.Take(item.Count - 1).ToList(), t[item[^1]].Identifier);
            return (item, null);
        }

        private bool HasAggregate(List<int> item)
        {
            foreach (var k in item)
                if (t[k].Kind == SqlTokenKind.Word && Aggregates.Contains(t[k].Text) && KindAt(k + 1, SqlTokenKind.OpenParen) &&
                    _match[k + 1] > k && !Is(_match[k + 1] + 1, "OVER"))
                    return true;
            return false;
        }

        /// <summary>Column references in the tokens, optionally skipping aggregate calls and window functions.</summary>
        private List<ColumnRef> ColumnRefs(List<int> tokens, bool skipAggregates = false)
        {
            var refs = new List<ColumnRef>();
            var set = tokens.ToHashSet();
            var skipUntil = -1;
            foreach (var k in tokens)
            {
                if (k <= skipUntil) continue;
                if (skipAggregates && t[k].Kind == SqlTokenKind.Word && KindAt(k + 1, SqlTokenKind.OpenParen) && _match[k + 1] > k)
                {
                    var close = _match[k + 1];
                    var aggregate = Aggregates.Contains(t[k].Text);
                    // FILTER (…), WITHIN GROUP (…), OVER (…) belong to the call.
                    var after = close + 1;
                    while (true)
                    {
                        if (Is(after, "FILTER") && KindAt(after + 1, SqlTokenKind.OpenParen) && _match[after + 1] > after) after = _match[after + 1] + 1;
                        else if (Is(after, "WITHIN") && Is(after + 1, "GROUP") && KindAt(after + 2, SqlTokenKind.OpenParen) && _match[after + 2] > after) after = _match[after + 2] + 1;
                        else break;
                    }
                    if (Is(after, "OVER"))
                    {
                        skipUntil = KindAt(after + 1, SqlTokenKind.OpenParen) && _match[after + 1] > after ? _match[after + 1] : after + 1;
                        continue;
                    }
                    if (aggregate)
                    {
                        skipUntil = after - 1;
                        continue;
                    }
                }
                if (!t[k].IsIdentifier || !set.Contains(k)) continue;

                if (KindAt(k + 1, SqlTokenKind.Dot) && k + 2 < _n && t[k + 2].IsIdentifier && !KindAt(k - 1, SqlTokenKind.Dot) &&
                    !KindAt(k + 3, SqlTokenKind.Dot) && !KindAt(k + 3, SqlTokenKind.OpenParen))
                {
                    skipUntil = k + 2;
                    // A misspelt column is reported as unknown; asking to group by it as well would only add noise.
                    if (SourcesFor(t[k].Identifier, _scopeOf[k]) is [{ Object: { } table }, ..] && owner.ColumnsOf(table).Count > 0 &&
                        !owner.HasColumn(table, t[k + 2].Identifier)) continue;
                    refs.Add(new ColumnRef(t[k].Identifier, t[k + 2].Identifier, t[k].Start, t[k + 2].End, k + 2));
                    continue;
                }
                if (!IsColumnCandidate(k) || _known.Contains(t[k].Identifier)) continue;
                // When the tables are all known, only their real columns count (typos are reported on their own).
                if (FullyKnown(_scopeOf[k]) && !Visible(_scopeOf[k]).Any(s => owner.HasColumn(s.Object!, t[k].Identifier))) continue;
                refs.Add(new ColumnRef(null, t[k].Identifier, t[k].Start, t[k].End, k));
            }
            return refs;
        }

        private bool IsGrouped(ColumnRef r, List<ColumnRef> groupRefs, Scope scope)
        {
            if (groupRefs.Any(g => Same(g.Name, r.Name) && (g.Qualifier is null || r.Qualifier is null || Same(g.Qualifier, r.Qualifier)))) return true;
            if (owner._providerKey == "SqlServer") return false;

            // PostgreSQL: grouping by a table's primary key makes its other columns usable too.
            var source = r.Qualifier is not null
                ? scope.Sources.FirstOrDefault(s => s.Answers(r.Qualifier))
                : ColumnSources(scope).Where(s => s.Object is { } o && owner.HasColumn(o, r.Name)).Take(2).ToList() is [var only] ? only : null;
            if (source?.Object is not { } obj) return false;
            var key = owner.ColumnsOf(obj).Where(c => c.IsPrimaryKey).ToList();
            return key.Count > 0 && key.All(c => groupRefs.Any(g => Same(g.Name, c.Name) &&
                (g.Qualifier is null || source.Answers(g.Qualifier))));
        }

        private string RefText(ColumnRef r) => text[r.Start..r.End];

        private string Normalized(List<int> tokens)
        {
            var sb = new StringBuilder();
            foreach (var k in tokens) sb.Append(t[k].Kind == SqlTokenKind.QuotedIdentifier ? t[k].Identifier : t[k].Text);
            return sb.ToString();
        }
    }
}
