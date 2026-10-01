using System.Collections;
using System.Globalization;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.Lab;

public enum RowChangeKind
{
    Inserted,
    Updated,
    Deleted,

    /// <summary>Inserted or updated after recording started (found through xmin / rowversion, old values unknown).</summary>
    Written
}

/// <summary>One column of a changed row; <see cref="Before"/> is null-and-unknown for inserts and written rows.</summary>
public sealed record ColumnChange(string Column, object? Before, object? After, bool Changed);

public sealed record RowChange(string Schema, string Table, RowChangeKind Kind, string Key, IReadOnlyList<ColumnChange> Columns)
{
    public string ChangedColumns => string.Join(", ", Columns.Where(c => c.Changed).Select(c => c.Column));
}

/// <summary>How many rows of one table an action inserted, updated and deleted (counter difference).</summary>
public sealed record TableActivity(string Database, string Schema, string Table, long Inserts, long Updates, long Deletes)
{
    public string FullName => $"{Schema}.{Table}";
    public long Total => Inserts + Updates + Deletes;

    /// <summary>How the rows of the table were found: "before/after diff", "rows written since start", "counts only".</summary>
    public string Detail { get; init; } = "";
}

/// <summary>The rows of one table read at the start, to diff against the rows at the end.</summary>
public sealed record TableRows(DbObject Table, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Complete);

public sealed record RecorderOptions
{
    /// <summary>Before/after snapshots of tables with at most this many rows (catalog estimate); 0 = no snapshots.</summary>
    public int SnapshotMaxRows { get; init; }

    public int MaxSnapshotTables { get; init; } = 300;

    /// <summary>Rows listed per changed table.</summary>
    public int MaxRowsPerTable { get; init; } = 200;

    public int MaxTables { get; init; } = 50;

    public DataSearchOptions Query { get; init; } = new(1000, 30, 2000);
}

public sealed record RecordingStart(
    string? Database,
    DateTimeOffset StartedAt,
    IReadOnlyList<TableChangeCounter> Counters,
    string? Marker,
    IReadOnlyDictionary<string, TableRows> Snapshots);

public sealed record RecordingResult(
    IReadOnlyList<TableActivity> Tables,
    IReadOnlyList<RowChange> Rows,
    IReadOnlyList<string> Notes,
    TimeSpan Duration);

/// <summary>
/// "What did my app just write?": reads the engine's per-table modification counters (and a change marker) at Start,
/// again at Stop, and reports the tables whose counters moved. For each of them it lists the rows: an exact
/// before/after diff when the table was snapshotted at Start, otherwise the rows written since Start (PostgreSQL xmin,
/// SQL Server rowversion), otherwise only the counts. Everything is read with the non-blocking data-search settings.
/// </summary>
public sealed class ChangeRecorder
{
    public async Task<RecordingStart> StartAsync(
        DatabaseSession session, string? database, RecorderOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Reading change counters…");
        var countersTask = session.Provider.GetTableChangeCountersAsync(database, ct);
        var markerTask = SafeMarkerAsync(session, database, ct);
        await Task.WhenAll(countersTask, markerTask);

        var snapshots = new Dictionary<string, TableRows>(StringComparer.OrdinalIgnoreCase);
        if (options.SnapshotMaxRows > 0)
        {
            var tables = SnapshotCandidates(session, database, options).ToList();
            var done = 0;
            await Parallel.ForEachAsync(tables, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (table, token) =>
            {
                try
                {
                    var rows = await ReadRowsAsync(session, table, null, options.SnapshotMaxRows, options.Query, token);
                    lock (snapshots) snapshots[Key(table.Schema, table.Name)] = rows;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Locked or unreadable table: it falls back to the rows-written / counts-only report.
                }
                progress?.Report($"Snapshotting small tables… {Interlocked.Increment(ref done)}/{tables.Count}");
            });
        }

        return new RecordingStart(database, DateTimeOffset.Now, countersTask.Result, markerTask.Result, snapshots);
    }

