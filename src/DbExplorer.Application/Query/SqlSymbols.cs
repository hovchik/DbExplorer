using System.Text.RegularExpressions;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

/// <summary>What a name in the script stands for.</summary>
public enum SqlSymbolKind
{
    /// <summary>The alias of a table, view, function or subquery in one query (FROM Orders o).</summary>
    TableAlias,

    /// <summary>A WITH … AS ( … ) name.</summary>
    Cte,

    /// <summary>An output column named in the script: SELECT … AS name, or the column list of WITH c (a, b).</summary>
    ColumnAlias,

    /// <summary>@name (SQL Server) or :name (a parameter).</summary>
    Variable,

    /// <summary>#temp / ##global temp table (SQL Server).</summary>
    TempTable,

    /// <summary>A table, view or routine of the catalog. Found, not renamed: the database would have to change too.</summary>
    Object,

    /// <summary>A column of a catalog table or view. Found, not renamed.</summary>
    Column
}

/// <summary>One place a symbol appears: <c>Start..Start+Length</c> of the text.</summary>
public readonly record struct SqlOccurrence(int Start, int Length, bool IsDefinition)
{
    public int End => Start + Length;
}

/// <summary>A name in the script and every place that refers to the same thing.</summary>
public sealed record SqlSymbol(SqlSymbolKind Kind, string Name, IReadOnlyList<SqlOccurrence> Occurrences)
{
    /// <summary>The catalog object (<see cref="SqlSymbolKind.Object"/>) or the column's table (<see cref="SqlSymbolKind.Column"/>).</summary>
    public DbObject? Object { get; init; }

    public string? Column { get; init; }

