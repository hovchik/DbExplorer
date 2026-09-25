namespace DbExplorer.Core.Models;

/// <summary>A schema-level database object (table, view, procedure, function, ...).</summary>
public sealed record DbObject
{
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public DbObjectType Type { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? ModifiedAt { get; init; }

    /// <summary>Approximate row count taken from catalog statistics (never from COUNT(*)).</summary>
    public long? RowCount { get; init; }

    public string FullName => $"{Schema}.{Name}";

    public bool IsTableLike => Type is DbObjectType.Table or DbObjectType.View
        or DbObjectType.MaterializedView or DbObjectType.ForeignTable;
}
