namespace DbExplorer.Providers.Postgres;

internal static class PostgresQueries
{
    private const string NsFilter = "n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'";

    private const string NotExtensionClass =
        "NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')";

    private const string NotExtensionProc =
        "NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')";

    public const string ServerVersion = "SELECT version();";

    public const string Databases =
        "SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn ORDER BY datname;";

    public static readonly string Objects = $"""
        SELECT n.nspname AS "Schema",
               c.relname AS "Name",
               CASE c.relkind
                   WHEN 'r' THEN 'Table' WHEN 'p' THEN 'Table'
                   WHEN 'v' THEN 'View' WHEN 'm' THEN 'MaterializedView'
                   WHEN 'f' THEN 'ForeignTable' WHEN 'S' THEN 'Sequence'
                   ELSE 'Other' END AS "Type",
               NULL::timestamp AS "CreatedAt",
               NULL::timestamp AS "ModifiedAt",
               CASE WHEN c.relkind IN ('r','p','m') AND c.reltuples >= 0 THEN c.reltuples::bigint END AS "RowCount"
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r','p','v','m','f','S') AND NOT c.relispartition
          AND {NsFilter} AND {NotExtensionClass}
        UNION ALL
        SELECT DISTINCT n.nspname, p.proname,
               CASE p.prokind WHEN 'p' THEN 'Procedure' ELSE 'Function' END,
               NULL::timestamp, NULL::timestamp, NULL::bigint
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE p.prokind IN ('f','p') AND {NsFilter} AND {NotExtensionProc}
        UNION ALL
        SELECT DISTINCT n.nspname, t.tgname, 'Trigger', NULL::timestamp, NULL::timestamp, NULL::bigint
        FROM pg_trigger t
        JOIN pg_class c ON c.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE NOT t.tgisinternal AND {NsFilter}
        ORDER BY 1, 2;
        """;

    public static readonly string Columns = $"""
        SELECT n.nspname AS "Schema",
               c.relname AS "Table",
               a.attname AS "Name",
               format_type(a.atttypid, a.atttypmod) AS "DataType",
               (CASE WHEN t.typtype = 'd' THEN bt.typname ELSE t.typname END)::text AS "BaseType",
               NOT a.attnotnull AS "IsNullable",
               a.attnum::int AS "Ordinal",
               (a.attgenerated <> '') AS "IsComputed",
               EXISTS (SELECT 1 FROM pg_index i
                        WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY (i.indkey)) AS "IsPrimaryKey",
               (a.attidentity <> '') AS "IsIdentity"
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_type t ON t.oid = a.atttypid
        LEFT JOIN pg_type bt ON bt.oid = t.typbasetype
        WHERE a.attnum > 0 AND NOT a.attisdropped
          AND c.relkind IN ('r','p','v','m','f') AND NOT c.relispartition
          AND {NsFilter} AND {NotExtensionClass}
        ORDER BY n.nspname, c.relname, a.attnum;
        """;

    public static readonly string Modules = $"""
        SELECT n.nspname AS "Schema", p.proname AS "Name",
               CASE p.prokind WHEN 'p' THEN 'Procedure' ELSE 'Function' END AS "Type",
               pg_get_functiondef(p.oid) AS "Definition"
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE p.prokind IN ('f','p') AND {NsFilter} AND {NotExtensionProc}
        UNION ALL
        SELECT n.nspname, c.relname,
               CASE c.relkind WHEN 'm' THEN 'MaterializedView' ELSE 'View' END,
               'CREATE ' || CASE c.relkind WHEN 'm' THEN 'MATERIALIZED VIEW ' ELSE 'OR REPLACE VIEW ' END
                   || quote_ident(n.nspname) || '.' || quote_ident(c.relname) || ' AS' || chr(10)
                   || pg_get_viewdef(c.oid, true)
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('v','m') AND {NsFilter} AND {NotExtensionClass}
        UNION ALL
        SELECT n.nspname, t.tgname, 'Trigger', pg_get_triggerdef(t.oid, true) || ';'
        FROM pg_trigger t
        JOIN pg_class c ON c.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE NOT t.tgisinternal AND {NsFilter}
        """;

    public static readonly string ModuleDefinition =
        $"""SELECT m."Definition" FROM ({Modules}) m WHERE m."Schema" = @schema AND m."Name" = @name;""";

    /// <summary>
    /// The parameters of one routine. An overloaded name has several routines: the first one (lowest oid) is described,
    /// so its parameters are never mixed with another overload's. Types come from format_type, so they can be cast to;
    /// unnamed parameters are called $1, $2… after their position; a VARIADIC one carries the word in its type.
    /// </summary>
    public const string RoutineParameters = """
        SELECT a."Schema", a."RoutineName", a."Name", a."DataType", a."Direction",
               (a."Direction" <> 2 AND count(*) FILTER (WHERE a."Direction" <> 2) OVER (ORDER BY a."Ordinal" DESC) <= a.defaults)
                   AS "HasDefault",
               a."Ordinal"
        FROM (
            SELECT n.nspname AS "Schema",
                   p.proname AS "RoutineName",
                   COALESCE(NULLIF(p.proargnames[u.i], ''), '$' || u.i) AS "Name",
                   CASE WHEN p.proargmodes[u.i] = 'v' THEN 'VARIADIC ' ELSE '' END || format_type(u.t, NULL) AS "DataType",
                   CASE p.proargmodes[u.i] WHEN 'b' THEN 1 WHEN 'o' THEN 2 WHEN 't' THEN 2 ELSE 0 END AS "Direction",
                   p.pronargdefaults AS defaults,
                   u.i::int AS "Ordinal"
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            CROSS JOIN LATERAL unnest(COALESCE(p.proallargtypes, p.proargtypes::oid[])) WITH ORDINALITY u(t, i)
            WHERE p.oid = (SELECT p2.oid FROM pg_proc p2 JOIN pg_namespace n2 ON n2.oid = p2.pronamespace
                           WHERE n2.nspname = @schema AND p2.proname = @name ORDER BY p2.oid LIMIT 1)
        ) a
        ORDER BY a."Ordinal";
        """;