    /// <summary>Names that would clash with this one after a rename (other aliases of the same query, …).</summary>
    internal IReadOnlySet<string> Taken { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names a rename may still use, but that would hide something (an outer alias, a table of the catalog).</summary>
    internal IReadOnlySet<string> Shadowed { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Script-only names can be renamed; catalog objects and columns can only be found.</summary>
    public bool CanRename => Kind is not (SqlSymbolKind.Object or SqlSymbolKind.Column);

    public string KindLabel => Kind switch
    {
        SqlSymbolKind.TableAlias => "alias",
        SqlSymbolKind.Cte => "CTE",
        SqlSymbolKind.ColumnAlias => "column",
        SqlSymbolKind.Variable => Name.StartsWith('@') ? "variable" : "parameter",
        SqlSymbolKind.TempTable => "temp table",
        SqlSymbolKind.Column => "column",
        _ => Object?.Type switch
        {
            DbObjectType.View => "view",
            DbObjectType.MaterializedView => "materialized view",
            DbObjectType.Procedure => "procedure",
            DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction => "function",
            DbObjectType.Synonym => "synonym",
            DbObjectType.Sequence => "sequence",
            _ => "table"
        }
    };

    /// <summary>"alias o", "column dbo.Customers.Name", "table sales.Orders".</summary>
    public string Title => Kind switch
    {
        SqlSymbolKind.Object when Object is not null => $"{KindLabel} {Object.FullName}",
        SqlSymbolKind.Column when Object is not null => $"column {Object.FullName}.{Column}",
        _ => $"{KindLabel} {Name}"
    };
}

/// <summary>The symbol at the caret, or why there is none.</summary>
public sealed record SqlSymbolLookup(SqlSymbol? Symbol, string? Reason)
{
    public static SqlSymbolLookup None(string reason) => new(null, reason);
}

/// <summary>The edits of a rename (applied from the end, so earlier offsets stay valid), or why it cannot be done.</summary>
public sealed record SqlRename(IReadOnlyList<SqlQuickFix> Edits, string? Error, string? Warning)
{
    public string NewText { get; init; } = "";

    public string ApplyTo(string text)
    {
        foreach (var edit in Edits.OrderByDescending(e => e.Start)) text = edit.ApplyTo(text);
        return text;
    }
}

/// <summary>Where a view, routine or trigger of the catalog uses an object or column.</summary>
public sealed record ModuleUsage(DbModule Module, int Start, int Length, int Line, string LineText);

/// <summary>
/// Rename and find usages in the query editor, like DataGrip's Shift+F6 and Alt+F7. The script is read the way the
/// server would bind it: each query keeps its own aliases (subqueries see the aliases of the queries around them, the
/// branches of a UNION do not see each other's), a CTE is visible from the query that defines it, and an output
/// column of a CTE or subquery is followed into the queries that read it. Nothing is sent to the server: catalog
/// objects come from the cached metadata, and "used by" searches the cached source of views, routines and triggers.
/// <para>
/// Like the live SQL warnings, it leans towards leaving a name out rather than renaming something that only looks
/// the same: a bare column is only counted when exactly one row source of its query has it.
/// </para>
/// </summary>
public sealed class SqlSymbolFinder
{
    private static readonly Regex SimpleIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex VariableName = new(@"^[@:][A-Za-z_][A-Za-z0-9_@$#]*$", RegexOptions.Compiled);
    private static readonly Regex TempTableName = new(@"^##?[A-Za-z_][A-Za-z0-9_@$#]*$", RegexOptions.Compiled);

    private readonly MetadataSnapshot? _snapshot;
    private readonly Func<string, string> _quote;
    private readonly string? _providerKey;
    private readonly ILookup<string, DbObject> _objectsByName;
    private readonly HashSet<string> _databases;

    /// <param name="snapshot">The catalog; null (not connected) still renames aliases, CTEs, columns and variables.</param>
    public SqlSymbolFinder(MetadataSnapshot? snapshot, Func<string, string> quote, string? providerKey = null)
    {
        _snapshot = snapshot;
        _quote = quote;
        _providerKey = providerKey;
        var objects = snapshot?.Objects.Where(o => o.Type is not DbObjectType.Trigger).ToList() ?? [];
        _objectsByName = objects.ToLookup(o => o.Name, StringComparer.OrdinalIgnoreCase);
        _databases = objects.Select(o => o.Database).Where(d => d.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The quoting of the connection's engine; without one, names are quoted for the script's own style.</summary>
    public static Func<string, string> QuoteFor(string? providerKey) => providerKey switch
    {
        SqlDialect.MySqlKey => id => "`" + id.Replace("`", "``") + "`",
        SqlDialect.PostgresKey or SqlDialect.SqliteKey => id => "\"" + id.Replace("\"", "\"\"") + "\"",
        _ => id => "[" + id.Replace("]", "]]") + "]"
    };

    public bool HasCatalog => _snapshot is not null;

    // ----- At the caret -----

    /// <summary>The symbol under <paramref name="offset"/> (or right before it) and all of its occurrences.</summary>
    public SqlSymbolLookup SymbolAt(string text, int offset)
    {
        var analysis = new Analysis(this, text, contextSchema: null);
        var t = analysis.T;
        var k = analysis.TokenAt(offset);
        if (k < 0) return SqlSymbolLookup.None("Put the caret on a name: an alias, CTE, column, variable or table.");
        var token = t[k];

        if (token.Kind == SqlTokenKind.Variable)
        {
            if (token.Text.StartsWith("@@", StringComparison.Ordinal))
                return SqlSymbolLookup.None($"{token.Text} is a built-in value of the server and cannot be renamed.");
            return new SqlSymbolLookup(analysis.VariableSymbol(k), null);
        }
        if (token.Kind == SqlTokenKind.Word && token.Text.StartsWith('#'))
            return new SqlSymbolLookup(analysis.TempTableSymbol(k), null);
        if (!token.IsIdentifier || (token.Kind == SqlTokenKind.Word && analysis.Roles[k] == Role.None && IsKeyword(token)))
            return SqlSymbolLookup.None("Put the caret on a name: an alias, CTE, column, variable or table.");

        return analysis.SymbolFor(k);
    }

    /// <summary>The edits that rename <paramref name="symbol"/> to <paramref name="newName"/>, or the reason they would break the script.</summary>
    public SqlRename Rename(SqlSymbol symbol, string newName)
    {
        if (!symbol.CanRename)
            return new SqlRename([], symbol.Kind == SqlSymbolKind.Column
                ? $"{symbol.Title} belongs to the database. Renaming it only in the script would break the script; change it in the Table designer."
                : $"{symbol.Title} belongs to the database. Renaming it only in the script would break the script.", null);

        var name = newName.Trim();
        string replacement;
        switch (symbol.Kind)
        {
            case SqlSymbolKind.Variable:
                var prefix = symbol.Name[..1];
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) name = prefix + name;
                if (!VariableName.IsMatch(name)) return Invalid($"{name} is not a valid name: use letters, digits and _ after {prefix}.");
                replacement = name;
                break;
            case SqlSymbolKind.TempTable:
                var hashes = symbol.Name.StartsWith("##", StringComparison.Ordinal) ? "##" : "#";
                name = hashes + name.TrimStart('#');
                if (!TempTableName.IsMatch(name)) return Invalid($"{name} is not a valid temp table name: use letters, digits and _ after {hashes}.");
                replacement = name;
                break;
            default:
                name = Unquote(name);
                if (name.Length == 0) return Invalid("Type the new name.");
                if (name.Contains('\n') || name.Contains('\r')) return Invalid("A name cannot span lines.");
                replacement = QuoteIfNeeded(name);
                break;
        }

        if (string.Equals(name, symbol.Name, StringComparison.Ordinal)) return Invalid("That is already its name.");
        if (!Same(name, symbol.Name) && symbol.Taken.Contains(name))
            return Invalid(symbol.Kind switch
            {
                SqlSymbolKind.TableAlias => $"{name} is already a table or alias of this query.",
                SqlSymbolKind.Cte => $"This query already has a CTE named {name}.",
                SqlSymbolKind.ColumnAlias => $"{name} is already a column of this query; the queries that read it could no longer tell them apart.",
                SqlSymbolKind.Variable => $"The batch already uses {name}.",
                SqlSymbolKind.TempTable => $"The script already uses {name}.",
                _ => $"{name} is already used here."
            });
        var warning = !Same(name, symbol.Name) && symbol.Shadowed.Contains(name)
            ? symbol.Kind == SqlSymbolKind.Cte
                ? $"A table named {name} exists; inside this query the CTE will be used instead of it."
                : $"{name} is also an alias of an outer query; inside this query it will mean the renamed one."
            : null;

        var title = $"Rename {symbol.KindLabel} {symbol.Name} to {name}";
        var edits = symbol.Occurrences
            .OrderByDescending(o => o.Start)
            .Select(o => new SqlQuickFix(title, o.Start, o.Length, replacement))
            .ToList();
        return new SqlRename(edits, null, warning) { NewText = replacement };

        static SqlRename Invalid(string error) => new([], error, null);
    }

    private static string Unquote(string name)
    {
        if (name.Length >= 2 && ((name[0] == '[' && name[^1] == ']') || (name[0] == '"' && name[^1] == '"') || (name[0] == '`' && name[^1] == '`')))
            return name[1..^1];
        return name;
    }

    private string QuoteIfNeeded(string identifier) =>
        SimpleIdentifier.IsMatch(identifier) && !SqlCompletionEngine.KeywordSet.Contains(identifier) ? identifier : _quote(identifier);

    // ----- In the catalog -----

    /// <summary>
    /// The views, routines and triggers whose cached source uses <paramref name="target"/> (or its column
    /// <paramref name="column"/>), one entry per place, in object order. The object's own definition is left out.
    /// </summary>
    public IReadOnlyList<ModuleUsage> FindInModules(DbObject target, string? column = null, int max = 2000, CancellationToken ct = default)
    {
        var result = new List<ModuleUsage>();
        if (_snapshot is null) return result;
        foreach (var module in _snapshot.Modules
                     .Where(m => !string.IsNullOrEmpty(m.Definition))
                     .Where(m => target.Database.Length == 0 || m.Database.Length == 0 || Same(m.Database, target.Database))
                     .OrderBy(m => m.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (Same(module.Schema, target.Schema) && Same(module.Name, target.Name) && module.Type == target.Type) continue;
            var definition = module.Definition!;
            if (definition.IndexOf(target.Name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (column is not null && definition.IndexOf(column, StringComparison.OrdinalIgnoreCase) < 0) continue;

            var analysis = new Analysis(this, definition, module.Schema);
            var occurrences = column is null ? analysis.ObjectOccurrences(target) : analysis.ColumnOccurrences(target, column);
            foreach (var o in occurrences.OrderBy(o => o.Start))
            {
                var (line, lineText) = LineOf(definition, o.Start);
                result.Add(new ModuleUsage(module, o.Start, o.Length, line, lineText));
                if (result.Count >= max) return result;
            }
        }
        return result;
    }

    /// <summary>Foreign keys from other tables to <paramref name="target"/> and from it to others (only those on
    /// <paramref name="column"/> when given): the other side of a change to the table.</summary>
    public IReadOnlyList<DbForeignKey> ForeignKeysOf(DbObject target, string? column = null)
    {
        if (_snapshot is null || !target.IsTableLike) return [];
        var keys = _snapshot.ReferencesTo(target.Database, target.Schema, target.Name)
            .Concat(_snapshot.ForeignKeysOf(target.Database, target.Schema, target.Name))
            .Distinct()
            .ToList();
        if (column is null) return keys;
        return keys.Where(k =>
            (Same(k.Schema, target.Schema) && Same(k.Table, target.Name) && ListHas(k.Columns, column)) ||
            (Same(k.ReferencedSchema, target.Schema) && Same(k.ReferencedTable, target.Name) && ListHas(k.ReferencedColumns, column))).ToList();

        static bool ListHas(string? list, string name) =>
            list?.Split(',').Any(c => Same(c.Trim().Trim('[', ']', '"', '`'), name)) == true;
    }

    private static (int Line, string Text) LineOf(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 1;
        for (var i = 0; i < offset; i++) if (text[i] == '\n') line++;
        var start = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        if (offset == 0) start = 0;
        var end = text.IndexOf('\n', offset);
        if (end < 0) end = text.Length;
        return (line, text[start..end].Trim());
    }

    // ----- Catalog helpers -----

    private DbObject? Resolve(IReadOnlyList<string> parts, string? contextSchema)
    {
        if (parts.Count == 0 || parts.Count > 3) return null;
        var name = parts[^1];
        var schema = parts.Count >= 2 ? parts[^2] : null;
        var database = parts.Count >= 3 ? parts[^3] : null;
        var candidates = _objectsByName[name].Where(o => database is null || database.Length == 0 || o.Database.Length == 0 || Same(o.Database, database)).ToList();
        if (candidates.Count == 0) return null;
        if (!string.IsNullOrEmpty(schema)) return candidates.FirstOrDefault(o => Same(o.Schema, schema));
        return candidates.FirstOrDefault(o => contextSchema is not null && Same(o.Schema, contextSchema))
               ?? candidates.Where(o => IsDefaultSchema(o.Schema)).OrderBy(o => o.IsTableLike ? 0 : 1).FirstOrDefault()
               ?? candidates.OrderBy(o => o.IsTableLike ? 0 : 1).First();
    }

    private bool IsDefaultSchema(string schema) => _providerKey switch
    {
        SqlDialect.SqlServerKey => Same(schema, "dbo"),
        SqlDialect.PostgresKey => Same(schema, "public"),
        SqlDialect.MySqlKey => _databases.Count <= 1,
        SqlDialect.SqliteKey => Same(schema, "main"),
        _ => Same(schema, "dbo") || Same(schema, "public")
    };

    /// <summary>Yes / no / unknown: whether a catalog object has the column (unknown when its columns are not loaded).</summary>
    private bool? HasColumn(DbObject o, string column)
    {
        if (_snapshot is null) return null;
        var columns = _snapshot.ColumnsOf(o.Database, o.Schema, o.Name).ToList();
        if (columns.Count == 0) return null;
        return columns.Any(c => Same(c.Name, column));
    }

    private string? ColumnName(DbObject o, string column) =>
        _snapshot?.ColumnsOf(o.Database, o.Schema, o.Name).FirstOrDefault(c => Same(c.Name, column))?.Name;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> NotColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "TIME", "ZONE", "LOCAL", "WITHOUT", "COLLATE", "ESCAPE", "TO", "FILTER", "WITHIN", "RECURSIVE", "NULLS", "FIRST",
        "LAST", "TIES", "PERCENT", "ROLLUP", "CUBE", "GROUPING", "SETS", "PRECEDING", "FOLLOWING", "UNBOUNDED", "CURRENT",
        "ROW", "RANGE", "GROUPS", "MATCHED", "SOURCE", "TARGET", "OF", "NOWAIT", "SKIP", "LOCKED", "OPTION", "FOR", "DO",
        "NOTHING", "CONFLICT", "PARTITION", "OVER", "INTERVAL", "INSERTED", "DELETED", "NEW", "OLD", "EXCLUDED", "MAX",
        "MATERIALIZED", "LOOP", "ELSIF", "PERFORM", "QUERY", "RAISE", "NOTICE", "EXCEPTION", "LANGUAGE", "PLPGSQL", "RETURNS"
    };

    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "GROUP", "ORDER", "HAVING", "UNION",
        "EXCEPT", "INTERSECT", "MINUS", "WITH", "SET", "VALUES", "SELECT", "LIMIT", "OFFSET", "AS", "APPLY", "NATURAL",
        "USING", "WINDOW", "FOR", "OPTION", "PIVOT", "UNPIVOT", "TABLESAMPLE", "WHEN", "THEN", "OUTPUT", "RETURNING",
        "DEFAULT", "FETCH", "LATERAL", "ONLY", "USE", "FORCE", "IGNORE", "STRAIGHT_JOIN", "INTO", "FROM", "AND", "OR", "NOT",
        "LOOP", "BEGIN", "END", "GO", "IS", "IN", "WHILE", "IF", "ELSE", "DECLARE", "RETURN", "LANGUAGE"
    };

    private static bool IsKeyword(SqlToken token) =>
        token.Kind == SqlTokenKind.Word && (SqlCompletionEngine.KeywordSet.Contains(token.Text) || NotColumns.Contains(token.Text));

    // ----- Reading one script -----

    private enum Role { None, SourceName, SourceAlias, CteName, CteColumn, ColumnAlias, InsertColumn }

    private enum SourceKind { Catalog, Cte, Derived, Temp, Variable, Function, Unknown, AliasRef }

    private sealed class Scope(Scope? parent, Scope? firstBranch)
    {
        public Scope? Parent { get; } = parent;

        /// <summary>For the later branches of a UNION: the first branch, which names the output columns.</summary>
        public Scope? FirstBranch { get; } = firstBranch;

        public List<Source> Sources { get; } = [];
        public List<Cte> Ctes { get; } = [];
        public List<SelectItem> Items { get; } = [];
        public string Clause { get; set; } = "";
        public int SelectIndex { get; set; } = -1;
        public bool HasStar { get; set; }
        public Cte? OwnerCte { get; set; }
        public Source? OwnerSource { get; set; }

        /// <summary>The scope whose select list names this query's output columns.</summary>
        public Scope Head => FirstBranch ?? this;
    }

    private sealed class Source
    {
        public required SourceKind Kind { get; set; }
        public required Scope Scope { get; init; }
        public List<int> NameTokens { get; } = [];
        public List<string> Parts { get; } = [];
        public int AliasToken { get; set; } = -1;
        public string? Alias { get; set; }
        public DbObject? Object { get; set; }
        public Cte? Cte { get; set; }
        public int BodyParen { get; init; } = -1;
        public Scope? Body { get; set; }
        public Source? AliasOf { get; set; }
        public bool HasColumnList { get; set; }
        public int ColumnListOpen { get; set; } = -1;
        public string Name => Parts.Count > 0 ? Parts[^1] : "";
    }

    private sealed class Cte
    {
        public required string Name { get; init; }
        public required int NameToken { get; init; }
        public required Scope Scope { get; init; }
        public List<int> ColumnTokens { get; } = [];
        public int Open { get; set; } = -1;
        public Scope? Body { get; set; }
    }

    private sealed class SelectItem
    {
        public required Scope Scope { get; init; }
        public int AliasToken { get; set; } = -1;

        /// <summary>The token of a plain column item (x or q.x): its name passes through as the output name.</summary>
        public int ColumnToken { get; set; } = -1;

        public string? OutputName { get; set; }
    }

    /// <summary>One pass over a script (or a module's source): scopes, row sources, CTEs and select lists.</summary>
    private sealed class Analysis
    {
        private readonly SqlSymbolFinder _owner;
        private readonly string _text;
        private readonly string? _contextSchema;
        private readonly int _n;
        private readonly int[] _match;
        private readonly int[] _batch;
        private readonly Scope[] _scopeOf;
        private readonly string[] _clauseOf;
        private readonly int[] _depthOf;
        private readonly Dictionary<int, Scope> _bodyAtParen = [];
        private readonly HashSet<int> _cteCloses = [];
        private readonly List<Scope> _scopes = [];
        private readonly List<Source> _sources = [];
        private readonly List<Cte> _ctes = [];
        private Scope _current = null!;

        public IReadOnlyList<SqlToken> T { get; }
        public Role[] Roles { get; }

        public Analysis(SqlSymbolFinder owner, string text, string? contextSchema)
        {
            _owner = owner;
            _text = text;
            _contextSchema = contextSchema;
            T = Expand(text);
            _n = T.Count;
            Roles = new Role[_n];
            _match = MatchParentheses(T);
            _batch = Batches();
            _scopeOf = new Scope[_n];
            _clauseOf = new string[_n];
            _depthOf = new int[_n];
            Walk();
            Link();
            ReadSelectLists();
        }

        /// <summary>The significant tokens, with PostgreSQL's $$ … $$ routine bodies read as code.</summary>
        private static List<SqlToken> Expand(string text)
        {
            var result = new List<SqlToken>();
            void Add(string source, int offset, int depth)
            {
                foreach (var t in SqlLexer.Tokenize(source))
                {
                    if (t.IsTrivia) continue;
                    if (t.Kind == SqlTokenKind.String && t.Text.StartsWith('$') && depth < 4)
                    {
                        var tagEnd = t.Text.IndexOf('$', 1);
                        if (tagEnd > 0)
                        {
                            var tag = t.Text[..(tagEnd + 1)];
                            var innerEnd = t.Text.Length >= 2 * tag.Length && t.Text.EndsWith(tag, StringComparison.Ordinal) ? t.Text.Length - tag.Length : t.Text.Length;
                            Add(t.Text[tag.Length..innerEnd], offset + t.Start + tag.Length, depth + 1);
                            continue;
                        }
                    }
                    result.Add(t with { Start = t.Start + offset });
                }
            }
            Add(text, 0, 0);
            return result;
        }

        private static int[] MatchParentheses(IReadOnlyList<SqlToken> tokens)
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

        /// <summary>The batch (GO-separated part) of each token: variables live for one batch.</summary>
        private int[] Batches()
        {
            var batch = new int[_n];
            var current = 0;
            for (var i = 0; i < _n; i++)
            {
                if (T[i].Is("GO") && AloneOnLine(T[i])) current++;
                batch[i] = current;
            }
            return batch;
        }

        private bool AloneOnLine(SqlToken token)
        {
            for (var j = token.Start - 1; j >= 0 && _text[j] != '\n'; j--) if (!char.IsWhiteSpace(_text[j])) return false;
            for (var j = token.End; j < _text.Length && _text[j] != '\n'; j++)
                if (!char.IsWhiteSpace(_text[j]) && !char.IsDigit(_text[j])) return _text[j] == '-' && j + 1 < _text.Length && _text[j + 1] == '-';
            return true;
        }

        /// <summary>The token under the caret, preferring a name when the caret sits between two tokens.</summary>
        public int TokenAt(int offset)
        {
            var best = -1;
            for (var i = 0; i < _n; i++)
            {
                var t = T[i];
                if (t.Start > offset) break;
                if (offset > t.End) continue;
                var nameLike = t.IsIdentifier || t.Kind == SqlTokenKind.Variable;
                if (best < 0 || (nameLike && !(T[best].IsIdentifier || T[best].Kind == SqlTokenKind.Variable))) best = i;
            }
            return best >= 0 && (T[best].IsIdentifier || T[best].Kind == SqlTokenKind.Variable) ? best : -1;
        }

        private bool Is(int i, string word) => i >= 0 && i < _n && T[i].Is(word);
        private bool KindAt(int i, SqlTokenKind kind) => i >= 0 && i < _n && T[i].Kind == kind;
        private bool IsName(int i) => i >= 0 && i < _n && T[i].IsIdentifier && !(T[i].Kind == SqlTokenKind.Word && T[i].Text.StartsWith('#'));

        private Scope NewScope(Scope? parent, Scope? firstBranch = null)
        {
            var scope = new Scope(parent, firstBranch);
            _scopes.Add(scope);
            return scope;
        }

        // ----- Pass 1: scopes, clauses, row sources and CTEs -----

        private void Walk()
        {
            _current = NewScope(null);
            var frames = new Stack<(bool IsScope, Scope Outer, int Depth)>();
            var depth = 0;
            var cases = 0;
            for (var i = 0; i < _n; i++)
            {
                var token = T[i];
                if (frames.Count == 0 && StartsNewRoot(i, cases))
                {
                    _current = NewScope(null);
                    depth = 0;
                    cases = 0;
                }

                if (token.Kind == SqlTokenKind.OpenParen)
                {
                    Mark(i, depth);
                    if (Is(i + 1, "SELECT") || Is(i + 1, "WITH") || _ctes.Any(c => c.Open == i))
                    {
                        frames.Push((true, _current, depth));
                        _current = NewScope(_current);
                        _bodyAtParen[i] = _current;
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

                Mark(i, depth);
                if (depth == 0 && token.Kind == SqlTokenKind.Word) OnClauseWord(i);
                else if (depth == 0 && token.Kind == SqlTokenKind.Comma && _current.Clause == "FROM") ParseSource(i + 1);
            }
        }

        private void Mark(int i, int depth)
        {
            _scopeOf[i] = _current;
            _clauseOf[i] = _current.Clause;
            _depthOf[i] = depth;
        }

        private static readonly HashSet<string> NotAfterVerb = new(StringComparer.OrdinalIgnoreCase)
            { "FOR", "DO", "THEN", "KEY", "ON", "OR", "OF", "BEFORE", "AFTER", "INSTEAD", "GRANT", "REVOKE", "DENY", "ALL" };

        private static readonly HashSet<string> NotBeforeIf = new(StringComparer.OrdinalIgnoreCase)
        {
            "TABLE", "VIEW", "INDEX", "SCHEMA", "DATABASE", "PROCEDURE", "PROC", "FUNCTION", "TRIGGER", "SEQUENCE", "TYPE",
            "EXTENSION", "ROLE", "USER", "COLUMN", "CONSTRAINT", "END", "DOMAIN", "SERVER", "POLICY", "RULE"
        };

        /// <summary>A new statement where there is no semicolon: T-SQL batches and routine bodies often have none.</summary>
        private bool StartsNewRoot(int i, int cases)
        {
            if (i == 0) return false;
            var previous = T[i - 1];
            if (previous.Kind == SqlTokenKind.Semicolon) return true;
            if (T[i].Is("GO") && AloneOnLine(T[i])) return true;
            if (T[i].Kind != SqlTokenKind.Word) return false;
            var afterVerbWord = previous.Kind == SqlTokenKind.Comma || (previous.Kind == SqlTokenKind.Word && NotAfterVerb.Contains(previous.Text));
            switch (T[i].Text.ToUpperInvariant())
            {
                case "BEGIN" or "WHILE" or "RETURN" or "PRINT" or "DECLARE" or "EXEC" or "EXECUTE" or "RAISERROR" or "THROW" or "PERFORM" or "OPEN" or "CLOSE":
                    return !previous.Is("END") || T[i].Is("BEGIN");
                case "IF":
                    return !(previous.Kind == SqlTokenKind.Word && NotBeforeIf.Contains(previous.Text));
                case "END" or "ELSE" or "ELSIF":
                    return cases == 0;
                case "SELECT":
                    if (previous.Is("UNION") || previous.Is("ALL") || previous.Is("EXCEPT") || previous.Is("INTERSECT") ||
                        previous.Is("MINUS") || previous.Is("DISTINCT") || previous.Is("GRANT") || previous.Kind == SqlTokenKind.Comma) return false;
                    return !(previous.Kind == SqlTokenKind.CloseParen && _cteCloses.Contains(i - 1));
                case "UPDATE" or "INSERT" or "DELETE" or "MERGE" or "TRUNCATE":
                    if (afterVerbWord) return false;
                    return !(previous.Kind == SqlTokenKind.CloseParen && _cteCloses.Contains(i - 1)) || !_current.Ctes.Any();
                case "CREATE" or "DROP":
                    return true;
                case "ALTER":
                    return !previous.Is("OR");
                case "WITH":
                    return IsCteStart(i);
                case "SET":
                    return !(_current.Clause is "UPDATE" || previous.Is("UPDATE") || afterVerbWord || previous.Is("CHARACTER"));
                default:
                    return false;
            }
        }

        /// <summary>WITH [RECURSIVE] name [(columns)] AS (…): not WITH (NOLOCK), WITH TIES, WITH ROLLUP or WITH CHECK OPTION.</summary>
        private bool IsCteStart(int i)
        {
            var j = i + 1;
            if (Is(j, "RECURSIVE")) j++;
            if (!IsName(j) || (T[j].Kind == SqlTokenKind.Word && IsKeyword(T[j]) && !Is(j + 1, "AS"))) return false;
            var k = j + 1;
            if (KindAt(k, SqlTokenKind.OpenParen) && _match[k] > k) k = _match[k] + 1;
            return Is(k, "AS");
        }

        private void OnClauseWord(int i)
        {
            var scope = _current;
            switch (T[i].Text.ToUpperInvariant())
            {
                case "UNION" or "EXCEPT" or "INTERSECT" or "MINUS":
                    _current = NewScope(scope.Parent, scope.Head);
                    return;
                case "WITH":
                    if (IsCteStart(i)) ParseCtes(i);
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
                case "USING":
                    if (!KindAt(i + 1, SqlTokenKind.OpenParen) || Is(i - 1, "MERGE") || scope.Clause is "INTO" or "DELETE") ParseSource(i + 1);
                    scope.Clause = "FROM";
                    return;
                case "ON":
                    if (Is(i - 1, "DISTINCT")) return;
                    scope.Clause = scope.Clause is "FROM" or "ON" ? "ON" : "OTHER";
                    return;
                case "WHERE" or "HAVING" or "WINDOW" or "VALUES" or "RETURNING" or "OUTPUT":
                    scope.Clause = T[i].Text.ToUpperInvariant();
                    return;
                case "GROUP":
                    if (!Is(i - 1, "WITHIN")) scope.Clause = "GROUP";
                    return;
                case "ORDER":
                    scope.Clause = "ORDER";
                    return;
                case "LIMIT" or "OFFSET" or "FETCH" or "FOR" or "OPTION":
                    scope.Clause = "LIMIT";
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
                    if (!Is(i + 1, "INTO") && !KindAt(i + 1, SqlTokenKind.OpenParen) && !Is(i + 1, "VALUES")) ParseSource(i + 1, insertTarget: true);
                    return;
                case "UPDATE":
                    if (Is(i + 1, "SET") || Is(i - 1, "FOR") || Is(i - 1, "DO") || Is(i - 1, "THEN") || Is(i - 1, "KEY") || Is(i - 1, "OR") || Is(i - 1, "ON"))
                    {
                        scope.Clause = "OTHER";
                        return;
                    }
                    scope.Clause = "UPDATE";
                    ParseSource(i + 1);
                    return;
                case "DELETE":
                    if (Is(i - 1, "ON") || Is(i - 1, "THEN") || Is(i - 1, "OR")) return;
                    scope.Clause = "DELETE";
                    if (IsName(i + 1) && !Is(i + 1, "FROM")) ParseSource(i + 1);
                    return;
                case "MERGE":
                    scope.Clause = "INTO";
                    if (!Is(i + 1, "INTO")) ParseSource(i + 1, insertTarget: true);
                    return;
                case "BEGIN" or "IF" or "WHILE" or "RETURN" or "PRINT" or "DECLARE" or "EXEC" or "EXECUTE" or "ELSE" or "END" or "CREATE" or "ALTER" or "DROP":
                    scope.Clause = "OTHER";
                    return;
            }
        }

        private void ParseCtes(int withIndex)
        {
            var j = withIndex + 1;
            if (Is(j, "RECURSIVE")) j++;
            while (IsName(j))
            {
                var cte = new Cte { Name = T[j].Identifier, NameToken = j, Scope = _current };
                Roles[j] = Role.CteName;
                var k = j + 1;
                if (KindAt(k, SqlTokenKind.OpenParen) && _match[k] > k)
                {
                    for (var c = k + 1; c < _match[k]; c++)
                        if (IsName(c)) { cte.ColumnTokens.Add(c); Roles[c] = Role.CteColumn; }
                    k = _match[k] + 1;
                }
                if (!Is(k, "AS")) break;
                k++;
                if (Is(k, "NOT")) k++;
                if (Is(k, "MATERIALIZED")) k++;
                if (!KindAt(k, SqlTokenKind.OpenParen) || _match[k] < k) break;
                cte.Open = k;
                _cteCloses.Add(_match[k]);
                _ctes.Add(cte);
                _current.Ctes.Add(cte);
                j = _match[k] + 1;
                if (!KindAt(j, SqlTokenKind.Comma)) break;
                j++;
            }
        }

        /// <summary>A row source after FROM, JOIN, a FROM-list comma, UPDATE, DELETE, MERGE or INSERT INTO.</summary>
        private void ParseSource(int j, bool insertTarget = false)
        {
            var scope = _current;
            while (Is(j, "LATERAL") || Is(j, "ONLY") || Is(j, "TABLE") && !insertTarget) j++;
            if (j >= _n) return;

            if (T[j].Kind == SqlTokenKind.OpenParen)
            {
                if (_match[j] < j) return;
                var derived = new Source { Kind = SourceKind.Derived, Scope = scope, BodyParen = j };
                ReadAlias(derived, _match[j] + 1);
                scope.Sources.Add(derived);
                _sources.Add(derived);
                return;
            }

            Source source;
            int k;
            if (T[j].Kind == SqlTokenKind.Variable)
            {
                source = new Source { Kind = SourceKind.Variable, Scope = scope };
                source.Parts.Add(T[j].Text);
                source.NameTokens.Add(j);
                k = j + 1;
            }
            else
            {
                if (!T[j].IsIdentifier || (T[j].Kind == SqlTokenKind.Word && IsKeyword(T[j]) && !T[j].Text.StartsWith('#') &&
                                            !KindAt(j + 1, SqlTokenKind.OpenParen) && !KindAt(j + 1, SqlTokenKind.Dot))) return;
                source = new Source { Kind = SourceKind.Unknown, Scope = scope };
                k = j;
                while (k < _n && T[k].IsIdentifier)
                {
                    source.Parts.Add(T[k].Identifier);
                    source.NameTokens.Add(k);
                    if (KindAt(k + 1, SqlTokenKind.Dot) && KindAt(k + 2, SqlTokenKind.Dot)) { source.Parts.Add(""); k += 3; continue; } // db..table
                    if (KindAt(k + 1, SqlTokenKind.Dot) && k + 2 < _n && T[k + 2].IsIdentifier) { k += 2; continue; }
                    k++;
                    break;
                }
                if (T[source.NameTokens[0]].Kind == SqlTokenKind.Word && T[source.NameTokens[0]].Text.StartsWith('#')) source.Kind = SourceKind.Temp;
            }
            foreach (var nameToken in source.NameTokens) Roles[nameToken] = Role.SourceName;

            if (KindAt(k, SqlTokenKind.OpenParen) && _match[k] > k)
            {
                if (insertTarget)
                {
                    source.ColumnListOpen = k;
                    for (var c = k + 1; c < _match[k]; c++) if (IsName(c)) Roles[c] = Role.InsertColumn;
                }
                else if (source.Kind is SourceKind.Unknown) source.Kind = SourceKind.Function;
                k = _match[k] + 1;
            }
            if (!insertTarget || Is(k, "AS") || IsName(k) && Is(k + 1, "USING")) ReadAlias(source, k);
            scope.Sources.Add(source);
            _sources.Add(source);
        }

        private void ReadAlias(Source source, int k)
        {
            var explicitAs = Is(k, "AS");
            if (explicitAs) k++;
            if (!IsName(k) || k >= _n) return;
            var token = T[k];
            if (token.Kind == SqlTokenKind.Word && (NotAnAlias.Contains(token.Text) || (!explicitAs && IsKeyword(token)))) return;
            source.AliasToken = k;
            source.Alias = token.Identifier;
            Roles[k] = Role.SourceAlias;
            if (KindAt(k + 1, SqlTokenKind.OpenParen) && source.Kind is SourceKind.Derived or SourceKind.Function)
                source.HasColumnList = true; // AS d (a, b): the subquery's own column names are hidden
        }

        // ----- Pass 2: what each source is -----

        private void Link()
        {
            foreach (var cte in _ctes)
                if (_bodyAtParen.TryGetValue(cte.Open, out var body))
                {
                    cte.Body = body;
                    if (cte.ColumnTokens.Count == 0) body.OwnerCte = cte;
                }

            foreach (var s in _sources)
            {
                if (s.Kind == SourceKind.Derived)
                {
                    if (_bodyAtParen.TryGetValue(s.BodyParen, out var body))
                    {
                        s.Body = body;
                        if (!s.HasColumnList) body.OwnerSource = s;
                    }
                    continue;
                }
                if (s.Kind != SourceKind.Unknown) continue;
                if (s.Parts.Count == 1 && s.Alias is null &&
                    s.Scope.Sources.FirstOrDefault(o => !ReferenceEquals(o, s) && Same(o.Alias, s.Name)) is { } aliased)
                {
                    // UPDATE o SET … FROM Orders o / DELETE o FROM Orders o (SQL Server): the name is the alias.
                    s.Kind = SourceKind.AliasRef;
                    s.AliasOf = aliased;
                    Roles[s.NameTokens[0]] = Role.SourceAlias;
                    continue;
                }
                if (s.Parts.Count == 1 && FindCte(s.Name, s.Scope) is { } cte)
                {
                    s.Kind = SourceKind.Cte;
                    s.Cte = cte;
                    continue;
                }
                if (_owner.Resolve(s.Parts, _contextSchema) is { } obj)
                {
                    s.Kind = SourceKind.Catalog;
                    s.Object = obj;
                }
            }
        }

        private Cte? FindCte(string name, Scope? scope)
        {
            for (var sc = scope; sc is not null; sc = sc.Parent)
            {
                if (sc.Ctes.FirstOrDefault(c => Same(c.Name, name)) is { } own) return own;
                if (sc.FirstBranch?.Ctes.FirstOrDefault(c => Same(c.Name, name)) is { } first) return first;
            }
            return null;
        }

        private static readonly HashSet<string> SelectListEnd = new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "INTO", "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "MINUS", "LIMIT", "OFFSET",
            "FETCH", "WINDOW", "FOR", "OPTION"
        };

        /// <summary>Each scope's output columns: SELECT expr AS name, expr name, name = expr (SQL Server) or a plain column.</summary>
        private void ReadSelectLists()
        {
            foreach (var scope in _scopes.Where(s => s.SelectIndex >= 0))
            {
                var i = scope.SelectIndex + 1;
                if (Is(i, "DISTINCT") || Is(i, "ALL")) i++;
                if (Is(i - 1, "DISTINCT") && Is(i, "ON") && KindAt(i + 1, SqlTokenKind.OpenParen)) i = _match[i + 1] + 1;
                if (Is(i, "TOP"))
                {
                    i++;
                    if (KindAt(i, SqlTokenKind.OpenParen) && _match[i] > i) i = _match[i] + 1;
                    else i++;
                    if (Is(i, "PERCENT")) i++;
                    if (Is(i, "WITH") && Is(i + 1, "TIES")) i += 2;
                }

                var start = i;
                while (true)
                {
                    var end = i < _n ? ItemEnd(scope, i) : i;
                    if (end > start) scope.Items.Add(ReadItem(scope, start, end));
                    if (end >= _n || T[end].Kind != SqlTokenKind.Comma) break;
                    i = start = end + 1;
                }
            }
        }

        private int ItemEnd(Scope scope, int i)
        {
            while (i < _n)
            {
                var t = T[i];
                if (t.Kind == SqlTokenKind.OpenParen && _match[i] > i) { i = _match[i] + 1; continue; }
                if (t.Kind is SqlTokenKind.CloseParen or SqlTokenKind.Semicolon or SqlTokenKind.Comma) return i;
                if (!ReferenceEquals(_scopeOf[i], scope)) return i;
                if (t.Kind == SqlTokenKind.Word && SelectListEnd.Contains(t.Text)) return i;
                i++;
            }
            return i;
        }

        private SelectItem ReadItem(Scope scope, int start, int end)
        {
            var item = new SelectItem { Scope = scope };
            var last = end - 1;
            if (T[last].Kind == SqlTokenKind.Operator && T[last].Text == "*")
            {
                scope.HasStar = true;
                return item;
            }
            if (IsName(last) && last - 1 >= start && Is(last - 1, "AS"))
                item.AliasToken = last;
            else if (IsName(last) && last - 1 >= start && !KindAt(last - 1, SqlTokenKind.Dot) && !KindAt(last - 1, SqlTokenKind.Operator) &&
                     !(T[last].Kind == SqlTokenKind.Word && IsKeyword(T[last])) &&
                     (T[last - 1].IsIdentifier && !(T[last - 1].Kind == SqlTokenKind.Word && IsKeyword(T[last - 1]) && !Is(last - 1, "END")) ||
                      T[last - 1].Kind is SqlTokenKind.Number or SqlTokenKind.String or SqlTokenKind.CloseParen or SqlTokenKind.Variable))
                item.AliasToken = last;
            else if (_owner._providerKey == SqlDialect.SqlServerKey && IsName(start) && end - start >= 3 &&
                     KindAt(start + 1, SqlTokenKind.Operator) && T[start + 1].Text == "=")
                item.AliasToken = start;

            if (item.AliasToken >= 0)
            {
                Roles[item.AliasToken] = Role.ColumnAlias;
                item.OutputName = T[item.AliasToken].Identifier;
                return item;
            }

            // A plain column: name, q.name or s.q.name.
            var plain = IsName(last) && !(T[last].Kind == SqlTokenKind.Word && IsKeyword(T[last]));
            for (var p = last - 1; plain && p >= start; p -= 2)
                plain = KindAt(p, SqlTokenKind.Dot) && p - 1 >= start && IsName(p - 1);
            if (plain && (last - start) % 2 == 0)
            {
                item.ColumnToken = last;
                item.OutputName = T[last].Identifier;
            }
            return item;
        }

        // ----- Binding -----

        /// <summary>The row source a qualifier (the q of q.column) means, looking outwards from its query.</summary>
        private Source? BindQualifier(string qualifier, Scope? scope)
        {
            for (var sc = scope; sc is not null; sc = sc.Parent)
            {
                var hit = sc.Sources.FirstOrDefault(s => s.Kind != SourceKind.AliasRef && Same(s.Alias, qualifier))
                          ?? sc.Sources.FirstOrDefault(s => s.Kind != SourceKind.AliasRef && s.Alias is null && Same(s.Name, qualifier));
                if (hit is not null) return hit;
            }
            return null;
        }

        /// <summary>Whether a row source has the column: true, false, or null when it cannot be told.</summary>
        private bool? Provides(Source s, string column, HashSet<Scope>? seen = null)
        {
            switch (s.Kind)
            {
                case SourceKind.Catalog:
                    return s.Object is null ? null : _owner.HasColumn(s.Object, column);
                case SourceKind.Cte when s.Cte is { } cte:
                    if (cte.ColumnTokens.Count > 0) return cte.ColumnTokens.Any(c => Same(T[c].Identifier, column));
                    return cte.Body is null ? null : Outputs(cte.Body, column, seen);
                case SourceKind.Derived when s.Body is not null && !s.HasColumnList:
                    return Outputs(s.Body, column, seen);
                default:
                    return null;
            }
        }

        private bool? Outputs(Scope body, string column, HashSet<Scope>? seen)
        {
            seen ??= [];
            if (!seen.Add(body)) return null;
            if (body.Items.Any(it => Same(it.OutputName, column))) return true;
            if (!body.HasStar) return false;
            // SELECT *: the columns of every source of the query.
            bool? result = false;
            foreach (var s in body.Sources.Where(s => s.Kind != SourceKind.AliasRef))
            {
                var p = Provides(s, column, seen);
                if (p == true) return true;
                if (p is null) result = null;
            }
            return result;
        }

        /// <summary>The row source a bare column comes from: the one source of the nearest query that has it, or null
        /// when none or several do (an ambiguous name is never guessed).</summary>
        private Source? OwnerOf(string column, Scope? scope)
        {
            for (var sc = scope; sc is not null; sc = sc.Parent)
            {
                var yes = sc.Sources.Where(s => s.Kind != SourceKind.AliasRef && Provides(s, column) == true).ToList();
                if (yes.Count == 1) return yes[0];
                if (yes.Count > 1) return null;
                if (sc.Sources.Any(s => s.Kind != SourceKind.AliasRef && Provides(s, column) is null)) return null;
            }
            return null;
        }

        /// <summary>Column references: (qualifier token or -1, name token) for every name that reads a column.</summary>
        private IEnumerable<(int Qualifier, int Name)> ColumnRefs(string name)
        {
            for (var i = 0; i < _n; i++)
            {
                if (!IsName(i) || Roles[i] != Role.None || !Same(T[i].Identifier, name)) continue;
                if (KindAt(i + 1, SqlTokenKind.Dot)) continue; // a qualifier itself
                if (KindAt(i + 1, SqlTokenKind.OpenParen) && T[i].Kind == SqlTokenKind.Word) continue; // a function call
                if (KindAt(i - 1, SqlTokenKind.Dot))
                {
                    if (IsName(i - 2) && !KindAt(i - 3, SqlTokenKind.Dot)) yield return (i - 2, i);
                    else if (IsName(i - 2) && KindAt(i - 3, SqlTokenKind.Dot) && IsName(i - 4)) yield return (i - 2, i); // s.t.column
                    continue;
                }
                if (T[i].Kind == SqlTokenKind.Word && IsKeyword(T[i])) continue;
                yield return (-1, i);
            }
        }

        private Source? Effective(Source? s) => s?.Kind == SourceKind.AliasRef ? s.AliasOf : s;

        // ----- Symbols -----

        public SqlSymbolLookup SymbolFor(int k)
        {
            var token = T[k];
            switch (Roles[k])
            {
                case Role.CteName:
                    return Found(CteSymbol(_ctes.First(c => c.NameToken == k)));
                case Role.CteColumn:
                    var owner = _ctes.First(c => c.ColumnTokens.Contains(k));
                    return Found(ColumnAliasSymbol(k, null, owner));
                case Role.SourceAlias:
                    var aliased = _sources.FirstOrDefault(s => s.AliasToken == k) ?? _sources.First(s => s.Kind == SourceKind.AliasRef && s.NameTokens[0] == k).AliasOf!;
                    return Found(AliasSymbol(aliased));
                case Role.ColumnAlias:
                    var item = _scopes.SelectMany(s => s.Items).First(it => it.AliasToken == k);
                    return Found(ColumnAliasSymbol(k, item, null));
                case Role.SourceName:
                    var source = _sources.First(s => s.NameTokens.Contains(k));
                    return source.Kind switch
                    {
                        SourceKind.Cte => Found(CteSymbol(source.Cte!)),
                        SourceKind.Catalog when source.NameTokens[^1] == k || source.NameTokens.Count == 1 => Found(ObjectSymbol(source.Object!)),
                        SourceKind.Catalog => SqlSymbolLookup.None($"{token.Identifier} is the schema of {source.Object!.FullName}."),
                        _ => SqlSymbolLookup.None(_owner.HasCatalog
                            ? $"{token.Identifier} is not in the loaded catalog, so it cannot be followed."
                            : $"{token.Identifier} is a table name; finding where tables are used needs a connection.")
                    };
                case Role.InsertColumn:
                    var target = _sources.First(s => s.ColumnListOpen >= 0 && k > s.ColumnListOpen && k < _match[s.ColumnListOpen]);
                    return target.Kind == SourceKind.Catalog && _owner.ColumnName(target.Object!, token.Identifier) is { } insertColumn
                        ? Found(ColumnSymbol(target.Object!, insertColumn))
                        : SqlSymbolLookup.None($"{token.Identifier} is a column of {target.Name}, which is not in the loaded catalog.");
            }

            // A qualifier: q of q.column.
            if (KindAt(k + 1, SqlTokenKind.Dot))
            {
                var bound = Effective(BindQualifier(token.Identifier, _scopeOf[k]));
                if (bound is null) return SqlSymbolLookup.None($"No table or alias of this query is called {token.Identifier}.");
                if (bound.Alias is not null) return Found(AliasSymbol(bound));
                if (bound.Kind == SourceKind.Cte) return Found(CteSymbol(bound.Cte!));
                if (bound.Kind == SourceKind.Catalog) return Found(ObjectSymbol(bound.Object!));
                return SqlSymbolLookup.None($"{token.Identifier} is not in the loaded catalog, so it cannot be followed.");
            }

            // A column.
            var name = token.Identifier;
            var scope = _scopeOf[k];
            Source? from;
            if (KindAt(k - 1, SqlTokenKind.Dot) && IsName(k - 2))
            {
                from = Effective(BindQualifier(T[k - 2].Identifier, _scopeOf[k - 2]));
                if (from is null) return SqlSymbolLookup.None($"No table or alias of this query is called {T[k - 2].Identifier}.");
            }
            else
            {
                if (_clauseOf[k] is "ORDER" or "GROUP" or "HAVING" && OutputAlias(scope, name) is { } byAlias)
                    return Found(ColumnAliasSymbol(byAlias.AliasToken, byAlias, null));
                from = OwnerOf(name, scope);
                if (from is null)
                    return SqlSymbolLookup.None(_owner.HasCatalog || scope.Sources.Any(s => s.Kind is SourceKind.Cte or SourceKind.Derived)
                        ? $"Can't tell which table {name} comes from. Qualify it (alias.{name}) and try again."
                        : $"{name} is a column of a table; finding columns needs a connection.");
            }

            switch (from.Kind)
            {
                case SourceKind.Catalog:
                    var column = _owner.ColumnName(from.Object!, name);
                    return column is null
                        ? SqlSymbolLookup.None($"{from.Object!.FullName} has no column {name}.")
                        : Found(ColumnSymbol(from.Object!, column));
                case SourceKind.Cte or SourceKind.Derived:
                    var definition = DefinitionOf(from, name);
                    if (definition.Token >= 0) return Found(ColumnAliasSymbol(definition.Token, definition.Item, definition.Cte));
                    return SqlSymbolLookup.None($"{name} is passed through from a table inside {(from.Kind == SourceKind.Cte ? "CTE " + from.Name : "the subquery")}. " +
                                                "Give it a name there (… AS new_name) to rename it.");
                default:
                    return SqlSymbolLookup.None($"{from.Name} is not described by the catalog, so its columns cannot be followed.");
            }
        }

        private static SqlSymbolLookup Found(SqlSymbol symbol) => new(symbol, null);

        /// <summary>Where a CTE's or subquery's output column is named: its column list entry or a select item's alias.</summary>
        private (int Token, SelectItem? Item, Cte? Cte) DefinitionOf(Source source, string column)
        {
            if (source.Kind == SourceKind.Cte && source.Cte is { } cte)
            {
                if (cte.ColumnTokens.FirstOrDefault(c => Same(T[c].Identifier, column), -1) is var listed and >= 0) return (listed, null, cte);
                if (cte.Body is { } body && body.Items.FirstOrDefault(it => it.AliasToken >= 0 && Same(it.OutputName, column)) is { } item)
                    return (item.AliasToken, item, null);
            }
            else if (source.Body is { } derived && derived.Items.FirstOrDefault(it => it.AliasToken >= 0 && Same(it.OutputName, column)) is { } item)
                return (item.AliasToken, item, null);
            return (-1, null, null);
        }

        /// <summary>The output alias an ORDER BY / GROUP BY / HAVING name refers to (its own query, or the first branch of a UNION).</summary>
        private SelectItem? OutputAlias(Scope scope, string name) =>
            scope.Items.Concat(scope.Head.Items).FirstOrDefault(it => it.AliasToken >= 0 && Same(it.OutputName, name));

        private SqlOccurrence Occ(int token, bool definition = false) => new(T[token].Start, T[token].Length, definition);

        private SqlSymbol AliasSymbol(Source source)
        {
            var list = new List<SqlOccurrence> { Occ(source.AliasToken, definition: true) };
            foreach (var s in _sources.Where(s => s.Kind == SourceKind.AliasRef && ReferenceEquals(s.AliasOf, source)))
                list.Add(Occ(s.NameTokens[0]));
            for (var i = 0; i < _n; i++)
                if (IsName(i) && Roles[i] == Role.None && KindAt(i + 1, SqlTokenKind.Dot) && !KindAt(i - 1, SqlTokenKind.Dot) &&
                    Same(T[i].Identifier, source.Alias) && ReferenceEquals(BindQualifier(T[i].Identifier, _scopeOf[i]), source))
                    list.Add(Occ(i));

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in source.Scope.Sources.Where(s => !ReferenceEquals(s, source) && s.Kind != SourceKind.AliasRef))
                taken.Add(s.Alias ?? s.Name);
            var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var sc = source.Scope.Parent; sc is not null; sc = sc.Parent)
                foreach (var s in sc.Sources.Where(s => s.Kind != SourceKind.AliasRef)) shadowed.Add(s.Alias ?? s.Name);
            return new SqlSymbol(SqlSymbolKind.TableAlias, source.Alias!, Distinct(list)) { Taken = taken, Shadowed = shadowed };
        }

        private SqlSymbol CteSymbol(Cte cte)
        {
            var list = new List<SqlOccurrence> { Occ(cte.NameToken, definition: true) };
            foreach (var s in _sources.Where(s => s.Kind == SourceKind.Cte && ReferenceEquals(s.Cte, cte)))
            {
                list.Add(Occ(s.NameTokens[0]));
                if (s.Alias is not null) continue;
                for (var i = 0; i < _n; i++)
                    if (IsName(i) && Roles[i] == Role.None && KindAt(i + 1, SqlTokenKind.Dot) && !KindAt(i - 1, SqlTokenKind.Dot) &&
                        Same(T[i].Identifier, cte.Name) && ReferenceEquals(BindQualifier(T[i].Identifier, _scopeOf[i]), s))
                        list.Add(Occ(i));
            }
            var taken = cte.Scope.Ctes.Where(c => !ReferenceEquals(c, cte)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shadowed = _sources.Where(s => s.Kind == SourceKind.Catalog).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in _owner._objectsByName.Select(g => g.Key)) shadowed.Add(name);
            return new SqlSymbol(SqlSymbolKind.Cte, cte.Name, Distinct(list)) { Taken = taken, Shadowed = shadowed };
        }

        /// <summary>An output column named in the script, followed into every query that reads it, also through
        /// plain pass-through columns (SELECT c.total FROM c inside another CTE).</summary>
        private SqlSymbol ColumnAliasSymbol(int definitionToken, SelectItem? item, Cte? listOwner)
        {
            var name = T[definitionToken].Identifier;
            var list = new List<SqlOccurrence> { Occ(definitionToken, definition: true) };
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<object>();

            if (listOwner is not null)
            {
                foreach (var c in listOwner.ColumnTokens.Where(c => c != definitionToken)) taken.Add(T[c].Identifier);
                Consumers(_sources.Where(s => s.Kind == SourceKind.Cte && ReferenceEquals(s.Cte, listOwner)).ToList(), name, list, seen);
            }
            else if (item is not null)
            {
                foreach (var other in item.Scope.Head.Items.Where(it => !ReferenceEquals(it, item) && it.OutputName is not null)) taken.Add(other.OutputName!);
                OrderByRefs(item.Scope, name, list);
                Consumers(ConsumersOf(item.Scope.Head), name, list, seen);
            }
            return new SqlSymbol(SqlSymbolKind.ColumnAlias, name, Distinct(list)) { Taken = taken };
        }

        /// <summary>The sources that read a query's output: the CTE's uses, or the subquery itself.</summary>
        private List<Source> ConsumersOf(Scope body)
        {
            if (body.OwnerCte is { } cte) return _sources.Where(s => s.Kind == SourceKind.Cte && ReferenceEquals(s.Cte, cte)).ToList();
            if (body.OwnerSource is { } derived) return [derived];
            return [];
        }

        /// <summary>ORDER BY name (and GROUP BY / HAVING name where it cannot be a source column) of the query and its UNION branches.</summary>
        private void OrderByRefs(Scope scope, string name, List<SqlOccurrence> list)
        {
            var head = scope.Head;
            foreach (var (q, i) in ColumnRefs(name))
            {
                if (q >= 0) continue;
                var at = _scopeOf[i];
                if (!(ReferenceEquals(at, head) || ReferenceEquals(at.FirstBranch, head))) continue;
                if (_clauseOf[i] == "ORDER") list.Add(Occ(i));
                else if (_clauseOf[i] is "GROUP" or "HAVING" && ReferenceEquals(at, scope) &&
                         !at.Sources.Any(s => s.Kind != SourceKind.AliasRef && Provides(s, name) != false))
                    list.Add(Occ(i));
            }
        }

        private void Consumers(List<Source> readers, string name, List<SqlOccurrence> list, HashSet<object> seen)
        {
            foreach (var reader in readers)
            {
                if (!seen.Add(reader)) continue;
                foreach (var (q, i) in ColumnRefs(name))
                {
                    var bound = q >= 0 ? Effective(BindQualifier(T[q].Identifier, _scopeOf[q])) : OwnerOf(name, _scopeOf[i]);
                    if (!ReferenceEquals(bound, reader)) continue;
                    list.Add(Occ(i));
                    // Passed on unchanged by another CTE or subquery: its readers use the same name.
                    var passing = _scopeOf[i].Items.FirstOrDefault(it => it.ColumnToken == i);
                    if (passing is not null && seen.Add(passing))
                    {
                        OrderByRefs(passing.Scope, name, list);
                        Consumers(ConsumersOf(passing.Scope.Head), name, list, seen);
                    }
                }
            }
        }

        public SqlSymbol VariableSymbol(int k)
        {
            var name = T[k].Text;
            var batch = _batch[k];
            var list = new List<SqlOccurrence>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _n; i++)
            {
                if (T[i].Kind != SqlTokenKind.Variable || _batch[i] != batch) continue;
                if (string.Equals(T[i].Text, name, StringComparison.OrdinalIgnoreCase)) list.Add(Occ(i, Is(i - 1, "DECLARE")));
                else taken.Add(T[i].Text);
            }
            return new SqlSymbol(SqlSymbolKind.Variable, name, Distinct(list)) { Taken = taken };
        }

        public SqlSymbol TempTableSymbol(int k)
        {
            var name = T[k].Text;
            var list = new List<SqlOccurrence>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _n; i++)
            {
                if (T[i].Kind != SqlTokenKind.Word || !T[i].Text.StartsWith('#')) continue;
                if (string.Equals(T[i].Text, name, StringComparison.OrdinalIgnoreCase))
                    list.Add(Occ(i, (Is(i - 1, "TABLE") && Is(i - 2, "CREATE")) || (Is(i - 1, "INTO") && !Is(i - 2, "INSERT"))));
                else taken.Add(T[i].Text);
            }
            return new SqlSymbol(SqlSymbolKind.TempTable, name, Distinct(list)) { Taken = taken };
        }

        private SqlSymbol ObjectSymbol(DbObject obj) =>
            new(SqlSymbolKind.Object, obj.Name, ObjectOccurrences(obj)) { Object = obj };

        private SqlSymbol ColumnSymbol(DbObject obj, string column) =>
            new(SqlSymbolKind.Column, column, ColumnOccurrences(obj, column)) { Object = obj, Column = column };

        private static readonly HashSet<string> ObjectPositions = new(StringComparer.OrdinalIgnoreCase)
            { "TABLE", "REFERENCES", "TRUNCATE", "EXEC", "EXECUTE", "CALL", "PERFORM", "VIEW", "INTO", "UPDATE", "FROM", "JOIN" };

        /// <summary>Every place that names the object: row sources, unaliased qualifiers, and names in DDL, EXEC and
        /// REFERENCES (schema-qualified anywhere; unqualified only right after such a keyword).</summary>
        public IReadOnlyList<SqlOccurrence> ObjectOccurrences(DbObject obj)
        {
            var list = new List<SqlOccurrence>();
            bool IsTarget(DbObject? o) => o is not null && Same(o.Schema, obj.Schema) && Same(o.Name, obj.Name) &&
                                          (o.Database.Length == 0 || obj.Database.Length == 0 || Same(o.Database, obj.Database));

            foreach (var s in _sources.Where(s => s.Kind == SourceKind.Catalog && IsTarget(s.Object)))
            {
                list.Add(Span(s.NameTokens[0], s.NameTokens[^1]));
                if (s.Alias is not null) continue;
                for (var i = 0; i < _n; i++)
                    if (IsName(i) && Roles[i] == Role.None && KindAt(i + 1, SqlTokenKind.Dot) && !KindAt(i - 1, SqlTokenKind.Dot) &&
                        Same(T[i].Identifier, s.Name) && ReferenceEquals(BindQualifier(T[i].Identifier, _scopeOf[i]), s))
                        list.Add(Occ(i));
            }

            // Names outside row sources: CREATE TRIGGER … ON t, ALTER TABLE t, EXEC p, REFERENCES t (…), dbo.f(…).
            for (var i = 0; i < _n; i++)
            {
                if (!IsName(i) || Roles[i] != Role.None || KindAt(i - 1, SqlTokenKind.Dot)) continue;
                var parts = new List<string> { T[i].Identifier };
                var last = i;
                while (KindAt(last + 1, SqlTokenKind.Dot) && IsName(last + 2) && Roles[last + 2] == Role.None)
                {
                    last += 2;
                    parts.Add(T[last].Identifier);
                }
                if (!Same(parts[^1], obj.Name)) { i = last; continue; }
                var previous = i > 0 && T[i - 1].Kind == SqlTokenKind.Word ? T[i - 1].Text.ToUpperInvariant() : "";
                var trigger = Same(previous, "ON") && (KindAt(last + 1, SqlTokenKind.OpenParen) || Is(last + 1, "FOR") || Is(last + 1, "AFTER") ||
                                                       Is(last + 1, "INSTEAD") || Is(last + 1, "BEFORE") || Is(last + 1, "WITH") || Is(last + 1, "REFERENCING"));
                var wanted = parts.Count >= 2
                    ? !(KindAt(last + 1, SqlTokenKind.Dot))
                    : ObjectPositions.Contains(previous) || trigger;
                if (parts.Count == 1 && obj.IsTableLike && previous is "EXEC" or "EXECUTE" or "CALL" or "PERFORM") wanted = false;
                if (wanted && IsTarget(_owner.Resolve(parts, _contextSchema))) list.Add(Span(i, last));
                i = last;
            }
            return Distinct(list);
        }

        /// <summary>Every place the column of <paramref name="obj"/> is read or written.</summary>
        public IReadOnlyList<SqlOccurrence> ColumnOccurrences(DbObject obj, string column)
        {
            var list = new List<SqlOccurrence>();
            bool IsTarget(Source? s) => s is { Kind: SourceKind.Catalog, Object: { } o } && Same(o.Schema, obj.Schema) && Same(o.Name, obj.Name) &&
                                        (o.Database.Length == 0 || obj.Database.Length == 0 || Same(o.Database, obj.Database));

            foreach (var (q, i) in ColumnRefs(column))
            {
                if (q >= 0)
                {
                    if (IsTarget(Effective(BindQualifier(T[q].Identifier, _scopeOf[q])))) list.Add(Occ(i));
                    continue;
                }
                // ORDER BY name means the output alias when the query has one by that name.
                if (_clauseOf[i] is "ORDER" && OutputAlias(_scopeOf[i], column) is not null) continue;
                if (IsTarget(OwnerOf(column, _scopeOf[i]))) list.Add(Occ(i));
            }
            foreach (var s in _sources.Where(s => s.ColumnListOpen >= 0 && IsTarget(s)))
                for (var c = s.ColumnListOpen + 1; c < _match[s.ColumnListOpen]; c++)
                    if (Roles[c] == Role.InsertColumn && Same(T[c].Identifier, column)) list.Add(Occ(c));
            return Distinct(list);
        }

        private SqlOccurrence Span(int first, int last) => new(T[first].Start, T[last].End - T[first].Start, false);

        private static IReadOnlyList<SqlOccurrence> Distinct(List<SqlOccurrence> list) =>
            list.GroupBy(o => o.Start).Select(g => g.OrderByDescending(o => o.IsDefinition).First()).OrderBy(o => o.Start).ToList();
    }
}
