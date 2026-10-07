namespace DbExplorer.Core.Models;

public sealed record DbIndex
{
    public string Database { get; init; } = "";
    public string Schema { get; init; } = "";
    public string Table { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public bool IsUnique { get; init; }
    public bool IsPrimaryKey { get; init; }

    /// <summary>The index behind a PRIMARY KEY or UNIQUE constraint (PostgreSQL, SQL Server): it goes away with
    /// ALTER TABLE … DROP CONSTRAINT, not DROP INDEX. Always false on MySQL, where a unique key is an index.</summary>
    public bool IsConstraint { get; init; }
    public bool IsDisabled { get; init; }
    public string? Columns { get; init; }
    public string? IncludedColumns { get; init; }
    public string? Filter { get; init; }
    public long? Rows { get; init; }
    public long? SizeBytes { get; init; }
    public long? Seeks { get; init; }
    public long? Scans { get; init; }
    public long? Updates { get; init; }
    public double? FragmentationPercent { get; init; }

    public double? SizeMb => SizeBytes is long b ? Math.Round(b / 1024d / 1024d, 2) : null;
}
