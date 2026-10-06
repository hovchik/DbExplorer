using System.Globalization;

namespace DbExplorer.Application.Query;

/// <summary>How the value field of a pivot is summarised in each cell.</summary>
public enum PivotAggregate
{
    Count,
    Sum,
    Average,
    Min,
    Max
}

/// <summary>What to pivot: the columns whose values become row groups, an optional column whose
/// values become result columns, and the value column with its aggregate. Without a value column
/// the cells count rows.</summary>
public sealed record PivotSpec(IReadOnlyList<int> RowFields, int? ColumnField, int? ValueField, PivotAggregate Aggregate)
{
    public static string Describe(PivotAggregate aggregate) => aggregate switch
    {
        PivotAggregate.Average => "Avg",
        _ => aggregate.ToString()
    };
}

/// <summary>A pivoted result: the row field columns, then one column per distinct column-field value
/// (or a single value column), then a total column when there is a column field. The last row holds
/// the grand totals. Empty cells are <c>null</c>.</summary>
public sealed record PivotResult(
    IReadOnlyList<string> Headers,
    IReadOnlyList<object?[]> Rows,
    int RowFieldCount,
    int ColumnValueCount,
    bool ColumnsTruncated)
{
    /// <summary>Index of the grand-total row in <see cref="Rows"/>.</summary>
    public int TotalRowIndex => Rows.Count - 1;
}

/// <summary>Client-side pivot of fetched result rows, so a result can be cross-tabulated without
/// writing a GROUP BY. Values group by what the grid shows (NULL is its own group) and sort the way
/// the grid sorts them. Sum and Avg skip values that aren't numbers; Min and Max compare any type.</summary>
public static class ResultPivot
{
    /// <summary>At most this many distinct column-field values become columns; the rest are left out.</summary>
    public const int MaxColumnValues = 500;

    public const string TotalLabel = "Total";

    public static PivotResult Build<T>(
        IReadOnlyList<T> rows,
        Func<T, IReadOnlyList<object?>> values,
        IReadOnlyList<string> columnNames,
        PivotSpec spec)
    {
        var rowFields = spec.RowFields.Where(f => f >= 0 && f < columnNames.Count).Distinct().ToList();
        var columnField = spec.ColumnField is { } cf && cf >= 0 && cf < columnNames.Count ? cf : (int?)null;
        var valueField = spec.ValueField is { } vf && vf >= 0 && vf < columnNames.Count ? vf : (int?)null;
        var aggregate = valueField is null ? PivotAggregate.Count : spec.Aggregate;

        static object? CellOf(IReadOnlyList<object?> cells, int column) => column < cells.Count ? cells[column] : null;

        // Distinct column-field values, in grid order, capped.
        var columnKeys = new List<(string? Key, object? Sample)>();
        var columnIndex = new Dictionary<string, int>();
        var nullColumn = -1;
        var truncated = false;
        if (columnField is { } field)
        {
            var distinct = new Dictionary<string, object?>();
            var hasNull = false;
            foreach (var row in rows)
            {
                var cell = CellOf(values(row), field);
                var text = ResultViewQuery.DisplayText(cell);
                if (text is null) hasNull = true;
                else distinct.TryAdd(text, cell);
            }
            var ordered = distinct.Select(kv => ((string?)kv.Key, kv.Value))
                .OrderBy(k => k.Value, ResultViewQuery.ValueComparer).ToList();
            if (hasNull) ordered.Insert(0, (null, null)); // NULL sorts first, as in the grid
            truncated = ordered.Count > MaxColumnValues;
            columnKeys.AddRange(ordered.Take(MaxColumnValues));
            for (var i = 0; i < columnKeys.Count; i++)
            {
                if (columnKeys[i].Key is { } key) columnIndex[key] = i;
                else nullColumn = i;
            }
        }
        var cellCount = columnField is null ? 1 : columnKeys.Count;

        // Group rows; each group has an accumulator per column value plus one for its row total.
        var groups = new Dictionary<string, (object?[] Keys, Accumulator[] Cells)>();
        var columnTotals = NewAccumulators(cellCount + 1, aggregate);
        foreach (var row in rows)
        {
            var cells = values(row);
            int slot;
            if (columnField is { } field2)
            {
                var text = ResultViewQuery.DisplayText(CellOf(cells, field2));
                slot = text is null ? nullColumn : columnIndex.GetValueOrDefault(text, -1);
                if (slot < 0) continue; // a column value beyond the cap
            }
            else slot = 0;

            var groupKey = GroupKey(cells, rowFields);
            if (!groups.TryGetValue(groupKey, out var group))
            {
                group = (rowFields.Select(f => CellOf(cells, f)).ToArray(), NewAccumulators(cellCount + 1, aggregate));
                groups[groupKey] = group;
            }

            var value = valueField is { } v ? CellOf(cells, v) : null;
            var countsRow = valueField is null;
            group.Cells[slot].Add(value, countsRow);
            group.Cells[cellCount].Add(value, countsRow);
            columnTotals[slot].Add(value, countsRow);
            columnTotals[cellCount].Add(value, countsRow);
        }

        var sortedGroups = groups.Values.ToList();
        sortedGroups.Sort((a, b) =>
        {
            for (var i = 0; i < a.Keys.Length; i++)
            {
                var c = ResultViewQuery.CompareValues(a.Keys[i], b.Keys[i]);
                if (c != 0) return c;
            }
            return 0;
        });

        var hasTotalColumn = columnField is not null;
        var width = rowFields.Count + cellCount + (hasTotalColumn ? 1 : 0);
        var result = new List<object?[]>(sortedGroups.Count + 1);
        if (rowFields.Count > 0) // without row fields the only group is the total itself
            foreach (var (keys, accumulators) in sortedGroups)
                result.Add(Line(keys, accumulators));

        var totalKeys = new object?[rowFields.Count];
        if (totalKeys.Length > 0) totalKeys[0] = TotalLabel;
        result.Add(Line(totalKeys, columnTotals));

        var valueName = valueField is { } vn ? $"{PivotSpec.Describe(aggregate)} of {columnNames[vn]}" : "Count";
        var headers = rowFields.Select(f => columnNames[f]).ToList();
        if (columnField is null) headers.Add(valueName);
        else
        {
            headers.AddRange(columnKeys.Select(k => k.Key ?? "NULL"));
            headers.Add(TotalLabel);
        }
        return new PivotResult(headers, result, rowFields.Count, cellCount, truncated);

        object?[] Line(object?[] keys, Accumulator[] accumulators)
        {
            var line = new object?[width];
            Array.Copy(keys, line, keys.Length);
            for (var i = 0; i < cellCount; i++) line[keys.Length + i] = accumulators[i].Result;
            if (hasTotalColumn) line[width - 1] = accumulators[cellCount].Result;
            return line;
        }
    }

