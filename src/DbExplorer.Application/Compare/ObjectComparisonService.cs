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
        CancellationToken ct = default)
    {
        var leftDefinition = await definitions.GetDefinitionAsync(leftSession, leftObject, ct) ?? "";
        var rightDefinition = await definitions.GetDefinitionAsync(rightSession, rightObject, ct) ?? "";
        return TextDiffer.Diff(leftDefinition, rightDefinition);
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
        int rowLimit, CancellationToken ct = default)
    {
        var leftColumns = leftSession.Snapshot.ColumnsOf(leftObject.Database, leftObject.Schema, leftObject.Name)
            .OrderBy(c => c.Ordinal).ToList();
        var rightColumns = rightSession.Snapshot.ColumnsOf(rightObject.Database, rightObject.Schema, rightObject.Name)
            .OrderBy(c => c.Ordinal).ToList();

        var rightNames = rightColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commonColumns = leftColumns.Where(c => rightNames.Contains(c.Name)).Select(c => c.Name).ToList();
        if (commonColumns.Count == 0)
            throw new InvalidOperationException("The two objects have no columns in common.");

        var keyColumns = leftColumns.Where(c => c.IsPrimaryKey && commonColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Name).ToList();
        var usedFallbackKey = keyColumns.Count == 0;
        if (usedFallbackKey) keyColumns = commonColumns;

        var leftSql = BuildSelect(leftObject, commonColumns, keyColumns, rowLimit, leftSession.Provider.ProviderKey, leftSession.Provider.QuoteIdentifier);
        var rightSql = BuildSelect(rightObject, commonColumns, keyColumns, rowLimit, rightSession.Provider.ProviderKey, rightSession.Provider.QuoteIdentifier);

        var leftResultTask = queryService.ExecuteScriptAsync(leftSession, leftSql, leftObject.Database, timeoutSeconds: 60, ct);
        var rightResultTask = queryService.ExecuteScriptAsync(rightSession, rightSql, rightObject.Database, timeoutSeconds: 60, ct);
        await Task.WhenAll(leftResultTask, rightResultTask);

        var leftRows = leftResultTask.Result.ResultSets.FirstOrDefault()?.Rows ?? [];
        var rightRows = rightResultTask.Result.ResultSets.FirstOrDefault()?.Rows ?? [];
        var leftResultColumns = leftResultTask.Result.ResultSets.FirstOrDefault()?.Columns ?? commonColumns;
        var rightResultColumns = rightResultTask.Result.ResultSets.FirstOrDefault()?.Columns ?? commonColumns;

        var keyIndexesLeft = keyColumns.Select(k => IndexOf(leftResultColumns, k)).ToList();
        var keyIndexesRight = keyColumns.Select(k => IndexOf(rightResultColumns, k)).ToList();

        var leftByKey = ToKeyedDictionary(leftRows, leftResultColumns, keyIndexesLeft);
        var rightByKey = ToKeyedDictionary(rightRows, rightResultColumns, keyIndexesRight);

        var rows = new List<DataComparisonRow>();
        foreach (var key in leftByKey.Keys.Union(rightByKey.Keys, StringComparer.Ordinal))
        {
            var hasLeft = leftByKey.TryGetValue(key, out var leftRow);
            var hasRight = rightByKey.TryGetValue(key, out var rightRow);

            if (hasLeft && hasRight)
            {
                var cells = commonColumns.Select(col =>
                {
                    var lv = GetValue(leftRow!, leftResultColumns, col);
                    var rv = GetValue(rightRow!, rightResultColumns, col);
                    return new DataCellDiff { Column = col, LeftValue = lv, RightValue = rv, IsDifferent = !ValuesEqual(lv, rv) };
                }).ToList();

                rows.Add(new DataComparisonRow
                {
                    Key = key,
                    Status = cells.Any(c => c.IsDifferent) ? DataRowStatus.Different : DataRowStatus.Same,
                    Cells = cells
                });
            }
            else if (hasLeft)
            {
                var cells = commonColumns.Select(col => new DataCellDiff
                {
                    Column = col, LeftValue = GetValue(leftRow!, leftResultColumns, col), RightValue = null, IsDifferent = true
                }).ToList();
                rows.Add(new DataComparisonRow { Key = key, Status = DataRowStatus.OnlyLeft, Cells = cells });
            }
            else
            {
                var cells = commonColumns.Select(col => new DataCellDiff
                {
                    Column = col, LeftValue = null, RightValue = GetValue(rightRow!, rightResultColumns, col), IsDifferent = true
                }).ToList();
                rows.Add(new DataComparisonRow { Key = key, Status = DataRowStatus.OnlyRight, Cells = cells });
            }
        }

        return new DataComparisonResult
        {
            Rows = rows,
            KeyColumns = keyColumns,
            UsedFallbackKey = usedFallbackKey,
            LeftTruncated = leftRows.Count >= rowLimit,
            RightTruncated = rightRows.Count >= rowLimit
        };
    }

    private static Dictionary<string, IReadOnlyList<object?>> ToKeyedDictionary(
        IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlyList<string> columns, IReadOnlyList<int> keyIndexes)
    {
        var dict = new Dictionary<string, IReadOnlyList<object?>>(StringComparer.Ordinal);
        var count = 0;
        foreach (var row in rows)
        {
            if (++count > MaxKeyedRows) break;
            var key = BuildKey(row, keyIndexes);
            dict.TryAdd(key, row);
        }
        return dict;
    }

    private static string BuildKey(IReadOnlyList<object?> row, IReadOnlyList<int> keyIndexes)
    {
        var parts = keyIndexes.Select(i => i >= 0 && i < row.Count ? FormatValue(row[i]) : "");
        return string.Join('\u0001', parts);
    }

    private static object? GetValue(IReadOnlyList<object?> row, IReadOnlyList<string> columns, string column)
    {
        var i = IndexOf(columns, column);
        return i >= 0 && i < row.Count ? row[i] : null;
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return FormatValue(left) == FormatValue(right);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
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
