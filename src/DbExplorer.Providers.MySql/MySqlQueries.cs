namespace DbExplorer.Providers.MySql;

/// <summary>
/// Catalog queries. MySQL has no level between server and schema: a schema is what the rest of the app calls a database,
/// so every row carries the schema name as both its database and its schema, and `db`.`table` is always a valid name.
/// The filter placeholder is either one schema (@db) or every non-system schema.
/// </summary>
internal static class MySqlQueries
{
    public static readonly string SystemSchemaList =
        string.Join(", ", MySqlSql.SystemSchemas.Select(s => "'" + s + "'"));

    public static readonly string Databases =
        $"SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME NOT IN ({SystemSchemaList}) ORDER BY SCHEMA_NAME;";

    public const string ServerVersion = "SELECT VERSION();";

    /// <summary>SQL that quotes an identifier column with backticks.</summary>
    private static string Q(string column) => $"CONCAT('`', REPLACE({column}, '`', '``'), '`')";

    /// <summary>Restricts <paramref name="column"/> to the connection's schema, or to application schemas.</summary>
    public static string SchemaFilter(string column, bool allDatabases) =>
        allDatabases ? $"{column} NOT IN ({SystemSchemaList})" : $"{column} = @db";

    public static string Objects(bool all) => $"""
        SELECT t.TABLE_SCHEMA AS `Database`, t.TABLE_SCHEMA AS `Schema`, t.TABLE_NAME AS `Name`,
               CASE t.TABLE_TYPE WHEN 'VIEW' THEN 'View' WHEN 'SEQUENCE' THEN 'Sequence' ELSE 'Table' END AS `Type`,
               t.CREATE_TIME AS `CreatedAt`, t.UPDATE_TIME AS `ModifiedAt`,
               CASE WHEN t.TABLE_TYPE NOT IN ('VIEW', 'SEQUENCE') THEN CAST(t.TABLE_ROWS AS SIGNED) END AS `RowCount`
        FROM information_schema.TABLES t
        WHERE {SchemaFilter("t.TABLE_SCHEMA", all)}
        UNION ALL
        SELECT r.ROUTINE_SCHEMA, r.ROUTINE_SCHEMA, r.ROUTINE_NAME,
               CASE r.ROUTINE_TYPE WHEN 'PROCEDURE' THEN 'Procedure' ELSE 'Function' END,
               r.CREATED, r.LAST_ALTERED, NULL
        FROM information_schema.ROUTINES r
        WHERE r.ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION') AND {SchemaFilter("r.ROUTINE_SCHEMA", all)}
        UNION ALL
        SELECT g.TRIGGER_SCHEMA, g.TRIGGER_SCHEMA, g.TRIGGER_NAME, 'Trigger', g.CREATED, NULL, NULL
        FROM information_schema.TRIGGERS g
        WHERE {SchemaFilter("g.TRIGGER_SCHEMA", all)}
        ORDER BY 1, 3;
        """;

    // COLUMN_KEY = 'PRI' would also flag the first unique NOT NULL key of a table without a primary key.
    public static string Columns(bool all) => $"""
        SELECT c.TABLE_SCHEMA AS `Database`, c.TABLE_SCHEMA AS `Schema`, c.TABLE_NAME AS `Table`, c.COLUMN_NAME AS `Name`,
               c.COLUMN_TYPE AS `DataType`, c.DATA_TYPE AS `BaseType`,
               (c.IS_NULLABLE = 'YES') AS `IsNullable`,
               CAST(c.ORDINAL_POSITION AS SIGNED) AS `Ordinal`,
               (COALESCE(c.GENERATION_EXPRESSION, '') <> '') AS `IsComputed`,
               (pk.COLUMN_NAME IS NOT NULL) AS `IsPrimaryKey`,
               (c.EXTRA LIKE '%auto_increment%') AS `IsIdentity`
        FROM information_schema.COLUMNS c
        LEFT JOIN information_schema.STATISTICS pk
            ON pk.TABLE_SCHEMA = c.TABLE_SCHEMA AND pk.TABLE_NAME = c.TABLE_NAME
           AND pk.COLUMN_NAME = c.COLUMN_NAME AND pk.INDEX_NAME = 'PRIMARY'
        WHERE {SchemaFilter("c.TABLE_SCHEMA", all)}
        ORDER BY 1, 3, c.ORDINAL_POSITION;
        """;

