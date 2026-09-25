namespace DbExplorer.Providers.SqlServer;

/// <summary>
/// Catalog queries. They read only catalog views and DMVs; combined with the session prefix
/// (READ UNCOMMITTED + LOCK_TIMEOUT) they never wait behind user transactions or DDL.
/// </summary>
internal static class SqlServerQueries
{
    private const string ObjectTypeCase = """
        CASE o.type
            WHEN 'U'  THEN 'Table'
            WHEN 'V'  THEN 'View'
            WHEN 'P'  THEN 'Procedure'
            WHEN 'PC' THEN 'Procedure'
            WHEN 'X'  THEN 'Procedure'
            WHEN 'FN' THEN 'ScalarFunction'
            WHEN 'FS' THEN 'ScalarFunction'
            WHEN 'IF' THEN 'TableFunction'
            WHEN 'TF' THEN 'TableFunction'
            WHEN 'FT' THEN 'TableFunction'
            WHEN 'TR' THEN 'Trigger'
            WHEN 'TA' THEN 'Trigger'
            WHEN 'SO' THEN 'Sequence'
            WHEN 'SN' THEN 'Synonym'
            ELSE 'Other'
        END
        """;

    public const string ServerVersion = "SELECT @@VERSION;";

    public const string Databases =
        "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name;";

    public static readonly string Objects = $"""
        SELECT s.name AS [Schema],
               o.name AS [Name],
               {ObjectTypeCase} AS [Type],
               o.create_date AS [CreatedAt],
               o.modify_date AS [ModifiedAt],
               rc.row_count AS [RowCount]
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN (
            SELECT p.object_id, CAST(SUM(p.rows) AS bigint) AS row_count
            FROM sys.partitions p
            WHERE p.index_id IN (0, 1)
            GROUP BY p.object_id
        ) rc ON rc.object_id = o.object_id AND o.type = 'U'
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('U','V','P','PC','X','FN','FS','IF','TF','FT','TR','TA','SO','SN')
        ORDER BY s.name, o.name;
        """;

    public const string Columns = """
        SELECT s.name AS [Schema],
               o.name AS [Table],
               c.name AS [Name],
               ut.name + CASE
                   WHEN ut.is_user_defined = 1 THEN ''
                   WHEN ut.name IN ('varchar','char','varbinary','binary')
                       THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                   WHEN ut.name IN ('nvarchar','nchar')
                       THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                   WHEN ut.name IN ('decimal','numeric')
                       THEN '(' + CAST(c.precision AS varchar(5)) + ',' + CAST(c.scale AS varchar(5)) + ')'
                   WHEN ut.name IN ('datetime2','time','datetimeoffset')
                       THEN '(' + CAST(c.scale AS varchar(5)) + ')'
                   ELSE '' END AS [DataType],
               TYPE_NAME(c.system_type_id) AS [BaseType],
               c.is_nullable AS [IsNullable],
               c.column_id AS [Ordinal],
               c.is_computed AS [IsComputed],
               CAST(CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS bit) AS [IsPrimaryKey]
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.types ut ON ut.user_type_id = c.user_type_id
        LEFT JOIN (
            SELECT ic.object_id, ic.column_id
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            WHERE i.is_primary_key = 1
        ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
        WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V')
        ORDER BY s.name, o.name, c.column_id;
        """;

    public static readonly string Modules = $"""
        SELECT s.name AS [Schema],
               o.name AS [Name],
               {ObjectTypeCase} AS [Type],
               m.definition AS [Definition]
        FROM sys.sql_modules m
        JOIN sys.objects o ON o.object_id = m.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0;
        """;

    public const string ObjectDefinition = "SELECT OBJECT_DEFINITION(OBJECT_ID(@name));";

    public const string SynonymDefinition = """
        SELECT 'CREATE SYNONYM ' + QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name)
             + ' FOR ' + base_object_name + ';'
        FROM sys.synonyms WHERE object_id = OBJECT_ID(@name);
        """;

    public const string SequenceDefinition = """
        SELECT 'CREATE SEQUENCE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name)
             + ' AS ' + TYPE_NAME(user_type_id)
             + ' START WITH ' + CAST(start_value AS nvarchar(50))
             + ' INCREMENT BY ' + CAST(increment AS nvarchar(50))
             + ' MINVALUE ' + CAST(minimum_value AS nvarchar(50))
             + ' MAXVALUE ' + CAST(maximum_value AS nvarchar(50))
             + CASE WHEN is_cycling = 1 THEN ' CYCLE' ELSE ' NO CYCLE' END + ';'
        FROM sys.sequences WHERE object_id = OBJECT_ID(@name);
        """;

