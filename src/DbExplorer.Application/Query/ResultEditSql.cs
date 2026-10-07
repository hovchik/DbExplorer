using System.Globalization;
using System.Text;
using DbExplorer.Application.Export;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

/// <summary>A result row with edited cells: its values as read (the key is taken from them) and the new values by result column.</summary>
public sealed record ResultRowEdit(IReadOnlyList<object?> OriginalValues, IReadOnlyDictionary<int, object?> NewValues);

/// <summary>What a statement built from result grid changes does to its row.</summary>
public enum ResultChangeKind { Update, Delete, Insert }

/// <summary>One statement that writes a change of one result row to one table: the edits of a row (UPDATE), a deleted row
/// (DELETE) or a new row (INSERT).</summary>
public sealed record ResultUpdate(string Sql, DbObject Table, IReadOnlyList<object?> OriginalValues)
{
    public ResultChangeKind Kind { get; init; } = ResultChangeKind.Update;

    /// <summary>For an INSERT: per column the statement returns (the row as stored, with defaults and generated keys), the
    /// result columns that show it. Empty when the stored row cannot be read back.</summary>
    public IReadOnlyList<IReadOnlyList<int>> ReturnedColumns { get; init; } = [];
}

/// <summary>
/// SQL for editing and following query results: UPDATE statements keyed by the table's primary (or unique) key, the
/// SELECT of the row a foreign key value refers to, and turning typed text back into a value of the column's type.
/// </summary>
public static class ResultEditSql
{
    /// <summary>One UPDATE per edited row and table, setting the changed columns and matching the row by the key values it
    /// was read with (so editing a key column works too).</summary>
    /// <exception cref="InvalidOperationException">A cell is not editable or the row has no key value.</exception>
    public static IReadOnlyList<ResultUpdate> BuildUpdates(
        ResultSource source, IEnumerable<ResultRowEdit> rows, SqlDialect dialect, Func<string, string> quote)
    {
        var updates = new List<ResultUpdate>();
        foreach (var row in rows)
        {
            foreach (var group in row.NewValues.GroupBy(kv =>
                         source.CanEdit(kv.Key)
                             ? source.Columns[kv.Key]!.Table
                             : throw new InvalidOperationException($"Result column {kv.Key + 1} cannot be edited.")))
            {
                var table = source.Tables[group.Key];
                var sets = group
                    .GroupBy(kv => source.Columns[kv.Key]!.Column.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => $"{quote(g.Key)} = {Literal(g.Last().Value, dialect)}");
                var sql = $"UPDATE {TableName(table.Table, source.Database, dialect, quote)} SET {string.Join(", ", sets)} WHERE {KeyCondition(table, row.OriginalValues, dialect, quote)};";
                updates.Add(new ResultUpdate(sql, table.Table, row.OriginalValues));
            }
        }
        return updates;
    }

    /// <summary>One DELETE per row of <see cref="ResultSource.RowTable"/>, matching it by the key values it was read with.</summary>
    /// <exception cref="InvalidOperationException">Rows of this result cannot be deleted, or a row has no key value.</exception>
    public static IReadOnlyList<ResultUpdate> BuildDeletes(
        ResultSource source, IEnumerable<IReadOnlyList<object?>> originalRows, SqlDialect dialect, Func<string, string> quote)
    {
        var table = source.RowTable ?? throw new InvalidOperationException(source.RowEditReason);
        var name = TableName(table.Table, source.Database, dialect, quote);
        return originalRows
            .Select(row => new ResultUpdate($"DELETE FROM {name} WHERE {KeyCondition(table, row, dialect, quote)};", table.Table, row)
            {
                Kind = ResultChangeKind.Delete
            })
            .ToList();
    }

