using System.Data;
using System.Diagnostics;
using System.Text;
using Dapper;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Providers.SqlServer;

public sealed class SqlServerProvider : IDatabaseProvider
{
    private const int MetadataLockTimeoutMs = 5000;
    private const int MetadataCommandTimeoutSeconds = 120;
    private const int MaxValueLength = 400;
    /// <summary>Most catalog connections one provider opens at once, across all the parallel catalog reads
    /// (objects, columns, routines, keys, indexes × databases), so a whole-server load does not swamp the server.</summary>
    private const int MaxParallelDatabases = 8;

    private readonly SemaphoreSlim _catalogGate = new(MaxParallelDatabases, MaxParallelDatabases);
    private readonly object _databasesLock = new();
    private (Task<IReadOnlyList<string>> Task, DateTime At)? _databases;

    private readonly ConnectionProfile _profile;
    private readonly string _metaConnectionString;
    private readonly string _searchConnectionString;

    /// <summary>True when no specific database was picked at connect time: every accessible database is read and merged.</summary>
    private readonly bool _allDatabases;

    public SqlServerProvider(ConnectionProfile profile)
    {
        _profile = profile;
        _allDatabases = string.IsNullOrWhiteSpace(profile.Database);
        _metaConnectionString = SqlServerSql.BuildConnectionString(profile, SqlServerSql.MetaAppName);
        _searchConnectionString = SqlServerSql.BuildConnectionString(profile, SqlServerSql.SearchAppName);
    }

    public string ProviderKey => SqlServerProviderFactory.ProviderKey;

    public string QuoteIdentifier(string identifier) => SqlServerSql.Quote(identifier);

    public async Task<string> GetServerVersionAsync(CancellationToken ct = default)
    {
        var version = await ScalarAsync<string>(SqlServerQueries.ServerVersion, null, ct);
        return (version ?? "").Split('\n')[0].Trim();
    }

    public Task<IReadOnlyList<DbObject>> GetObjectsAsync(CancellationToken ct = default) =>
        QueryAcrossDatabasesAsync<DbObject>(SqlServerQueries.Objects, (o, db) => o with { Database = db }, ct);

    public Task<IReadOnlyList<DbColumn>> GetColumnsAsync(CancellationToken ct = default) =>
        QueryAcrossDatabasesAsync<DbColumn>(SqlServerQueries.Columns, (c, db) => c with { Database = db }, ct);

    public Task<IReadOnlyList<DbModule>> GetModulesAsync(CancellationToken ct = default) =>
        QueryAcrossDatabasesAsync<DbModule>(SqlServerQueries.Modules, (m, db) => m with { Database = db }, ct);

    public Task<string?> GetDefinitionAsync(DbObject obj, CancellationToken ct = default)
    {
        var sql = obj.Type switch
        {
            DbObjectType.Synonym => SqlServerQueries.SynonymDefinition,
            DbObjectType.Sequence => SqlServerQueries.SequenceDefinition,
            _ => SqlServerQueries.ObjectDefinition
        };
        var database = string.IsNullOrEmpty(obj.Database) ? _profile.Database : obj.Database;
        return ScalarInDatabaseAsync<string?>(sql, new { name = SqlServerSql.QuoteFullName(obj.Schema, obj.Name) }, database, ct);
    }

