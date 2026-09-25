using System.Data;
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
    private const int MaxParallelDatabases = 4;

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

    public async Task<IReadOnlyList<DbIndex>> GetIndexesAsync(bool includePhysicalStats, CancellationToken ct = default)
    {
        async Task<IReadOnlyList<DbIndex>> QueryOneAsync(string? database, CancellationToken token)
        {
            try
            {
                return await QueryInDatabaseAsync<DbIndex>(SqlServerQueries.Indexes(true, includePhysicalStats), null, database, token);
            }
            catch (SqlException ex) when (ex.Number is 297 or 300)
            {
                // No VIEW SERVER STATE / VIEW DATABASE STATE: return the catalog part only.
                return await QueryInDatabaseAsync<DbIndex>(SqlServerQueries.Indexes(false, false), null, database, token);
            }
        }

        if (!_allDatabases)
            return await QueryOneAsync(_profile.Database, ct);

        var results = new List<DbIndex>();
        foreach (var db in await GetAccessibleDatabasesAsync(ct))
        {
            try
            {
                var rows = await QueryOneAsync(db, ct);
                results.AddRange(rows.Select(i => i with { Database = db }));
            }
            catch
            {
                // Inaccessible database: skip it and keep going.
            }
        }
        return results;
    }

    public Task<IReadOnlyList<DbLock>> GetLocksAsync(CancellationToken ct = default) =>
        QueryAsync<DbLock>(SqlServerQueries.Locks(_allDatabases), null, ct);

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
            string? rowKey = null;
            if (keys.Count > 0)
            {
                rowKey = string.Join(", ", keys.Select((key, k) =>
                    $"{key.Name}={(reader.IsDBNull(keyOffset + k) ? "NULL" : reader.GetString(keyOffset + k))}"));
            }

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
                    RowKey = rowKey
                });
            }
        }

        return results;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
                var cs = SqlServerSql.BuildConnectionString(_profile, SqlServerSql.MetaAppName, db);
                var rows = await QueryWithConnectionStringAsync<T>(sql, null, cs, token);
                foreach (var row in rows) results.Add(tag(row, db));
            }
            catch
            {
                // Inaccessible or unreadable database (permissions, offline, etc.): skip it.
            }
        });

        return results.ToList();
    }
}
