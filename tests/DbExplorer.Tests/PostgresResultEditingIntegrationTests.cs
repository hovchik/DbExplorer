using DbExplorer.Application;
using DbExplorer.Application.Export;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// Editing query results and following foreign keys against a real PostgreSQL server. Skipped unless DBEXPLORER_TEST_PG
/// is set to "host;port;user;password" of a server where the user may create the database dbx_edit_test.
/// </summary>
public sealed class PostgresResultEditingIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_edit_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-edit-" + Guid.NewGuid().ToString("N"));
    private DatabaseSession? _session;
    private ConnectionProfile? _profile;

    private static ConnectionProfile Profile(string database)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = PostgresProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test"
        };
    }

    private string ConnectionString => PostgresSql.BuildConnectionString(_profile!);

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await using (var admin = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile("postgres"))))
        {
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
            await Exec(admin, $"CREATE DATABASE {Database}");
            // Not UTC, so a timestamptz edit that lost its offset would land hours off.
            await Exec(admin, $"ALTER DATABASE {Database} SET timezone = 'America/New_York'");
        }

        _profile = Profile(Database);
        await using (var cn = new NpgsqlConnection(ConnectionString))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE TABLE customers (id int PRIMARY KEY, name text NOT NULL, vip boolean NOT NULL DEFAULT false);
                CREATE TABLE "Orders" (
                    "OrderId" int PRIMARY KEY,
                    customer_id int REFERENCES customers (id),
                    total numeric(10,2),
                    placed_at timestamptz,
                    token uuid,
                    note text);
                CREATE TABLE notes (id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, body text NOT NULL, created date NOT NULL DEFAULT DATE '2020-01-01');
                INSERT INTO notes (body) VALUES ('first'), ('second');
                INSERT INTO customers VALUES (1, 'Acme', false), (2, 'Globex', true);
                INSERT INTO "Orders" VALUES
                    (10, 1, 100.50, '2024-01-02 03:04:05+00', '7f1c0e5a-1111-4c3b-9a55-0123456789ab', NULL),
                    (11, 2, 7.25, NULL, NULL, 'it''s here');
                """);
        }

        var paths = new AppPaths(_root);
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        var provider = new PostgresProvider(_profile);
        var snapshot = await metadata.LoadAsync(_profile, provider, forceRefresh: true);
        _session = new DatabaseSession(_profile, new PostgresProviderFactory(), provider, await provider.GetServerVersionAsync(), snapshot);
    }

    public async Task DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static async Task Exec(NpgsqlConnection cn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var cn = new NpgsqlConnection(ConnectionString);
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, cn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<(QueryResultSetLike Result, ResultSource Source)> QueryAsync(string sql)
    {
        var result = await _session!.Provider.ExecuteScriptAsync(sql, null, 30);
        var sources = ResultSourceResolver.ResolveScript(sql, result.ResultSets.Select(r => r.Columns).ToList(),
            _session.Snapshot, _session.Provider.ProviderKey, null);
        var rs = Assert.Single(result.ResultSets);
        return (new QueryResultSetLike(rs.Columns, rs.Rows), sources[0] ?? throw new Xunit.Sdk.XunitException("no source"));
    }

    private sealed record QueryResultSetLike(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

    private async Task<int> ApplyAsync(ResultSource source, params ResultRowEdit[] edits)
    {
        var updates = ResultEditSql.BuildUpdates(source, edits, SqlDialect.Postgres, _session!.Provider.QuoteIdentifier);
        await using var tx = await _session.Provider.BeginScriptSessionAsync(null, transactional: true);
        var affected = 0;
        foreach (var u in updates) affected += await tx.ExecuteAsync(u.Sql, 30);
        await tx.CommitAsync();
        return affected;
    }

    [SkippableFact]
    public async Task Typed_edits_of_a_query_result_are_written_back_by_key()
    {
        Skip.If(Settings is null);
        var (result, source) = await QueryAsync("""SELECT o.*, c.name FROM "Orders" o JOIN customers c ON c.id = o.customer_id ORDER BY o."OrderId";""");

        Assert.True(source.CanEdit(result.Columns.ToList().IndexOf("total")));
        Assert.False(source.CanEdit(result.Columns.ToList().IndexOf("name"))); // customers.id itself is not in the result
        int Col(string name) => result.Columns.ToList().IndexOf(name);

        var first = result.Rows[0];
        var edits = new Dictionary<int, object?>
        {
            [Col("total")] = ResultEditSql.ParseValue("99.95", first[Col("total")], null),
            [Col("placed_at")] = ResultEditSql.ParseValue("2025-06-07 08:09:10", first[Col("placed_at")], null),
            [Col("token")] = ResultEditSql.ParseValue("00000000-0000-0000-0000-000000000001", first[Col("token")], null),
            [Col("note")] = ResultEditSql.ParseValue("O'Reilly", first[Col("note")], null),
        };
        Assert.Equal(1, await ApplyAsync(source, new ResultRowEdit(first, edits)));

        Assert.Equal(99.95m, await Scalar("""SELECT total FROM "Orders" WHERE "OrderId" = 10"""));
        Assert.Equal("O'Reilly", await Scalar("""SELECT note FROM "Orders" WHERE "OrderId" = 10"""));
        Assert.Equal("00000000-0000-0000-0000-000000000001", (await Scalar("""SELECT token::text FROM "Orders" WHERE "OrderId" = 10"""))?.ToString());
        // The timestamp is UTC as shown, whatever the session time zone is.
        Assert.Equal("2025-06-07 08:09:10", await Scalar("""SELECT to_char(placed_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM "Orders" WHERE "OrderId" = 10"""));

        // NULL and a NULL cell typed from the column's type; other row untouched.
        var second = result.Rows[1];
        Assert.Equal(1, await ApplyAsync(source, new ResultRowEdit(second, new Dictionary<int, object?>
        {
            [Col("note")] = null,
            [Col("placed_at")] = ResultEditSql.ParseValue("2024-12-31", second[Col("placed_at")], source.ColumnSource(Col("placed_at"))!.Column)
        })));
        Assert.Equal(DBNull.Value, await Scalar("""SELECT note FROM "Orders" WHERE "OrderId" = 11"""));
        Assert.Equal(99.95m, await Scalar("""SELECT total FROM "Orders" WHERE "OrderId" = 10"""));
    }

    [SkippableFact]
    public async Task Editing_the_key_matches_the_row_by_its_original_key_and_a_missing_row_affects_nothing()
    {
        Skip.If(Settings is null);
        var (result, source) = await QueryAsync("""SELECT "OrderId", note FROM "Orders" WHERE "OrderId" = 11""");
        var row = Assert.Single(result.Rows);

        Assert.Equal(1, await ApplyAsync(source, new ResultRowEdit(row, new Dictionary<int, object?> { [0] = 12 })));
        Assert.Equal(1L, await Scalar("""SELECT count(*) FROM "Orders" WHERE "OrderId" = 12"""));

        // The row as read (key 11) is gone now: the UPDATE matches nothing, which the app reports as a conflict.
        Assert.Equal(0, await ApplyAsync(source, new ResultRowEdit(row, new Dictionary<int, object?> { [1] = "late" })));
    }

    [SkippableFact]
    public async Task A_foreign_key_value_opens_the_referenced_row()
    {
        Skip.If(Settings is null);
        var (result, source) = await QueryAsync("""SELECT * FROM "Orders" WHERE "OrderId" = 11""");
        var column = result.Columns.ToList().IndexOf("customer_id");
        var reference = source.ReferenceOf(column)!;
        Assert.Equal("public.customers", reference.TargetName);

        var sql = ResultEditSql.ReferenceSelect(source, reference, result.Rows[0], SqlDialect.Postgres, _session!.Provider.QuoteIdentifier)!;
        var referenced = await _session.Provider.ExecuteScriptAsync(sql, null, 30);
        var customer = Assert.Single(Assert.Single(referenced.ResultSets).Rows);
        Assert.Equal("Globex", customer[1]);
    }

    [SkippableFact]
    public async Task New_rows_are_inserted_with_defaults_read_back_and_deleted_rows_go_by_key()
    {
        Skip.If(Settings is null);
        var (result, source) = await QueryAsync("SELECT * FROM notes ORDER BY id");
        Assert.Equal("notes", source.RowTable?.Table.Name);
        int Col(string name) => result.Columns.ToList().IndexOf(name);
        Assert.False(source.CanSetInNewRow(Col("id"))); // identity: the database sets it
        Assert.True(source.CanSetInNewRow(Col("body")));

        var changes = ResultEditSql.BuildChanges(source,
            deleted: [result.Rows[0]],
            edited: [],
            added: [new Dictionary<int, object?> { [Col("body")] = "it's new" }],
            SqlDialect.Postgres, _session!.Provider.QuoteIdentifier);

        IReadOnlyList<object?>? stored = null;
        await using (var tx = await _session.Provider.BeginScriptSessionAsync(null, transactional: true))
        {
            Assert.Equal(1, await tx.ExecuteAsync(changes[0].Sql, 30));
            var inserted = await tx.QueryAsync(changes[1].Sql, 30);
            stored = Assert.Single(Assert.Single(inserted.ResultSets).Rows);
            await tx.CommitAsync();
        }

        // RETURNING gives the generated key and the default, in the columns the result shows them in.
        Assert.Equal([[Col("id")], [Col("body")], [Col("created")]], changes[1].ReturnedColumns.Select(c => c.ToArray()));
        Assert.Equal(3, stored[0]);
        Assert.Equal("it's new", stored[1]);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM notes WHERE id = 3 AND created = DATE '2020-01-01'"));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM notes WHERE id = 1"));
    }
}
