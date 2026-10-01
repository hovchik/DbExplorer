using System.Globalization;
using System.Text;
using DbExplorer.Application.Export;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

/// <summary>A result row with edited cells: its values as read (the key is taken from them) and the new values by result column.</summary>
public sealed record ResultRowEdit(IReadOnlyList<object?> OriginalValues, IReadOnlyDictionary<int, object?> NewValues);

/// <summary>One UPDATE that writes the edits of one row to one table.</summary>
public sealed record ResultUpdate(string Sql, DbObject Table, IReadOnlyList<object?> OriginalValues);

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
                var where = table.Key.Select(k =>
                {
                    var value = k.ResultColumn < row.OriginalValues.Count ? row.OriginalValues[k.ResultColumn] : null;
                    if (value is null or DBNull)
                        throw new InvalidOperationException($"A row of {table.Table.FullName} has no value in its key column {k.Column.Name}.");
                    return $"{quote(k.Column.Name)} = {Literal(value, dialect)}";
                });
                var sql = $"UPDATE {TableName(table.Table, source.Database, dialect, quote)} SET {string.Join(", ", sets)} WHERE {string.Join(" AND ", where)};";
                updates.Add(new ResultUpdate(sql, table.Table, row.OriginalValues));
            }
        }
        return updates;
    }

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
    /// survive, UTC timestamps carry their offset on PostgreSQL.</summary>
    public static string Literal(object? value, SqlDialect dialect)
    {
        var sqlServer = dialect == SqlDialect.SqlServer;
        string Text(string s) => (sqlServer ? "N'" : "'") + s.Replace("'", "''") + "'";
        return value switch
        {
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
