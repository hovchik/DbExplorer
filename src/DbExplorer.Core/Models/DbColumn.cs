namespace DbExplorer.Core.Models;

public sealed record DbColumn
{
    public string Schema { get; init; } = "";
    public string Table { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Display type, e.g. nvarchar(50), numeric(18,2).</summary>
    public string DataType { get; init; } = "";

    /// <summary>Underlying system type name used to decide how a column can be searched.</summary>
    public string BaseType { get; init; } = "";

    public bool IsNullable { get; init; }
    public int Ordinal { get; init; }
    public bool IsComputed { get; init; }
    public bool IsPrimaryKey { get; init; }
}
