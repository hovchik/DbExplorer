namespace DbExplorer.Core.Models;

public enum DbParameterDirection
{
    Input,
    InputOutput,
    Output,
    ReturnValue
}

/// <summary>One parameter of a stored procedure or function.</summary>
public sealed record DbRoutineParameter
{
    public string Database { get; init; } = "";
    public string Schema { get; init; } = "";
    public string RoutineName { get; init; } = "";
    public string Name { get; init; } = "";
    public string DataType { get; init; } = "";
    public DbParameterDirection Direction { get; init; }
    public bool HasDefault { get; init; }
    public int Ordinal { get; init; }
}
