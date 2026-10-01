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

    /// <summary>Not a constraint in the database: a relationship DbExplorer inferred from names and data that the user
    /// accepted. Diagrams, relations and join suggestions use it; schema comparison and history ignore it.</summary>
    public bool IsVirtual { get; init; }
}
