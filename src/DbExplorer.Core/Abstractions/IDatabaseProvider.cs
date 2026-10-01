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

    /// <param name="includeUsageStats">Seeks/scans/updates since the server started; costly on large catalogs, so the
    /// cached metadata load leaves them out and only the Indexes tab asks for them.</param>
    Task<IReadOnlyList<DbIndex>> GetIndexesAsync(bool includePhysicalStats, CancellationToken ct = default, bool includeUsageStats = true);

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
    /// <param name="maxRows">Rows kept per result set; further rows are read and discarded (never cancelled, so the
    /// rest of the script still runs) and the result set is flagged as truncated.</param>
    /// <param name="readOnly">The script's statements when it only reads. With a row limit, the provider then stops
    /// every result set on the server just past <paramref name="maxRows"/> rows instead of reading the rest, and
    /// reports the total as a lower bound (<see cref="QueryResultSet.TotalRowCountIsExact"/>).</param>
    /// <exception cref="SqlExecutionException">The server rejected the script; carries the error position when known.</exception>
    Task<QueryExecutionResult> ExecuteScriptAsync(
        string sql, string? database, int timeoutSeconds, CancellationToken ct = default, int maxRows = int.MaxValue,
        ReadOnlyScript? readOnly = null);

    /// <summary>Executes a stored procedure or function call with the given argument values.</summary>
    Task<QueryExecutionResult> ExecuteRoutineAsync(
        DbObject routine, IReadOnlyList<DbRoutineParameter> parameters, IReadOnlyDictionary<string, object?> arguments,
        int timeoutSeconds, CancellationToken ct = default);

    /// <summary>Opens a read-write connection to the database (or the profile's default when null) for running
    /// several scripts in a row, inside one transaction when <paramref name="transactional"/> is set.</summary>
    Task<IScriptSession> BeginScriptSessionAsync(string? database, bool transactional, CancellationToken ct = default);

    /// <summary>Column defaults and check constraints of one table (catalog-only, no locks).</summary>
    Task<DbTableConstraints> GetTableConstraintsAsync(DbObject table, CancellationToken ct = default);

    /// <summary>
    /// Runs one query that only reads, with the same non-blocking settings as data search (SQL Server: dirty reads and a
    /// lock timeout on the search pool; PostgreSQL: a read-only transaction with statement and lock timeouts), keeping at
    /// most <paramref name="maxRows"/> rows. Used by features that probe data on their own (change recorder, query
    /// debugger, relationship inference) so they never queue behind other sessions.
    /// </summary>
    Task<QueryResultSet> QueryReadOnlyAsync(
        string sql, string? database, DataSearchOptions options, int maxRows = 1000, CancellationToken ct = default);

    /// <summary>Cumulative inserts/updates/deletes per table of the database (or the connection's default when null).
    /// Reads statistics views only; SQL Server needs VIEW DATABASE STATE.</summary>
    Task<IReadOnlyList<TableChangeCounter>> GetTableChangeCountersAsync(string? database, CancellationToken ct = default);

    /// <summary>
    /// A position in the database's change stream (PostgreSQL: next transaction id; SQL Server:
    /// MIN_ACTIVE_ROWVERSION), or null when the engine cannot provide one. <see cref="ChangedSincePredicate"/> turns it
    /// into a filter for the rows written after it.
    /// </summary>
    Task<string?> GetChangeMarkerAsync(string? database, CancellationToken ct = default);

    /// <summary>A WHERE condition matching the rows of a table with these columns that were inserted or updated after
    /// <paramref name="marker"/> (PostgreSQL: xmin; SQL Server: a rowversion column), or null when the table has no way to tell.</summary>
    string? ChangedSincePredicate(IReadOnlyList<DbColumn> columns, string marker);
}
