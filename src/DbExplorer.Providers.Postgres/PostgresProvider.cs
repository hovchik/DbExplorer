using System.Diagnostics;
using System.Text;
using Dapper;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using Npgsql;
using NpgsqlTypes;

namespace DbExplorer.Providers.Postgres;

public sealed class PostgresProvider : IDatabaseProvider
{
    private const int MetadataStatementTimeoutMs = 120_000;
    private const int MetadataLockTimeoutMs = 5000;
    private const int MaxValueLength = 400;
    private const int MaxParallelDatabases = 4;

    private readonly ConnectionProfile _profile;
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>True when no specific database was picked at connect time: every accessible database is read and merged.</summary>
    private readonly bool _allDatabases;

    public PostgresProvider(ConnectionProfile profile)
    {
        _profile = profile;
        _allDatabases = string.IsNullOrWhiteSpace(profile.Database);
        _dataSource = NpgsqlDataSource.Create(PostgresSql.BuildConnectionString(profile));
    }

    public string ProviderKey => PostgresProviderFactory.ProviderKey;

    public string QuoteIdentifier(string identifier) => PostgresSql.Quote(identifier);

    public async Task<string> GetServerVersionAsync(CancellationToken ct = default) =>
        await ScalarAsync<string>(PostgresQueries.ServerVersion, null, ct) ?? "";

    public Task<IReadOnlyList<DbObject>> GetObjectsAsync(CancellationToken ct = default) =>
        QueryAcrossDatabasesAsync<DbObject>(PostgresQueries.Objects, (o, db) => o with { Database = db }, ct);

    public Task<IReadOnlyList<DbColumn>> GetColumnsAsync(CancellationToken ct = default) =>
        QueryAcrossDatabasesAsync<DbColumn>(PostgresQueries.Columns, (c, db) => c with { Database = db }, ct);

    public Task<IReadOnlyList<DbModule>> GetModulesAsync(CancellationToken ct = default) =>
        QueryAcrossDatabasesAsync<DbModule>(PostgresQueries.Modules, (m, db) => m with { Database = db }, ct);

    public async Task<string?> GetDefinitionAsync(DbObject obj, CancellationToken ct = default)
    {
        var args = new { schema = obj.Schema, name = obj.Name };
        var database = string.IsNullOrEmpty(obj.Database) ? _profile.Database : obj.Database;

        if (obj.Type == DbObjectType.Sequence)
            return await ScalarInDatabaseAsync<string?>(PostgresQueries.SequenceDefinition, args, database, ct);

        // Functions can be overloaded: return every overload.
        var defs = await QueryInDatabaseAsync<string?>(PostgresQueries.ModuleDefinition, args, database, ct);
        var nonEmpty = defs.Where(d => !string.IsNullOrEmpty(d)).ToList();
        return nonEmpty.Count == 0 ? null : string.Join("\n\n", nonEmpty);
    }

    public Task<IReadOnlyList<DbIndex>> GetIndexesAsync(bool includePhysicalStats, CancellationToken ct = default) =>
        // Fragmentation would need the pgstattuple extension, which scans the index; not used.
        QueryAcrossDatabasesAsync<DbIndex>(PostgresQueries.Indexes, (i, db) => i with { Database = db }, ct);

    public Task<IReadOnlyList<DbLock>> GetLocksAsync(CancellationToken ct = default) =>
        QueryAsync<DbLock>(PostgresQueries.Locks, null, ct);

    public Task<IReadOnlyList<DbForeignKey>> GetForeignKeysAsync(CancellationToken ct = default) =>
        QueryAcrossDatabasesAsync<DbForeignKey>(PostgresQueries.ForeignKeys, (f, db) => f with { Database = db }, ct);

    public Task<IReadOnlyList<DbActiveRequest>> GetActiveRequestsAsync(CancellationToken ct = default) =>
        QueryAsync<DbActiveRequest>(PostgresDiagnostics.ActiveRequests, null, ct);

