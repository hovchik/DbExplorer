using DbExplorer.Core.Models;

namespace DbExplorer.Providers.SqlServer;

/// <summary>Activity (DMV) queries and column-profiling SQL for SQL Server.</summary>
public static class SqlServerDiagnostics
{
    private const string StatementText = """
        SUBSTRING(t.text, ({0}.statement_start_offset / 2) + 1,
            ((CASE {0}.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE {0}.statement_end_offset END
              - {0}.statement_start_offset) / 2) + 1)
        """;

    /// <summary>Running requests plus sleeping sessions that hold an open transaction (the usual hidden blockers). Needs VIEW SERVER STATE.</summary>
    public static readonly string ActiveRequests = $"""
        SELECT TOP (500) *
        FROM (
            SELECT r.session_id AS [SessionId],
                   s.login_name AS [LoginName],
                   s.host_name AS [HostName],
                   s.program_name AS [ProgramName],
                   DB_NAME(r.database_id) AS [DatabaseName],
                   r.status AS [Status],
                   r.command AS [Command],
                   r.start_time AS [StartTime],
                   CAST(r.total_elapsed_time AS bigint) AS [ElapsedMs],
                   CAST(r.cpu_time AS bigint) AS [CpuMs],
                   CAST(r.logical_reads AS bigint) AS [LogicalReads],
                   CAST(r.writes AS bigint) AS [Writes],
                   r.wait_type AS [WaitType],
                   CAST(r.wait_time AS bigint) AS [WaitMs],
                   NULLIF(CAST(r.blocking_session_id AS int), 0) AS [BlockedBy],
                   COALESCE({string.Format(StatementText, "r")}, t.text) AS [SqlText]
            FROM sys.dm_exec_requests r
            JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE s.is_user_process = 1 AND r.session_id <> @@SPID
            UNION ALL
            SELECT s.session_id, s.login_name, s.host_name, s.program_name,
                   DB_NAME(s.database_id),
                   'sleeping (open transaction)',
                   NULL,
                   s.last_request_start_time,
                   CAST(DATEDIFF_BIG(millisecond, s.last_request_end_time, SYSDATETIME()) AS bigint),
                   CAST(s.cpu_time AS bigint),
                   CAST(s.logical_reads AS bigint),
                   CAST(s.writes AS bigint),
                   NULL, NULL, NULL,
                   t.text
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id
            OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) t
            WHERE s.is_user_process = 1 AND s.status = 'sleeping' AND s.open_transaction_count > 0
              AND s.session_id <> @@SPID
        ) x
        ORDER BY x.[ElapsedMs] DESC;
        """;

    public static string TopQueries(QueryStatOrder order)
    {
        var orderBy = order switch
        {
            QueryStatOrder.TotalCpu => "total_worker_time",
            QueryStatOrder.TotalDuration => "total_elapsed_time",
            QueryStatOrder.AverageDuration => "total_elapsed_time / execution_count",
            QueryStatOrder.LogicalReads => "total_logical_reads",
            _ => "execution_count"
        };

        // TOP inside the derived table first, so sql_text is resolved only for the rows returned.
        return $"""
            SELECT DB_NAME(t.dbid) AS [DatabaseName],
                   COALESCE({string.Format(StatementText, "qs")}, t.text) AS [SqlText],
                   qs.execution_count AS [ExecutionCount],
                   qs.total_worker_time / 1000.0 AS [TotalCpuMs],
                   qs.total_worker_time / 1000.0 / qs.execution_count AS [AvgCpuMs],
                   qs.total_elapsed_time / 1000.0 AS [TotalElapsedMs],
                   qs.total_elapsed_time / 1000.0 / qs.execution_count AS [AvgElapsedMs],
                   qs.total_logical_reads AS [TotalLogicalReads],
                   qs.total_logical_reads * 1.0 / qs.execution_count AS [AvgLogicalReads],
                   qs.total_rows AS [TotalRows],
                   qs.last_execution_time AS [LastExecutionTime]
            FROM (
                SELECT TOP (@top) *
                FROM sys.dm_exec_query_stats
                WHERE execution_count > 0
                ORDER BY {orderBy} DESC
            ) qs
            OUTER APPLY sys.dm_exec_sql_text(qs.sql_handle) t
            ORDER BY qs.{orderBy} DESC;
            """;
    }

