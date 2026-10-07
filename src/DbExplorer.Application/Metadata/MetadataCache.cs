using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Application.Metadata;

/// <summary>
/// Persists metadata snapshots in a local SQLite file per server/database, so reopening a
/// connection does not query the server catalog again until the user asks for a refresh.
/// </summary>
public sealed class MetadataCache(AppPaths paths)
{
    private const string SchemaVersion = "5";

    /// <summary>Older formats still read (missing details default), so an app update does not force a slow full
    /// catalog read on the next connect; such snapshots are flagged <see cref="MetadataSnapshot.IsStale"/>.</summary>
    private const string PreviousSchemaVersion = "4";

    public static string CacheKey(ConnectionProfile p)
    {
        var raw = $"{p.ProviderKey}|{p.Host}|{p.Port}|{p.Database}".ToLowerInvariant();
        // Tunnelled databases are often all "localhost": keep them apart by SSH server. Direct keys stay as they were.
        if (p.TunnelKey is { } tunnel) raw += "|ssh:" + tunnel;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24];
    }

    public Task<MetadataSnapshot?> TryLoadAsync(string key, CancellationToken ct = default) =>
        Task.Run(() => TryLoad(key, ct), ct);

    public Task SaveAsync(string key, MetadataSnapshot snapshot, CancellationToken ct = default) =>
        Task.Run(() => Save(key, snapshot, ct), ct);

    public void Invalidate(string key)
    {
        var file = FilePath(key);
        if (File.Exists(file)) File.Delete(file);
    }

    private string FilePath(string key) => Path.Combine(paths.CacheDirectory, $"{key}.db");

    private static SqliteConnection Open(string file)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        var cn = new SqliteConnection(cs);
        cn.Open();
        return cn;
    }

    private MetadataSnapshot? TryLoad(string key, CancellationToken ct)
    {
        var file = FilePath(key);
        if (!File.Exists(file)) return null;

        try
        {
            using var cn = Open(file);
            var version = ReadMeta(cn, "version");
            if (version != SchemaVersion && version != PreviousSchemaVersion) return null;
            var stale = version != SchemaVersion;
            var refreshed = ReadMeta(cn, "refreshed_at");
            if (refreshed is null) return null;

            var objects = new List<DbObject>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT database_name, schema_name, name, type, created_at, modified_at, row_count FROM objects";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    objects.Add(new DbObject
                    {
                        Database = r.GetString(0),
                        Schema = r.GetString(1),
                        Name = r.GetString(2),
                        Type = Enum.TryParse<DbObjectType>(r.GetString(3), out var t) ? t : DbObjectType.Other,
                        CreatedAt = r.IsDBNull(4) ? null : ParseDate(r.GetString(4)),
                        ModifiedAt = r.IsDBNull(5) ? null : ParseDate(r.GetString(5)),
                        RowCount = r.IsDBNull(6) ? null : r.GetInt64(6)
                    });
                }
            }
            ct.ThrowIfCancellationRequested();

            var columns = new List<DbColumn>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT database_name, schema_name, table_name, name, data_type, base_type, is_nullable, ordinal, is_computed, is_primary_key" +
                                  (stale ? ", 0" : ", is_identity") + " FROM columns";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    columns.Add(new DbColumn
                    {
                        Database = r.GetString(0),
                        Schema = r.GetString(1),
                        Table = r.GetString(2),
                        Name = r.GetString(3),
                        DataType = r.GetString(4),
                        BaseType = r.GetString(5),
                        IsNullable = r.GetInt64(6) != 0,
                        Ordinal = r.GetInt32(7),
                        IsComputed = r.GetInt64(8) != 0,
                        IsPrimaryKey = r.GetInt64(9) != 0,
                        IsIdentity = r.GetInt64(10) != 0
                    });
                }
            }
            ct.ThrowIfCancellationRequested();

            var modules = new List<DbModule>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT database_name, schema_name, name, type, definition FROM modules";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    modules.Add(new DbModule
                    {
                        Database = r.GetString(0),
                        Schema = r.GetString(1),
                        Name = r.GetString(2),
                        Type = Enum.TryParse<DbObjectType>(r.GetString(3), out var t) ? t : DbObjectType.Other,
                        Definition = r.IsDBNull(4) ? null : r.GetString(4)
                    });
                }
            }

            var foreignKeys = new List<DbForeignKey>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT database_name, schema_name, table_name, name, columns, referenced_schema, referenced_table, referenced_columns, is_disabled FROM foreign_keys";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    foreignKeys.Add(new DbForeignKey
                    {
                        Database = r.GetString(0),
                        Schema = r.GetString(1),
                        Table = r.GetString(2),
                        Name = r.GetString(3),
                        Columns = r.IsDBNull(4) ? null : r.GetString(4),
                        ReferencedSchema = r.GetString(5),
                        ReferencedTable = r.GetString(6),
                        ReferencedColumns = r.IsDBNull(7) ? null : r.GetString(7),
                        IsDisabled = r.GetInt64(8) != 0
                    });
                }
            }

            var indexes = new List<DbIndex>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT database_name, schema_name, table_name, name, type, is_unique, is_primary_key, is_disabled, columns, included_columns, filter, row_count, size_bytes FROM indexes";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    indexes.Add(new DbIndex
                    {
                        Database = r.GetString(0),
                        Schema = r.GetString(1),
                        Table = r.GetString(2),
                        Name = r.GetString(3),
                        Type = r.GetString(4),
                        IsUnique = r.GetInt64(5) != 0,
                        IsPrimaryKey = r.GetInt64(6) != 0,
                        IsDisabled = r.GetInt64(7) != 0,
                        Columns = r.IsDBNull(8) ? null : r.GetString(8),
                        IncludedColumns = r.IsDBNull(9) ? null : r.GetString(9),
                        Filter = r.IsDBNull(10) ? null : r.GetString(10),
                        Rows = r.IsDBNull(11) ? null : r.GetInt64(11),
                        SizeBytes = r.IsDBNull(12) ? null : r.GetInt64(12)
                    });
                }
            }

            return new MetadataSnapshot
            {
                Objects = objects,
                Columns = columns,
                Modules = modules,
                ForeignKeys = foreignKeys,
                Indexes = indexes,
                RefreshedAt = DateTimeOffset.Parse(refreshed, CultureInfo.InvariantCulture),
                IsStale = stale
            };
        }
        catch (Exception ex) when (ex is SqliteException or FormatException or InvalidCastException or ArgumentException)
        {
            return null; // corrupt or old cache: fall back to the server
        }
    }

    private void Save(string key, MetadataSnapshot s, CancellationToken ct)
    {
        using var cn = Open(FilePath(key));
        using var tx = cn.BeginTransaction();

        Exec(cn, tx, """
            DROP TABLE IF EXISTS meta; DROP TABLE IF EXISTS objects;
            DROP TABLE IF EXISTS columns; DROP TABLE IF EXISTS modules; DROP TABLE IF EXISTS foreign_keys;
            DROP TABLE IF EXISTS indexes;
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE objects (database_name TEXT NOT NULL, schema_name TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL,
                                  created_at TEXT, modified_at TEXT, row_count INTEGER);
            CREATE TABLE columns (database_name TEXT NOT NULL, schema_name TEXT NOT NULL, table_name TEXT NOT NULL, name TEXT NOT NULL,
                                  data_type TEXT NOT NULL, base_type TEXT NOT NULL, is_nullable INTEGER NOT NULL,
                                  ordinal INTEGER NOT NULL, is_computed INTEGER NOT NULL, is_primary_key INTEGER NOT NULL,
                                  is_identity INTEGER NOT NULL);
            CREATE TABLE modules (database_name TEXT NOT NULL, schema_name TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL, definition TEXT);
            CREATE TABLE foreign_keys (database_name TEXT NOT NULL, schema_name TEXT NOT NULL, table_name TEXT NOT NULL, name TEXT NOT NULL,
                                  columns TEXT, referenced_schema TEXT NOT NULL, referenced_table TEXT NOT NULL, referenced_columns TEXT,
                                  is_disabled INTEGER NOT NULL);
            CREATE TABLE indexes (database_name TEXT NOT NULL, schema_name TEXT NOT NULL, table_name TEXT NOT NULL, name TEXT NOT NULL,
                                  type TEXT NOT NULL, is_unique INTEGER NOT NULL, is_primary_key INTEGER NOT NULL, is_disabled INTEGER NOT NULL,
                                  columns TEXT, included_columns TEXT, filter TEXT, row_count INTEGER, size_bytes INTEGER);
            """);

        BulkInsert(cn, tx, "objects", 7, s.Objects, o =>
        [
            o.Database, o.Schema, o.Name, o.Type.ToString(), FormatDate(o.CreatedAt), FormatDate(o.ModifiedAt), o.RowCount
        ], ct);

        BulkInsert(cn, tx, "columns", 11, s.Columns, c =>
        [
            c.Database, c.Schema, c.Table, c.Name, c.DataType, c.BaseType,
            c.IsNullable ? 1 : 0, c.Ordinal, c.IsComputed ? 1 : 0, c.IsPrimaryKey ? 1 : 0, c.IsIdentity ? 1 : 0
        ], ct);

        BulkInsert(cn, tx, "modules", 5, s.Modules, m =>
        [
            m.Database, m.Schema, m.Name, m.Type.ToString(), m.Definition
        ], ct);

        BulkInsert(cn, tx, "foreign_keys", 9, s.ForeignKeys, f =>
        [
            f.Database, f.Schema, f.Table, f.Name, f.Columns, f.ReferencedSchema, f.ReferencedTable, f.ReferencedColumns, f.IsDisabled ? 1 : 0
        ], ct);

        BulkInsert(cn, tx, "indexes", 13, s.Indexes, i =>
        [
            i.Database, i.Schema, i.Table, i.Name, i.Type, i.IsUnique ? 1 : 0, i.IsPrimaryKey ? 1 : 0, i.IsDisabled ? 1 : 0,
            i.Columns, i.IncludedColumns, i.Filter, i.Rows, i.SizeBytes
        ], ct);

        BulkInsert(cn, tx, "meta", 2, new[]
        {
            ("version", SchemaVersion),
            ("refreshed_at", s.RefreshedAt.ToString("o", CultureInfo.InvariantCulture))
        }, kv => [kv.Item1, kv.Item2], ct);

        tx.Commit();
    }

    private static void BulkInsert<T>(
        SqliteConnection cn, SqliteTransaction tx, string table, int columnCount,
        IEnumerable<T> rows, Func<T, object?[]> values, CancellationToken ct)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        var names = Enumerable.Range(0, columnCount).Select(i => $"$p{i}").ToArray();
        cmd.CommandText = $"INSERT INTO {table} VALUES ({string.Join(", ", names)})";
        // No explicit SqliteType: the type is inferred from each value (TEXT, INTEGER or NULL).
        var parameters = names.Select(n =>
        {
            var p = cmd.CreateParameter();
            p.ParameterName = n;
            cmd.Parameters.Add(p);
            return p;
        }).ToArray();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var v = values(row);
            for (var i = 0; i < columnCount; i++) parameters[i].Value = v[i] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private static void Exec(SqliteConnection cn, SqliteTransaction tx, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? ReadMeta(SqliteConnection cn, string key)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static string? FormatDate(DateTime? d) => d?.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
