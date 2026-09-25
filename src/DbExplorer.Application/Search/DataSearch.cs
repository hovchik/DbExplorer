using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.Search;

public sealed record DataSearchRequest
{
    public required string Term { get; init; }
    public SearchMatchMode Mode { get; init; } = SearchMatchMode.Contains;

    /// <summary>Empty = all schemas.</summary>
    public IReadOnlyCollection<string> Schemas { get; init; } = [];

    /// <summary>Only tables whose name contains this text; empty = all.</summary>
    public string? TableNameFilter { get; init; }

    public bool IncludeViews { get; init; }
    public bool IncludeNumericColumns { get; init; } = true;
    public bool IncludeGuidColumns { get; init; } = true;
    public int MaxMatchesPerTable { get; init; } = 50;
    public int MaxDegreeOfParallelism { get; init; } = 3;
    public int QueryTimeoutSeconds { get; init; } = 30;
    public int LockTimeoutMs { get; init; } = 1000;

    /// <summary>Skip tables whose estimated row count exceeds this; null = no limit.</summary>
    public long? MaxTableRows { get; init; }
}

public abstract record DataSearchEvent;

public sealed record DataMatchFound(DataMatch Match) : DataSearchEvent;

public sealed record TableSearched(string Schema, string Table, int Matches, int Done, int Total) : DataSearchEvent;

public sealed record TableSkipped(string Schema, string Table, string Reason, int Done, int Total) : DataSearchEvent;
