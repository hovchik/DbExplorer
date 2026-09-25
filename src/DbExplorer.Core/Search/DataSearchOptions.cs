namespace DbExplorer.Core.Search;

/// <summary>Per-query safety limits applied by a provider when searching a single table.</summary>
public sealed record DataSearchOptions(int MaxMatchesPerTable, int QueryTimeoutSeconds, int LockTimeoutMs);
