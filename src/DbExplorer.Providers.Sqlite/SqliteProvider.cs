using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dapper;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Providers.Sqlite;

/// <summary>
/// One SQLite file. The file is the app's only database, named "main" like SQLite names it, and "main" is its schema
/// too (like MySQL, where the schema is the database). There is no server: no sessions, locks between users, statement
/// statistics or routines, so those members explain that instead of reading anything.
/// <para>Microsoft.Data.Sqlite runs every call synchronously, so each one is moved to the thread pool here: a long query
/// must never hold the UI thread. Cancelling and timeouts interrupt the statement (sqlite3_interrupt).</para>
/// </summary>
public sealed class SqliteProvider(ConnectionProfile profile) : IDatabaseProvider
{
    private const int MaxValueLength = 400;
    private readonly string _connectionString = SqliteSql.BuildConnectionString(profile);
    private readonly string _readOnlyConnectionString = SqliteSql.BuildConnectionString(profile, readOnly: true);

    public string ProviderKey => SqliteProviderFactory.ProviderKey;

    public string QuoteIdentifier(string identifier) => SqliteSql.Quote(identifier);

    public Task<string> GetServerVersionAsync(CancellationToken ct = default) => Run(ct, () =>
    {
        using var cn = Open(readOnly: true);
        var version = cn.ExecuteScalar<string>("SELECT sqlite_version();");
        return $"SQLite {version} · {Path.GetFileName(cn.DataSource)}";
    });

    // ---- Catalog --------------------------------------------------------------------------------------------------

    /// <summary>Tables, views and triggers (an index is listed with its table); SQLite's own sqlite_ tables left out.</summary>
    internal const string ObjectsSql =
        "SELECT type, name, tbl_name AS tableName, sql FROM main.sqlite_master " +
        "WHERE type IN ('table', 'view', 'trigger', 'index') AND name NOT LIKE 'sqlite!_%' ESCAPE '!' ORDER BY type, name;";

    private sealed record MasterRow(string Type, string Name, string TableName, string? Sql);

    private static List<MasterRow> Master(SqliteConnection cn) =>
        cn.Query<(string type, string name, string tableName, string? sql)>(ObjectsSql)
            .Select(r => new MasterRow(r.type, r.name, r.tableName, r.sql)).ToList();

    public Task<IReadOnlyList<DbObject>> GetObjectsAsync(CancellationToken ct = default) => Run<IReadOnlyList<DbObject>>(ct, () =>
    {
        using var cn = Open(readOnly: true);
        var rows = RowCounts(cn);
        return Master(cn)
            .Where(m => m.Type != "index")
            .Select(m => new DbObject
            {
                Database = SqliteSql.Main,
                Schema = SqliteSql.Main,
                Name = m.Name,
                Type = m.Type switch { "view" => DbObjectType.View, "trigger" => DbObjectType.Trigger, _ => DbObjectType.Table },
                RowCount = m.Type == "table" && rows.TryGetValue(m.Name, out var n) ? n : null
            })
            .ToList();
    });

    /// <summary>Row counts ANALYZE left in sqlite_stat1 (the first number of an entry), when the file has them. Never COUNT(*).</summary>
    private static Dictionary<string, long> RowCounts(SqliteConnection cn)
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (cn.ExecuteScalar<long>("SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1';") == 0)
            return counts;
        foreach (var (table, stat) in cn.Query<(string, string?)>("SELECT tbl, stat FROM main.sqlite_stat1;"))
        {
            var first = stat?.Split(' ', 2)[0];
            if (long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                counts[table] = Math.Max(n, counts.GetValueOrDefault(table));
        }
        return counts;
    }

    public Task<IReadOnlyList<DbColumn>> GetColumnsAsync(CancellationToken ct = default) => Run<IReadOnlyList<DbColumn>>(ct, () =>
    {
        using var cn = Open(readOnly: true);
        var columns = new List<DbColumn>();
        foreach (var m in Master(cn).Where(m => m.Type is "table" or "view"))
        {
            ct.ThrowIfCancellationRequested();
            columns.AddRange(TableColumns(cn, m.Name));
        }
        return columns;
    });

