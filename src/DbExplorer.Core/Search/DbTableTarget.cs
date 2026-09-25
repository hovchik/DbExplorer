using DbExplorer.Core.Models;

namespace DbExplorer.Core.Search;

/// <summary>A table to search, with all of its columns (the provider picks the searchable ones).</summary>
public sealed record DbTableTarget(string Schema, string Name, IReadOnlyList<DbColumn> Columns);
