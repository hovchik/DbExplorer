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

    private readonly string _metaConnectionString;
    private readonly string _searchConnectionString;

    public SqlServerProvider(ConnectionProfile profile)
    {
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
        QueryAsync<DbObject>(SqlServerQueries.Objects, null, ct);

    public Task<IReadOnlyList<DbColumn>> GetColumnsAsync(CancellationToken ct = default) =>
        QueryAsync<DbColumn>(SqlServerQueries.Columns, null, ct);

    public Task<IReadOnlyList<DbModule>> GetModulesAsync(CancellationToken ct = default) =>
        QueryAsync<DbModule>(SqlServerQueries.Modules, null, ct);

    public Task<string?> GetDefinitionAsync(DbObject obj, CancellationToken ct = default)
    {
        var sql = obj.Type switch
        {
            DbObjectType.Synonym => SqlServerQueries.SynonymDefinition,
            DbObjectType.Sequence => SqlServerQueries.SequenceDefinition,
            _ => SqlServerQueries.ObjectDefinition
        };
        return ScalarAsync<string?>(sql, new { name = SqlServerSql.QuoteFullName(obj.Schema, obj.Name) }, ct);
    }

    public async Task<IReadOnlyList<DbIndex>> GetIndexesAsync(bool includePhysicalStats, CancellationToken ct = default)
    {
        try
        {
            return await QueryAsync<DbIndex>(SqlServerQueries.Indexes(true, includePhysicalStats), null, ct);
        }
        catch (SqlException ex) when (ex.Number is 297 or 300)
        {
            // No VIEW SERVER STATE / VIEW DATABASE STATE: return the catalog part only.
            return await QueryAsync<DbIndex>(SqlServerQueries.Indexes(false, false), null, ct);
        }
    }

    public Task<IReadOnlyList<DbLock>> GetLocksAsync(CancellationToken ct = default) =>
        QueryAsync<DbLock>(SqlServerQueries.Locks, null, ct);

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

        await using var cn = new SqlConnection(_searchConnectionString);
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
}
