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

    /// <summary>Whether this token was immediately followed by a line break in the original source text
    /// (Content comparison mode); used to preserve the original SQL formatting/line layout.</summary>
    public bool NewLineAfter { get; init; }

    /// <summary>Used by the flowing "content" view, which renders one merged, highlighted stream of text.</summary>
    public string DisplayText => LeftText ?? RightText ?? "";
}

/// <summary>A single rendered line in the content-mode view: a sequence of diff tokens that belong on
/// the same source line, used so the results window can reproduce the original SQL script layout.</summary>
public sealed record DiffLineGroup
{
    public required IReadOnlyList<DiffLine> Tokens { get; init; }
}