    /// <summary>
    /// One INSERT per new row into <see cref="ResultSource.RowTable"/>, with the values typed into its cells (by result
    /// column; columns left empty get their defaults). Each statement also returns the row as stored, so generated keys and
    /// defaults show up and the row can be edited afterwards: RETURNING on PostgreSQL; on SQL Server and MySQL a SELECT by
    /// the key, typed or taken from SCOPE_IDENTITY() / LAST_INSERT_ID() for an identity key (nothing is read back when the
    /// key is generated otherwise).
    /// </summary>
    /// <exception cref="InvalidOperationException">Rows cannot be added to this result, or a value is set in a column that
    /// cannot be written.</exception>
    public static IReadOnlyList<ResultUpdate> BuildInserts(
        ResultSource source, IEnumerable<IReadOnlyDictionary<int, object?>> rows, SqlDialect dialect, Func<string, string> quote)
    {
        var table = source.RowTable ?? throw new InvalidOperationException(source.RowEditReason);
        var name = TableName(table.Table, source.Database, dialect, quote);
        var shown = Enumerable.Range(0, source.Columns.Count)
            .Where(i => source.Columns[i] is not null)
            .GroupBy(i => source.Columns[i]!.Column.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shownList = string.Join(", ", shown.Select(g => quote(g.Key)));
        var returned = shown.Select(g => (IReadOnlyList<int>)g.ToList()).ToList();

        var inserts = new List<ResultUpdate>();
        foreach (var row in rows)
        {
            var values = row
                .Where(kv => source.CanSetInNewRow(kv.Key)
                    ? kv.Value is not (null or DBNull)
                    : throw new InvalidOperationException($"Result column {kv.Key + 1} cannot be set in a new row of {table.Table.FullName}."))
                .GroupBy(kv => source.Columns[kv.Key]!.Column.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Column: g.Key, Value: g.Last().Value))
                .ToList();
            var insert = values.Count == 0
                ? dialect == SqlDialect.MySql ? $"INSERT INTO {name} () VALUES ()" : $"INSERT INTO {name} DEFAULT VALUES"
                : $"INSERT INTO {name} ({string.Join(", ", values.Select(v => quote(v.Column)))}) VALUES ({string.Join(", ", values.Select(v => Literal(v.Value, dialect)))})";

            string sql;
            var readBack = true;
            if (dialect == SqlDialect.SqlServer || dialect == SqlDialect.MySql)
            {
                var lastIdentity = dialect == SqlDialect.MySql ? "LAST_INSERT_ID()" : "SCOPE_IDENTITY()";
                var conditions = new List<string>();
                foreach (var k in table.Key)
                {
                    var typed = values.FirstOrDefault(v => string.Equals(v.Column, k.Column.Name, StringComparison.OrdinalIgnoreCase));
                    if (typed.Column is not null) conditions.Add($"{quote(k.Column.Name)} = {Literal(typed.Value, dialect)}");
                    else if (table.Key.Count == 1 && k.Column.IsIdentity) conditions.Add($"{quote(k.Column.Name)} = {lastIdentity}");
                    else readBack = false;
                }
                sql = insert + ";" + (readBack ? $"\nSELECT {shownList} FROM {name} WHERE {string.Join(" AND ", conditions)};" : "");
            }
            else sql = $"{insert} RETURNING {shownList};";

            inserts.Add(new ResultUpdate(sql, table.Table, new object?[source.Columns.Count])
            {
                Kind = ResultChangeKind.Insert,
                ReturnedColumns = readBack ? returned : []
            });
        }
        return inserts;
    }

    /// <summary>Every change of a result as one ordered script: DELETEs first (so a deleted key can be reused), then UPDATEs,
    /// then INSERTs.</summary>
    public static IReadOnlyList<ResultUpdate> BuildChanges(
        ResultSource source, IEnumerable<IReadOnlyList<object?>> deleted, IEnumerable<ResultRowEdit> edited,
        IEnumerable<IReadOnlyDictionary<int, object?>> added, SqlDialect dialect, Func<string, string> quote)
    {
        var deletes = deleted.ToList();
        var inserts = added.ToList();
        return
        [
            .. deletes.Count > 0 ? BuildDeletes(source, deletes, dialect, quote) : [],
            .. BuildUpdates(source, edited, dialect, quote),
            .. inserts.Count > 0 ? BuildInserts(source, inserts, dialect, quote) : []
        ];
    }

    /// <summary>key1 = value AND key2 = value, from the key values a row was read with.</summary>
    private static string KeyCondition(ResultTable table, IReadOnlyList<object?> originalValues, SqlDialect dialect, Func<string, string> quote) =>
        string.Join(" AND ", table.Key.Select(k =>
        {
            var value = k.ResultColumn < originalValues.Count ? originalValues[k.ResultColumn] : null;
            if (value is null or DBNull)
                throw new InvalidOperationException($"A row of {table.Table.FullName} has no value in its key column {k.Column.Name}.");
            return $"{quote(k.Column.Name)} = {Literal(value, dialect)}";
        }));

    /// <summary>SELECT of the row(s) of the referenced table that <paramref name="row"/>'s foreign key values point to;
    /// null when a value is NULL (nothing referenced).</summary>
    public static string? ReferenceSelect(ResultSource source, ResultReference reference, IReadOnlyList<object?> row, SqlDialect dialect, Func<string, string> quote) =>
        ReferenceCondition(reference, row, dialect, quote) is { } where
            ? $"SELECT * FROM {TableName(ReferencedTable(reference), source.Database, dialect, quote)} WHERE {where};"
            : null;

    /// <summary>The WHERE condition (on the referenced table's columns) for the row a foreign key value points to; null when
    /// a value is NULL.</summary>
    public static string? ReferenceCondition(ResultReference reference, IReadOnlyList<object?> row, SqlDialect dialect, Func<string, string> quote)
    {
        var conditions = new List<string>();
        foreach (var (column, referenced) in reference.Columns)
        {
            var value = column < row.Count ? row[column] : null;
            if (value is null or DBNull) return null;
            conditions.Add($"{quote(referenced)} = {Literal(value, dialect)}");
        }
        return string.Join(" AND ", conditions);
    }

