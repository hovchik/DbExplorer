namespace DbExplorer.Core.Models;

/// <summary>A value found by the data search.</summary>
public sealed record DataMatch
{
    public string Database { get; init; } = "";
    public string Schema { get; init; } = "";
    public string Table { get; init; } = "";
    public string Column { get; init; } = "";
    public string? Value { get; init; }

    /// <summary>Primary key of the matching row, e.g. "Id=42", when the table has one.</summary>
    public string? RowKey { get; init; }

    /// <summary>Primary key column/value pairs of the matching row (values as text; null = SQL NULL); empty when the
    /// table has no primary key.</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> KeyValues { get; init; } = [];
}
