using DbExplorer.Application.Diagram;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

/// <summary>A table a result set reads from, with the result columns that hold its key (empty when the result does not
/// carry a whole primary key or unique key, so its rows cannot be told apart and are not editable).</summary>
public sealed record ResultTable(DbObject Table, IReadOnlyList<ResultKeyColumn> Key)
{
    public bool IsEditable => Table.Type == DbObjectType.Table && Key.Count > 0;
}

/// <param name="ResultColumn">Index of the result column holding the key value.</param>
public sealed record ResultKeyColumn(int ResultColumn, DbColumn Column);

/// <summary>Where one result column comes from: a plain column of <see cref="ResultSource.Tables"/>[<paramref name="Table"/>].</summary>
public sealed record ResultColumnSource(int Table, DbColumn Column);

/// <summary>A foreign key a result column takes part in: its value(s) identify a row of the referenced table.</summary>
/// <param name="Columns">The result columns holding the foreign key's columns, paired with the referenced column each matches.</param>
public sealed record ResultReference(DbForeignKey ForeignKey, DbObject? ReferencedTable, IReadOnlyList<(int ResultColumn, string ReferencedColumn)> Columns)
{
    public string TargetName => $"{ForeignKey.ReferencedSchema}.{ForeignKey.ReferencedTable}";
}

/// <summary>
/// What a query result is made of, as far as the catalog can tell: the tables it reads, the table column behind each
/// result column, their keys and foreign keys. It decides which cells can be edited (plain columns of a table whose key is
/// in the result) and which values link to a referenced row.
/// </summary>
public sealed record ResultSource
{
    /// <summary>Database the query ran in (null: the connection's default).</summary>
    public string? Database { get; init; }

    public required string ProviderKey { get; init; }

    public required IReadOnlyList<ResultTable> Tables { get; init; }

    /// <summary>Per result column: its table column, or null for expressions and anything not recognised.</summary>
    public required IReadOnlyList<ResultColumnSource?> Columns { get; init; }

    /// <summary>Per result column: the foreign key it is part of, or null.</summary>
    public required IReadOnlyList<ResultReference?> References { get; init; }

    /// <summary>False when the statement aggregates or merges rows (GROUP BY, DISTINCT, UNION…): a result row is then not
    /// one table row, so nothing is editable.</summary>
    public bool AllowsEdits { get; init; }

    /// <summary>Why nothing can be edited, shown to the user; null when some column can be.</summary>
    public string? ReadOnlyReason { get; init; }

    public ResultColumnSource? ColumnSource(int column) => column >= 0 && column < Columns.Count ? Columns[column] : null;

    public ResultReference? ReferenceOf(int column) => column >= 0 && column < References.Count ? References[column] : null;

    /// <summary>The column can be edited: a plain, writable column of a table whose key is in the result.</summary>
    public bool CanEdit(int column) =>
        AllowsEdits && ColumnSource(column) is { } s && Tables[s.Table].IsEditable && IsWritable(s.Column, ProviderKey);

    /// <summary>Not computed, not an identity, and not SQL Server's rowversion (whose type is named timestamp there).</summary>
    public static bool IsWritable(DbColumn column, string providerKey) =>
        !column.IsComputed && !column.IsIdentity &&
        !(providerKey == "SqlServer" && column.BaseType.ToLowerInvariant() is "timestamp" or "rowversion");

    public bool HasEditableColumns => Enumerable.Range(0, Columns.Count).Any(CanEdit);

    /// <summary>The table whole rows can be added to and deleted from: the only table the result's columns come from, with
    /// its key in the result. Null when rows cannot be added or deleted (<see cref="RowEditReason"/> says why).</summary>
    public ResultTable? RowTable => AllowsEdits && Tables.Count == 1 && Tables[0].IsEditable ? Tables[0] : null;

    /// <summary>Why rows cannot be added or deleted, shown to the user; null when they can.</summary>
    public string? RowEditReason =>
        RowTable is not null ? null
        : !AllowsEdits || Tables.Count == 0 ? ReadOnlyReason ?? "Rows cannot be added or deleted in this result."
        : Tables.Count > 1 ? $"Adding and deleting rows works on results from one table; this one reads {string.Join(", ", Tables.Select(t => t.Table.FullName))}."
        : Tables[0].Table.Type != DbObjectType.Table ? "Rows cannot be added or deleted: they come from a view."
        : $"Adding and deleting rows needs the primary key (or a unique key) of {Tables[0].Table.FullName} in the result.";