    /// <summary>The referenced table, from the catalog when it is there, else from the foreign key's names.</summary>
    public static DbObject ReferencedTable(ResultReference reference) =>
        reference.ReferencedTable ?? new DbObject
        {
            Database = reference.ForeignKey.Database, Schema = reference.ForeignKey.ReferencedSchema,
            Name = reference.ForeignKey.ReferencedTable, Type = DbObjectType.Table
        };

    /// <summary>schema.table, with the database in front on SQL Server when it is not the one the query ran in.</summary>
    public static string TableName(DbObject table, string? runDatabase, SqlDialect dialect, Func<string, string> quote)
    {
        var name = quote(table.Schema) + "." + quote(table.Name);
        return dialect == SqlDialect.SqlServer && !string.IsNullOrEmpty(table.Database) &&
               !string.Equals(table.Database, runDatabase, StringComparison.OrdinalIgnoreCase)
            ? quote(table.Database) + "." + name
            : name;
    }

    /// <summary>A literal that keeps the value's type: dates and times are cast on SQL Server so precision and offsets
    /// survive, UTC timestamps carry their offset on PostgreSQL. MySQL has no offsets: an instant is written in UTC.</summary>
    public static string Literal(object? value, SqlDialect dialect)
    {
        var sqlServer = dialect == SqlDialect.SqlServer;
        var mySql = dialect == SqlDialect.MySql;
        string Text(string s) => (sqlServer ? "N'" : "'") + s.Replace("'", "''") + "'";
        return value switch
        {
            DateTime dt when mySql => Text(dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)),
            DateTimeOffset dto when mySql => Text(dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)),
            TimeSpan ts when mySql => Text((ts < TimeSpan.Zero ? "-" : "") +
                $"{(long)ts.Duration().TotalHours:00}:{ts.Duration().Minutes:00}:{ts.Duration().Seconds:00}.{ts.Duration().Ticks % TimeSpan.TicksPerSecond / 10:000000}"),
            DateTime dt when sqlServer => $"CAST({Text(ResultExporter.FormatInvariant(dt))} AS datetime2(7))",
            DateTime dt when dt.Kind == DateTimeKind.Utc => Text(dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "+00"),
            DateTimeOffset dto when sqlServer => $"CAST({Text(ResultExporter.FormatInvariant(dto))} AS datetimeoffset(7))",
            DateOnly d when sqlServer => $"CAST({Text(ResultExporter.FormatInvariant(d))} AS date)",
            TimeOnly t when sqlServer => $"CAST({Text(ResultExporter.FormatInvariant(t))} AS time(7))",
            TimeSpan ts when sqlServer => $"CAST({Text(ts.ToString("c", CultureInfo.InvariantCulture))} AS time(7))",
            TimeSpan ts => Text(ts.Days != 0
                ? $"{ts.Days} days {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Ticks % TimeSpan.TicksPerSecond:0000000}"
                : ts.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture)),
            char c => Text(c.ToString()),
            _ => ResultExporter.SqlLiteral(value, dialect)
        };
    }

    /// <summary>Values the grid can turn into text for editing and back into a literal.</summary>
    public static bool IsEditableValue(object? value) => value is null or DBNull or string or char or bool or byte or sbyte or short
        or ushort or int or uint or long or ulong or decimal or double or float or Guid or DateTime or DateTimeOffset or DateOnly
        or TimeOnly or TimeSpan or byte[];

    /// <summary>The text shown in a cell editor; it parses back to the same value.</summary>
    public static string EditText(object? value) => value is null or DBNull ? "" : ResultExporter.FormatInvariant(value);

    /// <summary>
    /// Typed text back as a value of the cell's type — the type of the value it replaces, or, for a NULL cell, the column's
    /// type (text is left to the server to convert). Numbers and dates are read invariantly first, then in the user's culture.
    /// </summary>
    /// <exception cref="FormatException">The text is not a value of that type.</exception>
    public static object? ParseValue(string text, object? original, DbColumn? column)
    {
        var type = original is null or DBNull ? TypeOf(column) : original.GetType();
        if (type is null || type == typeof(string)) return text;

        var trimmed = text.Trim();
        var inv = CultureInfo.InvariantCulture;
        var cur = CultureInfo.CurrentCulture;
        bool TryBoth<T>(Func<IFormatProvider, (bool, T)> parse, out T result)
        {
            var (ok, value) = parse(inv);
            if (!ok) (ok, value) = parse(cur);
            result = value;
            return ok;
        }

        object? parsed = type switch
        {
            _ when type == typeof(char) => trimmed.Length == 1 ? trimmed[0] : text.Length == 1 ? text[0] : null,
            _ when type == typeof(bool) => trimmed.ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "y" or "t" or "on" => true,
                "0" or "false" or "no" or "n" or "f" or "off" => false,
                _ => null
            },
            _ when type == typeof(byte) => byte.TryParse(trimmed, NumberStyles.Integer, inv, out var v) ? v : null,
            _ when type == typeof(sbyte) => sbyte.TryParse(trimmed, NumberStyles.Integer, inv, out var v) ? v : null,
            _ when type == typeof(short) => short.TryParse(trimmed, NumberStyles.Integer, inv, out var v) ? v : null,
            _ when type == typeof(ushort) => ushort.TryParse(trimmed, NumberStyles.Integer, inv, out var v) ? v : null,
            _ when type == typeof(int) => int.TryParse(trimmed, NumberStyles.Integer, inv, out var v) ? v : null,
            _ when type == typeof(uint) => uint.TryParse(trimmed, NumberStyles.Integer, inv, out var v) ? v : null,
            _ when type == typeof(long) => long.TryParse(trimmed, NumberStyles.Integer, inv, out var v) ? v : null,
            _ when type == typeof(ulong) => ulong.TryParse(trimmed, NumberStyles.Integer, inv, out var v) ? v : null,
            _ when type == typeof(decimal) => TryBoth(p => (decimal.TryParse(trimmed, NumberStyles.Number | NumberStyles.AllowExponent, p, out var v), v), out var d) ? d : null,
            _ when type == typeof(double) => TryBoth(p => (double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, p, out var v), v), out var d) ? d : null,
            _ when type == typeof(float) => TryBoth(p => (float.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, p, out var v), v), out var f) ? f : null,
            _ when type == typeof(Guid) => Guid.TryParse(trimmed, out var g) ? g : null,
            _ when type == typeof(DateTime) => TryBoth(p => (DateTime.TryParse(trimmed, p, DateTimeStyles.None, out var v), v), out var dt)
                ? DateTime.SpecifyKind(dt, original is DateTime o ? o.Kind : DateTimeKind.Unspecified)
                : null,
            _ when type == typeof(DateTimeOffset) => TryBoth(p => (DateTimeOffset.TryParse(trimmed, p, DateTimeStyles.None, out var v), v), out var dto) ? dto : null,
            _ when type == typeof(DateOnly) => TryBoth(p => (DateOnly.TryParse(trimmed, p, DateTimeStyles.None, out var v), v), out var d) ? d : null,
            _ when type == typeof(TimeOnly) => TryBoth(p => (TimeOnly.TryParse(trimmed, p, DateTimeStyles.None, out var v), v), out var t) ? t : null,
            _ when type == typeof(TimeSpan) => TryBoth(p => (TimeSpan.TryParse(trimmed, p, out var v), v), out var ts) ? ts : null,
            _ when type == typeof(byte[]) => ParseHex(trimmed),
            _ => throw new FormatException($"Values of type {type.Name} cannot be edited here.")
        };
        return parsed ?? throw new FormatException($"'{Shorten(text)}' is not a valid {FriendlyName(type)}.");
    }

    /// <summary>The CLR type to parse a NULL cell's text as, from the column's declared type; null for text and anything
    /// the server is better at converting (dates, JSON, enums…).</summary>
    private static Type? TypeOf(DbColumn? column) => column?.BaseType.ToLowerInvariant() switch
    {
        "bit" or "bool" or "boolean" => typeof(bool),
        "tinyint" or "smallint" or "int" or "integer" or "bigint" or "int2" or "int4" or "int8" => typeof(long),
        "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
        "float" or "real" or "float4" or "float8" or "double precision" => typeof(double),
        "uniqueidentifier" or "uuid" => typeof(Guid),
        "binary" or "varbinary" or "image" or "bytea" => typeof(byte[]),
        _ => null
    };

    private static byte[]? ParseHex(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..]
            : text.StartsWith("\\x", StringComparison.OrdinalIgnoreCase) ? text[2..]
            : text;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string FriendlyName(Type type) =>
        type == typeof(bool) ? "true / false value"
        : type == typeof(Guid) ? "GUID"
        : type == typeof(byte[]) ? "hex value (0x…)"
        : type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) ? "date"
        : type == typeof(TimeOnly) || type == typeof(TimeSpan) ? "time"
        : type == typeof(char) ? "single character"
        : "number";

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length > 40 ? single[..40] + "…" : single;
    }

    /// <summary>The statements as a script, for previews and confirmations.</summary>
    public static string Script(IEnumerable<ResultUpdate> updates)
    {
        var sb = new StringBuilder();
        foreach (var u in updates) sb.AppendLine(u.Sql);
        return sb.ToString();
    }
}