    // Generated columns keep their expression in pg_attrdef too; those are not defaults.
    public const string ColumnDefaults = """
        SELECT a.attname AS "Name", pg_get_expr(d.adbin, d.adrelid) AS "Definition"
        FROM pg_attrdef d
        JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
        WHERE d.adrelid = to_regclass(@name) AND a.attgenerated = '' AND NOT a.attisdropped;
        """;

    public const string CheckConstraints = """
        SELECT conname AS "Name", pg_get_expr(conbin, conrelid) AS "Definition"
        FROM pg_constraint
        WHERE conrelid = to_regclass(@name) AND contype = 'c'
        ORDER BY conname;
        """;

    public const string SequenceDefinition = """
        SELECT 'CREATE SEQUENCE ' || quote_ident(schemaname) || '.' || quote_ident(sequencename)
            || ' AS ' || data_type::text
            || ' START ' || start_value || ' INCREMENT ' || increment_by
            || ' MINVALUE ' || min_value || ' MAXVALUE ' || max_value
            || CASE WHEN cycle THEN ' CYCLE' ELSE ' NO CYCLE' END || ';'
        FROM pg_sequences
        WHERE schemaname = @schema AND sequencename = @name;
        """;

    public static readonly string Indexes = $"""
        SELECT n.nspname AS "Schema",
               t.relname AS "Table",
               ic.relname AS "Name",
               am.amname::text AS "Type",
               i.indisunique AS "IsUnique",
               i.indisprimary AS "IsPrimaryKey",
               EXISTS (SELECT 1 FROM pg_constraint con
                        WHERE con.conindid = i.indexrelid AND con.contype IN ('u', 'p', 'x')) AS "IsConstraint",
               NOT i.indisvalid AS "IsDisabled",
               (SELECT string_agg(pg_get_indexdef(i.indexrelid, k, true), ', ' ORDER BY k)
                  FROM generate_series(1, i.indnkeyatts::int) AS k) AS "Columns",
               (SELECT string_agg(pg_get_indexdef(i.indexrelid, k, true), ', ' ORDER BY k)
                  FROM generate_series(i.indnkeyatts::int + 1, i.indnatts::int) AS k) AS "IncludedColumns",
               pg_get_expr(i.indpred, i.indrelid) AS "Filter",
               CASE WHEN ic.reltuples >= 0 THEN ic.reltuples::bigint END AS "Rows",
               pg_relation_size(i.indexrelid) AS "SizeBytes",
               s.idx_scan AS "Seeks",
               NULL::bigint AS "Scans",
               NULL::bigint AS "Updates",
               NULL::float8 AS "FragmentationPercent"
        FROM pg_index i
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_class t ON t.oid = i.indrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        JOIN pg_am am ON am.oid = ic.relam
        LEFT JOIN pg_stat_user_indexes s ON s.indexrelid = i.indexrelid
        WHERE {NsFilter}
        ORDER BY 1, 2, 3;
        """;

    public const string ForeignKeys = """
        SELECT n.nspname AS "Schema",
               t.relname AS "Table",
               con.conname AS "Name",
               (SELECT string_agg(a.attname, ', ' ORDER BY x.ord)
                  FROM unnest(con.conkey) WITH ORDINALITY AS x(attnum, ord)
                  JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = x.attnum) AS "Columns",
               rn.nspname AS "ReferencedSchema",
               rt.relname AS "ReferencedTable",
               (SELECT string_agg(a.attname, ', ' ORDER BY x.ord)
                  FROM unnest(con.confkey) WITH ORDINALITY AS x(attnum, ord)
                  JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = x.attnum) AS "ReferencedColumns",
               NOT con.convalidated AS "IsDisabled"
        FROM pg_constraint con
        JOIN pg_class t ON t.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        JOIN pg_class rt ON rt.oid = con.confrelid
        JOIN pg_namespace rn ON rn.oid = rt.relnamespace
        WHERE con.contype = 'f'
        ORDER BY 1, 2, 3;
        """;

    public const string Locks = """
        SELECT l.pid AS "SessionId",
               a.usename::text AS "LoginName",
               a.client_addr::text AS "HostName",
               a.application_name AS "ProgramName",
               a.datname::text AS "DatabaseName",
               l.locktype AS "ResourceType",
               CASE WHEN l.relation IS NOT NULL THEN l.relation::regclass::text END AS "ObjectName",
               l.mode AS "LockMode",
               CASE WHEN l.granted THEN 'GRANT' ELSE 'WAIT' END AS "Status",
               (pg_blocking_pids(l.pid))[1] AS "BlockedBy",
               CASE WHEN NOT l.granted
                    THEN (EXTRACT(EPOCH FROM (clock_timestamp() - a.query_start)) * 1000)::int END AS "WaitMs",
               a.wait_event_type AS "WaitType",
               a.query AS "SqlText"
        FROM pg_locks l
        LEFT JOIN pg_stat_activity a ON a.pid = l.pid
        WHERE (l.database = (SELECT oid FROM pg_database WHERE datname = current_database()) OR l.database IS NULL)
          AND l.pid <> pg_backend_pid()
        ORDER BY l.granted, l.pid
        LIMIT 5000;
        """;
}