    /// <summary>
    /// Definitions rebuilt from the catalog so the whole set comes back in one query (SHOW CREATE is one round trip per
    /// object). Routines keep their parameters, return type and the characteristics binary logging insists on.
    /// </summary>
    public static string Modules(bool all) => $"""
        SELECT v.TABLE_SCHEMA AS `Database`, v.TABLE_SCHEMA AS `Schema`, v.TABLE_NAME AS `Name`, 'View' AS `Type`,
               CONCAT('CREATE OR REPLACE VIEW ', {Q("v.TABLE_SCHEMA")}, '.', {Q("v.TABLE_NAME")}, ' AS', CHAR(10),
                      v.VIEW_DEFINITION, ';') AS `Definition`
        FROM information_schema.VIEWS v
        WHERE {SchemaFilter("v.TABLE_SCHEMA", all)}
        UNION ALL
        SELECT r.ROUTINE_SCHEMA, r.ROUTINE_SCHEMA, r.ROUTINE_NAME,
               CASE r.ROUTINE_TYPE WHEN 'PROCEDURE' THEN 'Procedure' ELSE 'Function' END,
               CONCAT('CREATE ', r.ROUTINE_TYPE, ' ', {Q("r.ROUTINE_SCHEMA")}, '.', {Q("r.ROUTINE_NAME")}, '(',
                      COALESCE((SELECT GROUP_CONCAT(CONCAT(
                                    CASE WHEN r.ROUTINE_TYPE = 'PROCEDURE' THEN CONCAT(p.PARAMETER_MODE, ' ') ELSE '' END,
                                    {Q("p.PARAMETER_NAME")}, ' ', p.DTD_IDENTIFIER)
                                    ORDER BY p.ORDINAL_POSITION SEPARATOR ', ')
                                FROM information_schema.PARAMETERS p
                                WHERE p.SPECIFIC_SCHEMA = r.ROUTINE_SCHEMA AND p.SPECIFIC_NAME = r.SPECIFIC_NAME
                                  AND p.ROUTINE_TYPE = r.ROUTINE_TYPE AND p.ORDINAL_POSITION > 0), ''),
                      ')',
                      CASE WHEN r.ROUTINE_TYPE = 'FUNCTION' THEN CONCAT(' RETURNS ', r.DTD_IDENTIFIER) ELSE '' END,
                      CASE WHEN r.IS_DETERMINISTIC = 'YES' THEN CONCAT(CHAR(10), 'DETERMINISTIC') ELSE '' END,
                      CASE WHEN r.SQL_DATA_ACCESS <> 'CONTAINS SQL' THEN CONCAT(CHAR(10), r.SQL_DATA_ACCESS) ELSE '' END,
                      CASE WHEN r.SECURITY_TYPE = 'INVOKER' THEN CONCAT(CHAR(10), 'SQL SECURITY INVOKER') ELSE '' END,
                      CHAR(10), r.ROUTINE_DEFINITION)
        FROM information_schema.ROUTINES r
        WHERE r.ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION') AND {SchemaFilter("r.ROUTINE_SCHEMA", all)}
        UNION ALL
        SELECT g.TRIGGER_SCHEMA, g.TRIGGER_SCHEMA, g.TRIGGER_NAME, 'Trigger',
               CONCAT('CREATE TRIGGER ', {Q("g.TRIGGER_SCHEMA")}, '.', {Q("g.TRIGGER_NAME")}, ' ',
                      g.ACTION_TIMING, ' ', g.EVENT_MANIPULATION, ' ON ',
                      {Q("g.EVENT_OBJECT_SCHEMA")}, '.', {Q("g.EVENT_OBJECT_TABLE")}, ' FOR EACH ROW', CHAR(10),
                      g.ACTION_STATEMENT)
        FROM information_schema.TRIGGERS g
        WHERE {SchemaFilter("g.TRIGGER_SCHEMA", all)};
        """;

