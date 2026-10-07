using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// The PostgreSQL provider against a real server. Skipped unless DBEXPLORER_TEST_PG is set to "host;port;user;password"
/// of a superuser on a server where the database dbx_provider_test and the role dbx_prov_nodb may be created (they are
/// dropped again).
/// </summary>
public sealed class PostgresProviderIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_provider_test";
    private const string Login = "dbx_prov_nodb";
    private const string LoginPassword = "Dbx_prov_pw1";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");
    private PostgresProvider? _provider;

    private static ConnectionProfile Profile(string database, string? user = null, string? password = null)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = PostgresProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = user ?? p[2], Password = password ?? p[3], Database = database, Name = "test"
        };
    }

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await DropAllAsync();
        await using (var admin = await OpenAsync(Profile("postgres")))
        {
            await Exec(admin, $"CREATE DATABASE {Database}");
            await Exec(admin, $"CREATE ROLE {Login} LOGIN PASSWORD '{LoginPassword}'");
        }
        await using (var cn = await OpenAsync(Profile(Database)))
            await Exec(cn, """
                CREATE TABLE items (id integer PRIMARY KEY, name text DEFAULT 'none', CHECK (id > 0));
                INSERT INTO items (id) SELECT g FROM generate_series(1, 5) g;
                GRANT SELECT ON items TO dbx_prov_nodb;
                CREATE FUNCTION add_one(a integer) RETURNS integer LANGUAGE sql AS 'SELECT a + 1';
                CREATE FUNCTION add_one(a integer, b integer) RETURNS integer LANGUAGE sql AS 'SELECT a + b + 1';
                CREATE FUNCTION describe(u uuid, b boolean, d date) RETURNS text LANGUAGE sql
                    AS 'SELECT u::text || '' '' || b::text || '' '' || to_char(d, ''YYYY-MM-DD'')';
                CREATE FUNCTION plus(integer, integer) RETURNS integer LANGUAGE sql AS 'SELECT $1 + $2';
                CREATE FUNCTION total(VARIADIC nums integer[]) RETURNS integer LANGUAGE sql AS 'SELECT sum(x)::int FROM unnest(nums) x';
                CREATE FUNCTION scaled(a integer, factor integer DEFAULT 5) RETURNS integer LANGUAGE sql AS 'SELECT a * factor';
                CREATE PROCEDURE shift(d date, INOUT n integer, OUT next_day date) LANGUAGE plpgsql
                    AS $$ BEGIN n := n * 2; next_day := d + 1; END $$;
                """);
        _provider = new PostgresProvider(Profile(Database));
    }

    public async Task DisposeAsync()
    {
        if (Settings is null) return;
        if (_provider is not null) await _provider.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await DropAllAsync();
    }

    private static async Task DropAllAsync()
    {
        await using var admin = await OpenAsync(Profile("postgres"));
        await Exec(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
        await Exec(admin, $"DROP ROLE IF EXISTS {Login}");
    }

    private static async Task<NpgsqlConnection> OpenAsync(ConnectionProfile profile)
    {
        var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(profile));
        await cn.OpenAsync();
        return cn;
    }

    private static async Task Exec(NpgsqlConnection cn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task All_databases_connection_works_for_a_login_without_a_database_of_its_name()
    {
        Skip.If(Settings is null);
        var profile = Profile("", Login, LoginPassword);
        await using var provider = new PostgresProvider(profile);

        Assert.NotEmpty(await provider.GetServerVersionAsync());
        var objects = await provider.GetObjectsAsync();
        var items = Assert.Single(objects, o => o.Database == Database && o.Name == "items");

        var script = await provider.ExecuteScriptAsync("SELECT current_database()", null, 30);
        Assert.Equal(PostgresSql.DefaultDatabase, script.ResultSets[0].Rows[0][0]);

        await using (var session = await provider.BeginScriptSessionAsync(null, transactional: false))
        {
            var result = await session.QueryAsync("SELECT current_database()", 30);
            Assert.Equal(PostgresSql.DefaultDatabase, result.ResultSets[0].Rows[0][0]);
        }

        // An object without its database (as a single-database connection would give it) falls back too.
        var constraints = await provider.GetTableConstraintsAsync(new DbObject { Schema = "pg_catalog", Name = "pg_class", Type = DbObjectType.Table });
        Assert.Empty(constraints.Checks);
        Assert.NotNull(await provider.GetTableChangeCountersAsync(null));
        await provider.GetChangeMarkerAsync(null);
        await provider.GetDebugSupportAsync(null);
        Assert.Contains(await new PostgresProviderFactory().ListDatabasesAsync(profile), d => d == Database);

        // The table's own database is still reached through its name.
        var tableConstraints = await provider.GetTableConstraintsAsync(items);
        Assert.Single(tableConstraints.Checks);
    }

    private async Task<QueryExecutionResult> RunAsync(string name, DbObjectType type, params (string Name, object? Value)[] args)
    {
        var routine = new DbObject { Database = Database, Schema = "public", Name = name, Type = type };
        var parameters = await _provider!.GetRoutineParametersAsync(routine);
        return await _provider.ExecuteRoutineAsync(routine, parameters, args.ToDictionary(a => a.Name, a => a.Value), 30);
    }

    [SkippableFact]
    public async Task Routine_arguments_are_cast_to_the_declared_types()
    {
        Skip.If(Settings is null);

        var described = await RunAsync("describe", DbObjectType.Function,
            ("u", "6f9619ff-8b86-d011-b42d-00c04fc964ff"), ("b", "true"), ("d", "2024-02-29"));
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff true 2024-02-29", described.ResultSets[0].Rows[0][0]);

        var plus = await RunAsync("plus", DbObjectType.Function, ("$1", "2"), ("$2", "3"));
        Assert.Equal(5, plus.ResultSets[0].Rows[0][0]);

        var total = await RunAsync("total", DbObjectType.Function, ("nums", "{1,2,3}"));
        Assert.Equal(6, total.ResultSets[0].Rows[0][0]);

        var withNull = await RunAsync("scaled", DbObjectType.Function, ("a", "4"), ("factor", null));
        Assert.Null(withNull.ResultSets[0].Rows[0][0]);
    }

    [SkippableFact]
    public async Task Routine_parameters_describe_one_overload_with_names_types_and_defaults()
    {
        Skip.If(Settings is null);
        var overloaded = await _provider!.GetRoutineParametersAsync(
            new DbObject { Database = Database, Schema = "public", Name = "add_one", Type = DbObjectType.Function });
        var only = Assert.Single(overloaded);
        Assert.Equal(("a", "integer"), (only.Name, only.DataType));
        var added = await RunAsync("add_one", DbObjectType.Function, ("a", "41"));
        Assert.Equal(42, added.ResultSets[0].Rows[0][0]);

        var unnamed = await _provider.GetRoutineParametersAsync(
            new DbObject { Database = Database, Schema = "public", Name = "plus", Type = DbObjectType.Function });
        Assert.Equal(["$1", "$2"], unnamed.Select(p => p.Name));

        var scaled = await _provider.GetRoutineParametersAsync(
            new DbObject { Database = Database, Schema = "public", Name = "scaled", Type = DbObjectType.Function });
        Assert.Equal([false, true], scaled.Select(p => p.HasDefault));

        var variadic = await _provider.GetRoutineParametersAsync(
            new DbObject { Database = Database, Schema = "public", Name = "total", Type = DbObjectType.Function });
        Assert.Equal("VARIADIC integer[]", Assert.Single(variadic).DataType);
    }

    [SkippableFact]
    public async Task Procedures_get_their_out_parameters_in_the_call()
    {
        Skip.If(Settings is null);
        var parameters = await _provider!.GetRoutineParametersAsync(
            new DbObject { Database = Database, Schema = "public", Name = "shift", Type = DbObjectType.Procedure });
        Assert.Equal(
            [DbParameterDirection.Input, DbParameterDirection.InputOutput, DbParameterDirection.Output],
            parameters.Select(p => p.Direction));

        var result = await RunAsync("shift", DbObjectType.Procedure, ("d", "2024-12-31"), ("n", "21"));
        var row = result.ResultSets[0].Rows[0];
        Assert.Equal(42, row[0]);
        Assert.Equal(new DateTime(2025, 1, 1), Convert.ToDateTime(row[1]));
    }
}
