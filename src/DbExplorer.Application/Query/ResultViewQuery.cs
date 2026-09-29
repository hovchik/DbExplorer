using System.Globalization;

namespace DbExplorer.Application.Query;

/// <summary>Conditions a result-grid column filter can apply to a cell.</summary>
public enum ColumnFilterOperator
{
    Contains,
    NotContains,
    Equals,
    NotEquals,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    IsNull,
    IsNotNull,
    IsEmpty
}

/// <summary>One column's filter: an optional condition plus optional sets of allowed and excluded
/// values (display texts, <c>null</c> standing for NULL). A row passes when it satisfies all of them.</summary>
public sealed record ColumnFilter(int Column)
{
    public ColumnFilterOperator? Operator { get; init; }
    public string Value { get; init; } = "";
    public IReadOnlySet<string?>? AllowedValues { get; init; }
    public IReadOnlySet<string?>? ExcludedValues { get; init; }

    public bool IsActive => Operator is not null || AllowedValues is not null || ExcludedValues is { Count: > 0 };

    public static bool NeedsValue(ColumnFilterOperator op) =>
        op is not (ColumnFilterOperator.IsNull or ColumnFilterOperator.IsNotNull or ColumnFilterOperator.IsEmpty);

    public static string Describe(ColumnFilterOperator op) => op switch
    {
        ColumnFilterOperator.Contains => "contains",
        ColumnFilterOperator.NotContains => "does not contain",
        ColumnFilterOperator.Equals => "=",
        ColumnFilterOperator.NotEquals => "≠",
        ColumnFilterOperator.StartsWith => "starts with",
        ColumnFilterOperator.EndsWith => "ends with",
        ColumnFilterOperator.GreaterThan => ">",
        ColumnFilterOperator.GreaterOrEqual => "≥",
        ColumnFilterOperator.LessThan => "<",
        ColumnFilterOperator.LessOrEqual => "≤",
        ColumnFilterOperator.IsNull => "is NULL",
        ColumnFilterOperator.IsNotNull => "is not NULL",
        ColumnFilterOperator.IsEmpty => "is empty",
        _ => op.ToString()
    };

    /// <summary>Short text for a filter chip, e.g. <c>price ≥ 10</c> or <c>status in (3)</c>.</summary>
    public string Summary(string columnName)
    {
        var parts = new List<string>();
        if (Operator is { } op)
            parts.Add(NeedsValue(op) ? $"{columnName} {Describe(op)} {Quote(Value)}" : $"{columnName} {Describe(op)}");
        if (AllowedValues is { } values)
            parts.Add(values.Count == 1
                ? $"{columnName} = {Quote(values.First())}"
                : $"{columnName} in ({values.Count:N0} values)");
        if (ExcludedValues is { Count: > 0 } excluded)
            parts.Add(excluded.Count == 1
                ? $"{columnName} ≠ {Quote(excluded.First())}"
                : $"{columnName} not in ({excluded.Count:N0} values)");
        return string.Join(" and ", parts);
    }

    private static string Quote(string? value) => value is null ? "NULL" : value.Length > 30 ? $"'{value[..30]}…'" : $"'{value}'";
}

/// <summary>A sort key: column index and direction. A list of these sorts by the first, then the next, …</summary>
public sealed record ColumnSort(int Column, bool Descending);

/// <summary>Filtering and ordering of fetched result rows, done client-side so the grid can refine
/// a result without re-running the query. Values compare by type (numbers numerically, dates
/// chronologically, text case-insensitively) and NULL sorts first.</summary>
public static class ResultViewQuery
{
    public static IComparer<object?> ValueComparer { get; } = Comparer<object?>.Create(CompareValues);

