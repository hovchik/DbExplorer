namespace DbExplorer.Application.Security;

/// <summary>
/// Catalog queries behind the Security tab. They read catalog views only (no locks on user data) and never a password
/// or its hash: PostgreSQL's pg_roles masks rolpassword, and nothing here reads pg_authid or sys.sql_logins.
/// </summary>
internal static class SecurityQueries
{
    // ---------- PostgreSQL: roles are server-wide, permissions belong to the database read ----------

    public const string PostgresRoles = """
        SELECT r.rolname AS name, r.rolcanlogin AS can_login, r.rolsuper AS superuser, r.rolcreatedb AS create_db,
               r.rolcreaterole AS create_role, r.rolinherit AS inherit, r.rolreplication AS replication,
               r.rolbypassrls AS bypass_rls, r.rolconnlimit AS conn_limit,
               to_char(r.rolvaliduntil, 'YYYY-MM-DD HH24:MI') AS valid_until,
               (r.rolname LIKE 'pg\_%' OR r.oid < 16384) AS is_system
        FROM pg_catalog.pg_roles r
        ORDER BY r.rolname
        """;

    public const string PostgresMemberships = """
        SELECT m.rolname AS member, g.rolname AS role_name, bool_or(am.admin_option) AS admin_option
        FROM pg_catalog.pg_auth_members am
        JOIN pg_catalog.pg_roles m ON m.oid = am.member
        JOIN pg_catalog.pg_roles g ON g.oid = am.roleid
        GROUP BY m.rolname, g.rolname
        ORDER BY g.rolname, m.rolname
        """;

    /// <summary>Explicit ACL entries, or the owner's implicit ones (acldefault) where an object has none, for the
    /// database itself, its schemas, tables/views/sequences and functions. Grantee 0 is PUBLIC.</summary>
    public const string PostgresGrants = """
        SELECT 'DATABASE' AS kind, '' AS schema_name, '' AS object_name, NULL::text AS arguments,
               CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee) END AS grantee,
               pg_catalog.pg_get_userbyid(a.grantor) AS grantor, a.privilege_type AS permission, a.is_grantable
        FROM pg_catalog.pg_database d
        CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))) a
        WHERE d.datname = current_database()
        UNION ALL
        SELECT 'SCHEMA', n.nspname, '', NULL,
               CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
               pg_catalog.pg_get_userbyid(a.grantor), a.privilege_type, a.is_grantable
        FROM pg_catalog.pg_namespace n
        CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_toast%' AND n.nspname NOT LIKE 'pg\_temp%'
        UNION ALL
        SELECT CASE c.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 'S' THEN 'SEQUENCE' ELSE 'TABLE' END,
               n.nspname, c.relname, NULL,
               CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
               pg_catalog.pg_get_userbyid(a.grantor), a.privilege_type, a.is_grantable
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(c.relacl, pg_catalog.acldefault(CASE WHEN c.relkind = 'S' THEN 's'::"char" ELSE 'r'::"char" END, c.relowner))) a
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_toast%' AND n.nspname NOT LIKE 'pg\_temp%'
        UNION ALL
        SELECT 'FUNCTION', n.nspname, p.proname, pg_catalog.pg_get_function_identity_arguments(p.oid),
               CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
               pg_catalog.pg_get_userbyid(a.grantor), a.privilege_type, a.is_grantable
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
        CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(p.proacl, pg_catalog.acldefault('f', p.proowner))) a
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend dep WHERE dep.classid = 'pg_catalog.pg_proc'::regclass AND dep.objid = p.oid AND dep.deptype = 'e')
        ORDER BY 1, 2, 3, 5, 7
        """;

    // ---------- SQL Server: logins and server roles are server-wide, users/roles/permissions per database ----------