    public static string Indexes(bool usageStats, bool physicalStats)
    {
        var usageJoin = usageStats
            ? "LEFT JOIN sys.dm_db_index_usage_stats us ON us.database_id = DB_ID() AND us.object_id = i.object_id AND us.index_id = i.index_id"
            : "";

        // LIMITED mode reads only the parent level of the B-tree: cheap and takes only IS locks.
        var physicalJoin = physicalStats
            ? """
              LEFT JOIN (
                  SELECT object_id, index_id, MAX(avg_fragmentation_in_percent) AS frag
                  FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED')
                  GROUP BY object_id, index_id
              ) ps ON ps.object_id = i.object_id AND ps.index_id = i.index_id
              """
            : "";

        return $"""
            SELECT s.name AS [Schema],
                   o.name AS [Table],
                   i.name AS [Name],
                   i.type_desc AS [Type],
                   i.is_unique AS [IsUnique],
                   i.is_primary_key AS [IsPrimaryKey],
                   i.is_disabled AS [IsDisabled],
                   STUFF((
                       SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                       FROM sys.index_columns ic
                       JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                       ORDER BY ic.key_ordinal
                       FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS [Columns],
                   STUFF((
                       SELECT ', ' + c.name
                       FROM sys.index_columns ic
                       JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                       ORDER BY c.name
                       FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS [IncludedColumns],
                   i.filter_definition AS [Filter],
                   (SELECT CAST(SUM(p.rows) AS bigint) FROM sys.partitions p
                     WHERE p.object_id = i.object_id AND p.index_id = i.index_id) AS [Rows],
                   (SELECT CAST(SUM(au.used_pages) AS bigint) * 8192
                      FROM sys.partitions p
                      JOIN sys.allocation_units au ON au.container_id = p.partition_id
                     WHERE p.object_id = i.object_id AND p.index_id = i.index_id) AS [SizeBytes],
                   {(usageStats ? "CAST(us.user_seeks AS bigint)" : "CAST(NULL AS bigint)")} AS [Seeks],
                   {(usageStats ? "CAST(us.user_scans + us.user_lookups AS bigint)" : "CAST(NULL AS bigint)")} AS [Scans],
                   {(usageStats ? "CAST(us.user_updates AS bigint)" : "CAST(NULL AS bigint)")} AS [Updates],
                   {(physicalStats ? "ps.frag" : "CAST(NULL AS float)")} AS [FragmentationPercent]
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            {usageJoin}
            {physicalJoin}
            WHERE o.is_ms_shipped = 0 AND i.type > 0 AND o.type IN ('U','V')
            ORDER BY s.name, o.name, i.index_id;
            """;
    }

    /// <summary>Locks in the current database plus who is blocking whom. Needs VIEW SERVER STATE.</summary>
    public const string Locks = """
        SELECT TOP (5000)
               l.request_session_id AS [SessionId],
               es.login_name AS [LoginName],
               es.host_name AS [HostName],
               es.program_name AS [ProgramName],
               DB_NAME(l.resource_database_id) AS [DatabaseName],
               l.resource_type AS [ResourceType],
               CASE
                   WHEN l.resource_type = 'OBJECT'
                       THEN OBJECT_SCHEMA_NAME(l.resource_associated_entity_id, l.resource_database_id) + '.'
                          + OBJECT_NAME(l.resource_associated_entity_id, l.resource_database_id)
                   WHEN l.resource_type IN ('PAGE','KEY','RID','HOBT')
                       THEN (SELECT TOP (1) OBJECT_SCHEMA_NAME(p.object_id) + '.' + OBJECT_NAME(p.object_id)
                               FROM sys.partitions p WHERE p.hobt_id = l.resource_associated_entity_id)
               END AS [ObjectName],
               l.request_mode AS [LockMode],
               l.request_status AS [Status],
               NULLIF(CAST(r.blocking_session_id AS int), 0) AS [BlockedBy],
               r.wait_time AS [WaitMs],
               r.wait_type AS [WaitType],
               t.text AS [SqlText]
        FROM sys.dm_tran_locks l
        LEFT JOIN sys.dm_exec_sessions es ON es.session_id = l.request_session_id
        LEFT JOIN sys.dm_exec_requests r ON r.session_id = l.request_session_id
        LEFT JOIN sys.dm_exec_connections c ON c.session_id = l.request_session_id
        OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) t
        WHERE l.resource_database_id = DB_ID()
          AND l.request_session_id <> @@SPID
        ORDER BY CASE WHEN l.request_status = 'GRANT' THEN 1 ELSE 0 END, l.request_session_id;
        """;
}
