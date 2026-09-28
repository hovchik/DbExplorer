namespace DbExplorer.Core.Models;

public enum DataRowStatus
{
    Same,
    Different,
    OnlyLeft,
    OnlyRight
}

/// <summary>One column's value on both sides of a data comparison.</summary>
public sealed record DataCellDiff
{
    public required string Column { get; init; }
    public object? LeftValue { get; init; }
    public object? RightValue { get; init; }
    public bool IsDifferent { get; init; }
}

/// <summary>One matched (by key) row from a data comparison between two tables.</summary>
public sealed record DataComparisonRow
{
    /// <summary>The row's key values, e.g. "42" or "42 | 2026-01-01" for a composite key.</summary>
    public required string Key { get; init; }
    public required DataRowStatus Status { get; init; }
    public IReadOnlyList<DataCellDiff> Cells { get; init; } = [];

    public string StatusText => Status switch
    {
        DataRowStatus.Different => "Different",
        DataRowStatus.OnlyLeft => "Only in left",
        DataRowStatus.OnlyRight => "Only in right",
        _ => "Same"
    };

    /// <summary>Number of compared columns whose values differ (0 for rows that exist on one side only).</summary>
    public int DifferentColumnCount => Status == DataRowStatus.Different ? Cells.Count(c => c.IsDifferent) : 0;

    /// <summary>A human-readable summary of the columns that differ, for quick scanning in a grid.</summary>
    public string Summary => Status switch
    {
        DataRowStatus.Same => "",
        DataRowStatus.OnlyLeft => "Row only exists in left",
        DataRowStatus.OnlyRight => "Row only exists in right",
        _ => string.Join("; ", Cells.Where(c => c.IsDifferent)
            .Select(c => $"{c.Column}: {Format(c.LeftValue)} → {Format(c.RightValue)}"))
    };

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => "0x" + Convert.ToHexString(bytes.Length > 32 ? bytes[..32] : bytes) + (bytes.Length > 32 ? "…" : ""),
        string s => s.Length > 200 ? s[..200] + "…" : s,
        _ => value.ToString() ?? ""
    };
}

/// <summary>How many matched rows differ in one column.</summary>
public sealed record ColumnDifferenceCount(string Column, int Count);

/// <summary>What a data comparison treats as equal, and which columns it skips.</summary>
public sealed record DataCompareOptions
{
    public static readonly DataCompareOptions Default = new();

    /// <summary>Columns left out of the comparison (e.g. audit timestamps). Key columns are still used for matching.</summary>
    public IReadOnlyCollection<string> IgnoredColumns { get; init; } = [];

    /// <summary>Text values that differ only in letter case are equal.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Text values that differ only in leading/trailing whitespace (e.g. padded CHAR columns) are equal.</summary>
    public bool TrimWhitespace { get; init; }
}

/// <summary>The outcome of comparing the rows of two (possibly remote) tables.</summary>
public sealed record DataComparisonResult
{
    public IReadOnlyList<DataComparisonRow> Rows { get; init; } = [];
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>The columns whose values were compared.</summary>
    public IReadOnlyList<string> ComparedColumns { get; init; } = [];

    public IReadOnlyList<string> ColumnsOnlyInLeft { get; init; } = [];
    public IReadOnlyList<string> ColumnsOnlyInRight { get; init; } = [];
    public IReadOnlyList<string> IgnoredColumns { get; init; } = [];

    /// <summary>Per compared column, how many matched rows differ in it; most frequent first, zero counts omitted.</summary>
    public IReadOnlyList<ColumnDifferenceCount> ColumnDifferences { get; init; } = [];

    public bool UsedFallbackKey { get; init; }
    public bool LeftTruncated { get; init; }
    public bool RightTruncated { get; init; }

    /// <summary>Rows read from each side.</summary>
    public int LeftRowCount { get; init; }
    public int RightRowCount { get; init; }

    /// <summary>Rows whose key repeats an earlier row on the same side; only the first one is compared.</summary>
    public int LeftDuplicateKeys { get; init; }
    public int RightDuplicateKeys { get; init; }

    public TimeSpan Elapsed { get; init; }
}
