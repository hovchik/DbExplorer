using System.Globalization;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Compare;

/// <summary>Compares the definition and/or data of two objects, which may live on different connections.</summary>
public sealed class ObjectComparisonService(DefinitionService definitions, QueryExecutionService queryService)
{
    private const string SqlServerProviderKey = "SqlServer";
    private const int MaxKeyedRows = 20_000;

    public async Task<IReadOnlyList<DiffLine>> CompareSchemaAsync(
        DatabaseSession leftSession, DbObject leftObject,
        DatabaseSession rightSession, DbObject rightObject,
        SchemaCompareMode mode = SchemaCompareMode.LineByLine,
        DiffOptions? options = null,
        CancellationToken ct = default)
    {
        var leftTask = definitions.GetDefinitionAsync(leftSession, leftObject, ct);
        var rightTask = definitions.GetDefinitionAsync(rightSession, rightObject, ct);
        await Task.WhenAll(leftTask, rightTask);
        return TextDiffer.Diff(leftTask.Result ?? "", rightTask.Result ?? "", mode, options);
    }

    /// <summary>Loads the full (limited) row set of a single table/view, with no comparison involved.</summary>
    public Task<QueryExecutionResult> LoadTableDataAsync(
        DatabaseSession session, DbObject table, int limit, CancellationToken ct = default)
    {
        var columns = session.Snapshot.ColumnsOf(table.Database, table.Schema, table.Name)
            .OrderBy(c => c.Ordinal).Select(c => c.Name).ToList();
        var sql = BuildFullSelect(table, columns, limit, session.Provider.ProviderKey, session.Provider.QuoteIdentifier);
        return queryService.ExecuteScriptAsync(session, sql, table.Database, timeoutSeconds: 60, ct);
    }

    private static string BuildFullSelect(
        DbObject table, IReadOnlyList<string> columns, int limit, string providerKey, Func<string, string> quote)
    {
        var schema = quote(table.Schema);
        var name = quote(table.Name);
        var columnList = columns.Count > 0 ? string.Join(", ", columns.Select(quote)) : "*";

        return providerKey == SqlServerProviderKey
            ? $"SELECT TOP ({limit}) {columnList} FROM {schema}.{name};"
            : $"SELECT {columnList} FROM {schema}.{name} LIMIT {limit};";
    }

    public async Task<DataComparisonResult> CompareDataAsync(
        DatabaseSession leftSession, DbObject leftObject,
        DatabaseSession rightSession, DbObject rightObject,
        int rowLimit, DataCompareOptions? options = null, CancellationToken ct = default)
    {
        options ??= DataCompareOptions.Default;
        var started = DateTime.UtcNow;
        var leftColumns = leftSession.Snapshot.ColumnsOf(leftObject.Database, leftObject.Schema, leftObject.Name)
            .OrderBy(c => c.Ordinal).ToList();
        var rightColumns = rightSession.Snapshot.ColumnsOf(rightObject.Database, rightObject.Schema, rightObject.Name)
            .OrderBy(c => c.Ordinal).ToList();

        var plan = Plan(leftColumns, rightColumns, options);

        // Rows beyond MaxKeyedRows would be silently dropped from the keyed lookup; cap the
        // fetch itself so the truncation flags below stay accurate and no row is dropped unflagged.
        rowLimit = Math.Min(rowLimit, MaxKeyedRows);

        var leftSql = BuildSelect(leftObject, plan.FetchColumns, plan.KeyColumns, rowLimit, leftSession.Provider.ProviderKey, leftSession.Provider.QuoteIdentifier);
        var rightSql = BuildSelect(rightObject, plan.FetchColumns, plan.KeyColumns, rowLimit, rightSession.Provider.ProviderKey, rightSession.Provider.QuoteIdentifier);

        var leftResultTask = queryService.ExecuteScriptAsync(leftSession, leftSql, leftObject.Database, timeoutSeconds: 60, ct);
        var rightResultTask = queryService.ExecuteScriptAsync(rightSession, rightSql, rightObject.Database, timeoutSeconds: 60, ct);
        await Task.WhenAll(leftResultTask, rightResultTask);
        ct.ThrowIfCancellationRequested();

        var leftSet = leftResultTask.Result.ResultSets.FirstOrDefault();
        var rightSet = rightResultTask.Result.ResultSets.FirstOrDefault();
        var result = MatchRows(
            leftSet?.Columns ?? plan.FetchColumns, leftSet?.Rows ?? [],
            rightSet?.Columns ?? plan.FetchColumns, rightSet?.Rows ?? [],
            plan.ComparedColumns, plan.KeyColumns, options);

        return result with
        {
            UsedFallbackKey = plan.UsedFallbackKey,
            ColumnsOnlyInLeft = plan.OnlyLeft,
            ColumnsOnlyInRight = plan.OnlyRight,
            IgnoredColumns = plan.Ignored,
            LeftTruncated = result.LeftRowCount >= rowLimit,
            RightTruncated = result.RightRowCount >= rowLimit,
            Elapsed = DateTime.UtcNow - started
        };
    }