    private static readonly HashSet<string> NullsOnlyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "ntext", "image", "xml", "geography", "geometry", "sql_variant", "hierarchyid"
    };

    private static readonly HashSet<string> DistinctOnlyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "uniqueidentifier", "binary", "varbinary", "timestamp", "rowversion"
    };

    private static readonly HashSet<string> DateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time"
    };

    public static ColumnProfileLevel ProfileLevel(DbColumn column) =>
        NullsOnlyTypes.Contains(column.BaseType) ? ColumnProfileLevel.NullsOnly
        : DistinctOnlyTypes.Contains(column.BaseType) ? ColumnProfileLevel.Distinct
        : ColumnProfileLevel.Full;

    /// <summary>
    /// One scan over the first @n rows. Result layout: total, then per column
    /// (non-null count, distinct count, min, max), with NULL where the type doesn't support it.
    /// </summary>
    public static string ProfileTable(string schema, string table, IReadOnlyList<DbColumn> columns)
    {
        var sampled = string.Join(", ", columns.Select((c, i) => $"{SqlServerSql.Quote(c.Name)} AS c{i}"));
        var aggregates = columns.Select((c, i) =>
        {
            var level = ProfileLevel(c);
            var nonNull = level == ColumnProfileLevel.NullsOnly
                ? $"SUM(CASE WHEN c{i} IS NULL THEN 0 ELSE 1 END)"
                : $"COUNT_BIG(c{i})";
            var distinct = level == ColumnProfileLevel.NullsOnly ? "CAST(NULL AS bigint)" : $"COUNT_BIG(DISTINCT c{i})";
            var (min, max) = level == ColumnProfileLevel.Full ? (MinMax("MIN", c, i), MinMax("MAX", c, i)) : ("NULL", "NULL");
            return $"CAST({nonNull} AS bigint) AS nn{i}, {distinct} AS d{i}, {min} AS mn{i}, {max} AS mx{i}";
        });

        return $"""
            WITH s AS (SELECT TOP (@n) {sampled} FROM {SqlServerSql.QuoteFullName(schema, table)})
            SELECT COUNT_BIG(*) AS total, {string.Join(", ", aggregates)}
            FROM s;
            """;
    }

    public static string TopValues(string schema, string table, DbColumn column)
    {
        if (column.BaseType is "image" or "geography" or "geometry")
            throw new NotSupportedException($"Top values are not available for {column.DataType} columns.");

        var value = ToText(SqlServerSql.Quote(column.Name), column);
        return $"""
            SELECT TOP (@top) v AS [Value], COUNT_BIG(*) AS [Count]
            FROM (SELECT TOP (@n) {value} AS v FROM {SqlServerSql.QuoteFullName(schema, table)}) s
            GROUP BY v
            ORDER BY COUNT_BIG(*) DESC, v;
            """;
    }

    private static string MinMax(string fn, DbColumn c, int i)
    {
        // MIN/MAX aren't defined for bit.
        var operand = string.Equals(c.BaseType, "bit", StringComparison.OrdinalIgnoreCase) ? $"CAST(c{i} AS tinyint)" : $"c{i}";
        return ToText($"{fn}({operand})", c);
    }

    private static string ToText(string expression, DbColumn c) =>
        DateTypes.Contains(c.BaseType) ? $"CONVERT(nvarchar(400), {expression}, 121)"
        : c.BaseType is "binary" or "varbinary" or "timestamp" or "rowversion" ? $"CONVERT(nvarchar(400), CAST({expression} AS varbinary(199)), 1)"
        : $"CAST({expression} AS nvarchar(400))";
}