    /// <summary>A value can be typed into this column of a new row: a writable column of <see cref="RowTable"/>.</summary>
    public bool CanSetInNewRow(int column) =>
        RowTable is not null && ColumnSource(column) is { } s && IsWritable(s.Column, ProviderKey);

    public bool HasReferences => References.Any(r => r is not null);
}

/// <summary>
/// Works out <see cref="ResultSource"/> for the result sets of a script from its SQL text and the cached catalog, without
/// asking the server. Only plain SELECTs are understood (one or several FROM items, joins, aliases, *, t.*, column
/// [AS] alias); anything else — or a result whose columns do not match what the statement says — gets no source,
/// so a guess never leads to an edit of the wrong column.
/// </summary>
public static class ResultSourceResolver
{
    /// <summary>One source per result set (null where unknown). Result sets are matched to the script's SELECT statements
    /// in order, which only works when every result comes from such a statement.</summary>
    public static IReadOnlyList<ResultSource?> ResolveScript(
        string script, IReadOnlyList<IReadOnlyList<string>> resultColumns, MetadataSnapshot snapshot, string providerKey, string? database)
    {
        var none = resultColumns.Select(_ => (ResultSource?)null).ToList();
        if (resultColumns.Count == 0) return none;

        var statements = SqlScriptTools.SplitStatements(script).Select(r => r.Of(script)).ToList();
        var selects = new List<string>();
        foreach (var statement in statements)
        {
            var kind = SqlAnatomy.KindOf(statement);
            if (kind == DmlKind.Select) selects.Add(statement);
            else if (kind == DmlKind.Other && !IsQuiet(statement)) return none; // EXEC, CALL, … may return rows of their own
            else if (kind is DmlKind.Insert or DmlKind.Update or DmlKind.Delete or DmlKind.Merge &&
                     SqlAnatomy.ParseDml(statement) is not { ReturningAt: < 0 }) return none; // OUTPUT / RETURNING rows
        }
        if (selects.Count != resultColumns.Count) return none;

        return selects.Select((s, i) => Resolve(s, resultColumns[i], snapshot, providerKey, database)).ToList();
    }

    /// <summary>Statements that never return rows (variables, settings, transactions, DDL).</summary>
    private static bool IsQuiet(string statement)
    {
        var first = SqlLexer.Tokenize(statement).FirstOrDefault(t => !t.IsTrivia);
        return first.Kind == SqlTokenKind.Word && first.Text.ToUpperInvariant() is
            "DECLARE" or "SET" or "USE" or "BEGIN" or "COMMIT" or "ROLLBACK" or "START" or "CREATE" or "ALTER" or "DROP" or
            "TRUNCATE" or "GRANT" or "REVOKE" or "PRINT" or "SAVEPOINT" or "RELEASE" or "ANALYZE" or "VACUUM";
    }

    private sealed record Output(string? Name, int? Item, string? Column);

