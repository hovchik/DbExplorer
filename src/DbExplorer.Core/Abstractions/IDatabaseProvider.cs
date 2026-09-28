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

    /// <summary>Foreign key constraints across every accessible table (catalog-only, no locks).</summary>
    Task<IReadOnlyList<DbForeignKey>> GetForeignKeysAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DbLock>> GetLocksAsync(CancellationToken ct = default);

    /// <summary>Statements running right now across the server (plus sessions idle in a transaction where the engine reports them).</summary>
    Task<IReadOnlyList<DbActiveRequest>> GetActiveRequestsAsync(CancellationToken ct = default);

    /// <summary>The most expensive cached statements. Throws <see cref="InvalidOperationException"/> with
    /// setup guidance when the engine's statistics source is unavailable (e.g. pg_stat_statements).</summary>
    Task<IReadOnlyList<DbQueryStat>> GetTopQueriesAsync(QueryStatOrder order, int top, CancellationToken ct = default);

    /// <summary>How much <see cref="ProfileTableAsync"/> can compute for a column of this type.</summary>
    ColumnProfileLevel GetProfileLevel(DbColumn column);

    /// <summary>Null/distinct/min/max per column over the first <paramref name="sampleRows"/> rows, in one scan
    /// with the same lock and statement timeouts as data search.</summary>
    Task<TableProfile> ProfileTableAsync(
        DbObject table, IReadOnlyList<DbColumn> columns, int sampleRows, DataSearchOptions options, CancellationToken ct = default);

    /// <summary>Most frequent values of one column within the first <paramref name="sampleRows"/> rows.</summary>
    Task<IReadOnlyList<ValueFrequency>> GetTopValuesAsync(
        DbObject table, DbColumn column, int sampleRows, int top, DataSearchOptions options, CancellationToken ct = default);

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