    public async Task<RecordingResult> StopAsync(
        DatabaseSession session, RecordingStart start, RecorderOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Reading change counters…");
        var after = await session.Provider.GetTableChangeCountersAsync(start.Database, ct);
        var activity = Diff(start.Counters, after);
        var notes = new List<string>();
        if (activity.Count > options.MaxTables)
            notes.Add($"{activity.Count} tables changed; rows are listed for the {options.MaxTables} busiest.");

        var tables = new List<TableActivity>();
        var rows = new List<RowChange>();
        var index = 0;
        foreach (var table in activity)
        {
            ct.ThrowIfCancellationRequested();
            if (index++ >= options.MaxTables) { tables.Add(table with { Detail = "counts only (table limit)" }); continue; }
            progress?.Report($"Reading changed rows… {index}/{Math.Min(activity.Count, options.MaxTables)}");

            var obj = FindTable(session, start.Database, table.Schema, table.Table);
            if (obj is null) { tables.Add(table with { Detail = "counts only (not in the cached catalog)" }); continue; }

            try
            {
                if (start.Snapshots.TryGetValue(Key(table.Schema, table.Table), out var before) && before.Complete)
                {
                    var now = await ReadRowsAsync(session, obj, null, options.SnapshotMaxRows, options.Query, ct);
                    if (now.Complete)
                    {
                        var keys = KeyColumns(session, obj);
                        var changes = DiffRows(obj, before, now, keys);
                        rows.AddRange(changes.Take(options.MaxRowsPerTable));
                        tables.Add(table with { Detail = $"before/after diff · {changes.Count:N0} row(s)" });
                        continue;
                    }
                }

                var columns = session.Snapshot.ColumnsOf(obj.Database, obj.Schema, obj.Name).OrderBy(c => c.Ordinal).ToList();
                var predicate = start.Marker is null ? null : session.Provider.ChangedSincePredicate(columns, start.Marker);
                if (predicate is null)
                {
                    tables.Add(table with
                    {
                        Detail = session.Provider.ProviderKey == SqlDialect.SqlServerKey
                            ? "counts only (no rowversion column; snapshot small tables to see rows)"
                            : "counts only"
                    });
                    continue;
                }

                var written = await ReadRowsAsync(session, obj, predicate, options.MaxRowsPerTable, options.Query, ct);
                var keyColumns = KeyColumns(session, obj);
                rows.AddRange(written.Rows.Select(r => new RowChange(obj.Schema, obj.Name, RowChangeKind.Written,
                    RowKey(written.Columns, r, keyColumns),
                    written.Columns.Select((c, i) => new ColumnChange(c, null, r[i], Changed: false)).ToList())));
                tables.Add(table with
                {
                    Detail = $"rows written since start · {written.Rows.Count:N0}{(written.Complete ? "" : "+")}" +
                             (table.Deletes > 0 ? " (deleted rows are gone; snapshot small tables to see them)" : "")
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tables.Add(table with { Detail = "counts only (" + ex.Message + ")" });
            }
        }

        if (activity.Count == 0)
            notes.Add(session.Provider.ProviderKey == SqlDialect.PostgresKey
                ? "No table changed. PostgreSQL publishes other sessions' counts up to ~10 s after their transaction ends: wait a moment and press Check again."
                : "No table changed (counters cover the heap / clustered index of user tables).");
        if (start.Marker is null && start.Snapshots.Count == 0)
            notes.Add("The server gave no change marker, so only counts are shown.");

        return new RecordingResult(tables, rows, notes, DateTimeOffset.Now - start.StartedAt);
    }

    /// <summary>Tables whose counters moved between the two readings, busiest first. A counter that went down was reset
    /// (statistics reset, metadata evicted): its new value is taken as the change.</summary>
    public static IReadOnlyList<TableActivity> Diff(IReadOnlyList<TableChangeCounter> before, IReadOnlyList<TableChangeCounter> after)
    {
        var start = new Dictionary<string, TableChangeCounter>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in before) start[Key(c.Schema, c.Table)] = c;

        static long Delta(long now, long then) => now >= then ? now - then : now;

        return after
            .Select(a =>
            {
                start.TryGetValue(Key(a.Schema, a.Table), out var b);
                return new TableActivity(a.Database, a.Schema, a.Table,
                    Delta(a.Inserts, b?.Inserts ?? 0), Delta(a.Updates, b?.Updates ?? 0), Delta(a.Deletes, b?.Deletes ?? 0));
            })
            .Where(t => t.Total > 0)
            .OrderByDescending(t => t.Total).ThenBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Inserted, deleted and updated rows between two reads of a table, matched by <paramref name="keyColumns"/>
    /// (every column when the table has no key, so an update then shows as a delete plus an insert).</summary>
    public static IReadOnlyList<RowChange> DiffRows(DbObject table, TableRows before, TableRows after, IReadOnlyList<string> keyColumns)
    {
        var keys = keyColumns.Count > 0 ? keyColumns : before.Columns;
        var beforeByKey = new Dictionary<string, IReadOnlyList<object?>>();
        foreach (var row in before.Rows) beforeByKey[RowKey(before.Columns, row, keys)] = row;

        var afterIndex = after.Columns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i, StringComparer.OrdinalIgnoreCase);
        var changes = new List<RowChange>();
        var seen = new HashSet<string>();
        foreach (var row in after.Rows)
        {
            var key = RowKey(after.Columns, row, keys);
            seen.Add(key);
            if (!beforeByKey.TryGetValue(key, out var old))
            {
                changes.Add(new RowChange(table.Schema, table.Name, RowChangeKind.Inserted, key,
                    after.Columns.Select((c, i) => new ColumnChange(c, null, row[i], Changed: true)).ToList()));
                continue;
            }

            var columns = before.Columns.Select((c, i) =>
            {
                var now = afterIndex.TryGetValue(c, out var j) ? row[j] : null;
                return new ColumnChange(c, old[i], now, !SameValue(old[i], now));
            }).ToList();
            if (columns.Any(c => c.Changed))
                changes.Add(new RowChange(table.Schema, table.Name, RowChangeKind.Updated, key, columns));
        }

        foreach (var (key, old) in beforeByKey)
        {
            if (seen.Contains(key)) continue;
            changes.Add(new RowChange(table.Schema, table.Name, RowChangeKind.Deleted, key,
                before.Columns.Select((c, i) => new ColumnChange(c, old[i], null, Changed: true)).ToList()));
        }
        return changes;
    }

    public static bool SameValue(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is byte[] x && b is byte[] y) return x.AsSpan().SequenceEqual(y);
        if (a is IEnumerable ea && b is IEnumerable eb && a is not string && b is not string)
            return ea.Cast<object?>().SequenceEqual(eb.Cast<object?>());
        return a.Equals(b);
    }

