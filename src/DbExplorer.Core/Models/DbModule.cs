namespace DbExplorer.Core.Models;

/// <summary>Source code of a programmable object (procedure, function, view, trigger).</summary>
public sealed record DbModule
{
    public string Database { get; init; } = "";
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public DbObjectType Type { get; init; }
    public string? Definition { get; init; }
}
