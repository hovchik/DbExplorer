namespace DbExplorer.Core.Models;

/// <summary>A CHECK constraint; <see cref="Expression"/> is the condition without the CHECK keyword, e.g. "([Qty]>(0))".</summary>
public sealed record DbCheckConstraint(string Name, string Expression);

/// <summary>Column defaults and check constraints of one table, read on demand (not cached with the catalog).</summary>
public sealed record DbTableConstraints
{
    public static readonly DbTableConstraints None = new();

    /// <summary>Default expression per column name, in the engine's own syntax (e.g. "(getdate())", "now()").</summary>
    public IReadOnlyDictionary<string, string> Defaults { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DbCheckConstraint> Checks { get; init; } = [];
}
