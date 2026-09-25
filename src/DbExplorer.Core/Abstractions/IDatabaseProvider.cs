using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Core.Abstractions;

/// <summary>
/// Everything the application needs from a database engine. Implementations must be
/// read-only and must never hold locks that block other sessions: use dirty/snapshot reads,
/// lock timeouts and statement timeouts.
/// </summary>
public interface IDatabaseProvider : IAsyncDisposable
{
    string ProviderKey { get; }

    string QuoteIdentifier(string identifier);

    Task<string> GetServerVersionAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DbObject>> GetObjectsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DbColumn>> GetColumnsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DbModule>> GetModulesAsync(CancellationToken ct = default);

    Task<string?> GetDefinitionAsync(DbObject obj, CancellationToken ct = default);

    Task<IReadOnlyList<DbIndex>> GetIndexesAsync(bool includePhysicalStats, CancellationToken ct = default);

    Task<IReadOnlyList<DbLock>> GetLocksAsync(CancellationToken ct = default);

    /// <summary>Whether the column's type can be compared with the term.</summary>
    bool IsSearchable(DbColumn column, SearchTerm term);

    /// <summary>Searches one table in a single scan, returning at most MaxMatchesPerTable rows.</summary>
    Task<IReadOnlyList<DataMatch>> SearchTableAsync(
        DbTableTarget table, SearchTerm term, DataSearchOptions options, CancellationToken ct = default);
}
