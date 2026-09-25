namespace DbExplorer.Core.Models;

/// <summary>One result set produced by an executed script or routine call.</summary>
public sealed record QueryResultSet
{
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];
}

/// <summary>The outcome of executing an ad-hoc script or a routine call.</summary>
public sealed record QueryExecutionResult
{
    public IReadOnlyList<QueryResultSet> ResultSets { get; init; } = [];
    public int RowsAffected { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = [];
    public TimeSpan Elapsed { get; init; }

    /// <summary>Output/return parameter values keyed by parameter name (without the @ / prefix).</summary>
    public IReadOnlyDictionary<string, object?> OutputValues { get; init; } =
        new Dictionary<string, object?>();
}
