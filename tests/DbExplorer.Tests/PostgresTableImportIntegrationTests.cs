using DbExplorer.Application.Design;
using DbExplorer.Application.Import;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// CSV import into a real PostgreSQL server, through COPY and through the INSERT batches the other engines use.
/// Skipped unless DBEXPLORER_TEST_PG is set to "host;port;user;password" of a server where the user may create the
/// database dbx_import_test.
/// </summary>
public sealed class PostgresTableImportIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_import_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");

    private ConnectionProfile? _profile;
    private PostgresProvider? _provider;

    private static ConnectionProfile Profile(string database)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = PostgresProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test"
        };
    }

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await using (var admin = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile("postgres"))))
        {
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
            await Exec(admin, $"CREATE DATABASE {Database}");
        }
        _profile = Profile(Database);
        await using (var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(_profile)))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE TABLE people (
                    id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    name varchar(20) NOT NULL,
                    born date,
                    score numeric(6,2),
                    active boolean NOT NULL DEFAULT true,
                    note text);
                INSERT INTO people (name) VALUES ('existing');
                """);
        }
        _provider = new PostgresProvider(_profile);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }

    private static async Task Exec(NpgsqlConnection cn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(_profile!));
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, cn);
        return await cmd.ExecuteScalarAsync();
    }

    private static readonly DbObject People = new() { Schema = "public", Name = "people", Type = DbObjectType.Table };

    private ImportPlan PeoplePlan(IReadOnlyList<string> headers)
    {
        DbColumn[] columns =
        [
            new() { Name = "id", DataType = "integer", BaseType = "int4", Ordinal = 1, IsIdentity = true },
            new() { Name = "name", DataType = "varchar(20)", BaseType = "varchar", Ordinal = 2 },
            new() { Name = "born", DataType = "date", BaseType = "date", Ordinal = 3 },
            new() { Name = "score", DataType = "numeric(6,2)", BaseType = "numeric", Ordinal = 4 },
            new() { Name = "active", DataType = "boolean", BaseType = "bool", Ordinal = 5 },
            new() { Name = "note", DataType = "text", BaseType = "text", Ordinal = 6 }
        ];
        return new ImportPlan { Table = People, Columns = TableImport.MapByName(headers, columns, byPosition: false) };
    }

    private static readonly string[][] Rows =
    [
        ["Ann", "1990-05-06", "12.5", "yes", "said \"hi\", left"],
        ["Bob", "", "", "no", ""],
        ["Cleo", "2001-02-03", "1,234.25", "true", "multi\nline"]
    ];

    /// <summary>The same provider, but its sessions hide COPY, so the import takes the INSERT path of SQL Server and MySQL.</summary>
    private sealed class InsertOnly(IDatabaseProvider inner) : IDatabaseProvider
    {
        public string ProviderKey => inner.ProviderKey;
        public string QuoteIdentifier(string identifier) => inner.QuoteIdentifier(identifier);
        public async Task<IScriptSession> BeginScriptSessionAsync(string? database, bool transactional, CancellationToken ct = default) =>
            new Session(await inner.BeginScriptSessionAsync(database, transactional, ct));
        private sealed class Session(IScriptSession s) : IScriptSession
        {
            public Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default) => s.ExecuteAsync(sql, timeoutSeconds, ct);
            public Task<QueryExecutionResult> QueryAsync(string sql, int timeoutSeconds, int maxRows = int.MaxValue, CancellationToken ct = default, ReadOnlyScript? readOnly = null) =>
                s.QueryAsync(sql, timeoutSeconds, maxRows, ct, readOnly);
            public Task CommitAsync(CancellationToken ct = default) => s.CommitAsync(ct);
            public Task RollbackAsync(CancellationToken ct = default) => s.RollbackAsync(ct);
            public ValueTask DisposeAsync() => s.DisposeAsync();
        }
        public Task<string> GetServerVersionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbObject>> GetObjectsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbColumn>> GetColumnsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbModule>> GetModulesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetDefinitionAsync(DbObject obj, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbIndex>> GetIndexesAsync(bool includePhysicalStats, CancellationToken ct = default, bool includeUsageStats = true) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbForeignKey>> GetForeignKeysAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbLock>> GetLocksAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbActiveRequest>> GetActiveRequestsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbQueryStat>> GetTopQueriesAsync(QueryStatOrder order, int top, CancellationToken ct = default) => throw new NotSupportedException();
        public ColumnProfileLevel GetProfileLevel(DbColumn column) => throw new NotSupportedException();
        public Task<TableProfile> ProfileTableAsync(DbObject table, IReadOnlyList<DbColumn> columns, int sampleRows, Core.Search.DataSearchOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ValueFrequency>> GetTopValuesAsync(DbObject table, DbColumn column, int sampleRows, int top, Core.Search.DataSearchOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public bool IsSearchable(DbColumn column, Core.Search.SearchTerm term) => throw new NotSupportedException();
        public Task<IReadOnlyList<DataMatch>> SearchTableAsync(Core.Search.DbTableTarget table, Core.Search.SearchTerm term, Core.Search.DataSearchOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbRoutineParameter>> GetRoutineParametersAsync(DbObject routine, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QueryExecutionResult> ExecuteScriptAsync(string sql, string? database, int timeoutSeconds, CancellationToken ct = default, int maxRows = int.MaxValue, ReadOnlyScript? readOnly = null) => throw new NotSupportedException();
        public Task<QueryExecutionResult> ExecuteRoutineAsync(DbObject routine, IReadOnlyList<DbRoutineParameter> parameters, IReadOnlyDictionary<string, object?> arguments, int timeoutSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DbTableConstraints> GetTableConstraintsAsync(DbObject table, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QueryResultSet> QueryReadOnlyAsync(string sql, string? database, Core.Search.DataSearchOptions options, int maxRows = 1000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TableChangeCounter>> GetTableChangeCountersAsync(string? database, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetChangeMarkerAsync(string? database, CancellationToken ct = default) => throw new NotSupportedException();
        public string? ChangedSincePredicate(IReadOnlyList<DbColumn> columns, string marker) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Imports_rows_with_types_nulls_quotes_and_line_breaks(bool copy)
    {
        Skip.If(Settings is null);
        IDatabaseProvider provider = copy ? _provider! : new InsertOnly(_provider!);
        var plan = PeoplePlan(["name", "born", "score", "active", "note"]);

        var result = await TableImport.RunAsync(provider, plan, Rows, 2, 30);

        Assert.Equal(3, result.Imported);
        Assert.Equal(copy, result.BulkLoaded);
        Assert.Equal(4L, await Scalar("SELECT count(*) FROM people"));
        Assert.Equal("said \"hi\", left", await Scalar("SELECT note FROM people WHERE name = 'Ann'"));
        Assert.Equal("multi\nline", await Scalar("SELECT note FROM people WHERE name = 'Cleo'"));
        Assert.Equal(1234.25m, await Scalar("SELECT score FROM people WHERE name = 'Cleo'"));
        Assert.Equal(new DateOnly(1990, 5, 6), DateOnly.FromDateTime((DateTime)(await Scalar("SELECT born FROM people WHERE name = 'Ann'"))!));
        Assert.Equal(false, await Scalar("SELECT active FROM people WHERE name = 'Bob'"));
        Assert.Equal(DBNull.Value, await Scalar("SELECT note FROM people WHERE name = 'Bob'"));
        Assert.Equal(DBNull.Value, await Scalar("SELECT born FROM people WHERE name = 'Bob'"));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Writes_identity_values_and_can_replace_existing_rows(bool copy)
    {
        Skip.If(Settings is null);
        IDatabaseProvider provider = copy ? _provider! : new InsertOnly(_provider!);
        var plan = PeoplePlan(["id", "name"]) with { DeleteExisting = true };

        await TableImport.RunAsync(provider, plan, [["100", "x"], ["101", "y"]], 2, 30);

        Assert.Equal(2L, await Scalar("SELECT count(*) FROM people"));
        Assert.Equal(201L, await Scalar("SELECT sum(id) FROM people"));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refused_row_rolls_back_everything_and_is_named(bool copy)
    {
        Skip.If(Settings is null);
        IDatabaseProvider provider = copy ? _provider! : new InsertOnly(_provider!);
        // The second data row (file row 3) has no name: NOT NULL refuses it on the server. Delete first, so a rollback shows.
        var plan = PeoplePlan(["name", "note"]) with { DeleteExisting = true, Values = new ImportValueOptions { EmptyIsNull = true } };

        var ex = await Assert.ThrowsAsync<ImportFailedException>(() =>
            TableImport.RunAsync(provider, plan, [["a", "1"], ["", "2"], ["c", "3"]], 2, 30));

        Assert.StartsWith("Nothing was imported.", ex.Message);
        Assert.Contains(copy ? "Row 3 or near it" : "one of rows 2–4", ex.Message);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM people"));
        Assert.Equal("existing", await Scalar("SELECT name FROM people"));
    }

    [SkippableFact]
    public async Task Rows_that_do_not_fit_stop_the_import_unless_skipped()
    {
        Skip.If(Settings is null);
        var plan = PeoplePlan(["name", "score"]);
        string[][] rows = [["ok", "1"], ["bad", "lots"], ["also ok", "2"]];

        var ex = await Assert.ThrowsAsync<ImportFailedException>(() => TableImport.RunAsync(_provider!, plan, rows, 2, 30));
        Assert.Contains("Row 3, score: 'lots' is not a number", ex.Message);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM people"));

        var result = await TableImport.RunAsync(_provider!, plan with { SkipInvalidRows = true }, rows, 2, 30);
        Assert.Equal((2L, 1L), (result.Imported, result.Skipped));
        Assert.Equal(3L, await Scalar("SELECT count(*) FROM people"));
    }

    [SkippableFact]
    public async Task Creates_a_new_table_from_guessed_types()
    {
        Skip.If(Settings is null);
        var options = new ImportValueOptions();
        string[] headers = ["Order Id", "placed", "total", "paid", "customer"];
        string[][] rows = [["1", "2024-01-02", "10.50", "true", "Acme"], ["2", "2024-01-03", "7", "false", "Globex"]];
        var columns = headers.Select((h, i) => ImportValues.GuessColumn(h, rows.Select(r => r[i]), "Postgres", options)).ToList();
        var design = new TableDesign { Schema = "staging", Name = "orders", Columns = columns };
        var script = TableCreator.Script(design, DesignContext.Empty("Postgres"));
        var plan = new ImportPlan
        {
            Table = new DbObject { Schema = "staging", Name = "orders", Type = DbObjectType.Table },
            Columns = columns.Select((c, i) => new ImportColumn(i, c.Name, c.FullType)).ToList(),
            Create = design,
            CreateScript = script
        };

        var result = await TableImport.RunAsync(_provider!, plan, rows, 2, 30);

        Assert.Equal(2, result.Imported);
        Assert.Contains("CREATE SCHEMA", script);
        Assert.Equal(17.5m, await Scalar("SELECT sum(total) FROM staging.orders"));
        Assert.Equal("date", await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name = 'orders' AND column_name = 'placed'"));
        Assert.Equal("boolean", await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name = 'orders' AND column_name = 'paid'"));
    }

    [SkippableFact]
    public async Task A_failed_import_into_a_new_table_leaves_no_table()
    {
        Skip.If(Settings is null);
        var design = new TableDesign { Schema = "public", Name = "tiny", Columns = [new ColumnDesign { Name = "code", Type = "varchar", Size = "2", IsNullable = false }] };
        var plan = new ImportPlan
        {
            Table = new DbObject { Schema = "public", Name = "tiny", Type = DbObjectType.Table },
            Columns = [new ImportColumn(0, "code", "varchar(2)")],
            Create = design,
            CreateScript = TableCreator.Script(design, DesignContext.Empty("Postgres") )
        };

        await Assert.ThrowsAsync<ImportFailedException>(() => TableImport.RunAsync(_provider!, plan, [["ab"], [""]], 2, 30));

        Assert.Equal(0L, await Scalar("SELECT count(*) FROM information_schema.tables WHERE table_name = 'tiny'"));
    }

    [SkippableFact]
    public async Task Cancelling_rolls_back()
    {
        Skip.If(Settings is null);
        using var cts = new CancellationTokenSource();
        IEnumerable<string[]> Endless()
        {
            for (var i = 0; ; i++)
            {
                if (i == 20_000) cts.Cancel();
                yield return [$"n{i}"];
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TableImport.RunAsync(_provider!, PeoplePlan(["name"]), Endless(), 2, 30, ct: cts.Token));

        Assert.Equal(1L, await Scalar("SELECT count(*) FROM people"));
    }
}
