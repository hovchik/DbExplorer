using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using MySqlConnector;

namespace DbExplorer.Providers.MySql;

/// <summary>
/// MySQL 8+ and MariaDB 10.6+. A MySQL schema is a database in the rest of the app (Database and Schema both carry its
/// name), and one connection sees every schema, so a server-wide load needs no connection per database.
/// </summary>
public sealed class MySqlProvider : IDatabaseProvider
{
    private const int MetadataStatementTimeoutMs = 120_000;
    private const int MetadataLockTimeoutMs = 5000;
    private const int MaxValueLength = 400;

    private readonly ConnectionProfile _profile;
    private readonly bool _allDatabases;
    private readonly ConcurrentDictionary<string, string> _connectionStrings = new(StringComparer.Ordinal);
    private readonly object _serverLock = new();
    private Task<ServerInfo>? _server;

    public MySqlProvider(ConnectionProfile profile)
    {
        _profile = profile;
        _allDatabases = string.IsNullOrWhiteSpace(profile.Database);
    }

    public string ProviderKey => MySqlProviderFactory.ProviderKey;

    public string QuoteIdentifier(string identifier) => MySqlSql.Quote(identifier);

    /// <summary>What the server is: MariaDB reports "10.11.6-MariaDB-…" as its version.</summary>
    internal sealed record ServerInfo(bool MariaDb, Version Version);

    internal Task<ServerInfo> GetServerAsync(CancellationToken ct)
    {
        lock (_serverLock)
        {
            if (_server is { IsFaulted: false, IsCanceled: false } known) return known;
            return _server = ReadServerAsync(ct);
        }
    }

    private async Task<ServerInfo> ReadServerAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(null, ct);
        await using var cmd = new MySqlCommand(MySqlQueries.ServerVersion, cn);
        var text = Convert.ToString(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? "";
        return ParseServer(text);
    }

    internal static ServerInfo ParseServer(string version)
    {
        var mariaDb = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        // Old MariaDB replication setups prefix the version with "5.5.5-".
        var text = mariaDb && version.StartsWith("5.5.5-", StringComparison.Ordinal) ? version[6..] : version;
        var m = Regex.Match(text, @"^\d+(\.\d+){1,2}");
        return new ServerInfo(mariaDb, m.Success && Version.TryParse(m.Value, out var v) ? v : new Version(0, 0));
    }

    public async Task<string> GetServerVersionAsync(CancellationToken ct = default)
    {
        var raw = await ScalarAsync<string>(MySqlQueries.ServerVersion, null, ct) ?? "";
        var server = ParseServer(raw);
        return (server.MariaDb ? "MariaDB " : "MySQL ") + raw;
    }

    private object CatalogArgs => new { db = _profile.Database };

    public Task<IReadOnlyList<DbObject>> GetObjectsAsync(CancellationToken ct = default) =>
        QueryAsync<DbObject>(MySqlQueries.Objects(_allDatabases), CatalogArgs, ct);

    public Task<IReadOnlyList<DbColumn>> GetColumnsAsync(CancellationToken ct = default) =>
        QueryAsync<DbColumn>(MySqlQueries.Columns(_allDatabases), CatalogArgs, ct);

    public Task<IReadOnlyList<DbModule>> GetModulesAsync(CancellationToken ct = default) =>
        QueryAsync<DbModule>(MySqlQueries.Modules(_allDatabases), CatalogArgs, ct);