    /// <summary>The rows that pass the quick filter (any cell contains the text) and every column
    /// filter, in the requested order. Sorting is stable, so unsorted ties keep the fetched order.</summary>
    public static List<T> Apply<T>(
        IReadOnlyList<T> rows,
        Func<T, IReadOnlyList<object?>> values,
        string? quickFilter,
        IReadOnlyCollection<ColumnFilter> filters,
        IReadOnlyList<ColumnSort> sorts)
    {
        var quick = quickFilter?.Trim() ?? "";
        var active = filters.Where(f => f.IsActive).ToList();
        IEnumerable<T> result = rows;

        if (quick.Length > 0)
            result = result.Where(r => values(r).Any(v => DisplayText(v)?.Contains(quick, StringComparison.OrdinalIgnoreCase) == true));
        if (active.Count > 0)
            result = result.Where(r =>
            {
                var cells = values(r);
                return active.All(f => Matches(f, f.Column < cells.Count ? cells[f.Column] : null));
            });

        if (sorts.Count > 0)
        {
            IOrderedEnumerable<T>? ordered = null;
            foreach (var sort in sorts)
            {
                var column = sort.Column;
                object? Key(T r)
                {
                    var cells = values(r);
                    return column < cells.Count ? cells[column] : null;
                }
                ordered = ordered is null
                    ? sort.Descending ? result.OrderByDescending(Key, ValueComparer) : result.OrderBy(Key, ValueComparer)
                    : sort.Descending ? ordered.ThenByDescending(Key, ValueComparer) : ordered.ThenBy(Key, ValueComparer);
            }
            result = ordered!;
        }
        return result.ToList();
    }

    /// <summary>Text a cell is matched against: what the grid shows, or <c>null</c> for NULL.</summary>
    public static string? DisplayText(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString()
    };

    public static bool Matches(ColumnFilter filter, object? value)
    {
        if (filter.AllowedValues is not null || filter.ExcludedValues is not null)
        {
            var text = DisplayText(value);
            if (filter.AllowedValues?.Contains(text) == false || filter.ExcludedValues?.Contains(text) == true)
                return false;
        }
        return filter.Operator is not { } op || Matches(op, filter.Value, value);
    }

    public static bool Matches(ColumnFilterOperator op, string filterValue, object? value)
    {
        var isNull = value is null or DBNull;
        switch (op)
        {
            case ColumnFilterOperator.IsNull: return isNull;
            case ColumnFilterOperator.IsNotNull: return !isNull;
            case ColumnFilterOperator.IsEmpty: return isNull || DisplayText(value)!.Trim().Length == 0;
        }
        var text = DisplayText(value);
        switch (op)
        {
            case ColumnFilterOperator.Contains: return text?.Contains(filterValue, StringComparison.OrdinalIgnoreCase) == true;
            case ColumnFilterOperator.NotContains: return text?.Contains(filterValue, StringComparison.OrdinalIgnoreCase) != true;
            case ColumnFilterOperator.StartsWith: return text?.StartsWith(filterValue, StringComparison.OrdinalIgnoreCase) == true;
            case ColumnFilterOperator.EndsWith: return text?.EndsWith(filterValue, StringComparison.OrdinalIgnoreCase) == true;
        }
        if (isNull) return op == ColumnFilterOperator.NotEquals;

        var comparison = CompareToText(value!, filterValue);
        return op switch
        {
            ColumnFilterOperator.Equals => comparison == 0,
            ColumnFilterOperator.NotEquals => comparison != 0,
            ColumnFilterOperator.GreaterThan => comparison > 0,
            ColumnFilterOperator.GreaterOrEqual => comparison >= 0,
            ColumnFilterOperator.LessThan => comparison < 0,
            ColumnFilterOperator.LessOrEqual => comparison <= 0,
            _ => true
        };
    }