    private sealed record XInfo(long Cid, string Name, string? Type, long NotNull, string? Default, long Pk, long Hidden);

    /// <summary>PRAGMA table_xinfo of one table or view; empty when SQLite can't read it (a view over a dropped table,
    /// a virtual table whose module isn't loaded), so one broken object never hides the rest of the catalog.</summary>
    private static List<XInfo> TableInfo(SqliteConnection cn, string table)
    {
        try
        {
            return cn.Query<(long cid, string name, string? type, long notnull, string? dflt, long pk, long hidden)>(
                    $"SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo({SqliteSql.Literal(table)});")
                .Select(r => new XInfo(r.cid, r.name, r.type, r.notnull, r.dflt, r.pk, r.hidden)).ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private static IEnumerable<DbColumn> TableColumns(SqliteConnection cn, string table)
    {
        var info = TableInfo(cn, table).Where(c => c.Hidden != 1).ToList();
        // A lone INTEGER PRIMARY KEY is the rowid itself: SQLite numbers it, like an identity column elsewhere.
        var keys = info.Count(c => c.Pk > 0);
        return info.Select((c, i) => new DbColumn
        {
            Database = SqliteSql.Main,
            Schema = SqliteSql.Main,
            Table = table,
            Name = c.Name,
            DataType = string.IsNullOrWhiteSpace(c.Type) ? "" : c.Type.Trim(),
            BaseType = SqliteSql.BaseType(c.Type),
            IsNullable = c.NotNull == 0 && c.Pk == 0,
            Ordinal = i + 1,
            IsComputed = c.Hidden is 2 or 3,
            IsPrimaryKey = c.Pk > 0,
            IsIdentity = keys == 1 && c.Pk > 0 && string.Equals(c.Type?.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase)
        });
    }

    public Task<IReadOnlyList<DbModule>> GetModulesAsync(CancellationToken ct = default) => Run<IReadOnlyList<DbModule>>(ct, () =>
    {
        using var cn = Open(readOnly: true);
        return Master(cn)
            .Where(m => m.Type is "view" or "trigger")
            .Select(m => new DbModule
            {
                Database = SqliteSql.Main,
                Schema = SqliteSql.Main,
                Name = m.Name,
                Type = m.Type == "view" ? DbObjectType.View : DbObjectType.Trigger,
                Definition = Statement(m.Sql)
            })
            .ToList();
    });

    /// <summary>The CREATE statement SQLite keeps for the object, which is exactly what created it.</summary>
    public Task<string?> GetDefinitionAsync(DbObject obj, CancellationToken ct = default) => Run(ct, () =>
    {
        using var cn = Open(readOnly: true);
        var schema = SqliteSql.SchemaOf(obj.Schema, obj.Database);
        var sql = cn.ExecuteScalar<string?>(
            $"SELECT sql FROM {SqliteSql.Quote(schema)}.sqlite_master WHERE name = @name AND type <> 'index';", new { name = obj.Name });
        if (sql is null || obj.Type != DbObjectType.Table) return Statement(sql);

        // A table's indexes and triggers belong to its script, as SHOW CREATE / generated scripts give them elsewhere.
        var extras = cn.Query<string>(
            $"SELECT sql FROM {SqliteSql.Quote(schema)}.sqlite_master WHERE tbl_name = @name AND type IN ('index', 'trigger') " +
            "AND sql IS NOT NULL ORDER BY type, name;", new { name = obj.Name });
        return string.Join("\n\n", new[] { sql }.Concat(extras).Select(s => Statement(s)));
    });

    private static string? Statement(string? sql) => sql is null ? null : sql.TrimEnd().TrimEnd(';') + ";";

    public Task<IReadOnlyList<DbIndex>> GetIndexesAsync(bool includePhysicalStats, CancellationToken ct = default, bool includeUsageStats = true) =>
        Run<IReadOnlyList<DbIndex>>(ct, () =>
        {
            using var cn = Open(readOnly: true);
            var master = Master(cn);
            var sqlOf = master.Where(m => m.Type == "index").ToDictionary(m => m.Name, m => m.Sql, StringComparer.OrdinalIgnoreCase);
            var sizes = includePhysicalStats ? IndexSizes(cn) : [];
            var indexes = new List<DbIndex>();
            foreach (var table in master.Where(m => m.Type == "table").Select(m => m.Name))
            {
                ct.ThrowIfCancellationRequested();
                List<(long seq, string name, long unique, string origin, long partial)> list;
                try
                {
                    list = cn.Query<(long, string, long, string, long)>(
                        $"SELECT seq, name, \"unique\", origin, partial FROM pragma_index_list({SqliteSql.Literal(table)});").AsList();
                }
                catch (SqliteException) { continue; }

                foreach (var (_, name, unique, origin, partial) in list)
                {
                    var columns = cn.Query<(long cid, string? name, long desc)>(
                            $"SELECT cid, name, \"desc\" FROM pragma_index_xinfo({SqliteSql.Literal(name)}) WHERE key = 1 ORDER BY seqno;")
                        .Select(c => (c.cid == -2 ? "(expression)" : c.name ?? "rowid") + (c.desc == 1 ? " DESC" : ""));
                    indexes.Add(new DbIndex
                    {
                        Database = SqliteSql.Main,
                        Schema = SqliteSql.Main,
                        Table = table,
                        Name = name,
                        Type = "BTREE",
                        IsUnique = unique == 1,
                        IsPrimaryKey = origin == "pk",
                        IsConstraint = origin is "pk" or "u",
                        Columns = string.Join(", ", columns),
                        Filter = partial == 1 ? PartialFilter(sqlOf.GetValueOrDefault(name)) : null,
                        SizeBytes = sizes.TryGetValue(name, out var size) ? size : null
                    });
                }
            }
            return indexes;
        });

    /// <summary>The WHERE of a partial index's CREATE INDEX.</summary>
    internal static string? PartialFilter(string? sql)
    {
        if (sql is null) return null;
        var at = SqliteScript.FindKeyword(sql, "WHERE");
        return at < 0 ? null : sql[(at + 5)..].Trim().TrimEnd(';').Trim();
    }

    /// <summary>Bytes per index from the dbstat virtual table, when the bundled SQLite has it.</summary>
    private static Dictionary<string, long> IndexSizes(SqliteConnection cn)
    {
        try
        {
            return cn.Query<(string name, long size)>("SELECT name, SUM(pgsize) FROM dbstat GROUP BY name;")
                .ToDictionary(r => r.name, r => r.size, StringComparer.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    public Task<IReadOnlyList<DbForeignKey>> GetForeignKeysAsync(CancellationToken ct = default) => Run<IReadOnlyList<DbForeignKey>>(ct, () =>
    {
        using var cn = Open(readOnly: true);
        var result = new List<DbForeignKey>();
        var tables = Master(cn).Where(m => m.Type == "table").Select(m => m.Name).ToList();
        foreach (var table in tables)
            result.AddRange(ForeignKeys(cn, table));
        return result;
    });

    private static IEnumerable<DbForeignKey> ForeignKeys(SqliteConnection cn, string table)
    {
        List<(long id, long seq, string parent, string from, string? to)> rows;
        try
        {
            rows = cn.Query<(long, long, string, string, string?)>(
                $"SELECT id, seq, \"table\", \"from\", \"to\" FROM pragma_foreign_key_list({SqliteSql.Literal(table)}) ORDER BY id, seq;").AsList();
        }
        catch (SqliteException) { yield break; }

        foreach (var key in rows.GroupBy(r => r.id))
        {
            var parts = key.OrderBy(r => r.seq).ToList();
            var parent = parts[0].parent;
            // REFERENCES parent with no column list points at the parent's primary key.
            var to = parts.Any(p => p.to is null)
                ? TableInfo(cn, parent).Where(c => c.Pk > 0).OrderBy(c => c.Pk).Select(c => c.Name).ToList()
                : parts.Select(p => p.to!).ToList();
            yield return new DbForeignKey
            {
                Database = SqliteSql.Main,
                // SQLite keys are usually unnamed; this name is stable for the same table and key order.
                Name = $"fk_{table}_{parent}_{key.Key}",
                Schema = SqliteSql.Main,
                Table = table,
                Columns = string.Join(", ", parts.Select(p => p.from)),
                ReferencedSchema = SqliteSql.Main,
                ReferencedTable = parent,
                ReferencedColumns = string.Join(", ", to)
            };
        }
    }

    public Task<DbTableConstraints> GetTableConstraintsAsync(DbObject table, CancellationToken ct = default) => Run(ct, () =>
    {
        using var cn = Open(readOnly: true);
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in TableInfo(cn, table.Name).Where(c => c.Default is not null && !string.Equals(c.Default, "NULL", StringComparison.OrdinalIgnoreCase)))
            defaults[c.Name] = c.Default!;
        var sql = cn.ExecuteScalar<string?>("SELECT sql FROM main.sqlite_master WHERE name = @name AND type = 'table';", new { name = table.Name });
        return new DbTableConstraints
        {
            Defaults = defaults,
            Checks = SqliteScript.CheckConstraints(sql).Select(c => new DbCheckConstraint(c.Name, c.Expression)).ToList()
        };
    });

    // ---- Server diagnostics: none on a file -----------------------------------------------------------------------

    public const string NoServer = "SQLite is a file, not a server: there are no other sessions";

    public Task<IReadOnlyList<DbLock>> GetLocksAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException(NoServer + " or locks to list. A writer locks the whole file while it commits.");

    public Task<IReadOnlyList<DbActiveRequest>> GetActiveRequestsAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException(NoServer + " to list.");

    public Task<IReadOnlyList<DbQueryStat>> GetTopQueriesAsync(QueryStatOrder order, int top, CancellationToken ct = default) =>
        throw new InvalidOperationException("SQLite keeps no statement statistics. Use Explain in a Query tab to see how one query runs.");

    public Task<IReadOnlyList<TableChangeCounter>> GetTableChangeCountersAsync(string? database, CancellationToken ct = default) =>
        throw new InvalidOperationException("SQLite keeps no row change counters.");

    public Task<string?> GetChangeMarkerAsync(string? database, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public string? ChangedSincePredicate(IReadOnlyList<DbColumn> columns, string marker) => null;

    public Task<IReadOnlyList<DbRoutineParameter>> GetRoutineParametersAsync(DbObject routine, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DbRoutineParameter>>([]);

    public Task<QueryExecutionResult> ExecuteRoutineAsync(
        DbObject routine, IReadOnlyList<DbRoutineParameter> parameters, IReadOnlyDictionary<string, object?> arguments,
        int timeoutSeconds, CancellationToken ct = default) =>
        throw new NotSupportedException("SQLite has no stored procedures or functions.");

    // ---- Profiling and data search --------------------------------------------------------------------------------

    public ColumnProfileLevel GetProfileLevel(DbColumn column) =>
        column.BaseType.Contains("blob", StringComparison.Ordinal) ? ColumnProfileLevel.NullsOnly : ColumnProfileLevel.Full;

    public Task<TableProfile> ProfileTableAsync(
        DbObject table, IReadOnlyList<DbColumn> columns, int sampleRows, DataSearchOptions options, CancellationToken ct = default) =>
        Run(ct, () =>
        {
            var sw = Stopwatch.StartNew();
            var parts = new List<string> { "COUNT(*)" };
            foreach (var c in columns)
            {
                var q = SqliteSql.Quote(c.Name);
                var full = GetProfileLevel(c) == ColumnProfileLevel.Full;
                parts.Add($"COUNT({q})");
                parts.Add(full ? $"COUNT(DISTINCT {q})" : "NULL");
                parts.Add(full ? $"MIN({q})" : "NULL");
                parts.Add(full ? $"MAX({q})" : "NULL");
            }
            var from = SqliteSql.QuoteFullName(SqliteSql.SchemaOf(table.Schema, table.Database), table.Name);
            var sql = $"SELECT {string.Join(", ", parts)} FROM (SELECT * FROM {from} LIMIT @n);";

            using var cn = Open(readOnly: true, options.LockTimeoutMs);
            using var cmd = Command(cn, sql);
            cmd.Parameters.AddWithValue("@n", Math.Max(1, sampleRows));
            using var guard = new Guard(cn, options.QueryTimeoutSeconds, ct);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return new TableProfile { SampleLimit = sampleRows };
            var total = reader.GetInt64(0);
            var profiles = columns.Select((c, i) =>
            {
                var o = 1 + i * 4;
                var nonNull = reader.GetInt64(o);
                return new ColumnProfile
                {
                    Column = c.Name,
                    DataType = c.DataType,
                    NonNullCount = nonNull,
                    NullCount = total - nonNull,
                    DistinctCount = reader.IsDBNull(o + 1) ? null : reader.GetInt64(o + 1),
                    MinValue = reader.IsDBNull(o + 2) ? null : Text(reader.GetValue(o + 2)),
                    MaxValue = reader.IsDBNull(o + 3) ? null : Text(reader.GetValue(o + 3))
                };
            }).ToList();
            return new TableProfile { SampledRows = total, SampleLimit = sampleRows, Columns = profiles, Elapsed = sw.Elapsed };
        });

    public Task<IReadOnlyList<ValueFrequency>> GetTopValuesAsync(
        DbObject table, DbColumn column, int sampleRows, int top, DataSearchOptions options, CancellationToken ct = default) =>
        Run<IReadOnlyList<ValueFrequency>>(ct, () =>
        {
            var from = SqliteSql.QuoteFullName(SqliteSql.SchemaOf(table.Schema, table.Database), table.Name);
            var sql = $"SELECT CAST(v AS TEXT) AS Value, COUNT(*) AS Count FROM (SELECT {SqliteSql.Quote(column.Name)} AS v FROM {from} LIMIT @n) " +
                      "GROUP BY v ORDER BY Count DESC, v LIMIT @top;";
            using var cn = Open(readOnly: true, options.LockTimeoutMs);
            using var guard = new Guard(cn, options.QueryTimeoutSeconds, ct);
            return cn.Query<ValueFrequency>(sql, new { n = Math.Max(1, sampleRows), top = Math.Clamp(top, 1, 1000) }).AsList();
        });

    /// <summary>Text columns (and untyped ones, which take any value) match text; numeric ones match numbers.</summary>
    public bool IsSearchable(DbColumn column, SearchTerm term) => SqliteSql.Affinity(column.DataType) switch
    {
        SqliteAffinity.Text => term.HasText,
        SqliteAffinity.Blob => column.DataType.Length == 0 && term.HasText,
        _ => term.Number is not null
    };

    public Task<IReadOnlyList<DataMatch>> SearchTableAsync(
        DbTableTarget table, SearchTerm term, DataSearchOptions options, CancellationToken ct = default) =>
        Run<IReadOnlyList<DataMatch>>(ct, () =>
        {
            var searchable = table.Columns.Where(c => IsSearchable(c, term)).OrderBy(c => c.Ordinal).ToList();
            if (searchable.Count == 0) return [];

            var keys = table.Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).ToList();
            var select = new List<string>();
            var conditions = new List<string>();
            for (var i = 0; i < searchable.Count; i++)
            {
                var q = SqliteSql.Quote(searchable[i].Name);
                // LIKE ignores case for ASCII letters, like the other engines' default collations.
                var condition = SqliteSql.Affinity(searchable[i].DataType) is SqliteAffinity.Text or SqliteAffinity.Blob
                    ? $"{q} LIKE @p ESCAPE '!'"
                    : $"{q} = @n";
                conditions.Add(condition);
                select.Add($"({condition}) AS m{i}");
                select.Add($"substr(CAST({q} AS TEXT), 1, {MaxValueLength}) AS v{i}");
            }
            for (var k = 0; k < keys.Count; k++)
                select.Add($"CAST({SqliteSql.Quote(keys[k].Name)} AS TEXT) AS k{k}");

            var from = SqliteSql.QuoteFullName(SqliteSql.SchemaOf(table.Schema, table.Database), table.Name);
            var sql = $"SELECT {string.Join(", ", select)} FROM {from} WHERE {string.Join(" OR ", conditions)} LIMIT @top;";

            using var cn = Open(readOnly: true, options.LockTimeoutMs);
            using var cmd = Command(cn, sql);
            cmd.Parameters.AddWithValue("@top", options.MaxMatchesPerTable);
            cmd.Parameters.AddWithValue("@p", term.HasText ? SqliteSql.BuildPattern(term.Text, term.Mode) : "");
            cmd.Parameters.AddWithValue("@n", term.Number is decimal d ? (d == decimal.Truncate(d) && d is >= long.MinValue and <= long.MaxValue ? (long)d : (double)d) : DBNull.Value);

            var results = new List<DataMatch>();
            var keyOffset = searchable.Count * 2;
            using var guard = new Guard(cn, options.QueryTimeoutSeconds, ct);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var keyValues = keys.Select((key, k) => new KeyValuePair<string, string?>(
                    key.Name, reader.IsDBNull(keyOffset + k) ? null : reader.GetString(keyOffset + k))).ToList();
                string? rowKey = keys.Count == 0 ? null : string.Join(", ", keyValues.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
                for (var i = 0; i < searchable.Count; i++)
                {
                    if (reader.IsDBNull(i * 2) || reader.GetInt64(i * 2) == 0) continue;
                    results.Add(new DataMatch
                    {
                        Database = table.Database,
                        Schema = table.Schema,
                        Table = table.Name,
                        Column = searchable[i].Name,
                        Value = reader.IsDBNull(i * 2 + 1) ? null : reader.GetString(i * 2 + 1),
                        RowKey = rowKey,
                        KeyValues = keyValues
                    });
                }
            }
            return results;
        });

    private static string? Text(object value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes.Length > 64 ? bytes[..64] : bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    // ---- Scripts --------------------------------------------------------------------------------------------------

    public Task<QueryExecutionResult> ExecuteScriptAsync(
        string sql, string? database, int timeoutSeconds, CancellationToken ct = default, int maxRows = int.MaxValue,
        ReadOnlyScript? readOnly = null) => Run(ct, () =>
    {
        var sw = Stopwatch.StartNew();
        using var cn = Open(readOnly: false);
        return RunScript(cn, null, sql, timeoutSeconds, maxRows, readOnly is not null, ct) with { Elapsed = sw.Elapsed };
    });

    public Task<long> StreamScriptAsync(string sql, string? database, ResultSetStream stream, CancellationToken ct = default) => Run(ct, () =>
    {
        using var cn = Open(readOnly: false);
        RunScript(cn, null, sql, timeoutSeconds: 0, maxRows: 0, stopAtLimit: false, ct, stream);
        return stream.RowCount;
    });

    /// <summary>
    /// Runs a (multi-statement) script, keeping at most <paramref name="maxRows"/> rows per result set. With
    /// <paramref name="stopAtLimit"/> (a script that only reads) a result set stops just past the limit instead of being
    /// read to its end: SQLite computes rows as they are stepped, so the rest is never produced.
    /// </summary>
    private static QueryExecutionResult RunScript(
        SqliteConnection cn, SqliteTransaction? tx, string sql, int timeoutSeconds, int maxRows, bool stopAtLimit, CancellationToken ct,
        ResultSetStream? stream = null)
    {
        var resultSets = new List<QueryResultSet>();
        var limited = stopAtLimit && maxRows is > 0 and < int.MaxValue;
        using var cmd = Command(cn, sql, tx);
        try
        {
            using var guard = new Guard(cn, timeoutSeconds, ct);
            using var reader = cmd.ExecuteReader();
            do
            {
                if (reader.FieldCount <= 0) continue;
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                if (stream?.Takes(resultSets.Count) == true)
                {
                    stream.Begin(columns);
                    while (reader.Read()) stream.Write(Values(reader));
                    resultSets.Add(new QueryResultSet { Columns = columns, TotalRowCount = stream.RowCount });
                    continue;
                }
                var rows = new List<IReadOnlyList<object?>>();
                long total = 0;
                var stopped = false;
                while (reader.Read())
                {
                    if (++total > maxRows)
                    {
                        if (limited) { stopped = true; break; }
                        continue;
                    }
                    rows.Add(Values(reader));
                }
                resultSets.Add(new QueryResultSet
                {
                    Columns = columns, Rows = rows, TotalRowCount = total, IsTruncated = total > maxRows, TotalRowCountIsExact = !stopped
                });
            } while (reader.NextResult());
            return new QueryExecutionResult { ResultSets = resultSets, RowsAffected = Math.Max(0, reader.RecordsAffected) };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode != SQLitePCL.raw.SQLITE_INTERRUPT)
        {
            throw ToExecutionException(ex, sql);
        }
    }

    private static object?[] Values(SqliteDataReader reader)
    {
        var values = new object?[reader.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;
    }

    private static SqlExecutionException ToExecutionException(SqliteException ex, string sql)
    {
        var message = ex.Message;
        if (SqliteScript.ErrorOffset(sql, ex.Message) is not int offset) return new SqlExecutionException(message, null, null, ex);
        var (line, column) = SqlExecutionException.LocationOf(sql, offset);
        return new SqlExecutionException($"Line {line}, column {column}: {message}", line, column, ex);
    }

    public Task<IScriptSession> BeginScriptSessionAsync(string? database, bool transactional, CancellationToken ct = default) =>
        Run<IScriptSession>(ct, () =>
        {
            var cn = Open(readOnly: false);
            try
            {
                return new ScriptSession(cn, transactional ? cn.BeginTransaction() : null);
            }
            catch
            {
                cn.Dispose();
                throw;
            }
        });

    public Task<QueryResultSet> QueryReadOnlyAsync(
        string sql, string? database, DataSearchOptions options, int maxRows = 1000, CancellationToken ct = default) => Run(ct, () =>
    {
        using var cn = Open(readOnly: true, options.LockTimeoutMs);
        using var cmd = Command(cn, sql);
        using var guard = new Guard(cn, options.QueryTimeoutSeconds, ct);
            using var reader = cmd.ExecuteReader();
        while (reader.FieldCount == 0 && reader.NextResult()) { }
        if (reader.FieldCount == 0) return new QueryResultSet();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;
        while (reader.Read())
        {
            if (rows.Count >= maxRows) { truncated = true; break; }
            rows.Add(Values(reader));
        }
        return new QueryResultSet
        {
            Columns = columns, Rows = rows, IsTruncated = truncated,
            TotalRowCount = rows.Count + (truncated ? 1 : 0), TotalRowCountIsExact = !truncated
        };
    });

    private sealed class ScriptSession(SqliteConnection connection, SqliteTransaction? transaction) : IScriptSession
    {
        private bool _finished;

        public Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default) => Run(ct, () =>
        {
            using var cmd = Command(connection, sql, transaction);
            try
            {
                using var guard = new Guard(connection, timeoutSeconds, ct);
                return Math.Max(0, cmd.ExecuteNonQuery());
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode != SQLitePCL.raw.SQLITE_INTERRUPT)
            {
                throw ToExecutionException(ex, sql);
            }
        });

        public Task<QueryExecutionResult> QueryAsync(
            string sql, int timeoutSeconds, int maxRows = int.MaxValue, CancellationToken ct = default, ReadOnlyScript? readOnly = null) =>
            Run(ct, () =>
            {
                var sw = Stopwatch.StartNew();
                return RunScript(connection, transaction, sql, timeoutSeconds, maxRows, readOnly is not null, ct) with { Elapsed = sw.Elapsed };
            });

        public Task<long> StreamAsync(string sql, ResultSetStream stream, CancellationToken ct = default) => Run(ct, () =>
        {
            RunScript(connection, transaction, sql, 0, 0, false, ct, stream);
            return stream.RowCount;
        });

        public Task CommitAsync(CancellationToken ct = default) => Run(ct, () =>
        {
            transaction?.Commit();
            _finished = true;
            return true;
        });

        public Task RollbackAsync(CancellationToken ct = default) => Run(ct, () =>
        {
            transaction?.Rollback();
            _finished = true;
            return true;
        });

        public ValueTask DisposeAsync()
        {
            if (transaction is not null)
            {
                if (!_finished)
                {
                    try { transaction.Rollback(); }
                    catch (Exception) { /* the statement that failed already ended it */ }
                }
                transaction.Dispose();
            }
            connection.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // ---- Connections ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the file. Reads that must not write go through a read-only connection, which also waits at most
    /// <paramref name="busyTimeoutMs"/> for a writer that holds the file.
    /// </summary>
    private SqliteConnection Open(bool readOnly, int? busyTimeoutMs = null)
    {
        var cn = new SqliteConnection(readOnly ? _readOnlyConnectionString : _connectionString);
        try
        {
            cn.Open();
            if (busyTimeoutMs is int ms)
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = $"PRAGMA busy_timeout = {Math.Max(0, ms).ToString(CultureInfo.InvariantCulture)};";
                cmd.ExecuteNonQuery();
            }
            return cn;
        }
        catch
        {
            cn.Dispose();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection cn, string sql, SqliteTransaction? tx = null)
    {
        var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>
    /// Interrupts the connection's running statement when <paramref name="ct"/> is cancelled or
    /// <paramref name="timeoutSeconds"/> pass (0 for no limit), for as long as the guard lives: it must cover reading the
    /// rows too, since SQLite computes them while they are stepped. SQLite's own command timeout only bounds waiting for
    /// a locked file, not how long a statement runs.
    /// </summary>
    private sealed class Guard : IDisposable
    {
        private readonly CancellationTokenSource? _timeout;
        private readonly CancellationTokenRegistration _onCancel;
        private readonly CancellationTokenRegistration _onTimeout;

        public Guard(SqliteConnection cn, int timeoutSeconds, CancellationToken ct)
        {
            var handle = cn.Handle;
            if (ct.CanBeCanceled) _onCancel = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle));
            if (timeoutSeconds > 0)
            {
                _timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                _onTimeout = _timeout.Token.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle));
            }
        }

        public void Dispose()
        {
            _onCancel.Dispose();
            _onTimeout.Dispose();
            _timeout?.Dispose();
        }
    }

    /// <summary>
    /// Moves a synchronous driver call off the caller's thread. A statement a <see cref="Guard"/> interrupted ends as a
    /// cancel when the caller cancelled, otherwise as a timeout.
    /// </summary>
    private static Task<T> Run<T>(CancellationToken ct, Func<T> work) => Task.Run(() =>
    {
        try
        {
            return work();
        }
        catch (Exception ex) when (Interrupted(ex) is { } interrupt)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The statement ran longer than its time limit and was stopped.", interrupt);
        }
    }, ct);

    private static SqliteException? Interrupted(Exception ex) => ex switch
    {
        SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_INTERRUPT } s => s,
        SqlExecutionException { InnerException: SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_INTERRUPT } s } => s,
        _ => null
    };

    /// <summary>
    /// Clears the pooled connections to the file, which would otherwise keep it open (and locked on Windows) after the
    /// session ends.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        using var cn = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(cn);
        using var ro = new SqliteConnection(_readOnlyConnectionString);
        SqliteConnection.ClearPool(ro);
        return ValueTask.CompletedTask;
    }
}