    public static string RowKey(IReadOnlyList<string> columns, IReadOnlyList<object?> row, IReadOnlyList<string> keyColumns)
    {
        var parts = new List<string>();
        foreach (var key in keyColumns)
        {
            var i = IndexOf(columns, key);
            if (i < 0) continue;
            parts.Add($"{key}={Format(row[i])}");
        }
        return string.Join(", ", parts);
    }

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        byte[] b => "0x" + Convert.ToHexString(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static IReadOnlyList<string> KeyColumns(DatabaseSession session, DbObject table) =>
        session.Snapshot.ColumnsOf(table.Database, table.Schema, table.Name)
            .Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).Select(c => c.Name).ToList();

    private static IEnumerable<DbObject> SnapshotCandidates(DatabaseSession session, string? database, RecorderOptions options) =>
        session.Snapshot.Objects
            .Where(o => o.Type == DbObjectType.Table && InDatabase(session, o, database))
            .Where(o => o.RowCount is long n && n >= 0 && n <= options.SnapshotMaxRows) // -1: never analyzed, size unknown
            .Where(o => session.Snapshot.ColumnsOf(o.Database, o.Schema, o.Name).Any(c => c.IsPrimaryKey))
            .OrderBy(o => o.RowCount)
            .Take(options.MaxSnapshotTables);

    private static bool InDatabase(DatabaseSession session, DbObject o, string? database)
    {
        var target = string.IsNullOrEmpty(database) ? session.Profile.Database : database;
        return string.IsNullOrEmpty(o.Database) || string.IsNullOrEmpty(target) ||
               string.Equals(o.Database, target, StringComparison.OrdinalIgnoreCase);
    }

    private static DbObject? FindTable(DatabaseSession session, string? database, string schema, string name) =>
        session.Snapshot.Objects.FirstOrDefault(o =>
            o.Type is DbObjectType.Table && InDatabase(session, o, database) &&
            string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Up to <paramref name="maxRows"/> rows of the table (Complete when there were no more), in key order.</summary>
    private static async Task<TableRows> ReadRowsAsync(
        DatabaseSession session, DbObject table, string? where, int maxRows, DataSearchOptions options, CancellationToken ct)
    {
        var dialect = SqlDialect.For(session.Provider.ProviderKey);
        var columns = session.Snapshot.ColumnsOf(table.Database, table.Schema, table.Name).OrderBy(c => c.Ordinal).ToList();
        var select = columns.Count == 0 ? "*" : string.Join(", ", columns.Select(dialect.SelectExpression));
        var keys = columns.Where(c => c.IsPrimaryKey).Select(c => dialect.Quote(c.Name)).ToList();
        var sql = dialect.SelectTop(select, dialect.Table(table.Schema, table.Name), where,
            keys.Count > 0 ? string.Join(", ", keys) : null, maxRows + 1);
        var result = await session.Provider.QueryReadOnlyAsync(sql, NullIfEmpty(table.Database), options, maxRows, ct);
        return new TableRows(table, result.Columns, result.Rows, Complete: !result.IsTruncated);
    }

    private static async Task<string?> SafeMarkerAsync(DatabaseSession session, string? database, CancellationToken ct)
    {
        try
        {
            return await session.Provider.GetChangeMarkerAsync(database, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string Key(string schema, string table) => schema + "\u0001" + table;
}