    public async Task<string?> GetDefinitionAsync(DbObject obj, CancellationToken ct = default)
    {
        var kind = obj.Type switch
        {
            DbObjectType.View => "VIEW",
            DbObjectType.Procedure => "PROCEDURE",
            DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction => "FUNCTION",
            DbObjectType.Trigger => "TRIGGER",
            DbObjectType.Sequence or DbObjectType.Table => "TABLE",
            _ => null
        };
        if (kind is null) return null;

        var schema = string.IsNullOrEmpty(obj.Schema) ? obj.Database : obj.Schema;
        await using var cn = await OpenAsync(null, ct);
        await using var cmd = new MySqlCommand($"SHOW CREATE {kind} {MySqlSql.QuoteFullName(schema, obj.Name)};", cn)
        {
            CommandTimeout = 30
        };
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if ((name.StartsWith("Create ", StringComparison.OrdinalIgnoreCase) || name == "SQL Original Statement")
                    && !reader.IsDBNull(i))
                    return reader.GetString(i);
            }
            return null;
        }
        catch (MySqlException)
        {
            // No privilege to see the body (it needs SHOW_ROUTINE/SELECT on mysql.proc or ownership).
            return null;
        }
    }

    public async Task<IReadOnlyList<DbIndex>> GetIndexesAsync(bool includePhysicalStats, CancellationToken ct = default, bool includeUsageStats = true)
    {
        var server = await GetServerAsync(ct);
        var indexes = await QueryAsync<DbIndex>(MySqlQueries.Indexes(_allDatabases, server.MariaDb), CatalogArgs, ct);
        if (!includePhysicalStats && !includeUsageStats) return indexes;

        Dictionary<(string, string, string), long?> sizes = [];
        Dictionary<(string, string, string), IndexUsage> usage = [];
        if (includePhysicalStats)
        {
            try
            {
                sizes = (await QueryAsync<IndexSize>(MySqlQueries.IndexSizes(_allDatabases), CatalogArgs, ct))
                    .GroupBy(s => (s.Database, s.Table, s.Name))
                    .ToDictionary(g => g.Key, g => g.First().SizeBytes);
            }
            catch (MySqlException) { /* no SELECT on the mysql schema: sizes stay empty */ }
        }
        if (includeUsageStats && await ScalarAsync<long>(MySqlDiagnostics.PerformanceSchemaEnabled, null, ct) == 1)
        {
            try
            {
                usage = (await QueryAsync<IndexUsage>(MySqlQueries.IndexUsage(_allDatabases), CatalogArgs, ct))
                    .GroupBy(u => (u.Database, u.Table, u.Name))
                    .ToDictionary(g => g.Key, g => g.First());
            }
            catch (MySqlException) { /* no SELECT on performance_schema */ }
        }

        return indexes.Select(i =>
        {
            var key = (i.Database, i.Table, i.Name);
            var withSize = sizes.TryGetValue(key, out var size) ? i with { SizeBytes = size } : i;
            return usage.TryGetValue(key, out var u) ? withSize with { Seeks = u.Reads, Updates = u.Writes } : withSize;
        }).ToList();
    }

    private sealed class IndexSize
    {
        public string Database { get; set; } = "";
        public string Table { get; set; } = "";
        public string Name { get; set; } = "";
        public long? SizeBytes { get; set; }
    }

    private sealed class IndexUsage
    {
        public string Database { get; set; } = "";
        public string Table { get; set; } = "";
        public string Name { get; set; } = "";
        public long? Reads { get; set; }
        public long? Writes { get; set; }
    }

    public Task<IReadOnlyList<DbForeignKey>> GetForeignKeysAsync(CancellationToken ct = default) =>
        QueryAsync<DbForeignKey>(MySqlQueries.ForeignKeys(_allDatabases), CatalogArgs, ct);

    public async Task<IReadOnlyList<DbLock>> GetLocksAsync(CancellationToken ct = default)
    {
        var server = await GetServerAsync(ct);
        try
        {
            return await QueryAsync<DbLock>(server.MariaDb ? MySqlQueries.MariaDbLocks : MySqlQueries.MySqlLocks, null, ct);
        }
        catch (MySqlException ex) when (ex.Number is 1227 or 1142)
        {
            throw new InvalidOperationException(
                "Reading locks needs the PROCESS privilege" + (server.MariaDb ? "." : " and SELECT on performance_schema.") +
                " " + ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<DbActiveRequest>> GetActiveRequestsAsync(CancellationToken ct = default)
    {
        var server = await GetServerAsync(ct);
        return await QueryAsync<DbActiveRequest>(MySqlDiagnostics.ActiveRequests(server.MariaDb), null, ct);
    }

    public async Task<IReadOnlyList<DbQueryStat>> GetTopQueriesAsync(QueryStatOrder order, int top, CancellationToken ct = default)
    {
        var server = await GetServerAsync(ct);
        if (await ScalarAsync<long>(MySqlDiagnostics.PerformanceSchemaEnabled, null, ct) != 1)
            throw new InvalidOperationException(MySqlDiagnostics.PerformanceSchemaMissing);
        var cpu = !server.MariaDb && server.Version >= new Version(8, 0, 28);
        return await QueryAsync<DbQueryStat>(MySqlDiagnostics.TopQueries(order, cpu), new { top = Math.Clamp(top, 1, 1000) }, ct);
    }

    public ColumnProfileLevel GetProfileLevel(DbColumn column) => MySqlDiagnostics.ProfileLevel(column);

    public async Task<TableProfile> ProfileTableAsync(
        DbObject table, IReadOnlyList<DbColumn> columns, int sampleRows, DataSearchOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = await OpenReadOnlyAsync(null, options, ct);
        await using var cmd = new MySqlCommand(
            MySqlDiagnostics.ProfileTable(SchemaOf(table), table.Name, columns), scope.Connection, scope.Transaction)
        {
            CommandTimeout = options.QueryTimeoutSeconds + 5
        };
        cmd.Parameters.AddWithValue("@n", Math.Max(1, sampleRows));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new TableProfile { SampleLimit = sampleRows };

        var total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        var profiles = columns.Select((c, i) =>
        {
            var o = 1 + i * 4;
            var nonNull = Convert.ToInt64(reader.GetValue(o), CultureInfo.InvariantCulture);
            return new ColumnProfile
            {
                Column = c.Name,
                DataType = c.DataType,
                NonNullCount = nonNull,
                NullCount = total - nonNull,
                DistinctCount = reader.IsDBNull(o + 1) ? null : Convert.ToInt64(reader.GetValue(o + 1), CultureInfo.InvariantCulture),
                MinValue = reader.IsDBNull(o + 2) ? null : Convert.ToString(reader.GetValue(o + 2), CultureInfo.InvariantCulture),
                MaxValue = reader.IsDBNull(o + 3) ? null : Convert.ToString(reader.GetValue(o + 3), CultureInfo.InvariantCulture)
            };
        }).ToList();

        return new TableProfile { SampledRows = total, SampleLimit = sampleRows, Columns = profiles, Elapsed = sw.Elapsed };
    }

    public async Task<IReadOnlyList<ValueFrequency>> GetTopValuesAsync(
        DbObject table, DbColumn column, int sampleRows, int top, DataSearchOptions options, CancellationToken ct = default)
    {
        await using var scope = await OpenReadOnlyAsync(null, options, ct);
        var rows = await scope.Connection.QueryAsync<ValueFrequency>(new CommandDefinition(
            MySqlDiagnostics.TopValues(SchemaOf(table), table.Name, column),
            new { n = Math.Max(1, sampleRows), top = Math.Clamp(top, 1, 1000) },
            scope.Transaction, commandTimeout: options.QueryTimeoutSeconds + 5, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>The schema that holds an object: its own, or the connection's when a caller left both empty.</summary>
    private string SchemaOf(DbObject obj) =>
        !string.IsNullOrEmpty(obj.Schema) ? obj.Schema : !string.IsNullOrEmpty(obj.Database) ? obj.Database : _profile.Database;

    public bool IsSearchable(DbColumn column, SearchTerm term)
    {
        var type = column.BaseType;
        if (MySqlSql.TextTypes.Contains(type)) return term.HasText;
        if (MySqlSql.NumericTypes.Contains(type)) return term.Number is not null;
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
        bool usesText = false, usesNumber = false;

        for (var i = 0; i < searchable.Count; i++)
        {
            var col = searchable[i];
            var q = MySqlSql.Quote(col.Name);
            string condition;
            // The usual _ci collations already compare without case, like SQL Server's defaults.
            if (MySqlSql.TextTypes.Contains(col.BaseType)) { condition = $"{q} LIKE @p ESCAPE '!'"; usesText = true; }
            else { condition = $"{q} = @n"; usesNumber = true; }

            conditions.Add(condition);
            select.Add($"({condition}) AS m{i}");
            select.Add($"LEFT(CAST({q} AS CHAR), {MaxValueLength}) AS v{i}");
        }

        for (var k = 0; k < keys.Count; k++)
            select.Add($"CAST({MySqlSql.Quote(keys[k].Name)} AS CHAR) AS k{k}");

        var schema = !string.IsNullOrEmpty(table.Schema) ? table.Schema : table.Database;
        var sql = new StringBuilder()
            .Append("SELECT ").AppendJoin(", ", select)
            .Append(" FROM ").Append(MySqlSql.QuoteFullName(schema, table.Name))
            .Append(" WHERE ").AppendJoin(" OR ", conditions)
            .Append(" LIMIT @top")
            .ToString();

        await using var scope = await OpenReadOnlyAsync(null, options, ct);
        await using var cmd = new MySqlCommand(sql, scope.Connection, scope.Transaction) { CommandTimeout = options.QueryTimeoutSeconds + 5 };
        cmd.Parameters.AddWithValue("@top", options.MaxMatchesPerTable);
        if (usesText) cmd.Parameters.AddWithValue("@p", MySqlSql.BuildPattern(term.Text, term.Mode));
        if (usesNumber) cmd.Parameters.AddWithValue("@n", term.Number!.Value);

        var results = new List<DataMatch>();
        var keyOffset = searchable.Count * 2;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var keyValues = keys.Select((key, k) => new KeyValuePair<string, string?>(
                key.Name, reader.IsDBNull(keyOffset + k) ? null : Text(reader.GetValue(keyOffset + k)))).ToList();
            string? rowKey = keys.Count == 0
                ? null
                : string.Join(", ", keyValues.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));

            for (var i = 0; i < searchable.Count; i++)
            {
                if (reader.IsDBNull(i * 2) || Convert.ToInt64(reader.GetValue(i * 2), CultureInfo.InvariantCulture) == 0) continue;
                results.Add(new DataMatch
                {
                    Database = table.Database,
                    Schema = table.Schema,
                    Table = table.Name,
                    Column = searchable[i].Name,
                    Value = reader.IsDBNull(i * 2 + 1) ? null : Text(reader.GetValue(i * 2 + 1)),
                    RowKey = rowKey,
                    KeyValues = keyValues
                });
            }
        }
        return results;
    }

    /// <summary>CAST(… AS CHAR) of a binary column comes back as bytes.</summary>
    private static string? Text(object value) => value switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<IReadOnlyList<DbRoutineParameter>> GetRoutineParametersAsync(DbObject routine, CancellationToken ct = default)
    {
        var schema = SchemaOf(routine);
        var type = routine.Type == DbObjectType.Procedure ? "PROCEDURE" : "FUNCTION";
        var rows = await QueryAsync<DbRoutineParameter>(MySqlQueries.RoutineParameters,
            new { schema, name = routine.Name, type }, ct);
        return rows.Select(p => p with { Database = string.IsNullOrEmpty(routine.Database) ? schema : routine.Database }).ToList();
    }

    public async Task<QueryExecutionResult> ExecuteScriptAsync(
        string sql, string? database, int timeoutSeconds, CancellationToken ct = default, int maxRows = int.MaxValue,
        ReadOnlyScript? readOnly = null)
    {
        var sw = Stopwatch.StartNew();
        await using var cn = await OpenAsync(database, ct);
        var result = await RunAsync(cn, null, sql, timeoutSeconds, maxRows, readOnly, ct);
        return result with { Elapsed = sw.Elapsed };
    }

    /// <summary>
    /// Runs a (multi-statement) script, keeping at most <paramref name="maxRows"/> rows per result set. A read-only
    /// script with a row limit gets sql_select_limit, so the server stops every SELECT just past the limit instead of
    /// sending (and the app discarding) every row of a large table.
    /// </summary>
    private static async Task<QueryExecutionResult> RunAsync(
        MySqlConnection cn, MySqlTransaction? tx, string sql, int timeoutSeconds, int maxRows, ReadOnlyScript? readOnly, CancellationToken ct)
    {
        var resultSets = new List<QueryResultSet>();
        var messages = new List<string>();
        var limited = readOnly is not null && maxRows is > 0 and < int.MaxValue;
        var text = MySqlSql.StripDelimiters(sql);

        void OnInfo(object? _, MySqlInfoMessageEventArgs e)
        {
            foreach (var error in e.Errors) messages.Add($"{error.Level} {(int)error.ErrorCode}: {error.Message}");
        }
        cn.InfoMessage += OnInfo;

        try
        {
            if (limited) await ExecuteNonQueryAsync(cn, tx, $"SET SESSION sql_select_limit = {(long)maxRows + 1};", ct);
            var rowsAffected = 0;
            try
            {
                await using var cmd = new MySqlCommand(text, cn, tx) { CommandTimeout = timeoutSeconds };
                rowsAffected = await ReadResultSetsAsync(cmd, maxRows, stoppedOnServer: limited, resultSets, ct);
            }
            catch (MySqlException ex) when (ex.ErrorCode != MySqlErrorCode.QueryInterrupted || !ct.IsCancellationRequested)
            {
                throw ToExecutionException(ex, sql);
            }
            finally
            {
                if (limited && cn.State == System.Data.ConnectionState.Open)
                {
                    try { await ExecuteNonQueryAsync(cn, tx, "SET SESSION sql_select_limit = DEFAULT;", CancellationToken.None); }
                    catch (MySqlException) { /* the connection is going away anyway */ }
                }
            }
            return new QueryExecutionResult { ResultSets = resultSets, RowsAffected = rowsAffected, Messages = messages };
        }
        finally
        {
            cn.InfoMessage -= OnInfo;
        }
    }

    private static async Task ExecuteNonQueryAsync(MySqlConnection cn, MySqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, cn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Reads every result set of the command, keeping at most <paramref name="maxRows"/> rows of each.</summary>
    private static async Task<int> ReadResultSetsAsync(
        MySqlCommand cmd, int maxRows, bool stoppedOnServer, List<QueryResultSet> resultSets, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        do
        {
            if (reader.FieldCount <= 0) continue;
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
            var truncated = total > maxRows;
            resultSets.Add(new QueryResultSet
            {
                Columns = columns, Rows = rows, TotalRowCount = total, IsTruncated = truncated,
                TotalRowCountIsExact = !(truncated && stoppedOnServer)
            });
        } while (await reader.NextResultAsync(ct));

        // The driver counts the affected rows of every statement in the script together.
        return Math.Max(0, reader.RecordsAffected);
    }

    private static SqlExecutionException ToExecutionException(MySqlException ex, string sql)
    {
        var message = $"{ex.Number} ({ex.SqlState}): {ex.Message}";
        if (MySqlSql.ErrorOffset(sql, ex.Message) is not int offset)
            return new SqlExecutionException(message, null, null, ex);
        var (line, column) = SqlExecutionException.LocationOf(sql, offset);
        return new SqlExecutionException($"Line {line}, column {column}: {message}", line, column, ex);
    }

    public async Task<QueryExecutionResult> ExecuteRoutineAsync(
        DbObject routine, IReadOnlyList<DbRoutineParameter> parameters, IReadOnlyDictionary<string, object?> arguments,
        int timeoutSeconds, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var name = MySqlSql.QuoteFullName(SchemaOf(routine), routine.Name);
        var ordered = parameters.OrderBy(p => p.Ordinal).ToList();

        await using var cn = await OpenAsync(null, ct);
        await using var cmd = new MySqlCommand { Connection = cn, CommandTimeout = timeoutSeconds };
        var sql = new StringBuilder();
        var outputs = new List<(DbRoutineParameter Parameter, string Variable)>();

        if (routine.Type == DbObjectType.Procedure)
        {
            // OUT and INOUT arguments go through session variables, read back after the CALL.
            var args = new List<string>();
            for (var i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                if (p.Direction == DbParameterDirection.Input)
                {
                    args.Add($"@a{i}");
                    cmd.Parameters.AddWithValue($"@a{i}", arguments.GetValueOrDefault(p.Name) ?? DBNull.Value);
                    continue;
                }
                var variable = $"@dbx_out{i}";
                outputs.Add((p, variable));
                args.Add(variable);
                if (p.Direction == DbParameterDirection.InputOutput)
                {
                    sql.Append($"SET {variable} = @a{i};\n");
                    cmd.Parameters.AddWithValue($"@a{i}", arguments.GetValueOrDefault(p.Name) ?? DBNull.Value);
                }
                else
                {
                    sql.Append($"SET {variable} = NULL;\n");
                }
            }
            sql.Append($"CALL {name}({string.Join(", ", args)});");
        }
        else
        {
            for (var i = 0; i < ordered.Count; i++)
                cmd.Parameters.AddWithValue($"@a{i}", arguments.GetValueOrDefault(ordered[i].Name) ?? DBNull.Value);
            sql.Append($"SELECT {name}({string.Join(", ", ordered.Select((_, i) => $"@a{i}"))}) AS {MySqlSql.Quote(routine.Name)};");
        }

        cmd.CommandText = sql.ToString();
        var resultSets = new List<QueryResultSet>();
        int rowsAffected;
        try
        {
            rowsAffected = await ReadResultSetsAsync(cmd, int.MaxValue, stoppedOnServer: false, resultSets, ct);
        }
        catch (MySqlException ex)
        {
            throw ToExecutionException(ex, cmd.CommandText);
        }

        var outputValues = new Dictionary<string, object?>();
        if (outputs.Count > 0)
        {
            await using var read = new MySqlCommand(
                "SELECT " + string.Join(", ", outputs.Select(o => o.Variable)) + ";", cn);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                for (var i = 0; i < outputs.Count; i++)
                    outputValues[outputs[i].Parameter.Name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
        }

        return new QueryExecutionResult
        {
            ResultSets = resultSets,
            RowsAffected = rowsAffected,
            OutputValues = outputValues,
            Elapsed = sw.Elapsed
        };
    }

    public async Task<IScriptSession> BeginScriptSessionAsync(string? database, bool transactional, CancellationToken ct = default)
    {
        var cn = await OpenAsync(database, ct);
        try
        {
            var tx = transactional ? await cn.BeginTransactionAsync(ct) : null;
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
        var server = await GetServerAsync(ct);
        var args = new { schema = SchemaOf(table), name = table.Name };
        var defaults = await QueryAsync<ColumnDefault>(MySqlQueries.ColumnDefaults, args, ct);
        var checks = await QueryAsync<NameDefinition>(MySqlQueries.CheckConstraints(server.MariaDb), args, ct);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defaults)
        {
            if (DefaultExpression(d.Definition, d.DataType, d.Extra, server.MariaDb) is { } expression)
                map[d.Name] = expression;
        }

        return new DbTableConstraints
        {
            Defaults = map,
            Checks = checks.Where(c => c.Definition is not null).Select(c => new DbCheckConstraint(c.Name, c.Definition!)).ToList()
        };
    }

    private static readonly Regex TemporalFunction = new(
        @"^(current_timestamp|now|localtime|localtimestamp|current_date|curdate|current_time|curtime)(\(\d*\))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> UnquotedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "decimal", "numeric", "float", "double", "real", "bit", "year"
    };

    /// <summary>
    /// A column default as SQL. MariaDB already reports one (quoted literals, NULL as the word NULL). MySQL reports
    /// literals bare and expressions without their parentheses, marking expressions with DEFAULT_GENERATED.
    /// </summary>
    public static string? DefaultExpression(string? reported, string dataType, string? extra, bool mariaDb)
    {
        if (reported is null) return null;
        if (mariaDb) return string.Equals(reported, "NULL", StringComparison.OrdinalIgnoreCase) ? null : reported;

        if (extra?.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase) == true)
            return TemporalFunction.IsMatch(reported) ? reported : "(" + reported + ")";
        if (UnquotedTypes.Contains(dataType) || reported.StartsWith("b'", StringComparison.OrdinalIgnoreCase))
            return reported;
        return MySqlSql.Literal(reported);
    }

    private sealed class ColumnDefault
    {
        public string Name { get; set; } = "";
        public string? Definition { get; set; }
        public string DataType { get; set; } = "";
        public string? Extra { get; set; }
    }

    public async Task<QueryResultSet> QueryReadOnlyAsync(
        string sql, string? database, DataSearchOptions options, int maxRows = 1000, CancellationToken ct = default)
    {
        await using var scope = await OpenReadOnlyAsync(database, options, ct);
        await using var cmd = new MySqlCommand(sql, scope.Connection, scope.Transaction) { CommandTimeout = options.QueryTimeoutSeconds + 5 };
        var reader = await cmd.ExecuteReaderAsync(ct);
        QueryResultSet? result = null;
        try
        {
            result = await ReadFirstResultSetAsync(reader, maxRows, ct);
            // Closing the reader would otherwise read (and drop) every remaining row: cancel the rest on the server.
            if (result.IsTruncated) cmd.Cancel();
        }
        finally
        {
            try
            {
                await reader.DisposeAsync();
            }
            catch (MySqlException ex) when (result is { IsTruncated: true } && ex.ErrorCode == MySqlErrorCode.QueryInterrupted)
            {
                // The cancellation just sent (KILL QUERY); the connection stays usable.
            }
        }
        return result;
    }

    /// <summary>The first result set with at most <paramref name="maxRows"/> rows; stops reading there.</summary>
    private static async Task<QueryResultSet> ReadFirstResultSetAsync(MySqlDataReader reader, int maxRows, CancellationToken ct)
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
        var db = string.IsNullOrEmpty(database) ? _profile.Database : database;
        if (await ScalarAsync<long>(MySqlDiagnostics.PerformanceSchemaEnabled, null, ct) != 1)
            throw new InvalidOperationException(MySqlDiagnostics.PerformanceSchemaMissing.Replace("Statement statistics", "Row change counters"));
        return await QueryAsync<TableChangeCounter>(MySqlDiagnostics.ChangeCounters, new { db }, ct);
    }

    /// <summary>MySQL keeps no per-row change position (no xmin, no rowversion).</summary>
    public Task<string?> GetChangeMarkerAsync(string? database, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public string? ChangedSincePredicate(IReadOnlyList<DbColumn> columns, string marker) => null;

    private sealed class NameDefinition
    {
        public string Name { get; set; } = "";
        public string? Definition { get; set; }
    }

    private sealed class ScriptSession(MySqlConnection connection, MySqlTransaction? transaction) : IScriptSession
    {
        private bool _committed;

        public async Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default)
        {
            await using var cmd = new MySqlCommand(MySqlSql.StripDelimiters(sql), connection, transaction) { CommandTimeout = timeoutSeconds };
            try
            {
                return Math.Max(await cmd.ExecuteNonQueryAsync(ct), 0);
            }
            catch (MySqlException ex)
            {
                throw ToExecutionException(ex, sql);
            }
        }

        public async Task<QueryExecutionResult> QueryAsync(
            string sql, int timeoutSeconds, int maxRows = int.MaxValue, CancellationToken ct = default, ReadOnlyScript? readOnly = null)
        {
            var sw = Stopwatch.StartNew();
            var result = await RunAsync(connection, transaction, sql, timeoutSeconds, maxRows, readOnly, ct);
            return result with { Elapsed = sw.Elapsed };
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            _committed = true;
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
        }
    }

    private string ConnectionString(string? database)
    {
        var db = database ?? _profile.Database;
        return _connectionStrings.GetOrAdd(db ?? "", d => MySqlSql.BuildConnectionString(_profile, d));
    }

    /// <summary>
    /// A connection to <paramref name="database"/> (or the profile's). On a read-only connection every session starts
    /// its transactions READ ONLY, so the server refuses writes too; the pool resets session state on return.
    /// </summary>
    private async Task<MySqlConnection> OpenAsync(string? database, CancellationToken ct)
    {
        var cn = new MySqlConnection(ConnectionString(database));
        try
        {
            await cn.OpenAsync(ct);
            if (_profile.ReadOnly) await ExecuteNonQueryAsync(cn, null, "SET SESSION TRANSACTION READ ONLY;", ct);
            return cn;
        }
        catch
        {
            await cn.DisposeAsync();
            throw;
        }
    }

    /// <summary>A connection inside a read-only transaction with the given timeouts; disposing rolls back.</summary>
    private async Task<ReadOnlyScope> OpenReadOnlyAsync(string? database, DataSearchOptions options, CancellationToken ct) =>
        await OpenReadOnlyAsync(database, options.QueryTimeoutSeconds * 1000, options.LockTimeoutMs, ct);

    private async Task<ReadOnlyScope> OpenReadOnlyAsync(string? database, int statementTimeoutMs, int lockTimeoutMs, CancellationToken ct)
    {
        var server = await GetServerAsync(ct);
        var cn = await OpenAsync(database, ct);
        try
        {
            await ExecuteNonQueryAsync(cn, null,
                MySqlSql.ReadOnlySettings(server.MariaDb, statementTimeoutMs, lockTimeoutMs) +
                " SET SESSION group_concat_max_len = 1048576;", ct);
            await ExecuteNonQueryAsync(cn, null, "START TRANSACTION READ ONLY;", ct);
            // The driver's transaction object is not needed: the scope rolls back with a statement.
            return new ReadOnlyScope(cn);
        }
        catch
        {
            await cn.DisposeAsync();
            throw;
        }
    }

    private sealed class ReadOnlyScope(MySqlConnection connection) : IAsyncDisposable
    {
        public MySqlConnection Connection { get; } = connection;
        public MySqlTransaction? Transaction => null;

        public async ValueTask DisposeAsync()
        {
            try { await ExecuteNonQueryAsync(Connection, null, "ROLLBACK;", CancellationToken.None); }
            catch { /* connection may already be broken */ }
            await Connection.DisposeAsync();
        }
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param, CancellationToken ct)
    {
        await using var scope = await OpenReadOnlyAsync(null, MetadataStatementTimeoutMs, MetadataLockTimeoutMs, ct);
        var rows = await scope.Connection.QueryAsync<T>(new CommandDefinition(
            sql, param, commandTimeout: MetadataStatementTimeoutMs / 1000 + 5, cancellationToken: ct));
        return rows.AsList();
    }

    private async Task<T?> ScalarAsync<T>(string sql, object? param, CancellationToken ct)
    {
        await using var scope = await OpenReadOnlyAsync(null, MetadataStatementTimeoutMs, MetadataLockTimeoutMs, ct);
        return await scope.Connection.ExecuteScalarAsync<T>(new CommandDefinition(
            sql, param, commandTimeout: MetadataStatementTimeoutMs / 1000 + 5, cancellationToken: ct));
    }
}
