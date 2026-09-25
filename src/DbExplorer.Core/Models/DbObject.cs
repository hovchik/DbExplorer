namespace DbExplorer.Core.Models;

/// <summary>A schema-level database object (table, view, procedure, function, ...).</summary>
public sealed record DbObject
{
    /// <summary>Empty when the connection targets a single, already-selected database.</summary>
    public string Database { get; init; } = "";
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public DbObjectType Type { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? ModifiedAt { get; init; }

    /// <summary>Approximate row count taken from catalog statistics (never from COUNT(*)).</summary>
    public long? RowCount { get; init; }

    public string FullName => $"{Schema}.{Name}";

    public bool IsRoutine => Type is DbObjectType.Procedure or DbObjectType.Function
        or DbObjectType.ScalarFunction or DbObjectType.TableFunction;

    public bool IsTableLike => Type is DbObjectType.Table or DbObjectType.View
        or DbObjectType.MaterializedView or DbObjectType.ForeignTable;
}