    public async Task<IReadOnlyList<DbQueryStat>> GetTopQueriesAsync(QueryStatOrder order, int top, CancellationToken ct = default)
    {
        var schema = await ScalarAsync<string?>(PostgresDiagnostics.FindStatStatements, null, ct)
            ?? throw new InvalidOperationException(
                "The pg_stat_statements extension is not installed in this database. Add it to shared_preload_libraries, " +
                "restart the server, then run CREATE EXTENSION pg_stat_statements;");
        var version = await ScalarAsync<int>("SELECT current_setting('server_version_num')::int;", null, ct);
        return await QueryAsync<DbQueryStat>(
            PostgresDiagnostics.TopQueries(schema, version, order), new { top = Math.Clamp(top, 1, 1000) }, ct);
    }

    public ColumnProfileLevel GetProfileLevel(DbColumn column) => PostgresDiagnostics.ProfileLevel(column);

    public async Task<TableProfile> ProfileTableAsync(
        DbObject table, IReadOnlyList<DbColumn> columns, int sampleRows, DataSearchOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = await OpenReadOnlyAsync(table.Database, options, ct);
        await using var cmd = new NpgsqlCommand(
            PostgresDiagnostics.ProfileTable(table.Schema, table.Name, columns), scope.Connection, scope.Transaction)
        {
            CommandTimeout = options.QueryTimeoutSeconds + 5
        };
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = Math.Max(1, sampleRows) });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new TableProfile { SampleLimit = sampleRows };

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
                DistinctCount = reader.GetInt64(o + 1),
                MinValue = reader.IsDBNull(o + 2) ? null : reader.GetString(o + 2),
                MaxValue = reader.IsDBNull(o + 3) ? null : reader.GetString(o + 3)
            };
        }).ToList();

        return new TableProfile { SampledRows = total, SampleLimit = sampleRows, Columns = profiles, Elapsed = sw.Elapsed };
    }

    public async Task<IReadOnlyList<ValueFrequency>> GetTopValuesAsync(
        DbObject table, DbColumn column, int sampleRows, int top, DataSearchOptions options, CancellationToken ct = default)
    {
        await using var scope = await OpenReadOnlyAsync(table.Database, options, ct);
        var rows = await scope.Connection.QueryAsync<ValueFrequency>(new CommandDefinition(
            PostgresDiagnostics.TopValues(table.Schema, table.Name, column),
            new { n = Math.Max(1, sampleRows), top = Math.Clamp(top, 1, 1000) },
            scope.Transaction, commandTimeout: options.QueryTimeoutSeconds + 5, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>A connection to the table's database inside a read-only transaction with the given timeouts; disposing rolls back.</summary>
    private async Task<ReadOnlyScope> OpenReadOnlyAsync(string? database, DataSearchOptions options, CancellationToken ct)
    {
        var useOwnDataSource = _allDatabases && !string.IsNullOrEmpty(database) && database != _profile.Database;
        var scoped = useOwnDataSource ? NpgsqlDataSource.Create(PostgresSql.BuildConnectionString(_profile, database)) : null;
        NpgsqlConnection? cn = null;
        try
        {
            cn = await (scoped ?? _dataSource).OpenConnectionAsync(ct);
            var tx = await cn.BeginTransactionAsync(ct);
            await ApplySettingsAsync(cn, tx, options.QueryTimeoutSeconds * 1000, options.LockTimeoutMs, ct);
            return new ReadOnlyScope(scoped, cn, tx);
        }
        catch
        {
            if (cn is not null) await cn.DisposeAsync();
            if (scoped is not null) await scoped.DisposeAsync();
            throw;
        }
    }

    private sealed class ReadOnlyScope(NpgsqlDataSource? dataSource, NpgsqlConnection connection, NpgsqlTransaction transaction)
        : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            try { await Transaction.RollbackAsync(); } catch { /* connection may already be broken */ }
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
            if (dataSource is not null) await dataSource.DisposeAsync();
        }
    }

    public bool IsSearchable(DbColumn column, SearchTerm term)
    {
        var type = column.BaseType;
        if (PostgresSql.TextTypes.Contains(type)) return term.HasText;
        if (PostgresSql.NumericTypes.Contains(type)) return term.Number is not null;
        if (string.Equals(type, "uuid", StringComparison.OrdinalIgnoreCase)) return term.Uuid is not null;
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
            var q = PostgresSql.Quote(col.Name);
            string condition;

            // ILIKE: case-insensitive, like the default SQL Server collations.
            if (PostgresSql.TextTypes.Contains(col.BaseType)) { condition = $"{q} ILIKE @p"; usesText = true; }
            else if (PostgresSql.NumericTypes.Contains(col.BaseType)) { condition = $"{q} = @n"; usesNumber = true; }
            else { condition = $"{q} = @g"; usesGuid = true; }

            conditions.Add(condition);
            select.Add($"({condition}) AS m{i}");
            select.Add($"left({q}::text, {MaxValueLength}) AS v{i}");
        }

        for (var k = 0; k < keys.Count; k++)
            select.Add($"{PostgresSql.Quote(keys[k].Name)}::text AS k{k}");

        var sql = new StringBuilder()
            .Append("SELECT ").AppendJoin(", ", select)
            .Append(" FROM ").Append(PostgresSql.QuoteFullName(table.Schema, table.Name))
            .Append(" WHERE ").AppendJoin(" OR ", conditions)
            .Append(" LIMIT @top")
            .ToString();

        var useOwnDataSource = _allDatabases && !string.IsNullOrEmpty(table.Database) && table.Database != _profile.Database;
        await using var scopedDataSource = useOwnDataSource
            ? NpgsqlDataSource.Create(PostgresSql.BuildConnectionString(_profile, table.Database))
            : null;
        var dataSource = scopedDataSource ?? _dataSource;

        await using var cn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ApplySettingsAsync(cn, tx, options.QueryTimeoutSeconds * 1000, options.LockTimeoutMs, ct);

        await using var cmd = new NpgsqlCommand(sql, cn, tx) { CommandTimeout = options.QueryTimeoutSeconds + 5 };
        cmd.Parameters.Add(new NpgsqlParameter("top", NpgsqlDbType.Integer) { Value = options.MaxMatchesPerTable });
        if (usesText)
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = PostgresSql.BuildPattern(term.Text, term.Mode) });
        if (usesNumber)
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Numeric) { Value = term.Number!.Value });
        if (usesGuid)
            cmd.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Uuid) { Value = term.Uuid!.Value });

        var results = new List<DataMatch>();
        var keyOffset = searchable.Count * 2;

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                string? rowKey = null;
                if (keys.Count > 0)
                {
                    rowKey = string.Join(", ", keys.Select((key, k) =>
                        $"{key.Name}={(reader.IsDBNull(keyOffset + k) ? "NULL" : reader.GetString(keyOffset + k))}"));
                }

                for (var i = 0; i < searchable.Count; i++)
                {
                    if (reader.IsDBNull(i * 2) || !reader.GetBoolean(i * 2)) continue;
                    results.Add(new DataMatch
                    {
                        Database = table.Database,
                        Schema = table.Schema,
                        Table = table.Name,
                        Column = searchable[i].Name,
                        Value = reader.IsDBNull(i * 2 + 1) ? null : reader.GetString(i * 2 + 1),
                        RowKey = rowKey
                    });
                }
            }
        }

        await tx.RollbackAsync(ct);
        return results;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    public async Task<IReadOnlyList<DbRoutineParameter>> GetRoutineParametersAsync(DbObject routine, CancellationToken ct = default)
    {
        var database = string.IsNullOrEmpty(routine.Database) ? _profile.Database : routine.Database;
        var args = new { schema = routine.Schema, name = routine.Name };
        var rows = string.IsNullOrEmpty(database) || database == _profile.Database
            ? await QueryAsync<DbRoutineParameter>(PostgresQueries.RoutineParameters, args, ct)
            : await QueryInDatabaseAsync<DbRoutineParameter>(PostgresQueries.RoutineParameters, args, database, ct);
        return rows.Select(p => p with { Database = database ?? "" }).ToList();
    }

    public async Task<QueryExecutionResult> ExecuteScriptAsync(
        string sql, string? database, int timeoutSeconds, CancellationToken ct = default, int maxRows = int.MaxValue)
    {
        var sw = Stopwatch.StartNew();
        var targetDatabase = database ?? _profile.Database;
        var useOwnDataSource = !string.IsNullOrEmpty(targetDatabase) && targetDatabase != _profile.Database;

        await using var scopedDataSource = useOwnDataSource
            ? NpgsqlDataSource.Create(PostgresSql.BuildConnectionString(_profile, targetDatabase))
            : null;
        var dataSource = scopedDataSource ?? _dataSource;

        await using var cn = await dataSource.OpenConnectionAsync(ct);
        var result = await RunAsync(cn, null, sql, timeoutSeconds, maxRows, ct);
        return result with { Elapsed = sw.Elapsed };
    }

    /// <summary>
    /// Runs a (multi-statement) script, keeping at most <paramref name="maxRows"/> rows per result set (the rest is read
    /// and discarded so later statements still run). Server errors become <see cref="SqlExecutionException"/> with the
    /// line/column in the script, located through the failing statement of the batch.
    /// </summary>
    private static async Task<QueryExecutionResult> RunAsync(
        NpgsqlConnection cn, NpgsqlTransaction? tx, string sql, int timeoutSeconds, int maxRows, CancellationToken ct)
    {
        var resultSets = new List<QueryResultSet>();
        var messages = new List<string>();
        var rowsAffected = 0;

        void OnNotice(object? _, NpgsqlNoticeEventArgs e) => messages.Add(e.Notice.MessageText);
        cn.Notice += OnNotice;

        try
        {
            await using var cmd = new NpgsqlCommand(sql, cn, tx) { CommandTimeout = timeoutSeconds };
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
                        for (var i = 0; i < row.Length; i++)
                            row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        rows.Add(row);
                    }
                    resultSets.Add(new QueryResultSet { Columns = columns, Rows = rows, TotalRowCount = total, IsTruncated = total > maxRows });
                }
                else
                {
                    rowsAffected += reader.RecordsAffected > 0 ? reader.RecordsAffected : 0;
                }
            } while (await reader.NextResultAsync(ct));
        }
        catch (PostgresException ex)
        {
            throw ToExecutionException(ex, sql);
        }
        finally
        {
            cn.Notice -= OnNotice;
        }

        return new QueryExecutionResult { ResultSets = resultSets, RowsAffected = rowsAffected, Messages = messages };
    }

    /// <summary>Position is 1-based inside the failing statement; find that statement in the script to get a script offset.</summary>
    private static SqlExecutionException ToExecutionException(PostgresException ex, string sql)
    {
        var message = ex.MessageText + (string.IsNullOrEmpty(ex.Detail) ? "" : $" ({ex.Detail})") +
                      (string.IsNullOrEmpty(ex.Hint) ? "" : $" Hint: {ex.Hint}");
        if (ex.Position <= 0) return new SqlExecutionException($"{ex.SqlState}: {message}", null, null, ex);

        var statement = ex.BatchCommand?.CommandText;
        var statementStart = string.IsNullOrEmpty(statement) ? 0 : Math.Max(0, sql.IndexOf(statement, StringComparison.Ordinal));
        var (line, column) = SqlExecutionException.LocationOf(sql, statementStart + ex.Position - 1);
        return new SqlExecutionException($"Line {line}, column {column}: {ex.SqlState}: {message}", line, column, ex);
    }

    public async Task<QueryExecutionResult> ExecuteRoutineAsync(
        DbObject routine, IReadOnlyList<DbRoutineParameter> parameters, IReadOnlyDictionary<string, object?> arguments,
        int timeoutSeconds, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var database = string.IsNullOrEmpty(routine.Database) ? _profile.Database : routine.Database;
        var useOwnDataSource = !string.IsNullOrEmpty(database) && database != _profile.Database;

        await using var scopedDataSource = useOwnDataSource
            ? NpgsqlDataSource.Create(PostgresSql.BuildConnectionString(_profile, database))
            : null;
        var dataSource = scopedDataSource ?? _dataSource;

        var isProcedure = routine.Type == DbObjectType.Procedure;
        var inputParams = parameters.Where(p => p.Direction != DbParameterDirection.Output).ToList();
        var argList = string.Join(", ", inputParams.Select(p => "@" + p.Name));
        var sql = isProcedure
            ? $"CALL {PostgresSql.QuoteFullName(routine.Schema, routine.Name)}({argList});"
            : $"SELECT * FROM {PostgresSql.QuoteFullName(routine.Schema, routine.Name)}({argList});";

        await using var cn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, cn) { CommandTimeout = timeoutSeconds };

        foreach (var p in inputParams)
            cmd.Parameters.AddWithValue(p.Name, arguments.GetValueOrDefault(p.Name) ?? DBNull.Value);

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
                        for (var i = 0; i < row.Length; i++)
                            row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
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

        return new QueryExecutionResult
        {
            ResultSets = resultSets,
            RowsAffected = rowsAffected,
            Messages = [],
            Elapsed = sw.Elapsed
        };
    }

    public async Task<IScriptSession> BeginScriptSessionAsync(string? database, bool transactional, CancellationToken ct = default)
    {
        var targetDatabase = database ?? _profile.Database;
        var scoped = !string.IsNullOrEmpty(targetDatabase) && targetDatabase != _profile.Database
            ? NpgsqlDataSource.Create(PostgresSql.BuildConnectionString(_profile, targetDatabase))
            : null;
        NpgsqlConnection? cn = null;
        try
        {
            cn = await (scoped ?? _dataSource).OpenConnectionAsync(ct);
            var tx = transactional ? await cn.BeginTransactionAsync(ct) : null;
            return new ScriptSession(cn, tx, scoped);
        }
        catch
        {
            if (cn is not null) await cn.DisposeAsync();
            if (scoped is not null) await scoped.DisposeAsync();
            throw;
        }
    }

    public async Task<DbTableConstraints> GetTableConstraintsAsync(DbObject table, CancellationToken ct = default)
    {
        var database = string.IsNullOrEmpty(table.Database) ? _profile.Database : table.Database;
        var args = new { name = PostgresSql.QuoteFullName(table.Schema, table.Name) };
        var defaults = await QueryInDatabaseAsync<NameDefinition>(PostgresQueries.ColumnDefaults, args, database, ct);
        var checks = await QueryInDatabaseAsync<NameDefinition>(PostgresQueries.CheckConstraints, args, database, ct);
        return new DbTableConstraints
        {
            Defaults = defaults.Where(d => d.Definition is not null)
                .ToDictionary(d => d.Name, d => d.Definition!, StringComparer.OrdinalIgnoreCase),
            Checks = checks.Where(c => c.Definition is not null).Select(c => new DbCheckConstraint(c.Name, c.Definition!)).ToList()
        };
    }

    private sealed class NameDefinition
    {
        public string Name { get; set; } = "";
        public string? Definition { get; set; }
    }

    private sealed class ScriptSession(NpgsqlConnection connection, NpgsqlTransaction? transaction, NpgsqlDataSource? scoped)
        : IScriptSession
    {
        private bool _committed;

        public async Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = timeoutSeconds };
            try
            {
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                return Math.Max(rows, 0);
            }
            catch (PostgresException ex)
            {
                throw ToExecutionException(ex, sql);
            }
        }

        public async Task<QueryExecutionResult> QueryAsync(string sql, int timeoutSeconds, int maxRows = int.MaxValue, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = await RunAsync(connection, transaction, sql, timeoutSeconds, maxRows, ct);
            return result with { Elapsed = sw.Elapsed };
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
                    catch (Exception) { /* connection already broken: the server discards the transaction */ }
                }
                await transaction.DisposeAsync();
            }
            await connection.DisposeAsync();
            if (scoped is not null) await scoped.DisposeAsync();
        }
    }

    private static async Task ApplySettingsAsync(
        NpgsqlConnection cn, NpgsqlTransaction tx, int statementTimeoutMs, int lockTimeoutMs, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            PostgresSql.TransactionSettings(statementTimeoutMs, lockTimeoutMs), cn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param, CancellationToken ct)
    {
        await using var cn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ApplySettingsAsync(cn, tx, MetadataStatementTimeoutMs, MetadataLockTimeoutMs, ct);
        var rows = await cn.QueryAsync<T>(new CommandDefinition(
            sql, param, tx, commandTimeout: MetadataStatementTimeoutMs / 1000 + 5, cancellationToken: ct));
        var list = rows.AsList();
        await tx.RollbackAsync(ct);
        return list;
    }

    private async Task<T?> ScalarAsync<T>(string sql, object? param, CancellationToken ct)
    {
        await using var cn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ApplySettingsAsync(cn, tx, MetadataStatementTimeoutMs, MetadataLockTimeoutMs, ct);
        var value = await cn.ExecuteScalarAsync<T>(new CommandDefinition(
            sql, param, tx, commandTimeout: MetadataStatementTimeoutMs / 1000 + 5, cancellationToken: ct));
        await tx.RollbackAsync(ct);
        return value;
    }

    private async Task<IReadOnlyList<T>> QueryInDatabaseAsync<T>(string sql, object? param, string database, CancellationToken ct)
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresSql.BuildConnectionString(_profile, database));
        await using var cn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ApplySettingsAsync(cn, tx, MetadataStatementTimeoutMs, MetadataLockTimeoutMs, ct);
        var rows = await cn.QueryAsync<T>(new CommandDefinition(
            sql, param, tx, commandTimeout: MetadataStatementTimeoutMs / 1000 + 5, cancellationToken: ct));
        var list = rows.AsList();
        await tx.RollbackAsync(ct);
        return list;
    }

    private async Task<T?> ScalarInDatabaseAsync<T>(string sql, object? param, string database, CancellationToken ct)
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresSql.BuildConnectionString(_profile, database));
        await using var cn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ApplySettingsAsync(cn, tx, MetadataStatementTimeoutMs, MetadataLockTimeoutMs, ct);
        var value = await cn.ExecuteScalarAsync<T>(new CommandDefinition(
            sql, param, tx, commandTimeout: MetadataStatementTimeoutMs / 1000 + 5, cancellationToken: ct));
        await tx.RollbackAsync(ct);
        return value;
    }

    /// <summary>Lists every database the current login can access, used only when no database was selected at connect time.</summary>
    private async Task<IReadOnlyList<string>> GetAccessibleDatabasesAsync(CancellationToken ct)
    {
        var db = string.IsNullOrWhiteSpace(_profile.Database) ? "postgres" : _profile.Database;
        return await QueryInDatabaseAsync<string>(PostgresQueries.Databases, null, db, ct);
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
            var rows = await QueryAsync<T>(sql, null, ct);
            return rows.Select(r => tag(r, _profile.Database)).ToList();
        }

        var databases = await GetAccessibleDatabasesAsync(ct);
        var results = new System.Collections.Concurrent.ConcurrentBag<T>();

        await Parallel.ForEachAsync(databases, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelDatabases,
            CancellationToken = ct
        }, async (db, token) =>
        {
            try
            {
                var rows = await QueryInDatabaseAsync<T>(sql, null, db, token);
                foreach (var row in rows) results.Add(tag(row, db));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Inaccessible or unreadable database (permissions, offline, etc.): skip it.
            }
        });

        return results.ToList();
    }
}