    public const string RoutineParameters = """
        SELECT p.SPECIFIC_SCHEMA AS `Schema`, p.SPECIFIC_NAME AS `RoutineName`,
               COALESCE(p.PARAMETER_NAME, '') AS `Name`, p.DTD_IDENTIFIER AS `DataType`,
               CASE p.PARAMETER_MODE WHEN 'INOUT' THEN 1 WHEN 'OUT' THEN 2 ELSE 0 END AS `Direction`,
               0 AS `HasDefault`,
               CAST(p.ORDINAL_POSITION AS SIGNED) AS `Ordinal`
        FROM information_schema.PARAMETERS p
        WHERE p.SPECIFIC_SCHEMA = @schema AND p.SPECIFIC_NAME = @name AND p.ROUTINE_TYPE = @type
          AND p.ORDINAL_POSITION > 0
        ORDER BY p.ORDINAL_POSITION;
        """;

    /// <summary>MySQL 8 has invisible and expression (functional) index parts; MariaDB has ignored indexes instead.</summary>
    public static string Indexes(bool all, bool mariaDb)
    {
        var part = mariaDb ? "s.COLUMN_NAME" : "COALESCE(s.COLUMN_NAME, CONCAT('(', s.EXPRESSION, ')'))";
        var disabled = mariaDb ? "MAX(s.IGNORED = 'YES')" : "MAX(s.IS_VISIBLE = 'NO')";
        return $"""
            SELECT s.TABLE_SCHEMA AS `Database`, s.TABLE_SCHEMA AS `Schema`, s.TABLE_NAME AS `Table`, s.INDEX_NAME AS `Name`,
                   MIN(s.INDEX_TYPE) AS `Type`,
                   (MIN(s.NON_UNIQUE) = 0) AS `IsUnique`,
                   (s.INDEX_NAME = 'PRIMARY') AS `IsPrimaryKey`,
                   {disabled} AS `IsDisabled`,
                   GROUP_CONCAT(CONCAT({part},
                                       CASE WHEN s.SUB_PART IS NULL THEN '' ELSE CONCAT('(', s.SUB_PART, ')') END,
                                       CASE WHEN s.COLLATION = 'D' THEN ' DESC' ELSE '' END)
                                ORDER BY s.SEQ_IN_INDEX SEPARATOR ', ') AS `Columns`,
                   CAST(MAX(t.TABLE_ROWS) AS SIGNED) AS `Rows`
            FROM information_schema.STATISTICS s
            LEFT JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = s.TABLE_SCHEMA AND t.TABLE_NAME = s.TABLE_NAME
            WHERE {SchemaFilter("s.TABLE_SCHEMA", all)}
            GROUP BY s.TABLE_SCHEMA, s.TABLE_NAME, s.INDEX_NAME
            ORDER BY 1, 3, 4;
            """;
    }

    /// <summary>Index sizes from InnoDB's persistent statistics (needs SELECT on the mysql schema).</summary>
    public static string IndexSizes(bool all) => $"""
        SELECT database_name AS `Database`, table_name AS `Table`, index_name AS `Name`,
               CAST(stat_value * @@innodb_page_size AS SIGNED) AS `SizeBytes`
        FROM mysql.innodb_index_stats
        WHERE stat_name = 'size' AND {SchemaFilter("database_name", all)};
        """;

    /// <summary>Rows read and written through each index since the server started (performance_schema).</summary>
    public static string IndexUsage(bool all) => $"""
        SELECT OBJECT_SCHEMA AS `Database`, OBJECT_NAME AS `Table`, INDEX_NAME AS `Name`,
               CAST(COUNT_READ AS SIGNED) AS `Reads`, CAST(COUNT_WRITE AS SIGNED) AS `Writes`
        FROM performance_schema.table_io_waits_summary_by_index_usage
        WHERE INDEX_NAME IS NOT NULL AND {SchemaFilter("OBJECT_SCHEMA", all)};
        """;