    /// <summary>The source of a single SELECT's result with the given column names, or null when it cannot be told.</summary>
    public static ResultSource? Resolve(string sql, IReadOnlyList<string> resultColumns, MetadataSnapshot snapshot, string providerKey, string? database)
    {
        var select = SqlAnatomy.ParseSelect(sql);
        if (select is null || select.HasSetOperation || select.From.Count == 0) return null;

        var cteNames = CteNames(select.Prefix);
        var items = select.From
            .Select(f => f.Name is { } name && !(IsSinglePart(name) && cteNames.Contains(Unquote(name))) ? FindTable(snapshot, name, providerKey, database) : null)
            .ToList();
        var itemColumns = items
            .Select(t => t is null ? null : snapshot.ColumnsOf(t.Database, t.Schema, t.Name).OrderBy(c => c.Ordinal).ToList())
            .ToList();

        // Expand the select list into one entry per result column.
        var outputs = new List<Output>();
        foreach (var part in SqlAnatomy.SplitList(select.SelectList))
        {
            var tokens = SqlLexer.Tokenize(part).Where(t => !t.IsTrivia).ToList();
            if (tokens.Count == 0) return null;

            if (tokens.Count == 1 && tokens[0].Kind == SqlTokenKind.Operator && tokens[0].Text == "*")
            {
                for (var i = 0; i < items.Count; i++)
                {
                    if (itemColumns[i] is not { Count: > 0 } columns) return null; // a derived table's width is unknown
                    outputs.AddRange(columns.Select(c => new Output(c.Name, i, c.Name)));
                }
                continue;
            }

            if (tokens.Count >= 3 && tokens[^1].Text == "*" && tokens[^2].Kind == SqlTokenKind.Dot)
            {
                var qualifier = tokens[^3].Identifier;
                var item = FindItem(select.From, items, qualifier);
                if (item < 0 || itemColumns[item] is not { Count: > 0 } columns) return null;
                outputs.AddRange(columns.Select(c => new Output(c.Name, item, c.Name)));
                continue;
            }

            outputs.Add(ParseItem(tokens, select.From, items, itemColumns));
        }

        if (outputs.Count != resultColumns.Count) return null;

        var tables = new List<ResultTable>();
        var tableOfItem = new Dictionary<int, int>();
        var columnSources = new ResultColumnSource?[outputs.Count];
        for (var i = 0; i < outputs.Count; i++)
        {
            var o = outputs[i];
            // The server's column names must agree with the statement, or the parse misread it.
            if (o.Name is not null && !string.Equals(o.Name, resultColumns[i], StringComparison.OrdinalIgnoreCase)) return null;
            if (o.Item is not { } item || o.Column is null || itemColumns[item] is not { } columns) continue;
            var column = columns.FirstOrDefault(c => string.Equals(c.Name, o.Column, StringComparison.OrdinalIgnoreCase));
            if (column is null) continue;
            if (!tableOfItem.TryGetValue(item, out var t))
            {
                t = tables.Count;
                tableOfItem[item] = t;
                tables.Add(new ResultTable(items[item]!, []));
            }
            columnSources[i] = new ResultColumnSource(t, column);
        }

        // Keys: the primary key, else a unique key, all of whose columns are in the result.
        for (var t = 0; t < tables.Count; t++)
        {
            var table = tables[t].Table;
            var mapped = Enumerable.Range(0, columnSources.Length)
                .Where(i => columnSources[i]?.Table == t)
                .GroupBy(i => columnSources[i]!.Column.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var columns = itemColumns[tableOfItem.First(kv => kv.Value == t).Key]!;

            var keys = new List<IReadOnlyList<string>>();
            var pk = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            if (pk.Count > 0) keys.Add(pk);
            var indexes = snapshot.IndexesOf(table.Database, table.Schema, table.Name).Where(x => !x.IsDisabled).ToList();
            keys.AddRange(indexes.Where(x => x.IsPrimaryKey).Select(x => (IReadOnlyList<string>)ErDiagramBuilder.SplitColumns(x.Columns).ToList()));
            keys.AddRange(indexes.Where(x => x.IsUnique && !x.IsPrimaryKey && string.IsNullOrWhiteSpace(x.Filter))
                .Select(x => (IReadOnlyList<string>)ErDiagramBuilder.SplitColumns(x.Columns).ToList())
                .Where(k => k.All(name => columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { IsNullable: false }))
                .OrderBy(k => k.Count));

            var key = keys.FirstOrDefault(k => k.Count > 0 && k.All(mapped.ContainsKey));
            if (key is not null)
                tables[t] = tables[t] with
                {
                    Key = key.Select(name => new ResultKeyColumn(mapped[name], columnSources[mapped[name]]!.Column)).ToList()
                };
        }

        var references = new ResultReference?[outputs.Count];
        for (var t = 0; t < tables.Count; t++)
        {
            var table = tables[t].Table;
            var mapped = Enumerable.Range(0, columnSources.Length)
                .Where(i => columnSources[i]?.Table == t)
                .GroupBy(i => columnSources[i]!.Column.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            // Single-column keys first, so a column in both a simple and a composite foreign key links the simple one.
            foreach (var fk in snapshot.ForeignKeysOf(table.Database, table.Schema, table.Name)
                         .Where(f => !f.IsDisabled).OrderBy(f => ErDiagramBuilder.SplitColumns(f.Columns).Count()))
            {
                var from = ErDiagramBuilder.SplitColumns(fk.Columns).ToList();
                var to = ErDiagramBuilder.SplitColumns(fk.ReferencedColumns).ToList();
                if (from.Count == 0 || from.Count != to.Count || !from.All(mapped.ContainsKey)) continue;
                var pairs = from.Select((c, k) => (mapped[c][0], to[k])).ToList();
                var target = FindTable(snapshot, $"{fk.ReferencedSchema}.{fk.ReferencedTable}", providerKey,
                    string.IsNullOrEmpty(fk.Database) ? database : fk.Database);
                var reference = new ResultReference(fk, target, pairs);
                foreach (var c in from)
                    foreach (var resultColumn in mapped[c])
                        references[resultColumn] ??= reference;
            }
        }

        var aggregates = select.GroupBy is not null || select.Having is not null ||
                         select.Modifiers.Contains("DISTINCT", StringComparison.OrdinalIgnoreCase);
        var source = new ResultSource
        {
            Database = database,
            ProviderKey = providerKey,
            Tables = tables,
            Columns = columnSources,
            References = references,
            AllowsEdits = !aggregates
        };
        return source.HasEditableColumns ? source : source with { ReadOnlyReason = ReadOnlyReason(source, aggregates) };
    }

    private static string ReadOnlyReason(ResultSource source, bool aggregates)
    {
        if (aggregates) return "Read-only: the query groups or de-duplicates rows (GROUP BY / DISTINCT).";
        if (source.Tables.Count == 0) return "Read-only: no result column is a plain column of a known table.";
        if (source.Tables.All(t => t.Table.Type != DbObjectType.Table)) return "Read-only: the rows come from a view.";
        var noKey = source.Tables.Where(t => t.Table.Type == DbObjectType.Table && t.Key.Count == 0).Select(t => t.Table.FullName).ToList();
        return noKey.Count > 0
            ? $"Read-only: the result does not include the primary key of {string.Join(", ", noKey)}."
            : "Read-only: none of the columns can be written (computed, identity or rowversion).";
    }

    /// <summary>A select-list item: a plain column reference (qualified or not, optionally aliased) or an expression.</summary>
    private static Output ParseItem(List<SqlToken> tokens, IReadOnlyList<FromItem> from, List<DbObject?> items, List<List<DbColumn>?> itemColumns)
    {
        string? alias = null;
        var expression = tokens;
        // SQL Server: alias = expression
        if (tokens.Count >= 3 && tokens[0].IsIdentifier && tokens[1].Kind == SqlTokenKind.Operator && tokens[1].Text == "=")
        {
            alias = tokens[0].Identifier;
            expression = tokens.Skip(2).ToList();
        }
        else
        {
            // expression [AS] alias
            var n = tokens.Count;
            if (n >= 3 && tokens[n - 2].Is("AS") && (tokens[n - 1].IsIdentifier || tokens[n - 1].Kind == SqlTokenKind.String))
            {
                alias = tokens[n - 1].Kind == SqlTokenKind.String ? StringValue(tokens[n - 1].Text) : tokens[n - 1].Identifier;
                expression = tokens.Take(n - 2).ToList();
            }
            else if (n >= 2 && tokens[n - 1].IsIdentifier && tokens[n - 2].IsIdentifier)
            {
                alias = tokens[n - 1].Identifier;
                expression = tokens.Take(n - 1).ToList();
            }
        }

        // column | q.column | schema.table.column | db.schema.table.column
        var parts = new List<string>();
        for (var i = 0; i < expression.Count; i++)
        {
            if (i % 2 == 0)
            {
                if (!expression[i].IsIdentifier) return new Output(alias, null, null);
                parts.Add(expression[i].Identifier);
            }
            else if (expression[i].Kind != SqlTokenKind.Dot) return new Output(alias, null, null);
        }
        if (parts.Count == 0 || expression.Count % 2 == 0 || parts.Count > 4) return new Output(alias, null, null);

        var column = parts[^1];
        var name = alias ?? column;
        if (parts.Count >= 2)
        {
            var item = FindItem(from, items, parts[^2]);
            return new Output(name, item < 0 ? null : item, item < 0 ? null : column);
        }

        // Unqualified: the only FROM item that has the column (unknown when a derived table might have it too).
        if (itemColumns.Any(c => c is null)) return new Output(name, null, null);
        var owners = Enumerable.Range(0, items.Count)
            .Where(i => itemColumns[i]!.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return owners.Count == 1 ? new Output(name, owners[0], column) : new Output(name, null, null);
    }

    /// <summary>The FROM item a qualifier refers to: its alias, or the table name when it has none.</summary>
    private static int FindItem(IReadOnlyList<FromItem> from, List<DbObject?> items, string qualifier)
    {
        qualifier = Unquote(qualifier);
        var byAlias = Enumerable.Range(0, from.Count).Where(i => string.Equals(from[i].Alias, qualifier, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byAlias.Count == 1) return byAlias[0];
        if (byAlias.Count > 1) return -1;
        var byName = Enumerable.Range(0, from.Count)
            .Where(i => from[i].Alias is null && from[i].Name is { } n && string.Equals(Unquote(n.Split('.')[^1]), qualifier, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return byName.Count == 1 ? byName[0] : -1;
    }

    /// <summary>The table, view or other table-like object a (1–3 part) name refers to, or null when unknown or ambiguous.</summary>
    public static DbObject? FindTable(MetadataSnapshot snapshot, string name, string providerKey, string? database)
    {
        var parts = SplitName(name);
        if (parts.Count is 0 or > 3) return null;
        var table = parts[^1];
        var schema = parts.Count >= 2 ? parts[^2] : null;
        var db = parts.Count == 3 ? parts[0] : database;
        if (table.StartsWith('#')) return null;

        var candidates = snapshot.Objects
            .Where(o => o.IsTableLike && Same(o.Name, table) &&
                        (schema is null || Same(o.Schema, schema)) &&
                        (string.IsNullOrEmpty(db) || string.IsNullOrEmpty(o.Database) || Same(o.Database, db)))
            .ToList();
        if (candidates.Count > 1 && schema is null)
        {
            var defaultSchema = providerKey switch { "Postgres" => "public", "MySql" => db ?? "", _ => "dbo" };
            var preferred = candidates.Where(o => Same(o.Schema, defaultSchema)).ToList();
            if (preferred.Count > 0) candidates = preferred;
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static List<string> SplitName(string name)
    {
        var tokens = SqlLexer.Tokenize(name).Where(t => !t.IsTrivia).ToList();
        var parts = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i % 2 == 0)
            {
                if (!tokens[i].IsIdentifier) return [];
                parts.Add(tokens[i].Identifier);
            }
            else if (tokens[i].Kind != SqlTokenKind.Dot) return [];
        }
        return parts;
    }

    private static bool IsSinglePart(string name) => SplitName(name).Count == 1;

    /// <summary>Names defined by a leading WITH, so a CTE named like a real table is not taken for it.</summary>
    private static HashSet<string> CteNames(string prefix)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        foreach (var t in SqlLexer.Tokenize(prefix))
        {
            if (t.Kind == SqlTokenKind.OpenParen) depth++;
            else if (t.Kind == SqlTokenKind.CloseParen) depth--;
            else if (depth == 0 && t.IsIdentifier && !(t.Is("WITH") || t.Is("RECURSIVE") || t.Is("AS") || t.Is("MATERIALIZED") || t.Is("NOT")))
                names.Add(t.Identifier);
        }
        return names;
    }

    /// <summary>'text' / N'text' without the quotes.</summary>
    private static string StringValue(string literal)
    {
        var start = literal.IndexOf('\'');
        return start < 0 || literal.Length < start + 2 ? literal : literal[(start + 1)..^1].Replace("''", "'");
    }

    private static string Unquote(string s) => s.Length >= 2 && s[0] is '[' or '"' or '`' ? s[1..^1] : s;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