    public const string SqlServerServerPrincipals = """
        SELECT p.name, p.type AS type_code, p.type_desc, p.is_disabled, p.default_database_name,
               CAST(CASE WHEN p.type = 'R' AND (p.is_fixed_role = 1 OR p.name = N'public') THEN 1 ELSE 0 END AS bit) AS is_fixed_role,
               CAST(CASE WHEN p.principal_id < 256 OR p.name LIKE N'NT SERVICE\%' OR p.name LIKE N'NT AUTHORITY\%' THEN 1 ELSE 0 END AS bit) AS is_system
        FROM sys.server_principals p
        WHERE p.type IN ('S', 'U', 'G', 'R', 'E', 'X', 'C', 'K') AND p.name NOT LIKE N'##%'
        ORDER BY p.name
        """;

    public const string SqlServerServerRoleMembers = """
        SELECT m.name AS member, r.name AS role_name
        FROM sys.server_role_members rm
        JOIN sys.server_principals r ON r.principal_id = rm.role_principal_id
        JOIN sys.server_principals m ON m.principal_id = rm.member_principal_id
        ORDER BY r.name, m.name
        """;

    public const string SqlServerDatabasePrincipals = """
        SELECT dp.name, dp.type AS type_code, dp.type_desc, dp.default_schema_name,
               CAST(CASE WHEN dp.type = 'R' AND (dp.is_fixed_role = 1 OR dp.name = N'public') THEN 1 ELSE 0 END AS bit) AS is_fixed_role,
               CAST(CASE WHEN dp.principal_id < 5 OR dp.is_fixed_role = 1 OR dp.name IN (N'public', N'dbo', N'guest', N'sys', N'INFORMATION_SCHEMA') THEN 1 ELSE 0 END AS bit) AS is_system,
               sp.name AS login_name,
               dp.authentication_type_desc AS authentication
        FROM sys.database_principals dp
        LEFT JOIN sys.server_principals sp ON sp.sid = dp.sid AND dp.type IN ('S', 'U', 'G', 'E', 'X')
        WHERE dp.type IN ('S', 'U', 'G', 'R', 'A', 'E', 'X', 'C', 'K') AND dp.name NOT LIKE N'##%'
        ORDER BY dp.name
        """;

    public const string SqlServerDatabaseRoleMembers = """
        SELECT m.name AS member, r.name AS role_name
        FROM sys.database_role_members rm
        JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
        JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
        ORDER BY r.name, m.name
        """;

    /// <summary>Permissions on the database (class 0), objects and columns (1), schemas (3) and types (6).</summary>
    public const string SqlServerPermissions = """
        SELECT pr.name AS grantee, gp.name AS grantor, perm.permission_name AS permission, perm.state,
               perm.class,
               CASE perm.class WHEN 1 THEN OBJECT_SCHEMA_NAME(perm.major_id) WHEN 3 THEN SCHEMA_NAME(perm.major_id)
                               WHEN 6 THEN SCHEMA_NAME(t.schema_id) ELSE N'' END AS schema_name,
               CASE perm.class WHEN 1 THEN OBJECT_NAME(perm.major_id) WHEN 6 THEN t.name ELSE N'' END AS object_name,
               CASE WHEN perm.class = 1 THEN o.type ELSE NULL END AS object_type,
               CASE WHEN perm.class = 1 AND perm.minor_id > 0 THEN COL_NAME(perm.major_id, perm.minor_id) END AS column_name
        FROM sys.database_permissions perm
        JOIN sys.database_principals pr ON pr.principal_id = perm.grantee_principal_id
        LEFT JOIN sys.database_principals gp ON gp.principal_id = perm.grantor_principal_id
        LEFT JOIN sys.objects o ON perm.class = 1 AND o.object_id = perm.major_id
        LEFT JOIN sys.types t ON perm.class = 6 AND t.user_type_id = perm.major_id
        WHERE perm.class IN (0, 1, 3, 6) AND pr.name NOT LIKE N'##%'
          AND NOT (perm.class = 1 AND o.is_ms_shipped = 1)
        ORDER BY pr.name, perm.class, schema_name, object_name, perm.permission_name
        """;
}
