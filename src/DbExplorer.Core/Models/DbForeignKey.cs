namespace DbExplorer.Core.Models;

/// <summary>A foreign key constraint linking a child (referencing) table to a parent (referenced) table.</summary>
public sealed record DbForeignKey
{
    public string Database { get; init; } = "";
    public string Name { get; init; } = "";

    public string Schema { get; init; } = "";
    public string Table { get; init; } = "";
    public string? Columns { get; init; }

    public string ReferencedSchema { get; init; } = "";
    public string ReferencedTable { get; init; } = "";
    public string? ReferencedColumns { get; init; }

    public bool IsDisabled { get; init; }
}