    private static string GroupKey(IReadOnlyList<object?> cells, List<int> fields)
    {
        if (fields.Count == 0) return "";
        // NULL and text are kept apart by a prefix, and fields by a separator that display text won't contain.
        return string.Join('\u001F', fields.Select(f =>
            ResultViewQuery.DisplayText(f < cells.Count ? cells[f] : null) is { } text ? "v" + text : "\u0000"));
    }

    private static Accumulator[] NewAccumulators(int count, PivotAggregate aggregate)
    {
        var list = new Accumulator[count];
        for (var i = 0; i < count; i++) list[i] = new Accumulator(aggregate);
        return list;
    }

    /// <summary>Running summary of one pivot cell. Sums stay exact as decimals until a float or a
    /// value too large for decimal shows up, then continue as doubles.</summary>
    private sealed class Accumulator(PivotAggregate aggregate)
    {
        private int _count;
        private int _numbers;
        private decimal _decimalSum;
        private double _doubleSum;
        private bool _useDouble;
        private object? _extreme;

        public void Add(object? value, bool countsRow)
        {
            if (countsRow) { _count++; return; }
            if (value is null or DBNull) return;
            _count++;
            switch (aggregate)
            {
                case PivotAggregate.Sum or PivotAggregate.Average:
                    AddNumber(value);
                    break;
                case PivotAggregate.Min:
                    if (_extreme is null || ResultViewQuery.CompareValues(value, _extreme) < 0) _extreme = value;
                    break;
                case PivotAggregate.Max:
                    if (_extreme is null || ResultViewQuery.CompareValues(value, _extreme) > 0) _extreme = value;
                    break;
            }
        }

        private void AddNumber(object value)
        {
            switch (value)
            {
                case float or double:
                    var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (!_useDouble) { _useDouble = true; _doubleSum = (double)_decimalSum; }
                    _doubleSum += d;
                    break;
                case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                    var m = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    if (_useDouble) _doubleSum += (double)m;
                    else
                    {
                        try { _decimalSum += m; }
                        catch (OverflowException) { _useDouble = true; _doubleSum = (double)_decimalSum + (double)m; }
                    }
                    break;
                default:
                    return; // not a number
            }
            _numbers++;
        }

        public object? Result => aggregate switch
        {
            PivotAggregate.Count => _count == 0 ? null : _count,
            PivotAggregate.Sum => _numbers == 0 ? null : _useDouble ? _doubleSum : _decimalSum,
            PivotAggregate.Average => _numbers == 0 ? null : _useDouble ? _doubleSum / _numbers : _decimalSum / _numbers,
            _ => _extreme
        };
    }
}