    /// <summary>Compares a cell with typed-in text, parsing the text as the cell's type when it can
    /// (so <c>price &gt; 9</c> is numeric and <c>created &gt; 2024-01-01</c> is by date).</summary>
    private static int CompareToText(object value, string text)
    {
        var trimmed = text.Trim();
        if (ToDouble(value) is { } number && TryParseNumber(trimmed, out var parsedNumber))
            return number.CompareTo(parsedNumber);
        if (ToDateTime(value) is { } date && TryParseDate(trimmed, out var parsedDate))
            return date.CompareTo(parsedDate);
        if (value is TimeSpan span && TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var parsedSpan))
            return span.CompareTo(parsedSpan);
        if (value is bool flag && TryParseBool(trimmed, out var parsedFlag))
            return flag.CompareTo(parsedFlag);
        if (value is Guid guid && Guid.TryParse(trimmed, out var parsedGuid))
            return guid.CompareTo(parsedGuid);
        return string.Compare(DisplayText(value), text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Orders two cells: NULL first, then by type-aware value; mixed types fall back to text.</summary>
    public static int CompareValues(object? a, object? b)
    {
        var aNull = a is null or DBNull;
        var bNull = b is null or DBNull;
        if (aNull || bNull) return aNull == bNull ? 0 : aNull ? -1 : 1;
        return CompareNonNull(a!, b!);
    }

    private static int CompareNonNull(object a, object b)
    {

        if (a is decimal da && b is decimal db) return da.CompareTo(db);
        if (a is DateTimeOffset oa && b is DateTimeOffset ob) return oa.CompareTo(ob);
        if (IsInteger(a) && IsInteger(b) && a is not ulong && b is not ulong)
            return Convert.ToInt64(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToInt64(b, CultureInfo.InvariantCulture));
        if (ToDouble(a) is { } na && ToDouble(b) is { } nb) return na.CompareTo(nb);
        if (ToDateTime(a) is { } ta && ToDateTime(b) is { } tb) return ta.CompareTo(tb);
        if (a is string sa && b is string sb) return CompareText(sa, sb);
        if (a.GetType() == b.GetType() && a is IComparable comparable)
        {
            try { return comparable.CompareTo(b); }
            catch (ArgumentException) { }
        }
        if (a is byte[] ba && b is byte[] bb) return ba.AsSpan().SequenceCompareTo(bb);
        return CompareText(DisplayText(a) ?? "", DisplayText(b) ?? "");
    }

    private static int CompareText(string a, string b)
    {
        var result = string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        return result != 0 ? result : string.CompareOrdinal(a, b);
    }

    /// <summary>Distinct values of a column with how often each occurs, most frequent first
    /// (ties by value), for the column filter's value list. <c>null</c> is NULL.</summary>
    public static IReadOnlyList<(string? Value, int Count)> DistinctValues<T>(
        IEnumerable<T> rows, Func<T, IReadOnlyList<object?>> values, int column, int max, out bool truncated)
    {
        var counts = new Dictionary<string, (object? Sample, int Count)>();
        var nullCount = 0;
        foreach (var row in rows)
        {
            var cells = values(row);
            var cell = column < cells.Count ? cells[column] : null;
            var text = DisplayText(cell);
            if (text is null) { nullCount++; continue; }
            counts[text] = counts.TryGetValue(text, out var entry) ? (entry.Sample, entry.Count + 1) : (cell, 1);
        }

        var ordered = counts
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Value.Sample, ValueComparer)
            .Select(kv => ((string?)kv.Key, kv.Value.Count));
        var list = new List<(string? Value, int Count)>();
        if (nullCount > 0) list.Add((null, nullCount));
        list.AddRange(ordered.Take(Math.Max(0, max - list.Count)));
        truncated = counts.Count + (nullCount > 0 ? 1 : 0) > list.Count;
        return list;
    }

    /// <summary>True when every non-NULL value in the sample is a number, so the column can be right-aligned.</summary>
    public static bool IsNumericColumn(IEnumerable<object?> sample)
    {
        var any = false;
        foreach (var value in sample)
        {
            if (value is null or DBNull) continue;
            if (ToDouble(value) is null) return false;
            any = true;
        }
        return any;
    }

    private static bool IsInteger(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong;

    private static double? ToDouble(object value) => value switch
    {
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => null
    };

    private static DateTime? ToDateTime(object value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.DateTime, // the clock time the grid shows
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => null
    };

    // Cells display in the current culture, so that parse wins; no thousands separators, which would
    // make "9.5" mean 95 where '.' groups digits.
    private static bool TryParseNumber(string text, out double number) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out number) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    private static bool TryParseDate(string text, out DateTime date) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
        DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date);

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.ToLowerInvariant())
        {
            case "1" or "true" or "yes": value = true; return true;
            case "0" or "false" or "no": value = false; return true;
            default: value = false; return bool.TryParse(text, out value);
        }
    }
}
