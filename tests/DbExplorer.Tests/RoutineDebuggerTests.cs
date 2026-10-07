using DbExplorer.Core.Debugging;
using DbExplorer.Providers.Postgres;

namespace DbExplorer.Tests;

public class RoutineDebuggerTests
{
    private static DebugTarget Target(bool procedure, params DebugParameter[] parameters) => new()
    {
        Id = 1, Schema = "sales", Name = "add_order", Signature = "sales.add_order(...)", Language = "plpgsql",
        IsProcedure = procedure, Parameters = parameters
    };

    [Fact]
    public void Function_call_uses_named_notation_and_casts_text_arguments()
    {
        var target = Target(false,
            new DebugParameter("customer", "integer", DebugParameterMode.In, false),
            new DebugParameter("note", "text", DebugParameterMode.In, false),
            new DebugParameter("total", "numeric", DebugParameterMode.Out, false));

        var (sql, values) = PostgresDebugger.BuildCall(target, ["42", null]);

        Assert.Equal("""SELECT * FROM "sales"."add_order"("customer" => $1::integer, "note" => NULL::text)""", sql);
        Assert.Equal(["42"], values);
    }

    [Fact]
    public void Procedure_call_passes_null_for_out_parameters()
    {
        var target = Target(true,
            new DebugParameter("id", "integer", DebugParameterMode.In, false),
            new DebugParameter("result", "text", DebugParameterMode.Out, false),
            new DebugParameter("counter", "bigint", DebugParameterMode.InOut, false));

        var (sql, values) = PostgresDebugger.BuildCall(target, ["1", "5"]);

        Assert.Equal("""CALL "sales"."add_order"("id" => $1::integer, "result" => NULL::text, "counter" => $2::bigint)""", sql);
        Assert.Equal(["1", "5"], values);
    }

    [Fact]
    public void Parameters_with_defaults_can_be_left_out()
    {
        var target = Target(false,
            new DebugParameter("a", "integer", DebugParameterMode.In, false),
            new DebugParameter("b", "text", DebugParameterMode.In, true),
            new DebugParameter("c", "date", DebugParameterMode.In, true));

        var (sql, _) = PostgresDebugger.BuildCall(target, ["1", DebugArguments.DefaultArgument, "2026-01-01"]);

        Assert.Equal("""SELECT * FROM "sales"."add_order"("a" => $1::integer, "c" => $2::date)""", sql);
        Assert.Throws<ArgumentException>(() => PostgresDebugger.BuildCall(target, [DebugArguments.DefaultArgument, "x", "y"]));
    }

    [Fact]
    public void Unnamed_parameters_use_positional_notation_and_only_trailing_defaults()
    {
        var target = Target(false,
            new DebugParameter("", "integer", DebugParameterMode.In, false),
            new DebugParameter("", "text", DebugParameterMode.In, true),
            new DebugParameter("", "text", DebugParameterMode.In, true));

        Assert.Equal("""SELECT * FROM "sales"."add_order"($1::integer)""",
            PostgresDebugger.BuildCall(target, ["1", DebugArguments.DefaultArgument, DebugArguments.DefaultArgument]).Sql);
        Assert.Throws<ArgumentException>(() => PostgresDebugger.BuildCall(target, ["1", DebugArguments.DefaultArgument, "x"]));
    }

    [Fact]
    public void Variadic_argument_is_marked()
    {
        var target = Target(false, new DebugParameter("ids", "integer[]", DebugParameterMode.Variadic, false));
        Assert.Equal("""SELECT * FROM "sales"."add_order"(VARIADIC "ids" => $1::integer[])""",
            PostgresDebugger.BuildCall(target, ["{1,2}"]).Sql);
    }

    [Fact]
    public void Wrong_argument_count_is_refused() =>
        Assert.Throws<ArgumentException>(() =>
            PostgresDebugger.BuildCall(Target(false, new DebugParameter("a", "int", DebugParameterMode.In, false)), []));

    [Fact]
    public void Defaults_belong_to_the_last_input_parameters()
    {
        var p = PostgresDebugger.ParametersOf(["integer", "text", "numeric", "date"], ["i", "i", "o", "i"], ["a", "b", "c", "d"], 2);

        Assert.Equal([false, true, false, true], p.Select(x => x.HasDefault));
        Assert.Equal(DebugParameterMode.Out, p[2].Mode);
        Assert.False(p[2].TakesValue);
    }

    [Fact]
    public void Table_columns_are_out_parameters_and_missing_modes_mean_in()
    {
        Assert.Equal(DebugParameterMode.Out, PostgresDebugger.ParametersOf(["int"], ["t"], ["x"], 0)[0].Mode);
        Assert.Equal(DebugParameterMode.In, PostgresDebugger.ParametersOf(["int"], null, null, 0)[0].Mode);
    }

    [Fact]
    public void Break_notice_carries_the_port()
    {
        Assert.Equal(3, PostgresDebugger.ParseBreakNotice("PLDBGBREAK:3"));
        Assert.Equal(54321, PostgresDebugger.ParseBreakNotice(" PLDBGBREAK:54321 "));
        Assert.Null(PostgresDebugger.ParseBreakNotice("hello PLDBGBREAK:3"));
    }