    public static string ForeignKeys(bool all) => $"""
        SELECT k.TABLE_SCHEMA AS `Database`, k.TABLE_SCHEMA AS `Schema`, k.TABLE_NAME AS `Table`, k.CONSTRAINT_NAME AS `Name`,
               GROUP_CONCAT(k.COLUMN_NAME ORDER BY k.ORDINAL_POSITION SEPARATOR ', ') AS `Columns`,
               MIN(k.REFERENCED_TABLE_SCHEMA) AS `ReferencedSchema`,
               MIN(k.REFERENCED_TABLE_NAME) AS `ReferencedTable`,
               GROUP_CONCAT(k.REFERENCED_COLUMN_NAME ORDER BY k.ORDINAL_POSITION SEPARATOR ', ') AS `ReferencedColumns`,
               0 AS `IsDisabled`
        FROM information_schema.KEY_COLUMN_USAGE k
        WHERE k.REFERENCED_TABLE_NAME IS NOT NULL AND {SchemaFilter("k.TABLE_SCHEMA", all)}
        GROUP BY k.TABLE_SCHEMA, k.TABLE_NAME, k.CONSTRAINT_NAME
        ORDER BY 1, 3, 4;
        """;

    public const string ColumnDefaults = """
        SELECT COLUMN_NAME AS `Name`, COLUMN_DEFAULT AS `Definition`, DATA_TYPE AS `DataType`, EXTRA AS `Extra`
        FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name AND COLUMN_DEFAULT IS NOT NULL
          AND COALESCE(GENERATION_EXPRESSION, '') = '';
        """;

    // MySQL names check constraints per schema; MariaDB per table, and adds json_valid() checks for its JSON alias.
    public static string CheckConstraints(bool mariaDb) => mariaDb
        ? """
          SELECT CONSTRAINT_NAME AS `Name`, CHECK_CLAUSE AS `Definition`
          FROM information_schema.CHECK_CONSTRAINTS
          WHERE CONSTRAINT_SCHEMA = @schema AND TABLE_NAME = @name
            AND NOT (LEVEL = 'Column' AND CHECK_CLAUSE LIKE 'json\_valid(%')
          ORDER BY CONSTRAINT_NAME;
          """
        : """
          SELECT cc.CONSTRAINT_NAME AS `Name`, cc.CHECK_CLAUSE AS `Definition`
          FROM information_schema.TABLE_CONSTRAINTS tc
          JOIN information_schema.CHECK_CONSTRAINTS cc
            ON cc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND cc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
          WHERE tc.TABLE_SCHEMA = @schema AND tc.TABLE_NAME = @name AND tc.CONSTRAINT_TYPE = 'CHECK'
          ORDER BY cc.CONSTRAINT_NAME;
          """;

