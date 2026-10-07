using DbExplorer.Core.Connections;
using DbExplorer.Core.Debugging;
using DbExplorer.Core.Models;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// The routine debugger against a real PostgreSQL server with pldebugger (plugin_debugger preloaded). Skipped unless
/// DBEXPLORER_TEST_PG is set to "host;port;user;password" of a superuser who may create the database dbx_debug_test.
/// </summary>
[Collection(nameof(DebuggerCollection))]
public sealed class PostgresDebuggerIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_debug_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");

    private PostgresProvider? _provider;
    private bool _debuggerInstalled;

    private static ConnectionProfile Profile(string database, bool readOnly = false)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = PostgresProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test", ReadOnly = readOnly
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
            await using var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pldbgapi')", admin);
            _debuggerInstalled = (bool)(await check.ExecuteScalarAsync())!;
        }

        await using (var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile(Database))))
        {
            await cn.OpenAsync();
            await Exec(cn, """
                CREATE TABLE audit (id int PRIMARY KEY, note text);
                CREATE FUNCTION helper(x int) RETURNS int LANGUAGE plpgsql AS $$
                begin
                  x := x * 10;
                  return x;
                end $$;
                CREATE FUNCTION add_one(a int, note text DEFAULT 'none') RETURNS int LANGUAGE plpgsql AS $$
                declare
                  r int := 0;
                begin
                  r := a + 1;
                  insert into audit values (r, note);
                  raise notice 'inserted %', r;
                  r := helper(r);
                  return r;
                end $$;
                CREATE PROCEDURE bump(INOUT n int) LANGUAGE plpgsql AS $$
                begin
                  n := n + 1;
                end $$;
                CREATE FUNCTION slow() RETURNS void LANGUAGE plpgsql AS $$
                begin
                  insert into audit values (99, 'slow');
                  perform pg_sleep(60);
                end $$;
                CREATE FUNCTION plain_sql(x int) RETURNS int LANGUAGE sql AS 'SELECT x';
                """);
            if (_debuggerInstalled) await Exec(cn, "CREATE EXTENSION pldbgapi");
        }
        _provider = new PostgresProvider(Profile(Database));
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        if (Settings is null) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile("postgres")));
        await admin.OpenAsync();
        await Exec(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
    }

    private static async Task Exec(NpgsqlConnection cn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> AuditRows()
    {
        await using var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile(Database)));
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit", cn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<DebugTarget> Target(string name) =>
        Assert.Single(await _provider!.GetDebugTargetsAsync(new DbObject { Schema = "public", Name = name, Type = DbObjectType.Function }));

    private static int LineOf(DebugTarget target, string text) =>
        target.Source.Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains(text)).i + 1;

    [SkippableFact]
    public async Task Steps_through_a_function_and_rolls_back()
    {
        Skip.If(Settings is null || !_debuggerInstalled, "DBEXPLORER_TEST_PG not set or pldebugger not installed");
        Assert.True((await _provider!.GetDebugSupportAsync(Database)).IsAvailable);

        var target = await Target("add_one");
        Assert.Null(target.Problem);
        Assert.Equal(["integer", "text"], target.Parameters.Select(p => p.DataType));
        Assert.True(target.Parameters[1].HasDefault);

        var helper = await Target("helper");
        await using var session = await _provider.StartDebugAsync(target, ["5", DebugArguments.DefaultArgument], [], commit: false);
        var messages = new List<string>();
        session.MessageReceived += (_, m) => { lock (messages) messages.Add(m); };

        Assert.Equal(LineOf(target, "r := a + 1"), session.Position.Top!.Line);
        var variables = await session.GetVariablesAsync(0);
        Assert.Equal("5", variables.Single(v => v.Name == "a").Value);
        Assert.Equal(DebugVariableKind.Argument, variables.Single(v => v.Name == "a").Kind);
        Assert.Equal("0", variables.Single(v => v.Name == "r").Value);
        Assert.Equal("integer", variables.Single(v => v.Name == "r").DataType);

        var p = await session.StepOverAsync();
        Assert.Equal(LineOf(target, "insert into"), p.Top!.Line);
        Assert.Equal("6", (await session.GetVariablesAsync(0)).Single(v => v.Name == "r").Value);

        // A breakpoint inside the called function stops a continue there.
        Assert.True(await session.AddBreakpointAsync(new DebugBreakpoint(helper.Id, LineOf(helper, "return x"))));
        p = await session.ContinueAsync();
        Assert.Equal(2, p.Depth);
        Assert.Equal(helper.Id, p.Top!.RoutineId);
        Assert.Equal(LineOf(helper, "return x"), p.Top.Line);
        Assert.Equal("60", (await session.GetVariablesAsync(0)).Single(v => v.Name == "x").Value);
        Assert.Equal("6", (await session.GetVariablesAsync(1)).Single(v => v.Name == "r").Value);
        Assert.Contains("x * 10", await session.GetSourceAsync(helper.Id));

        // Step over at the last line of the called function comes back to the caller instead of running to the end.
        p = await session.StepOverAsync();
        Assert.Equal(1, p.Depth);
        Assert.Equal(LineOf(target, "return r"), p.Top!.Line);

        Assert.True(await session.SetVariableAsync("r", "1000"));
        Assert.False(await session.SetVariableAsync("r", "not a number"));

        p = await session.ContinueAsync();
        Assert.True(p.IsFinished);
        var outcome = await session.Outcome;
        Assert.Null(outcome.Error);
        Assert.False(outcome.Committed);
        Assert.Equal(1000, Assert.Single(Assert.Single(outcome.ResultSets).Rows)[0]);
        Assert.Contains(outcome.Messages, m => m.Contains("inserted 6"));
        lock (messages) Assert.Contains(messages, m => m.Contains("inserted 6"));
        Assert.Equal(0, await AuditRows());
    }

    [SkippableFact]
    public async Task Step_into_and_out_of_a_called_function()
    {
        Skip.If(Settings is null || !_debuggerInstalled, "DBEXPLORER_TEST_PG not set or pldebugger not installed");
        var target = await Target("add_one");
        await using var session = await _provider!.StartDebugAsync(target, ["1", "x"], [new DebugBreakpoint(target.Id, LineOf(target, "r := helper"))], commit: false);

        var p = await session.ContinueAsync();
        Assert.Equal(LineOf(target, "r := helper"), p.Top!.Line);
        p = await session.StepIntoAsync();
        Assert.Equal(2, p.Depth);
        Assert.StartsWith("helper", p.Top!.Routine);
        p = await session.StepOutAsync();
        Assert.Equal(1, p.Depth);
        Assert.Equal(LineOf(target, "return r"), p.Top!.Line);
        Assert.Equal("20", (await session.GetVariablesAsync(0)).Single(v => v.Name == "r").Value);
        p = await session.StepOutAsync();
        Assert.True(p.IsFinished);
        Assert.Null((await session.Outcome).Error);
    }

    [SkippableFact]
    public async Task Commits_when_asked()
    {
        Skip.If(Settings is null || !_debuggerInstalled, "DBEXPLORER_TEST_PG not set or pldebugger not installed");
        await using var session = await _provider!.StartDebugAsync(await Target("add_one"), ["41", "kept"], [], commit: true);
        await session.ContinueAsync();
        var outcome = await session.Outcome;
        Assert.True(outcome.Committed);
        Assert.Equal(1, await AuditRows());
    }

    [SkippableFact]
    public async Task Stop_ends_the_call_and_rolls_back()
    {
        Skip.If(Settings is null || !_debuggerInstalled, "DBEXPLORER_TEST_PG not set or pldebugger not installed");
        var target = await Target("add_one");
        await using var session = await _provider!.StartDebugAsync(target, ["7", "x"], [], commit: true);
        await session.StepOverAsync();
        await session.StepOverAsync(); // the insert has run
        await session.StopAsync();
        var outcome = await session.Outcome;
        Assert.NotNull(outcome.Error);
        Assert.False(outcome.Committed);
        Assert.Equal(0, await AuditRows());
    }

    [SkippableFact]
    public async Task Stop_interrupts_a_long_running_statement()
    {
        Skip.If(Settings is null || !_debuggerInstalled, "DBEXPLORER_TEST_PG not set or pldebugger not installed");
        await using var session = await _provider!.StartDebugAsync(await Target("slow"), [], [], commit: true);
        var running = session.ContinueAsync();
        await Task.Delay(500);
        Assert.False(running.IsCompleted);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await session.StopAsync();
        Assert.True((await running).IsFinished);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
        var outcome = await session.Outcome;
        Assert.NotNull(outcome.Error);
        Assert.False(outcome.Committed);
        Assert.Equal(0, await AuditRows());
    }

    [SkippableFact]
    public async Task Debugs_a_procedure_with_an_inout_parameter()
    {
        Skip.If(Settings is null || !_debuggerInstalled, "DBEXPLORER_TEST_PG not set or pldebugger not installed");
        var targets = await _provider!.GetDebugTargetsAsync(new DbObject { Schema = "public", Name = "bump", Type = DbObjectType.Procedure });
        var target = Assert.Single(targets);
        Assert.True(target.IsProcedure);
        await using var session = await _provider.StartDebugAsync(target, ["41"], [], commit: false);
        Assert.False(session.Position.IsFinished);
        await session.ContinueAsync();
        var outcome = await session.Outcome;
        Assert.Null(outcome.Error);
        Assert.Equal(42, Assert.Single(Assert.Single(outcome.ResultSets).Rows)[0]);
    }

    [SkippableFact]
    public async Task Refuses_sql_functions_and_read_only_connections()
    {
        Skip.If(Settings is null || !_debuggerInstalled, "DBEXPLORER_TEST_PG not set or pldebugger not installed");
        var sql = await Target("plain_sql");
        Assert.Contains("PL/pgSQL", sql.Problem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _provider!.StartDebugAsync(sql, ["1"], [], false));

        await using var readOnly = new PostgresProvider(Profile(Database, readOnly: true));
        var target = await Target("add_one");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.StartDebugAsync(target, ["1", "x"], [], false));
        Assert.Contains("read-only", ex.Message);
    }

    [SkippableFact]
    public async Task Reports_an_error_raised_before_the_first_statement()
    {
        Skip.If(Settings is null || !_debuggerInstalled, "DBEXPLORER_TEST_PG not set or pldebugger not installed");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _provider!.StartDebugAsync(Target("add_one").Result, ["not a number", "x"], [], false));
        Assert.Contains("integer", ex.Message);
    }
}

/// <summary>
/// Runs alone: pldebugger gives up a wait when a server signal interrupts it, and other test classes drop databases,
/// which signals every backend (the app reports that as a lost link and ends the run).
/// </summary>
[CollectionDefinition(nameof(DebuggerCollection), DisableParallelization = true)]
public sealed class DebuggerCollection;