    [Fact]
    public void Support_explains_each_missing_piece()
    {
        Assert.True(PostgresDebugger.Support(true, true, "pg_stat_statements, plugin_debugger").IsAvailable);
        Assert.True(PostgresDebugger.Support(true, true, "\"$libdir/plugin_debugger\"").IsAvailable);
        Assert.True(PostgresDebugger.Support(true, true, null).IsAvailable); // hidden setting: try it

        var notInstalled = PostgresDebugger.Support(false, false, "");
        Assert.False(notInstalled.IsAvailable);
        Assert.Contains("not installed", notInstalled.Problem);
        Assert.Contains("pldebugger package", notInstalled.Setup);
        Assert.Contains("CREATE EXTENSION pldbgapi", notInstalled.Setup);

        var notCreated = PostgresDebugger.Support(false, true, "plugin_debugger");
        Assert.Contains("not created", notCreated.Problem);
        Assert.DoesNotContain("shared_preload_libraries", notCreated.Setup);

        var notPreloaded = PostgresDebugger.Support(true, true, "pg_stat_statements");
        Assert.Contains("preload", notPreloaded.Problem);
        Assert.Contains("shared_preload_libraries", notPreloaded.Setup);
    }

    [Fact]
    public void Only_plpgsql_routines_can_be_stepped()
    {
        Assert.Null(PostgresDebugger.ProblemOf("plpgsql", "f", "f()"));
        Assert.Contains("written in sql", PostgresDebugger.ProblemOf("sql", "f", "f()"));
        Assert.Contains("aggregate", PostgresDebugger.ProblemOf("internal", "a", "f()"));
    }

    // A fake engine: a script of positions that step into walks through; step over / continue are recorded.
    private sealed class FakeEngine(params int[][] script) : IDebugPrimitives
    {
        private int _next;
        public List<string> Calls { get; } = [];

        // Each script entry: depth, then the line of the top frame.
        private DebugPosition Next()
        {
            if (_next >= script.Length) return DebugPosition.Finished;
            var s = script[_next++];
            return new DebugPosition(Enumerable.Range(0, s[0]).Select(l => new DebugFrame(l, l == 0 ? 2 : 1, "", l == 0 ? s[1] : 1, "")).ToList());
        }

        public Task<DebugPosition> StepIntoAsync(CancellationToken ct) { Calls.Add("into"); return Task.FromResult(Next()); }
        public Task<DebugPosition> StepOverAsync(CancellationToken ct) { Calls.Add("over"); return Task.FromResult(Next()); }
        public Task<DebugPosition> ContinueAsync(CancellationToken ct) { Calls.Add("continue"); return Task.FromResult(DebugPosition.Finished); }
    }

    private static DebugPosition At(int depth, int line) =>
        new(Enumerable.Range(0, depth).Select(l => new DebugFrame(l, l == 0 ? 2 : 1, "", l == 0 ? line : 1, "")).ToList());

    [Fact]
    public async Task Step_over_in_the_outermost_routine_is_native()
    {
        var engine = new FakeEngine([1, 6]);
        var p = await DebugStepper.StepOverAsync(engine, At(1, 5), _ => false);
        Assert.Equal(["over"], engine.Calls);
        Assert.Equal(6, p.Top!.Line);
    }

    [Fact]
    public async Task Step_over_in_a_called_routine_runs_nested_calls_and_stops_back_at_its_depth()
    {
        // From depth 2: a call into depth 3 (two lines), then the next line at depth 2.
        var engine = new FakeEngine([3, 1], [3, 2], [2, 7]);
        var p = await DebugStepper.StepOverAsync(engine, At(2, 6), _ => false);
        Assert.Equal(3, engine.Calls.Count);
        Assert.Equal(2, p.Depth);
        Assert.Equal(7, p.Top!.Line);
    }

    [Fact]
    public async Task Step_over_stops_at_a_breakpoint_in_a_nested_call()
    {
        var engine = new FakeEngine([3, 1], [3, 2], [2, 7]);
        var p = await DebugStepper.StepOverAsync(engine, At(2, 6), f => f.Line == 2);
        Assert.Equal(3, p.Depth);
        Assert.Equal(2, engine.Calls.Count);
    }

    [Fact]
    public async Task Step_out_returns_to_the_caller()
    {
        var engine = new FakeEngine([2, 4], [2, 5], [1, 9]);
        var p = await DebugStepper.StepOutAsync(engine, At(2, 3), _ => false);
        Assert.Equal(1, p.Depth);
        Assert.Equal(9, p.Top!.Line);
    }

    [Fact]
    public async Task Step_out_of_the_outermost_routine_continues()
    {
        var engine = new FakeEngine();
        var p = await DebugStepper.StepOutAsync(engine, At(1, 3), _ => false);
        Assert.Equal(["continue"], engine.Calls);
        Assert.True(p.IsFinished);
    }

    [Fact]
    public async Task Stepping_stops_when_the_call_finishes()
    {
        var engine = new FakeEngine([2, 4]);
        var p = await DebugStepper.StepOutAsync(engine, At(2, 3), _ => false);
        Assert.True(p.IsFinished);
        Assert.Equal(["into", "into"], engine.Calls);
    }
}
