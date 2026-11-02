using DbExplorer.Application.Copy;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.Documentation;

/// <summary>
/// The descriptions people wrote in the database: COMMENT ON (PostgreSQL), MS_Description extended properties
/// (SQL Server) and COMMENT clauses (MySQL), on the database, schemas, objects and columns. The metadata cache does not
/// keep them, so they are read once when documentation is exported.
/// </summary>
public sealed class CatalogComments
{
    public static readonly CatalogComments None = new([]);

    private readonly Dictionary<string, string> _comments = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="rows">(schema, object, column, comment): object and column null for a schema comment, all three
    /// null for the database's own comment.</param>
    public CatalogComments(IEnumerable<(string? Schema, string? Object, string? Column, string? Comment)> rows)
    {
        foreach (var (schema, obj, column, comment) in rows)
        {
            if (string.IsNullOrWhiteSpace(comment)) continue;
            _comments[Key(schema, obj, column)] = comment.Trim();
        }
    }

    public int Count => _comments.Count;

    public string? Database => Get(null, null, null);
    public string? ForSchema(string schema) => Get(schema, null, null);
    public string? ForObject(string schema, string name) => Get(schema, name, null);
    public string? ForColumn(string schema, string table, string column) => Get(schema, table, column);

    private string? Get(string? schema, string? obj, string? column) => _comments.GetValueOrDefault(Key(schema, obj, column));

    private static string Key(string? schema, string? obj, string? column) => $"{schema}\u001f{obj}\u001f{column}";

    /// <summary>The catalog query for the engine, or null when the engine has none. Returns four columns: schema,
    /// object, column, comment.</summary>
    public static string? Sql(string providerKey, string? database) => providerKey switch
    {
        SqlDialect.SqlServerKey => """
            SELECT NULL AS [schema], NULL AS [object], NULL AS [column], CAST(ep.value AS nvarchar(max)) AS [comment]
            FROM sys.extended_properties ep
            WHERE ep.class = 0 AND ep.name = N'MS_Description'
            UNION ALL
            SELECT s.name, NULL, NULL, CAST(ep.value AS nvarchar(max))
            FROM sys.extended_properties ep
            JOIN sys.schemas s ON s.schema_id = ep.major_id
            WHERE ep.class = 3 AND ep.name = N'MS_Description'
            UNION ALL
            SELECT s.name, o.name, c.name, CAST(ep.value AS nvarchar(max))
            FROM sys.extended_properties ep
            JOIN sys.objects o ON o.object_id = ep.major_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.columns c ON ep.minor_id > 0 AND c.object_id = ep.major_id AND c.column_id = ep.minor_id
            WHERE ep.class = 1 AND ep.name = N'MS_Description' AND (ep.minor_id = 0 OR c.column_id IS NOT NULL)
            """,
        SqlDialect.PostgresKey => """
            SELECT NULL::text AS schema, NULL::text AS object, NULL::text AS column_name,
                   pg_catalog.shobj_description(d.oid, 'pg_database') AS comment
            FROM pg_catalog.pg_database d
            WHERE d.datname = pg_catalog.current_database()
            UNION ALL
            SELECT n.nspname::text, NULL, NULL, d.description
            FROM pg_catalog.pg_description d
            JOIN pg_catalog.pg_namespace n ON d.classoid = 'pg_catalog.pg_namespace'::regclass AND n.oid = d.objoid
            UNION ALL
            SELECT n.nspname::text, c.relname::text, a.attname::text, d.description
            FROM pg_catalog.pg_description d
            JOIN pg_catalog.pg_class c ON d.classoid = 'pg_catalog.pg_class'::regclass AND c.oid = d.objoid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_attribute a ON d.objsubid > 0 AND a.attrelid = c.oid AND a.attnum = d.objsubid
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND (d.objsubid = 0 OR a.attnum IS NOT NULL)
            UNION ALL
            SELECT n.nspname::text, p.proname::text, NULL, d.description
            FROM pg_catalog.pg_description d
            JOIN pg_catalog.pg_proc p ON d.classoid = 'pg_catalog.pg_proc'::regclass AND p.oid = d.objoid
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
            UNION ALL
            SELECT n.nspname::text, t.tgname::text, NULL, d.description
            FROM pg_catalog.pg_description d
            JOIN pg_catalog.pg_trigger t ON d.classoid = 'pg_catalog.pg_trigger'::regclass AND t.oid = d.objoid
            JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
            """,
        SqlDialect.MySqlKey when !string.IsNullOrEmpty(database) => $"""
            SELECT TABLE_SCHEMA AS `schema`, TABLE_NAME AS `object`, NULL AS `column`, TABLE_COMMENT AS `comment`
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = {MySqlLiteral(database)} AND TABLE_TYPE <> 'VIEW' AND TABLE_COMMENT <> ''
            UNION ALL
            SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, COLUMN_COMMENT
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = {MySqlLiteral(database)} AND COLUMN_COMMENT <> ''
            UNION ALL
            SELECT ROUTINE_SCHEMA, ROUTINE_NAME, NULL, ROUTINE_COMMENT
            FROM information_schema.ROUTINES
            WHERE ROUTINE_SCHEMA = {MySqlLiteral(database)} AND ROUTINE_COMMENT <> ''
            """,
        _ => null
    };

    private static string MySqlLiteral(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";

    /// <summary>Reads the comments of one database with the provider's non-blocking read-only path.</summary>
    public static async Task<CatalogComments> LoadAsync(IDatabaseProvider provider, string? database, CancellationToken ct = default)
    {
        var sql = Sql(provider.ProviderKey, database);
        if (sql is null) return None;
        var result = await provider.QueryReadOnlyAsync(sql, database, new DataSearchOptions(1, 60, 5000), maxRows: 500_000, ct);
        return FromRows(result);
    }

    public static CatalogComments FromRows(QueryResultSet result) =>
        new(result.Rows.Select(r => (Text(r, 0), Text(r, 1), Text(r, 2), Text(r, 3))));

    private static string? Text(IReadOnlyList<object?> row, int i) =>
        i < row.Count && row[i] is { } v and not DBNull ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
}
