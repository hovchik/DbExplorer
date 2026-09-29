namespace DbExplorer.Core.Models;

/// <summary>One result set produced by an executed script or routine call.</summary>
public sealed record QueryResultSet
{
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];

    /// <summary>True when the result set had more rows than the row limit; only the first ones are in <see cref="Rows"/>.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>Rows the server returned, including those beyond the row limit that were read and discarded.</summary>
    public long TotalRowCount { get; init; }
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
