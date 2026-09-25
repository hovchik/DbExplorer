using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Core.Abstractions;

/// <summary>
/// Everything the application needs from a database engine. Metadata/search members must be
/// read-only and must never hold locks that block other sessions: use dirty/snapshot reads,
/// lock timeouts and statement timeouts. Script/routine execution members run whatever the
/// user asks for (including writes) and are opt-in from the UI.
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

    /// <summary>Parameters declared by a procedure or function, in ordinal position.</summary>
    Task<IReadOnlyList<DbRoutineParameter>> GetRoutineParametersAsync(DbObject routine, CancellationToken ct = default);

    /// <summary>
    /// Executes an arbitrary, possibly multi-statement, script against the given database
    /// (or the profile's default database when null) and returns every produced result set.
    /// </summary>
    Task<QueryExecutionResult> ExecuteScriptAsync(
        string sql, string? database, int timeoutSeconds, CancellationToken ct = default);

    /// <summary>Executes a stored procedure or function call with the given argument values.</summary>
    Task<QueryExecutionResult> ExecuteRoutineAsync(
        DbObject routine, IReadOnlyList<DbRoutineParameter> parameters, IReadOnlyDictionary<string, object?> arguments,
        int timeoutSeconds, CancellationToken ct = default);
}