    /// <summary>
    /// MySQL 8: InnoDB row and table locks (performance_schema.data_locks) plus metadata locks, the usual reason an ALTER
    /// or a TRUNCATE hangs behind an open transaction. A pending metadata lock is shown blocked by a session holding a
    /// lock on the same object.
    /// </summary>
    public const string MySqlLocks = """
        SELECT CAST(t.PROCESSLIST_ID AS SIGNED) AS `SessionId`,
               t.PROCESSLIST_USER AS `LoginName`, t.PROCESSLIST_HOST AS `HostName`, NULL AS `ProgramName`,
               l.OBJECT_SCHEMA AS `DatabaseName`,
               CONCAT(l.LOCK_TYPE, CASE WHEN l.INDEX_NAME IS NULL THEN '' ELSE CONCAT(' (', l.INDEX_NAME, ')') END) AS `ResourceType`,
               CONCAT(l.OBJECT_SCHEMA, '.', l.OBJECT_NAME) AS `ObjectName`,
               CONCAT(l.LOCK_MODE, CASE WHEN l.LOCK_DATA IS NULL THEN '' ELSE CONCAT(' ', l.LOCK_DATA) END) AS `LockMode`,
               CASE WHEN l.LOCK_STATUS = 'GRANTED' THEN 'GRANT' ELSE 'WAIT' END AS `Status`,
               (SELECT CAST(bt.PROCESSLIST_ID AS SIGNED)
                  FROM performance_schema.data_lock_waits w
                  JOIN performance_schema.threads bt ON bt.THREAD_ID = w.BLOCKING_THREAD_ID
                 WHERE w.REQUESTING_ENGINE_LOCK_ID = l.ENGINE_LOCK_ID LIMIT 1) AS `BlockedBy`,
               CASE WHEN l.LOCK_STATUS <> 'GRANTED'
                    THEN CAST(TIMESTAMPDIFF(MICROSECOND, x.trx_wait_started, NOW(6)) / 1000 AS SIGNED) END AS `WaitMs`,
               CASE WHEN l.LOCK_STATUS <> 'GRANTED' THEN 'row lock' END AS `WaitType`,
               COALESCE(t.PROCESSLIST_INFO, x.trx_query) AS `SqlText`
        FROM performance_schema.data_locks l
        JOIN performance_schema.threads t ON t.THREAD_ID = l.THREAD_ID
        LEFT JOIN information_schema.INNODB_TRX x ON x.trx_mysql_thread_id = t.PROCESSLIST_ID
        WHERE t.PROCESSLIST_ID IS NOT NULL AND t.PROCESSLIST_ID <> CONNECTION_ID()
        UNION ALL
        SELECT CAST(t.PROCESSLIST_ID AS SIGNED), t.PROCESSLIST_USER, t.PROCESSLIST_HOST, NULL,
               m.OBJECT_SCHEMA,
               CONCAT('METADATA ', m.OBJECT_TYPE),
               CONCAT_WS('.', m.OBJECT_SCHEMA, m.OBJECT_NAME),
               m.LOCK_TYPE,
               CASE WHEN m.LOCK_STATUS = 'GRANTED' THEN 'GRANT' ELSE 'WAIT' END,
               CASE WHEN m.LOCK_STATUS = 'PENDING' THEN
                   (SELECT CAST(ht.PROCESSLIST_ID AS SIGNED)
                      FROM performance_schema.metadata_locks h
                      JOIN performance_schema.threads ht ON ht.THREAD_ID = h.OWNER_THREAD_ID
                     WHERE h.OBJECT_TYPE = m.OBJECT_TYPE AND h.OBJECT_SCHEMA <=> m.OBJECT_SCHEMA
                       AND h.OBJECT_NAME <=> m.OBJECT_NAME AND h.LOCK_STATUS = 'GRANTED'
                       AND h.OWNER_THREAD_ID <> m.OWNER_THREAD_ID AND ht.PROCESSLIST_ID IS NOT NULL
                     ORDER BY h.LOCK_TYPE = 'SHARED_READ_ONLY' DESC LIMIT 1) END,
               CASE WHEN m.LOCK_STATUS = 'PENDING' THEN CAST(t.PROCESSLIST_TIME * 1000 AS SIGNED) END,
               CASE WHEN m.LOCK_STATUS = 'PENDING' THEN 'metadata lock' END,
               t.PROCESSLIST_INFO
        FROM performance_schema.metadata_locks m
        JOIN performance_schema.threads t ON t.THREAD_ID = m.OWNER_THREAD_ID
        WHERE t.PROCESSLIST_ID IS NOT NULL AND t.PROCESSLIST_ID <> CONNECTION_ID()
          AND m.OBJECT_TYPE IN ('TABLE', 'SCHEMA', 'FUNCTION', 'PROCEDURE', 'TRIGGER', 'EVENT')
          AND COALESCE(m.OBJECT_SCHEMA, '') NOT IN ('performance_schema', 'information_schema')
        ORDER BY 9 DESC, 1
        LIMIT 5000;
        """;

