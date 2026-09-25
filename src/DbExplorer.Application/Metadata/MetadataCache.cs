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
    private const string SchemaVersion = "2";

    public static string CacheKey(ConnectionProfile p)
    {
        var raw = $"{p.ProviderKey}|{p.Host}|{p.Port}|{p.Database}".ToLowerInvariant();
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
            if (ReadMeta(cn, "version") != SchemaVersion) return null;
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
                cmd.CommandText = "SELECT database_name, schema_name, table_name, name, data_type, base_type, is_nullable, ordinal, is_computed, is_primary_key FROM columns";
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
                        IsPrimaryKey = r.GetInt64(9) != 0
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

            return new MetadataSnapshot
            {
                Objects = objects,
                Columns = columns,
                Modules = modules,
                RefreshedAt = DateTimeOffset.Parse(refreshed, CultureInfo.InvariantCulture)
            };
        }
        catch (SqliteException)
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
            DROP TABLE IF EXISTS columns; DROP TABLE IF EXISTS modules;
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE objects (database_name TEXT NOT NULL, schema_name TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL,
                                  created_at TEXT, modified_at TEXT, row_count INTEGER);
            CREATE TABLE columns (database_name TEXT NOT NULL, schema_name TEXT NOT NULL, table_name TEXT NOT NULL, name TEXT NOT NULL,
                                  data_type TEXT NOT NULL, base_type TEXT NOT NULL, is_nullable INTEGER NOT NULL,
                                  ordinal INTEGER NOT NULL, is_computed INTEGER NOT NULL, is_primary_key INTEGER NOT NULL);
            CREATE TABLE modules (database_name TEXT NOT NULL, schema_name TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL, definition TEXT);
            """);

        BulkInsert(cn, tx, "objects", 7, s.Objects, o =>
        [
            o.Database, o.Schema, o.Name, o.Type.ToString(), FormatDate(o.CreatedAt), FormatDate(o.ModifiedAt), o.RowCount
        ], ct);

        BulkInsert(cn, tx, "columns", 10, s.Columns, c =>
        [
            c.Database, c.Schema, c.Table, c.Name, c.DataType, c.BaseType,
            c.IsNullable ? 1 : 0, c.Ordinal, c.IsComputed ? 1 : 0, c.IsPrimaryKey ? 1 : 0
        ], ct);

        BulkInsert(cn, tx, "modules", 5, s.Modules, m =>
        [
            m.Database, m.Schema, m.Name, m.Type.ToString(), m.Definition
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