    private sealed record ComparePlan(
        IReadOnlyList<string> KeyColumns, IReadOnlyList<string> ComparedColumns, IReadOnlyList<string> FetchColumns,
        bool UsedFallbackKey, IReadOnlyList<string> OnlyLeft, IReadOnlyList<string> OnlyRight, IReadOnlyList<string> Ignored);

    private static ComparePlan Plan(IReadOnlyList<DbColumn> leftColumns, IReadOnlyList<DbColumn> rightColumns, DataCompareOptions options)
    {
        var leftNames = leftColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightNames = rightColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignored = options.IgnoredColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var common = leftColumns.Where(c => rightNames.Contains(c.Name)).Select(c => c.Name).ToList();
        if (common.Count == 0)
            throw new InvalidOperationException("The two objects have no columns in common.");

        var keyColumns = leftColumns.Where(c => c.IsPrimaryKey && rightNames.Contains(c.Name)).Select(c => c.Name).ToList();
        var compared = common.Where(c => !ignored.Contains(c)).ToList();
        if (compared.Count == 0)
            throw new InvalidOperationException("Every common column is ignored; nothing is left to compare.");

        var usedFallbackKey = keyColumns.Count == 0;
        if (usedFallbackKey) keyColumns = compared;

        var fetch = common.Where(c => !ignored.Contains(c) || keyColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

        return new ComparePlan(
            keyColumns,
            compared,
            fetch,
            usedFallbackKey,
            leftColumns.Where(c => !rightNames.Contains(c.Name)).Select(c => c.Name).ToList(),
            rightColumns.Where(c => !leftNames.Contains(c.Name)).Select(c => c.Name).ToList(),
            common.Where(ignored.Contains).ToList());
    }

    /// <summary>Pairs rows of both sides by key and compares every column in <paramref name="comparedColumns"/>.
    /// Pure (no I/O) so it can be unit tested.</summary>
    public static DataComparisonResult MatchRows(
        IReadOnlyList<string> leftColumns, IReadOnlyList<IReadOnlyList<object?>> leftRows,
        IReadOnlyList<string> rightColumns, IReadOnlyList<IReadOnlyList<object?>> rightRows,
        IReadOnlyList<string> comparedColumns, IReadOnlyList<string> keyColumns, DataCompareOptions? options = null)
    {
        options ??= DataCompareOptions.Default;
        var keyIndexesLeft = keyColumns.Select(k => IndexOf(leftColumns, k)).ToList();
        var keyIndexesRight = keyColumns.Select(k => IndexOf(rightColumns, k)).ToList();
        var leftIndexes = comparedColumns.Select(c => IndexOf(leftColumns, c)).ToList();
        var rightIndexes = comparedColumns.Select(c => IndexOf(rightColumns, c)).ToList();

        var leftByKey = ToKeyedDictionary(leftRows, keyIndexesLeft, out var leftDuplicates);
        var rightByKey = ToKeyedDictionary(rightRows, keyIndexesRight, out var rightDuplicates);
        var differencesPerColumn = new int[comparedColumns.Count];

        var rows = new List<DataComparisonRow>(Math.Max(leftByKey.Count, rightByKey.Count));
        foreach (var key in leftByKey.Keys.Union(rightByKey.Keys, StringComparer.Ordinal))
        {
            var hasLeft = leftByKey.TryGetValue(key, out var leftRow);
            var hasRight = rightByKey.TryGetValue(key, out var rightRow);
            var displayKey = DisplayKey(hasLeft ? leftRow!.Row : rightRow!.Row, hasLeft ? keyIndexesLeft : keyIndexesRight);

            var cells = new List<DataCellDiff>(comparedColumns.Count);
            var anyDifferent = false;
            for (var i = 0; i < comparedColumns.Count; i++)
            {
                var lv = hasLeft ? At(leftRow!.Row, leftIndexes[i]) : null;
                var rv = hasRight ? At(rightRow!.Row, rightIndexes[i]) : null;
                var different = !hasLeft || !hasRight || !ValuesEqual(lv, rv, options);
                if (hasLeft && hasRight && different)
                {
                    differencesPerColumn[i]++;
                    anyDifferent = true;
                }
                cells.Add(new DataCellDiff { Column = comparedColumns[i], LeftValue = lv, RightValue = rv, IsDifferent = different });
            }

            var status = !hasRight ? DataRowStatus.OnlyLeft
                : !hasLeft ? DataRowStatus.OnlyRight
                : anyDifferent ? DataRowStatus.Different
                : DataRowStatus.Same;
            rows.Add(new DataComparisonRow { Key = displayKey, Status = status, Cells = cells });
        }

        return new DataComparisonResult
        {
            Rows = rows,
            KeyColumns = keyColumns,
            ComparedColumns = comparedColumns,
            ColumnDifferences = comparedColumns
                .Select((c, i) => new ColumnDifferenceCount(c, differencesPerColumn[i]))
                .Where(c => c.Count > 0)
                .OrderByDescending(c => c.Count)
                .ToList(),
            LeftRowCount = leftRows.Count,
            RightRowCount = rightRows.Count,
            LeftDuplicateKeys = leftDuplicates,
            RightDuplicateKeys = rightDuplicates
        };
    }

    private sealed record KeyedRow(IReadOnlyList<object?> Row);

    private static Dictionary<string, KeyedRow> ToKeyedDictionary(
        IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlyList<int> keyIndexes, out int duplicates)
    {
        var dict = new Dictionary<string, KeyedRow>(StringComparer.Ordinal);
        duplicates = 0;
        var count = 0;
        foreach (var row in rows)
        {
            if (++count > MaxKeyedRows) break;
            if (!dict.TryAdd(BuildKey(row, keyIndexes), new KeyedRow(row))) duplicates++;
        }
        return dict;
    }

    private static string BuildKey(IReadOnlyList<object?> row, IReadOnlyList<int> keyIndexes)
    {
        // A NULL key part must not collide with an empty string: prefix every part with a type marker.
        var parts = keyIndexes.Select(i => At(row, i) is { } v ? "v" + FormatValue(v) : "\u0000");
        return string.Join('\u0001', parts);
    }

    private static string DisplayKey(IReadOnlyList<object?> row, IReadOnlyList<int> keyIndexes) =>
        string.Join(" | ", keyIndexes.Select(i => At(row, i) is { } v ? Truncate(FormatValue(v), 60) : "NULL"));

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "\u2026" : s;

    private static object? At(IReadOnlyList<object?> row, int index) =>
        index >= 0 && index < row.Count ? row[index] is DBNull ? null : row[index] : null;

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Equality that tolerates provider differences: numbers compare by value (1.0 = 1.00, int = bigint),
    /// binary by content, text optionally case- and padding-insensitive.</summary>
    internal static bool ValuesEqual(object? left, object? right, DataCompareOptions options)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;

        if (TryDecimal(left, out var ld) && TryDecimal(right, out var rd)) return ld == rd;
        if (left is double or float || right is double or float)
        {
            if (TryDouble(left, out var ldb) && TryDouble(right, out var rdb)) return ldb.Equals(rdb);
        }

        if (left is string ls && right is string rs)
        {
            if (options.TrimWhitespace)
            {
                ls = ls.Trim();
                rs = rs.Trim();
            }
            return string.Equals(ls, rs, options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        return FormatValue(left) == FormatValue(right);
    }

    private static bool TryDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case decimal d: result = d; return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture); return true;
            default: result = 0; return false;
        }
    }

    private static bool TryDouble(object value, out double result)
    {
        switch (value)
        {
            case double or float or decimal or byte or sbyte or short or ushort or int or uint or long or ulong:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture); return true;
            default: result = 0; return false;
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        decimal d => (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string BuildSelect(
        DbObject table, IReadOnlyList<string> columns, IReadOnlyList<string> orderBy,
        int limit, string providerKey, Func<string, string> quote)
    {
        var schema = quote(table.Schema);
        var name = quote(table.Name);
        var columnList = string.Join(", ", columns.Select(quote));
        var orderList = string.Join(", ", orderBy.Select(quote));

        return providerKey == SqlServerProviderKey
            ? $"SELECT TOP ({limit}) {columnList} FROM {schema}.{name} ORDER BY {orderList};"
            : $"SELECT {columnList} FROM {schema}.{name} ORDER BY {orderList} LIMIT {limit};";
    }
}
