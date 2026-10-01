using DbExplorer.Core.Models;

namespace DbExplorer.Providers.Postgres;

/// <summary>Activity queries and column-profiling SQL for PostgreSQL.</summary>
public static class PostgresDiagnostics
{
    /// <summary>Non-idle client backends, including "idle in transaction" (the usual hidden blockers).</summary>
    public const string ActiveRequests = """
        SELECT a.pid AS "SessionId",
               a.usename::text AS "LoginName",
               a.client_addr::text AS "HostName",
               a.application_name AS "ProgramName",
               a.datname::text AS "DatabaseName",
               a.state AS "Status",
               split_part(ltrim(a.query), ' ', 1) AS "Command",
               COALESCE(a.query_start, a.xact_start)::timestamp AS "StartTime",
               (EXTRACT(EPOCH FROM (clock_timestamp() - COALESCE(a.query_start, a.xact_start))) * 1000)::bigint AS "ElapsedMs",
               NULL::bigint AS "CpuMs",
               NULL::bigint AS "LogicalReads",
               NULL::bigint AS "Writes",
               CASE WHEN a.wait_event IS NOT NULL THEN a.wait_event_type || ':' || a.wait_event END AS "WaitType",
               NULL::bigint AS "WaitMs",
               (pg_blocking_pids(a.pid))[1] AS "BlockedBy",
               a.query AS "SqlText"
        FROM pg_stat_activity a
        WHERE a.backend_type = 'client backend'
          AND a.state IS DISTINCT FROM 'idle'
          AND a.pid <> pg_backend_pid()
        ORDER BY 9 DESC NULLS LAST
        LIMIT 500;
        """;

    public const string FindStatStatements = """
        SELECT n.nspname
        FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
        WHERE e.extname = 'pg_stat_statements';
        """;

    /// <param name="schema">Schema the extension is installed in.</param>
    /// <param name="serverVersionNum">server_version_num; columns were renamed in 13 (total_time → total_exec_time).</param>
    public static string TopQueries(string schema, int serverVersionNum, QueryStatOrder order)
    {
        var total = serverVersionNum >= 130000 ? "s.total_exec_time" : "s.total_time";
        var mean = serverVersionNum >= 130000 ? "s.mean_exec_time" : "s.mean_time";
        var orderBy = order switch
        {
            QueryStatOrder.TotalCpu or QueryStatOrder.TotalDuration => total,
            QueryStatOrder.AverageDuration => mean,
            QueryStatOrder.LogicalReads => "(s.shared_blks_hit + s.shared_blks_read)",
            _ => "s.calls"
        };

        return $"""
            SELECT d.datname::text AS "DatabaseName",
                   s.query AS "SqlText",
                   s.calls AS "ExecutionCount",
                   NULL::float8 AS "TotalCpuMs",
                   NULL::float8 AS "AvgCpuMs",
                   {total} AS "TotalElapsedMs",
                   {mean} AS "AvgElapsedMs",
                   (s.shared_blks_hit + s.shared_blks_read) AS "TotalLogicalReads",
                   (s.shared_blks_hit + s.shared_blks_read)::float8 / NULLIF(s.calls, 0) AS "AvgLogicalReads",
                   s.rows AS "TotalRows",
                   NULL::timestamp AS "LastExecutionTime"
            FROM {PostgresSql.Quote(schema)}.pg_stat_statements s
            LEFT JOIN pg_database d ON d.oid = s.dbid
            WHERE s.calls > 0
            ORDER BY {orderBy} DESC
            LIMIT @top;
            """;
    }

    private static readonly HashSet<string> OrderedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int2", "int4", "int8", "numeric", "float4", "float8", "money", "oid",
        "text", "varchar", "bpchar", "name", "citext", "char",
        "date", "time", "timetz", "timestamp", "timestamptz", "interval", "inet", "cidr"
    };

    public static ColumnProfileLevel ProfileLevel(DbColumn column) =>
        OrderedTypes.Contains(column.BaseType) ? ColumnProfileLevel.Full : ColumnProfileLevel.Distinct;

    /// <summary>Same layout as the SQL Server version: total, then (non-null, distinct, min, max) per column.
    /// Distinct counts compare the text form so every type (json, arrays, …) can be counted.</summary>
    public static string ProfileTable(string schema, string table, IReadOnlyList<DbColumn> columns)
    {
        var sampled = string.Join(", ", columns.Select((c, i) => $"{PostgresSql.Quote(c.Name)} AS c{i}"));
        var aggregates = columns.Select((c, i) =>
        {
            var full = ProfileLevel(c) == ColumnProfileLevel.Full;
            var min = full ? $"left(min(c{i})::text, 400)" : "NULL::text";
            var max = full ? $"left(max(c{i})::text, 400)" : "NULL::text";
            return $"count(c{i})::bigint AS nn{i}, count(DISTINCT c{i}::text)::bigint AS d{i}, {min} AS mn{i}, {max} AS mx{i}";
        });

        return $"""
            WITH s AS (SELECT {sampled} FROM {PostgresSql.QuoteFullName(schema, table)} LIMIT @n)
            SELECT count(*)::bigint AS total, {string.Join(", ", aggregates)}
            FROM s;
            """;
    }

    public static string TopValues(string schema, string table, DbColumn column) => $"""
        SELECT left(v::text, 400) AS "Value", count(*)::bigint AS "Count"
        FROM (SELECT {PostgresSql.Quote(column.Name)} AS v FROM {PostgresSql.QuoteFullName(schema, table)} LIMIT @n) s
        GROUP BY 1
        ORDER BY 2 DESC, 1
        LIMIT @top;
        """;
    /// <summary>Rows inserted/updated/deleted per user table since the statistics were last reset. Other sessions report
    /// their counts when their transaction ends, at most about once a second (later while they stay busy).</summary>
    public const string ChangeCounters = """
        SELECT schemaname::text AS "Schema", relname::text AS "Table",
               n_tup_ins AS "Inserts", n_tup_upd AS "Updates", n_tup_del AS "Deletes"
        FROM pg_stat_user_tables;
        """;

    /// <summary>The next transaction id to be assigned: every transaction that starts writing from now on has an id at or
    /// after it. (The oldest running id would also catch transactions already open at the start, but any old transaction
    /// still running — autovacuum, a forgotten session — would then pull in rows written long before.)</summary>
    public const string ChangeMarker = "SELECT txid_snapshot_xmax(txid_current_snapshot())::text;";

    /// <summary>Rows whose current version was written by a transaction at or after <paramref name="marker"/> (a 64-bit
    /// txid). xmin is the 32-bit id that wraps around, so "at or after" is a modular comparison.</summary>
    public static string? ChangedSincePredicate(string marker)
    {
        if (!long.TryParse(marker, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var txid))
            return null;
        var xid = txid & 0xFFFFFFFFL;
        return $"((xmin::text::bigint - {xid} + 4294967296) % 4294967296) < 2147483648";
    }
}
