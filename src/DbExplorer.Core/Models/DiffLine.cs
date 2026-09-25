namespace DbExplorer.Core.Models;

public enum DiffLineKind
{
    Equal,
    Removed,
    Added
}

/// <summary>One aligned row of a side-by-side text diff.</summary>
public sealed record DiffLine
{
    public int? LeftLineNumber { get; init; }
    public string? LeftText { get; init; }
    public int? RightLineNumber { get; init; }
    public string? RightText { get; init; }
    public required DiffLineKind Kind { get; init; }
}
