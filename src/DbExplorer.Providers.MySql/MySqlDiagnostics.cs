using DbExplorer.Core.Models;

namespace DbExplorer.Providers.MySql;

/// <summary>Activity queries and column-profiling SQL for MySQL and MariaDB.</summary>
public static class MySqlDiagnostics
{
    /// <summary>
    /// Running statements plus sessions sleeping inside an open transaction (the usual hidden blockers). MariaDB reports
    /// milliseconds and rows examined; MySQL whole seconds only. Without the PROCESS privilege only the user's own
    /// sessions are listed.
    /// </summary>
    public static string ActiveRequests(bool mariaDb)
    {
        var elapsed = mariaDb ? "CAST(p.TIME_MS AS SIGNED)" : "CAST(p.TIME AS SIGNED) * 1000";
        var reads = mariaDb ? "CAST(p.EXAMINED_ROWS AS SIGNED)" : "NULL";
        var blockedBy = mariaDb
            ? """
              (SELECT CAST(b.trx_mysql_thread_id AS SIGNED)
                 FROM information_schema.INNODB_LOCK_WAITS w
                 JOIN information_schema.INNODB_TRX b ON b.trx_id = w.blocking_trx_id
                WHERE w.requesting_trx_id = x.trx_id LIMIT 1)
              """
            : """
              (SELECT CAST(bt.PROCESSLIST_ID AS SIGNED)
                 FROM performance_schema.data_lock_waits w
                 JOIN performance_schema.threads bt ON bt.THREAD_ID = w.BLOCKING_THREAD_ID
                 JOIN performance_schema.threads rt ON rt.THREAD_ID = w.REQUESTING_THREAD_ID
                WHERE rt.PROCESSLIST_ID = p.ID LIMIT 1)
              """;

        return $"""
            SELECT CAST(p.ID AS SIGNED) AS `SessionId`,
                   p.USER AS `LoginName`, p.HOST AS `HostName`, NULL AS `ProgramName`, p.DB AS `DatabaseName`,
                   CASE WHEN p.COMMAND = 'Sleep' THEN 'idle in transaction'
                        ELSE COALESCE(NULLIF(p.STATE, ''), p.COMMAND) END AS `Status`,
                   CASE WHEN p.INFO IS NULL THEN p.COMMAND ELSE SUBSTRING_INDEX(TRIM(p.INFO), ' ', 1) END AS `Command`,
                   CASE WHEN p.COMMAND = 'Sleep' THEN x.trx_started ELSE NOW() - INTERVAL p.TIME SECOND END AS `StartTime`,
                   CASE WHEN p.COMMAND = 'Sleep' THEN CAST(TIMESTAMPDIFF(MICROSECOND, x.trx_started, NOW(6)) / 1000 AS SIGNED)
                        ELSE {elapsed} END AS `ElapsedMs`,
                   NULL AS `CpuMs`,
                   {reads} AS `LogicalReads`,
                   CAST(x.trx_rows_modified AS SIGNED) AS `Writes`,
                   CASE WHEN x.trx_state = 'LOCK WAIT' THEN 'row lock'
                        WHEN p.STATE LIKE 'Waiting for%' THEN p.STATE END AS `WaitType`,
                   CASE WHEN x.trx_state = 'LOCK WAIT'
                        THEN CAST(TIMESTAMPDIFF(MICROSECOND, x.trx_wait_started, NOW(6)) / 1000 AS SIGNED) END AS `WaitMs`,
                   CASE WHEN x.trx_state = 'LOCK WAIT' THEN {blockedBy} END AS `BlockedBy`,
                   COALESCE(p.INFO, x.trx_query) AS `SqlText`
            FROM information_schema.PROCESSLIST p
            LEFT JOIN information_schema.INNODB_TRX x ON x.trx_mysql_thread_id = p.ID
            WHERE p.ID <> CONNECTION_ID()
              AND p.COMMAND NOT IN ('Daemon', 'Binlog Dump', 'Binlog Dump GTID', 'Slave_IO', 'Slave_SQL', 'Slave_worker')
              AND p.USER NOT IN ('system user', 'event_scheduler')
              AND (p.COMMAND <> 'Sleep' OR x.trx_id IS NOT NULL)
            ORDER BY 9 DESC
            LIMIT 500;
            """;
    }

    public const string PerformanceSchemaEnabled = "SELECT @@performance_schema;";

    public const string PerformanceSchemaMissing =
        "Statement statistics come from performance_schema, which is switched off on this server. Add " +
        "performance_schema=ON under [mysqld] in the server's configuration file and restart the server.";