    public async Task<IReadOnlyList<DbIndex>> GetIndexesAsync(bool includePhysicalStats, CancellationToken ct = default, bool includeUsageStats = true)
    {
        async Task<IReadOnlyList<DbIndex>> QueryOneAsync(string? database, CancellationToken token)
        {
            try
            {
                return await QueryInDatabaseAsync<DbIndex>(SqlServerQueries.Indexes(includeUsageStats, includePhysicalStats), null, database, token);
            }
            catch (SqlException ex) when (ex.Number is 297 or 300)
            {
                // No VIEW SERVER STATE / VIEW DATABASE STATE: return the catalog part only.
                return await QueryInDatabaseAsync<DbIndex>(SqlServerQueries.Indexes(false, false), null, database, token);
            }
        }

        if (!_allDatabases)
        {
            var rows = await GatedAsync(() => QueryOneAsync(_profile.Database, ct), ct).ConfigureAwait(false);
            return rows.Select(i => i with { Database = _profile.Database }).ToList();
        }

        var databases = await GetDatabasesForCatalogAsync(ct).ConfigureAwait(false);
        var results = new System.Collections.Concurrent.ConcurrentBag<DbIndex>();

        await Parallel.ForEachAsync(databases, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelDatabases,
            CancellationToken = ct
        }, async (db, token) =>
        {
            try
            {
                var rows = await GatedAsync(() => QueryOneAsync(db, token), token).ConfigureAwait(false);
                foreach (var row in rows) results.Add(row with { Database = db });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Inaccessible database: skip it and keep going.
            }
        }).ConfigureAwait(false);
        return results.ToList();
    }

    public Task<IReadOnlyList<DbLock>> GetLocksAsync(CancellationToken ct = default) =>
        QueryAsync<DbLock>(SqlServerQueries.Locks(_allDatabases), null, ct);

    public Task<IReadOnlyList<DbForeignKey>> GetForeignKeysAsync(CancellationToken ct = default) =>
        QueryAcrossDatabasesAsync<DbForeignKey>(SqlServerQueries.ForeignKeys, (f, db) => f with { Database = db }, ct);

    public Task<IReadOnlyList<DbActiveRequest>> GetActiveRequestsAsync(CancellationToken ct = default) =>
        QueryAsync<DbActiveRequest>(SqlServerDiagnostics.ActiveRequests, null, ct);

    public Task<IReadOnlyList<DbQueryStat>> GetTopQueriesAsync(QueryStatOrder order, int top, CancellationToken ct = default) =>
        QueryAsync<DbQueryStat>(SqlServerDiagnostics.TopQueries(order), new { top = Math.Clamp(top, 1, 1000) }, ct);

    public ColumnProfileLevel GetProfileLevel(DbColumn column) => SqlServerDiagnostics.ProfileLevel(column);

    public async Task<TableProfile> ProfileTableAsync(
        DbObject table, IReadOnlyList<DbColumn> columns, int sampleRows, DataSearchOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var cn = await OpenSearchConnectionAsync(table.Database, ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = SqlServerSql.SessionPrefix(options.LockTimeoutMs) +
                          SqlServerDiagnostics.ProfileTable(table.Schema, table.Name, columns);
        cmd.CommandTimeout = options.QueryTimeoutSeconds;
        cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = Math.Max(1, sampleRows) });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new TableProfile { SampleLimit = sampleRows };

        var total = reader.GetInt64(0);
        var profiles = columns.Select((c, i) =>
        {
            var o = 1 + i * 4;
            var nonNull = reader.IsDBNull(o) ? 0 : reader.GetInt64(o);
            return new ColumnProfile
            {
                Column = c.Name,
                DataType = c.DataType,
                NonNullCount = nonNull,
                NullCount = total - nonNull,
                DistinctCount = reader.IsDBNull(o + 1) ? null : reader.GetInt64(o + 1),
                MinValue = reader.IsDBNull(o + 2) ? null : reader.GetString(o + 2),
                MaxValue = reader.IsDBNull(o + 3) ? null : reader.GetString(o + 3)
            };
        }).ToList();

        return new TableProfile { SampledRows = total, SampleLimit = sampleRows, Columns = profiles, Elapsed = sw.Elapsed };
    }

    public async Task<IReadOnlyList<ValueFrequency>> GetTopValuesAsync(
        DbObject table, DbColumn column, int sampleRows, int top, DataSearchOptions options, CancellationToken ct = default)
    {
        var sql = SqlServerSql.SessionPrefix(options.LockTimeoutMs) +
                  SqlServerDiagnostics.TopValues(table.Schema, table.Name, column);
        await using var cn = await OpenSearchConnectionAsync(table.Database, ct);
        var rows = await cn.QueryAsync<ValueFrequency>(new CommandDefinition(
            sql, new { n = Math.Max(1, sampleRows), top = Math.Clamp(top, 1, 1000) },
            commandTimeout: options.QueryTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    private async Task<SqlConnection> OpenSearchConnectionAsync(string? database, CancellationToken ct)
    {
        var cn = new SqlConnection(string.IsNullOrEmpty(database) || !_allDatabases
            ? _searchConnectionString
            : SqlServerSql.BuildConnectionString(_profile, SqlServerSql.SearchAppName, database));
        try
        {
            await cn.OpenAsync(ct);
            return cn;
        }
        catch
        {
            await cn.DisposeAsync();
            throw;
        }
    }


    public bool IsSearchable(DbColumn column, SearchTerm term)
    {
        var type = column.BaseType;
        if (SqlServerSql.TextTypes.Contains(type)) return term.HasText;
        if (SqlServerSql.NumericTypes.Contains(type)) return term.Number is not null;
        if (string.Equals(type, "uniqueidentifier", StringComparison.OrdinalIgnoreCase)) return term.Uuid is not null;
        return false;
    }

    public async Task<IReadOnlyList<DataMatch>> SearchTableAsync(
        DbTableTarget table, SearchTerm term, DataSearchOptions options, CancellationToken ct = default)
    {
        var searchable = table.Columns.Where(c => IsSearchable(c, term)).OrderBy(c => c.Ordinal).ToList();
        if (searchable.Count == 0) return [];

        var keys = table.Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).ToList();

        var select = new List<string>();
        var conditions = new List<string>();
        bool usesText = false, usesNumber = false, usesGuid = false;

        for (var i = 0; i < searchable.Count; i++)
        {
            var col = searchable[i];
            var q = SqlServerSql.Quote(col.Name);
            string condition;

            if (SqlServerSql.TextTypes.Contains(col.BaseType)) { condition = $"{q} LIKE @p"; usesText = true; }
            else if (SqlServerSql.NumericTypes.Contains(col.BaseType)) { condition = $"{q} = @n"; usesNumber = true; }
            else { condition = $"{q} = @g"; usesGuid = true; }

            conditions.Add(condition);
            select.Add($"CASE WHEN {condition} THEN 1 ELSE 0 END AS m{i}");
            select.Add($"CAST({q} AS nvarchar({MaxValueLength})) AS v{i}");
        }

        for (var k = 0; k < keys.Count; k++)
            select.Add($"CAST({SqlServerSql.Quote(keys[k].Name)} AS nvarchar(200)) AS k{k}");

        var sql = new StringBuilder()
            .Append(SqlServerSql.SessionPrefix(options.LockTimeoutMs))
            .Append("SELECT TOP (@top) ").AppendJoin(", ", select)
            .Append(" FROM ").Append(SqlServerSql.QuoteFullName(table.Schema, table.Name))
            .Append(" WHERE ").AppendJoin(" OR ", conditions)
            .Append(';')
            .ToString();

        await using var cn = new SqlConnection(
            string.IsNullOrEmpty(table.Database) || !_allDatabases
                ? _searchConnectionString
                : SqlServerSql.BuildConnectionString(_profile, SqlServerSql.SearchAppName, table.Database));
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = options.QueryTimeoutSeconds;
        cmd.Parameters.Add(new SqlParameter("@top", SqlDbType.Int) { Value = options.MaxMatchesPerTable });
        if (usesText)
            cmd.Parameters.Add(new SqlParameter("@p", SqlDbType.NVarChar, 4000) { Value = SqlServerSql.BuildPattern(term.Text, term.Mode) });
        if (usesNumber)
            cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.Decimal) { Precision = 38, Scale = 10, Value = term.Number!.Value });
        if (usesGuid)
            cmd.Parameters.Add(new SqlParameter("@g", SqlDbType.UniqueIdentifier) { Value = term.Uuid!.Value });

        var results = new List<DataMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var keyOffset = searchable.Count * 2;

        while (await reader.ReadAsync(ct))
        {
            var keyValues = keys.Select((key, k) => new KeyValuePair<string, string?>(
                key.Name, reader.IsDBNull(keyOffset + k) ? null : reader.GetString(keyOffset + k))).ToList();
            string? rowKey = keys.Count == 0
                ? null
                : string.Join(", ", keyValues.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));

            for (var i = 0; i < searchable.Count; i++)
            {
                if (reader.GetInt32(i * 2) != 1) continue;
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
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<IReadOnlyList<DbRoutineParameter>> GetRoutineParametersAsync(DbObject routine, CancellationToken ct = default)
    {
        var database = string.IsNullOrEmpty(routine.Database) ? _profile.Database : routine.Database;
        var rows = await QueryInDatabaseAsync<DbRoutineParameter>(
            SqlServerQueries.RoutineParameters, new { schema = routine.Schema, name = routine.Name }, database, ct);
        return rows.Select(p => p with { Database = database ?? "" }).ToList();
    }

    public async Task<QueryExecutionResult> ExecuteScriptAsync(
        string sql, string? database, int timeoutSeconds, CancellationToken ct = default, int maxRows = int.MaxValue,
        ReadOnlyScript? readOnly = null)
    {
        var sw = Stopwatch.StartNew();
        var connectionString = SqlServerSql.BuildConnectionString(
            _profile, SqlServerSql.ScriptAppName, database ?? _profile.Database, forceReadWrite: true);

        var messages = new List<string>();
        await using var cn = new SqlConnection(connectionString);
        cn.InfoMessage += (_, e) => messages.Add(e.Message);
        await cn.OpenAsync(ct);

        var (resultSets, rowsAffected) = await RunBatchesAsync(cn, null, sql, timeoutSeconds, maxRows, readOnly, ct);
        return new QueryExecutionResult
        {
            ResultSets = resultSets,
            RowsAffected = rowsAffected,
            Messages = messages,
            Elapsed = sw.Elapsed
        };
    }

    /// <summary>
    /// Runs every GO-separated batch on the connection, keeping at most <paramref name="maxRows"/> rows per result set
    /// (the rest is read and discarded so later statements still run, unless a read-only script lets the server stop). Server errors are rethrown as
    /// <see cref="SqlExecutionException"/> with the line in the whole script, not in the batch.
    /// </summary>
    private static async Task<(List<QueryResultSet> ResultSets, int RowsAffected)> RunBatchesAsync(
        SqlConnection cn, SqlTransaction? tx, string sql, int timeoutSeconds, int maxRows, ReadOnlyScript? readOnly, CancellationToken ct)
    {
        if (readOnly is null || maxRows is <= 0 or int.MaxValue)
            return await RunBatchesAsync(cn, tx, sql, timeoutSeconds, maxRows, stoppedOnServer: false, ct);

        // SET ROWCOUNT makes the server stop every result set one row past the limit (and plan for that many rows):
        // a SELECT over a huge table costs what is shown, not the whole scan. It also caps INSERT/UPDATE/DELETE, which is
        // why it is only used for read-only scripts, and it outlives the script on the connection, so it is always
        // reset — and a connection where the reset failed never goes back to the pool.
        await SetRowCountAsync(cn, tx, maxRows + 1, ct);
        try
        {
            return await RunBatchesAsync(cn, tx, sql, timeoutSeconds, maxRows, stoppedOnServer: true, ct);
        }
        finally
        {
            try
            {
                await SetRowCountAsync(cn, tx, 0, CancellationToken.None);
            }
            catch
            {
                SqlConnection.ClearPool(cn);
            }
        }
    }

    private static async Task SetRowCountAsync(SqlConnection cn, SqlTransaction? tx, int rows, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SET ROWCOUNT {rows};";
        cmd.CommandTimeout = 30;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(List<QueryResultSet> ResultSets, int RowsAffected)> RunBatchesAsync(
        SqlConnection cn, SqlTransaction? tx, string sql, int timeoutSeconds, int maxRows, bool stoppedOnServer, CancellationToken ct)
    {
        var resultSets = new List<QueryResultSet>();
        var rowsAffected = 0;

        foreach (var (batch, startLine) in SplitBatches(sql))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;

            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = batch;
            cmd.CommandTimeout = timeoutSeconds;

            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                do
                {
                    if (reader.FieldCount > 0)
                    {
                        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                        var rows = new List<IReadOnlyList<object?>>();
                        long total = 0;
                        while (await reader.ReadAsync(ct))
                        {
                            if (++total > maxRows) continue;
                            var row = new object?[reader.FieldCount];
                            reader.GetValues(row!);
                            for (var i = 0; i < row.Length; i++)
                                if (row[i] == DBNull.Value) row[i] = null;
                            rows.Add(row);
                        }
                        var truncated = total > maxRows;
                        resultSets.Add(new QueryResultSet
                        {
                            Columns = columns, Rows = rows, TotalRowCount = total, IsTruncated = truncated,
                            TotalRowCountIsExact = !(truncated && stoppedOnServer)
                        });
                    }
                    else
                    {
                        rowsAffected += reader.RecordsAffected > 0 ? reader.RecordsAffected : 0;
                    }
                } while (await reader.NextResultAsync(ct));
            }
            catch (SqlException ex)
            {
                int? line = ex.LineNumber > 0 ? startLine + ex.LineNumber - 1 : null;
                throw new SqlExecutionException(line is null ? ex.Message : $"Line {line}: {ex.Message}", line, null, ex);
            }
        }

        return (resultSets, rowsAffected);
    }

    public async Task<QueryExecutionResult> ExecuteRoutineAsync(
        DbObject routine, IReadOnlyList<DbRoutineParameter> parameters, IReadOnlyDictionary<string, object?> arguments,
        int timeoutSeconds, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var database = string.IsNullOrEmpty(routine.Database) ? _profile.Database : routine.Database;
        var connectionString = SqlServerSql.BuildConnectionString(
            _profile, SqlServerSql.ScriptAppName, database, forceReadWrite: true);

        await using var cn = new SqlConnection(connectionString);
        var messages = new List<string>();
        cn.InfoMessage += (_, e) => messages.Add(e.Message);
        await cn.OpenAsync(ct);

        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = timeoutSeconds;

        var isFunction = routine.Type is DbObjectType.ScalarFunction or DbObjectType.TableFunction;
        if (isFunction)
        {
            var argList = string.Join(", ", parameters
                .Where(p => p.Direction != DbParameterDirection.ReturnValue)
                .Select(p => p.Name));
            var qualifiedName = SqlServerSql.QuoteFullName(routine.Schema, routine.Name);
            cmd.CommandText = routine.Type == DbObjectType.TableFunction
                ? $"SELECT * FROM {qualifiedName}({argList});"
                : $"SELECT {qualifiedName}({argList});";
        }
        else
        {
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandText = SqlServerSql.QuoteFullName(routine.Schema, routine.Name);
        }

        var outputParams = new Dictionary<string, SqlParameter>();
        foreach (var p in parameters.Where(p => p.Direction != DbParameterDirection.ReturnValue))
        {
            var sqlParam = new SqlParameter(p.Name, arguments.GetValueOrDefault(p.Name) ?? DBNull.Value);
            if (p.Direction == DbParameterDirection.Output || p.Direction == DbParameterDirection.InputOutput)
            {
                sqlParam.Direction = p.Direction == DbParameterDirection.Output
                    ? ParameterDirection.Output
                    : ParameterDirection.InputOutput;
                sqlParam.Size = 4000;
                outputParams[p.Name] = sqlParam;
            }
            cmd.Parameters.Add(sqlParam);
        }

        SqlParameter? returnParam = null;
        if (!isFunction)
        {
            returnParam = new SqlParameter("@__return", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
            cmd.Parameters.Add(returnParam);
        }

        var resultSets = new List<QueryResultSet>();
        var rowsAffected = 0;

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            do
            {
                if (reader.FieldCount > 0)
                {
                    var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                    var rows = new List<IReadOnlyList<object?>>();
                    while (await reader.ReadAsync(ct))
                    {
                        var row = new object?[reader.FieldCount];
                        reader.GetValues(row!);
                        for (var i = 0; i < row.Length; i++)
                            if (row[i] == DBNull.Value) row[i] = null;
                        rows.Add(row);
                    }
                    resultSets.Add(new QueryResultSet { Columns = columns, Rows = rows });
                }
                else
                {
                    rowsAffected += reader.RecordsAffected > 0 ? reader.RecordsAffected : 0;
                }
            } while (await reader.NextResultAsync(ct));
        }

        var outputValues = new Dictionary<string, object?>();
        foreach (var (name, sqlParam) in outputParams)
            outputValues[name] = sqlParam.Value == DBNull.Value ? null : sqlParam.Value;
        if (returnParam is not null)
            outputValues["ReturnValue"] = returnParam.Value == DBNull.Value ? null : returnParam.Value;

        return new QueryExecutionResult
        {
            ResultSets = resultSets,
            RowsAffected = rowsAffected,
            Messages = messages,
            Elapsed = sw.Elapsed,
            OutputValues = outputValues
        };
    }

    public async Task<IScriptSession> BeginScriptSessionAsync(string? database, bool transactional, CancellationToken ct = default)
    {
        var cn = new SqlConnection(SqlServerSql.BuildConnectionString(
            _profile, SqlServerSql.ScriptAppName, database ?? _profile.Database, forceReadWrite: true));
        try
        {
            await cn.OpenAsync(ct);
            var tx = transactional ? (SqlTransaction)await cn.BeginTransactionAsync(ct) : null;
            return new ScriptSession(cn, tx);
        }
        catch
        {
            await cn.DisposeAsync();
            throw;
        }
    }

    public async Task<DbTableConstraints> GetTableConstraintsAsync(DbObject table, CancellationToken ct = default)
    {
        var database = string.IsNullOrEmpty(table.Database) ? _profile.Database : table.Database;
        var args = new { name = SqlServerSql.QuoteFullName(table.Schema, table.Name) };
        var defaults = await QueryInDatabaseAsync<NameDefinition>(SqlServerQueries.ColumnDefaults, args, database, ct);
        var checks = await QueryInDatabaseAsync<NameDefinition>(SqlServerQueries.CheckConstraints, args, database, ct);
        return new DbTableConstraints
        {
            Defaults = defaults.Where(d => d.Definition is not null)
                .ToDictionary(d => d.Name, d => d.Definition!, StringComparer.OrdinalIgnoreCase),
            Checks = checks.Where(c => c.Definition is not null).Select(c => new DbCheckConstraint(c.Name, c.Definition!)).ToList()
        };
    }

    public async Task<QueryResultSet> QueryReadOnlyAsync(
        string sql, string? database, DataSearchOptions options, int maxRows = 1000, CancellationToken ct = default)
    {
        var connectionString = string.IsNullOrEmpty(database) || string.Equals(database, _profile.Database, StringComparison.OrdinalIgnoreCase)
            ? _searchConnectionString
            : SqlServerSql.BuildConnectionString(_profile, SqlServerSql.SearchAppName, database);
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = SqlServerSql.SessionPrefix(options.LockTimeoutMs) + sql;
        cmd.CommandTimeout = options.QueryTimeoutSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await ReadFirstResultSetAsync(reader, maxRows, ct);
    }

    /// <summary>The first result set with at most <paramref name="maxRows"/> rows; stops reading there.</summary>
    internal static async Task<QueryResultSet> ReadFirstResultSetAsync(SqlDataReader reader, int maxRows, CancellationToken ct)
    {
        while (reader.FieldCount == 0 && await reader.NextResultAsync(ct)) { }
        if (reader.FieldCount == 0) return new QueryResultSet();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= maxRows) { truncated = true; break; }
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new QueryResultSet
        {
            Columns = columns, Rows = rows, IsTruncated = truncated,
            TotalRowCount = rows.Count + (truncated ? 1 : 0), TotalRowCountIsExact = !truncated
        };
    }

    public async Task<IReadOnlyList<TableChangeCounter>> GetTableChangeCountersAsync(string? database, CancellationToken ct = default)
    {
        // The primary (read-write intent): a readable secondary does not count the primary's writes.
        var db = string.IsNullOrEmpty(database) ? _profile.Database : database;
        var rows = await QueryWithConnectionStringAsync<TableChangeCounter>(SqlServerDiagnostics.ChangeCounters, null,
            SqlServerSql.BuildConnectionString(_profile, SqlServerSql.MetaAppName, db, forceReadWrite: true), ct);
        return rows.Select(r => r with { Database = db }).ToList();
    }

    public Task<string?> GetChangeMarkerAsync(string? database, CancellationToken ct = default) =>
        ScalarWithConnectionStringAsync<string?>(SqlServerDiagnostics.ChangeMarker, null,
            SqlServerSql.BuildConnectionString(_profile, SqlServerSql.MetaAppName,
                string.IsNullOrEmpty(database) ? _profile.Database : database, forceReadWrite: true), ct);

    public string? ChangedSincePredicate(IReadOnlyList<DbColumn> columns, string marker) =>
        SqlServerDiagnostics.ChangedSincePredicate(columns, marker);

    private sealed class NameDefinition
    {
        public string Name { get; set; } = "";
        public string? Definition { get; set; }
    }

    /// <summary>Keeps one connection (and transaction) open across scripts; GO still separates batches.</summary>
    private sealed class ScriptSession(SqlConnection connection, SqlTransaction? transaction) : IScriptSession
    {
        private bool _committed;

        public async Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default)
        {
            var affected = 0;
            foreach (var (batch, startLine) in SplitBatches(sql))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = batch;
                cmd.CommandTimeout = timeoutSeconds;
                try
                {
                    var rows = await cmd.ExecuteNonQueryAsync(ct);
                    if (rows > 0) affected += rows;
                }
                catch (SqlException ex)
                {
                    int? line = ex.LineNumber > 0 ? startLine + ex.LineNumber - 1 : null;
                    throw new SqlExecutionException(line is null ? ex.Message : $"Line {line}: {ex.Message}", line, null, ex);
                }
            }
            return affected;
        }

        public async Task<QueryExecutionResult> QueryAsync(
            string sql, int timeoutSeconds, int maxRows = int.MaxValue, CancellationToken ct = default, ReadOnlyScript? readOnly = null)
        {
            var sw = Stopwatch.StartNew();
            var messages = new List<string>();
            void OnInfo(object? _, SqlInfoMessageEventArgs e) => messages.Add(e.Message);
            connection.InfoMessage += OnInfo;
            try
            {
                var (resultSets, rowsAffected) = await RunBatchesAsync(connection, transaction, sql, timeoutSeconds, maxRows, readOnly, ct);
                return new QueryExecutionResult { ResultSets = resultSets, RowsAffected = rowsAffected, Messages = messages, Elapsed = sw.Elapsed };
            }
            finally
            {
                connection.InfoMessage -= OnInfo;
            }
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            _committed = true; // nothing left to roll back on dispose
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (transaction is not null) await transaction.CommitAsync(ct);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (transaction is not null)
            {
                if (!_committed)
                {
                    try { await transaction.RollbackAsync(); }
                    catch (Exception) { /* already rolled back by XACT_ABORT or a broken connection */ }
                }
                await transaction.DisposeAsync();
            }
            await connection.DisposeAsync();
        }
    }

    /// <summary>Splits a script on GO batch separators (SSMS convention); the word must be alone on its line.
    /// Each batch comes with the 1-based script line it starts on, to map error lines back to the script.</summary>
    private static IReadOnlyList<(string Text, int StartLine)> SplitBatches(string sql)
    {
        var lines = sql.Replace("\r\n", "\n").Split('\n');
        var batches = new List<(string, int)>();
        var current = new StringBuilder();
        var startLine = 1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                batches.Add((current.ToString(), startLine));
                current.Clear();
                startLine = i + 2;
            }
            else
            {
                current.AppendLine(lines[i]);
            }
        }
        if (current.Length > 0) batches.Add((current.ToString(), startLine));
        return batches;
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param, CancellationToken ct)
    {
        await using var cn = new SqlConnection(_metaConnectionString);
        await cn.OpenAsync(ct);
        var rows = await cn.QueryAsync<T>(new CommandDefinition(
            SqlServerSql.SessionPrefix(MetadataLockTimeoutMs) + sql, param,
            commandTimeout: MetadataCommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    private async Task<T?> ScalarAsync<T>(string sql, object? param, CancellationToken ct)
    {
        await using var cn = new SqlConnection(_metaConnectionString);
        await cn.OpenAsync(ct);
        return await cn.ExecuteScalarAsync<T>(new CommandDefinition(
            SqlServerSql.SessionPrefix(MetadataLockTimeoutMs) + sql, param,
            commandTimeout: MetadataCommandTimeoutSeconds, cancellationToken: ct));
    }

    private Task<IReadOnlyList<T>> QueryInDatabaseAsync<T>(string sql, object? param, string? database, CancellationToken ct) =>
        QueryWithConnectionStringAsync<T>(sql, param,
            SqlServerSql.BuildConnectionString(_profile, SqlServerSql.MetaAppName, database), ct);

    private Task<T?> ScalarInDatabaseAsync<T>(string sql, object? param, string? database, CancellationToken ct) =>
        ScalarWithConnectionStringAsync<T>(sql, param,
            SqlServerSql.BuildConnectionString(_profile, SqlServerSql.MetaAppName, database), ct);

    private static async Task<IReadOnlyList<T>> QueryWithConnectionStringAsync<T>(
        string sql, object? param, string connectionString, CancellationToken ct)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        var rows = await cn.QueryAsync<T>(new CommandDefinition(
            SqlServerSql.SessionPrefix(MetadataLockTimeoutMs) + sql, param,
            commandTimeout: MetadataCommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    private static async Task<T?> ScalarWithConnectionStringAsync<T>(
        string sql, object? param, string connectionString, CancellationToken ct)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        return await cn.ExecuteScalarAsync<T>(new CommandDefinition(
            SqlServerSql.SessionPrefix(MetadataLockTimeoutMs) + sql, param,
            commandTimeout: MetadataCommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>Lists every database the current login can access, used only when no database was selected at connect time.</summary>
    private Task<IReadOnlyList<string>> GetAccessibleDatabasesAsync(CancellationToken ct) =>
        QueryAsync<string>(SqlServerQueries.Databases, null, ct);

    /// <summary>Runs one catalog read once a slot in <see cref="_catalogGate"/> is free.</summary>
    private async Task<T> GatedAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        await _catalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await read().ConfigureAwait(false);
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    /// <summary>The accessible databases, read once for the parallel catalog reads of one load rather than once each.</summary>
    private Task<IReadOnlyList<string>> GetDatabasesForCatalogAsync(CancellationToken ct)
    {
        lock (_databasesLock)
        {
            if (_databases is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(30) && !cached.Task.IsFaulted && !cached.Task.IsCanceled)
                return cached.Task;
            var task = GatedAsync(() => GetAccessibleDatabasesAsync(ct), ct);
            _databases = (task, DateTime.UtcNow);
            return task;
        }
    }

    /// <summary>
    /// Runs a catalog query against the selected database, or against every accessible database when
    /// none was selected, tagging each row with the database it came from.
    /// </summary>
    private async Task<IReadOnlyList<T>> QueryAcrossDatabasesAsync<T>(
        string sql, Func<T, string, T> tag, CancellationToken ct)
    {
        if (!_allDatabases)
        {
            var rows = await GatedAsync(() => QueryAsync<T>(sql, null, ct), ct).ConfigureAwait(false);
            return rows.Select(r => tag(r, _profile.Database)).ToList();
        }

        var databases = await GetDatabasesForCatalogAsync(ct).ConfigureAwait(false);
        var results = new System.Collections.Concurrent.ConcurrentBag<T>();

        await Parallel.ForEachAsync(databases, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelDatabases,
            CancellationToken = ct
        }, async (db, token) =>
        {
            try
            {
                var cs = SqlServerSql.BuildConnectionString(_profile, SqlServerSql.MetaAppName, db);
                var rows = await GatedAsync(() => QueryWithConnectionStringAsync<T>(sql, null, cs, token), token).ConfigureAwait(false);
                foreach (var row in rows) results.Add(tag(row, db));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Inaccessible or unreadable database (permissions, offline, etc.): skip it.
            }
        }).ConfigureAwait(false);

        return results.ToList();
    }
}
