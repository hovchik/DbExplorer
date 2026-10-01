using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Application.Lab;

/// <summary>One object as it was in a recorded version: its identity and the hash of its canonical text.</summary>
public sealed record SchemaEntry(string Database, string Schema, string Name, DbObjectType Type, string Hash)
{
    public string FullName => (Database.Length > 0 ? Database + "." : "") + Schema + "." + Name;
}

public sealed record SchemaVersion(long Id, DateTimeOffset TakenAt, int ObjectCount);

public enum SchemaChangeKind
{
    Added,
    Removed,
    Changed
}

public sealed record SchemaChange(SchemaChangeKind Kind, string Database, string Schema, string Name, DbObjectType Type,
    string? BeforeHash, string? AfterHash)
{
    public string FullName => (Database.Length > 0 ? Database + "." : "") + Schema + "." + Name;
}

/// <summary>When an object's definition changed, as seen by the recorded versions.</summary>
public sealed record ObjectRevision(SchemaVersion Version, string? Hash, SchemaChangeKind Kind);

/// <summary>
/// A local history of the catalog: every metadata read from the server is stored as a version (only when something
/// changed), with definitions de-duplicated by hash, so "what changed in Production since Tuesday" needs no DDL
/// triggers or audit on the server. Lives next to the metadata cache in <c>&lt;key&gt;.history.db</c>.
/// </summary>
public sealed class SchemaHistoryStore(AppPaths paths)
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private string FilePath(string key) => Path.Combine(paths.CacheDirectory, $"{key}.history.db");

    /// <summary>Stores the snapshot as a new version unless it equals the latest one; returns the new version.</summary>
    public async Task<SchemaVersion?> RecordAsync(string key, MetadataSnapshot snapshot, CancellationToken ct = default)
    {
        var entries = await Task.Run(() => Canonicalize(snapshot), ct);
        await WriteLock.WaitAsync(ct);
        try
        {
            return await Task.Run(() => Record(key, snapshot.RefreshedAt, entries), ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public Task<IReadOnlyList<SchemaVersion>> GetVersionsAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    {
        if (!File.Exists(FilePath(key))) return (IReadOnlyList<SchemaVersion>)[];
        using var cn = Open(key);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT id, taken_at, object_count FROM versions ORDER BY id DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<SchemaVersion>();
        while (r.Read())
            list.Add(new SchemaVersion(r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetInt32(2)));
        return (IReadOnlyList<SchemaVersion>)list;
    }, ct);

    public Task<IReadOnlyList<SchemaEntry>> GetEntriesAsync(string key, long versionId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var cn = Open(key);
            return ReadEntries(cn, versionId);
        }, ct);

    public async Task<IReadOnlyList<SchemaChange>> CompareAsync(string key, long fromVersion, long toVersion, CancellationToken ct = default)
    {
        var from = await GetEntriesAsync(key, fromVersion, ct);
        var to = await GetEntriesAsync(key, toVersion, ct);
        return Diff(from, to);
    }

    /// <summary>The canonical text stored under <paramref name="hash"/> (a definition, or a table's column/index/key list).</summary>
    public Task<string?> GetTextAsync(string key, string? hash, CancellationToken ct = default) => Task.Run(() =>
    {
        if (hash is null) return null;
        using var cn = Open(key);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT text FROM blobs WHERE hash = $h";
        cmd.Parameters.AddWithValue("$h", hash);
        return cmd.ExecuteScalar() as string;
    }, ct);

    /// <summary>The versions in which the object appeared, changed or disappeared, newest first.</summary>
    public Task<IReadOnlyList<ObjectRevision>> GetObjectHistoryAsync(string key, string database, string schema, string name, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var cn = Open(key);
            var versions = new List<SchemaVersion>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, taken_at, object_count FROM versions ORDER BY id";
                using var r = cmd.ExecuteReader();
                while (r.Read()) versions.Add(new SchemaVersion(r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetInt32(2)));
            }

            var hashes = new Dictionary<long, string>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT version_id, hash FROM entries WHERE database_name = $d AND schema_name = $s AND name = $n COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$d", database);
                cmd.Parameters.AddWithValue("$s", schema);
                cmd.Parameters.AddWithValue("$n", name);
                using var r = cmd.ExecuteReader();
                while (r.Read()) hashes[r.GetInt64(0)] = r.GetString(1);
            }
            return (IReadOnlyList<ObjectRevision>)Revisions(versions, hashes);
        }, ct);

    /// <summary>Walks the versions in order and keeps the ones where the object's hash differs from the previous version.</summary>
    public static IReadOnlyList<ObjectRevision> Revisions(IReadOnlyList<SchemaVersion> versionsOldestFirst, IReadOnlyDictionary<long, string> hashes)
    {
        var result = new List<ObjectRevision>();
        string? previous = null;
        var existed = false;
        foreach (var v in versionsOldestFirst)
        {
            var exists = hashes.TryGetValue(v.Id, out var hash);
            if (exists && !existed) result.Add(new ObjectRevision(v, hash, SchemaChangeKind.Added));
            else if (!exists && existed) result.Add(new ObjectRevision(v, null, SchemaChangeKind.Removed));
            else if (exists && hash != previous) result.Add(new ObjectRevision(v, hash, SchemaChangeKind.Changed));
            existed = exists;
            previous = hash;
        }
        result.Reverse();
        return result;
    }

    /// <summary>Objects added, removed or changed from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static IReadOnlyList<SchemaChange> Diff(IReadOnlyList<SchemaEntry> from, IReadOnlyList<SchemaEntry> to)
    {
        static string K(SchemaEntry e) => $"{e.Database}\u0001{e.Schema}\u0001{e.Name}\u0001{e.Type}".ToUpperInvariant();
        var before = from.GroupBy(K).ToDictionary(g => g.Key, g => g.First());
        var after = to.GroupBy(K).ToDictionary(g => g.Key, g => g.First());
        var changes = new List<SchemaChange>();
        foreach (var (k, a) in after)
        {
            if (!before.TryGetValue(k, out var b))
                changes.Add(new SchemaChange(SchemaChangeKind.Added, a.Database, a.Schema, a.Name, a.Type, null, a.Hash));
            else if (b.Hash != a.Hash)
                changes.Add(new SchemaChange(SchemaChangeKind.Changed, a.Database, a.Schema, a.Name, a.Type, b.Hash, a.Hash));
        }
        foreach (var (k, b) in before)
            if (!after.ContainsKey(k))
                changes.Add(new SchemaChange(SchemaChangeKind.Removed, b.Database, b.Schema, b.Name, b.Type, b.Hash, null));
        return changes.OrderBy(c => c.Kind).ThenBy(c => c.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Every object of the snapshot with the text that identifies its shape: the definition of views and routines, and
    /// for tables a stable listing of columns, primary key, indexes and foreign keys (order-independent where the
    /// engine's order carries no meaning).
    /// </summary>
    public static IReadOnlyList<(SchemaEntry Entry, string Text)> Canonicalize(MetadataSnapshot snapshot)
    {
        var modules = snapshot.Modules
            .GroupBy(m => (m.Database.ToUpperInvariant(), m.Schema.ToUpperInvariant(), m.Name.ToUpperInvariant(), m.Type))
            .ToDictionary(g => g.Key, g => string.Join("\n\n", g.Select(m => Normalize(m.Definition ?? "")).Order(StringComparer.Ordinal)));

        var result = new List<(SchemaEntry, string)>();
        foreach (var o in snapshot.Objects.DistinctBy(o => (o.Database.ToUpperInvariant(), o.Schema.ToUpperInvariant(), o.Name.ToUpperInvariant(), o.Type)))
        {
            string text;
            if (modules.TryGetValue((o.Database.ToUpperInvariant(), o.Schema.ToUpperInvariant(), o.Name.ToUpperInvariant(), o.Type), out var definition))
                text = definition;
            else if (o.IsTableLike)
                text = DescribeTable(snapshot, o);
            else
                text = $"{o.Type} {o.Schema}.{o.Name}";
            result.Add((new SchemaEntry(o.Database, o.Schema, o.Name, o.Type, Hash(text)), text));
        }
        return result;
    }

    public static string DescribeTable(MetadataSnapshot snapshot, DbObject table)
    {
        var sb = new StringBuilder();
        sb.Append(table.Type.ToString().ToUpperInvariant()).Append(' ').Append(table.Schema).Append('.').Append(table.Name).Append('\n');
        foreach (var c in snapshot.ColumnsOf(table.Database, table.Schema, table.Name).OrderBy(c => c.Ordinal))
        {
            sb.Append("  ").Append(c.Name).Append(' ').Append(c.DataType).Append(c.IsNullable ? " NULL" : " NOT NULL");
            if (c.IsPrimaryKey) sb.Append(" PRIMARY KEY");
            if (c.IsIdentity) sb.Append(" IDENTITY");
            if (c.IsComputed) sb.Append(" COMPUTED");
            sb.Append('\n');
        }
        foreach (var i in snapshot.IndexesOf(table.Database, table.Schema, table.Name).OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(i.IsPrimaryKey ? "PRIMARY KEY INDEX " : i.IsUnique ? "UNIQUE INDEX " : "INDEX ").Append(i.Name)
              .Append(' ').Append(i.Type).Append(" (").Append(i.Columns).Append(')');
            if (!string.IsNullOrEmpty(i.IncludedColumns)) sb.Append(" INCLUDE (").Append(i.IncludedColumns).Append(')');
            if (!string.IsNullOrEmpty(i.Filter)) sb.Append(" WHERE ").Append(i.Filter);
            if (i.IsDisabled) sb.Append(" DISABLED");
            sb.Append('\n');
        }
        foreach (var fk in snapshot.ForeignKeysOf(table.Database, table.Schema, table.Name).Where(f => !f.IsVirtual)
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            sb.Append("  FOREIGN KEY ").Append(fk.Name).Append(" (").Append(fk.Columns).Append(") REFERENCES ")
              .Append(fk.ReferencedSchema).Append('.').Append(fk.ReferencedTable).Append(" (").Append(fk.ReferencedColumns).Append(')')
              .Append(fk.IsDisabled ? " DISABLED" : "").Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Line endings and trailing spaces differ between servers and drivers without the code changing.</summary>
    private static string Normalize(string definition) =>
        string.Join("\n", definition.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd())).Trim();

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    private SchemaVersion? Record(string key, DateTimeOffset takenAt, IReadOnlyList<(SchemaEntry Entry, string Text)> entries)
    {
        using var cn = Open(key);
        long? latest;
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT MAX(id) FROM versions";
            latest = cmd.ExecuteScalar() is long id ? id : null;
        }

        if (latest is long last)
        {
            var previous = ReadEntries(cn, last);
            if (Diff(previous, entries.Select(e => e.Entry).ToList()).Count == 0) return null;
        }

        using var tx = cn.BeginTransaction();
        long versionId;
        using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO versions (taken_at, object_count) VALUES ($t, $c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", takenAt.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$c", entries.Count);
            versionId = (long)cmd.ExecuteScalar()!;
        }

        using (var blob = cn.CreateCommand())
        using (var entry = cn.CreateCommand())
        {
            blob.Transaction = tx;
            blob.CommandText = "INSERT OR IGNORE INTO blobs (hash, text) VALUES ($h, $x)";
            var h = blob.Parameters.Add("$h", SqliteType.Text);
            var x = blob.Parameters.Add("$x", SqliteType.Text);

            entry.Transaction = tx;
            entry.CommandText = "INSERT INTO entries (version_id, database_name, schema_name, name, type, hash) VALUES ($v, $d, $s, $n, $y, $h)";
            entry.Parameters.AddWithValue("$v", versionId);
            var d = entry.Parameters.Add("$d", SqliteType.Text);
            var s = entry.Parameters.Add("$s", SqliteType.Text);
            var n = entry.Parameters.Add("$n", SqliteType.Text);
            var y = entry.Parameters.Add("$y", SqliteType.Text);
            var eh = entry.Parameters.Add("$h", SqliteType.Text);

            foreach (var (e, text) in entries)
            {
                h.Value = e.Hash;
                x.Value = text;
                blob.ExecuteNonQuery();
                d.Value = e.Database;
                s.Value = e.Schema;
                n.Value = e.Name;
                y.Value = e.Type.ToString();
                eh.Value = e.Hash;
                entry.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return new SchemaVersion(versionId, takenAt, entries.Count);
    }

    private static IReadOnlyList<SchemaEntry> ReadEntries(SqliteConnection cn, long versionId)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT database_name, schema_name, name, type, hash FROM entries WHERE version_id = $v";
        cmd.Parameters.AddWithValue("$v", versionId);
        using var r = cmd.ExecuteReader();
        var list = new List<SchemaEntry>();
        while (r.Read())
            list.Add(new SchemaEntry(r.GetString(0), r.GetString(1), r.GetString(2),
                Enum.TryParse<DbObjectType>(r.GetString(3), out var t) ? t : DbObjectType.Other, r.GetString(4)));
        return list;
    }

    private SqliteConnection Open(string key)
    {
        var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath(key),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS versions (id INTEGER PRIMARY KEY AUTOINCREMENT, taken_at TEXT NOT NULL, object_count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS blobs (hash TEXT PRIMARY KEY, text TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS entries (version_id INTEGER NOT NULL, database_name TEXT NOT NULL, schema_name TEXT NOT NULL,
                                                name TEXT NOT NULL, type TEXT NOT NULL, hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS entries_version ON entries (version_id);
            CREATE INDEX IF NOT EXISTS entries_name ON entries (name COLLATE NOCASE);
            """;
        cmd.ExecuteNonQuery();
        return cn;
    }
}