    /// <summary>
    /// MariaDB: InnoDB reports the locks that are part of a wait (INNODB_LOCKS), so every open transaction is listed too,
    /// as one row: a transaction holding row locks is how a blocking chain usually starts.
    /// </summary>
    public const string MariaDbLocks = """
        SELECT CAST(x.trx_mysql_thread_id AS SIGNED) AS `SessionId`,
               p.USER AS `LoginName`, p.HOST AS `HostName`, NULL AS `ProgramName`, p.DB AS `DatabaseName`,
               CONCAT(l.lock_type, CASE WHEN l.lock_index IS NULL THEN '' ELSE CONCAT(' (', l.lock_index, ')') END) AS `ResourceType`,
               REPLACE(l.lock_table, '`', '') AS `ObjectName`,
               CONCAT(l.lock_mode, CASE WHEN l.lock_data IS NULL THEN '' ELSE CONCAT(' ', l.lock_data) END) AS `LockMode`,
               CASE WHEN l.lock_id = x.trx_requested_lock_id THEN 'WAIT' ELSE 'GRANT' END AS `Status`,
               CASE WHEN l.lock_id = x.trx_requested_lock_id THEN
                   (SELECT CAST(b.trx_mysql_thread_id AS SIGNED)
                      FROM information_schema.INNODB_LOCK_WAITS w
                      JOIN information_schema.INNODB_TRX b ON b.trx_id = w.blocking_trx_id
                     WHERE w.requesting_trx_id = x.trx_id LIMIT 1) END AS `BlockedBy`,
               CASE WHEN l.lock_id = x.trx_requested_lock_id
                    THEN CAST(TIMESTAMPDIFF(MICROSECOND, x.trx_wait_started, NOW(6)) / 1000 AS SIGNED) END AS `WaitMs`,
               CASE WHEN l.lock_id = x.trx_requested_lock_id THEN 'row lock' END AS `WaitType`,
               COALESCE(p.INFO, x.trx_query) AS `SqlText`
        FROM information_schema.INNODB_LOCKS l
        JOIN information_schema.INNODB_TRX x ON x.trx_id = l.lock_trx_id
        LEFT JOIN information_schema.PROCESSLIST p ON p.ID = x.trx_mysql_thread_id
        WHERE x.trx_mysql_thread_id <> CONNECTION_ID()
        UNION ALL
        SELECT CAST(x.trx_mysql_thread_id AS SIGNED), p.USER, p.HOST, NULL, p.DB,
               'TRANSACTION', NULL,
               CONCAT(CAST(x.trx_rows_locked AS CHAR), ' rows locked, ', CAST(x.trx_rows_modified AS CHAR), ' modified'),
               CASE WHEN x.trx_state = 'LOCK WAIT' THEN 'WAIT' ELSE 'GRANT' END,
               (SELECT CAST(b.trx_mysql_thread_id AS SIGNED)
                  FROM information_schema.INNODB_LOCK_WAITS w
                  JOIN information_schema.INNODB_TRX b ON b.trx_id = w.blocking_trx_id
                 WHERE w.requesting_trx_id = x.trx_id LIMIT 1),
               CASE WHEN x.trx_state = 'LOCK WAIT'
                    THEN CAST(TIMESTAMPDIFF(MICROSECOND, x.trx_wait_started, NOW(6)) / 1000 AS SIGNED) END,
               CASE WHEN x.trx_state = 'LOCK WAIT' THEN 'row lock' END,
               COALESCE(p.INFO, x.trx_query)
        FROM information_schema.INNODB_TRX x
        LEFT JOIN information_schema.PROCESSLIST p ON p.ID = x.trx_mysql_thread_id
        WHERE x.trx_mysql_thread_id <> CONNECTION_ID() AND (x.trx_rows_locked > 0 OR x.trx_state = 'LOCK WAIT')
        ORDER BY 9 DESC, 1
        LIMIT 5000;
        """;
}