    /// <param name="cpu">The server reports CPU time per statement digest (MySQL 8.0.28 and later).</param>
    public static string TopQueries(QueryStatOrder order, bool cpu)
    {
        // Timers are in picoseconds; dividing by a float literal keeps the result a double.
        var totalCpu = cpu ? "SUM_CPU_TIME / 1e9" : "NULL";
        var orderBy = order switch
        {
            QueryStatOrder.TotalCpu when cpu => "SUM_CPU_TIME",
            QueryStatOrder.TotalCpu or QueryStatOrder.TotalDuration => "SUM_TIMER_WAIT",
            QueryStatOrder.AverageDuration => "AVG_TIMER_WAIT",
            QueryStatOrder.LogicalReads => "SUM_ROWS_EXAMINED",
            _ => "COUNT_STAR"
        };

        return $"""
            SELECT SCHEMA_NAME AS `DatabaseName`,
                   DIGEST_TEXT AS `SqlText`,
                   CAST(COUNT_STAR AS SIGNED) AS `ExecutionCount`,
                   {totalCpu} AS `TotalCpuMs`,
                   {(cpu ? "SUM_CPU_TIME / 1e9 / NULLIF(COUNT_STAR, 0)" : "NULL")} AS `AvgCpuMs`,
                   SUM_TIMER_WAIT / 1e9 AS `TotalElapsedMs`,
                   AVG_TIMER_WAIT / 1e9 AS `AvgElapsedMs`,
                   CAST(SUM_ROWS_EXAMINED AS SIGNED) AS `TotalLogicalReads`,
                   SUM_ROWS_EXAMINED / 1e0 / NULLIF(COUNT_STAR, 0) AS `AvgLogicalReads`,
                   CAST(SUM_ROWS_SENT + SUM_ROWS_AFFECTED AS SIGNED) AS `TotalRows`,
                   LAST_SEEN AS `LastExecutionTime`
            FROM performance_schema.events_statements_summary_by_digest
            WHERE COUNT_STAR > 0 AND DIGEST_TEXT IS NOT NULL
            ORDER BY {orderBy} DESC
            LIMIT @top;
            """;
    }

    private static readonly HashSet<string> OrderedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "decimal", "numeric", "float", "double", "real",
        "char", "varchar", "tinytext", "text", "mediumtext", "longtext", "enum", "set",
        "date", "time", "datetime", "timestamp", "year"
    };

    private static readonly HashSet<string> OpaqueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyblob", "blob", "mediumblob", "longblob",
        "geometry", "point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon",
        "geometrycollection", "geomcollection"
    };

    public static ColumnProfileLevel ProfileLevel(DbColumn column) =>
        OrderedTypes.Contains(column.BaseType) ? ColumnProfileLevel.Full
        : OpaqueTypes.Contains(column.BaseType) ? ColumnProfileLevel.NullsOnly
        : ColumnProfileLevel.Distinct;

    /// <summary>Same layout as the other engines: total, then (non-null, distinct, min, max) per column.</summary>
    public static string ProfileTable(string schema, string table, IReadOnlyList<DbColumn> columns)
    {
        var sampled = string.Join(", ", columns.Select((c, i) => $"{MySqlSql.Quote(c.Name)} AS c{i}"));
        var aggregates = columns.Select((c, i) =>
        {
            var level = ProfileLevel(c);
            var distinct = level == ColumnProfileLevel.NullsOnly ? "NULL" : $"COUNT(DISTINCT CAST(c{i} AS CHAR))";
            var full = level == ColumnProfileLevel.Full;
            var min = full ? $"LEFT(CAST(MIN(c{i}) AS CHAR), 400)" : "NULL";
            var max = full ? $"LEFT(CAST(MAX(c{i}) AS CHAR), 400)" : "NULL";
            return $"COUNT(c{i}) AS nn{i}, {distinct} AS d{i}, {min} AS mn{i}, {max} AS mx{i}";
        });

        return $"""
            SELECT COUNT(*) AS total, {string.Join(", ", aggregates)}
            FROM (SELECT {sampled} FROM {MySqlSql.QuoteFullName(schema, table)} LIMIT @n) s;
            """;
    }

    public static string TopValues(string schema, string table, DbColumn column) => $"""
        SELECT LEFT(CAST(v AS CHAR), 400) AS `Value`, COUNT(*) AS `Count`
        FROM (SELECT {MySqlSql.Quote(column.Name)} AS v FROM {MySqlSql.QuoteFullName(schema, table)} LIMIT @n) s
        GROUP BY 1
        ORDER BY 2 DESC, 1
        LIMIT @top;
        """;

    /// <summary>Rows inserted/updated/deleted per table since the server started (performance_schema table I/O).</summary>
    public const string ChangeCounters = """
        SELECT OBJECT_SCHEMA AS `Database`, OBJECT_SCHEMA AS `Schema`, OBJECT_NAME AS `Table`,
               CAST(COUNT_INSERT AS SIGNED) AS `Inserts`, CAST(COUNT_UPDATE AS SIGNED) AS `Updates`,
               CAST(COUNT_DELETE AS SIGNED) AS `Deletes`
        FROM performance_schema.table_io_waits_summary_by_table
        WHERE OBJECT_TYPE = 'TABLE' AND OBJECT_SCHEMA = @db;
        """;
}
