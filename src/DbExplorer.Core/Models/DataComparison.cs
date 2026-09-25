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
    public required string Key { get; init; }
    public required DataRowStatus Status { get; init; }
    public IReadOnlyList<DataCellDiff> Cells { get; init; } = [];

    /// <summary>A human-readable summary of the columns that differ, for quick scanning in a grid.</summary>
    public string Summary => Status switch
    {
        DataRowStatus.Same => "",
        DataRowStatus.OnlyLeft => "Row only exists in left",
        DataRowStatus.OnlyRight => "Row only exists in right",
        _ => string.Join("; ", Cells.Where(c => c.IsDifferent)
            .Select(c => $"{c.Column}: {Format(c.LeftValue)} \u2192 {Format(c.RightValue)}"))
    };

    private static string Format(object? value) => value is null ? "NULL" : value.ToString() ?? "";
}

/// <summary>The outcome of comparing the rows of two (possibly remote) tables.</summary>
public sealed record DataComparisonResult
{
    public IReadOnlyList<DataComparisonRow> Rows { get; init; } = [];
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
    public bool UsedFallbackKey { get; init; }
    public bool LeftTruncated { get; init; }
    public bool RightTruncated { get; init; }
}
