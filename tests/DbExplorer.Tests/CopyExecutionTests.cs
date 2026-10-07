using System.Reflection;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

/// <summary>How <see cref="ObjectCopyService.ExecuteAsync"/> reports every step while a plan runs.</summary>
public class CopyExecutionTests
{
    [Fact]
    public async Task Every_step_is_reported_running_then_done()
    {
        var script = new FakeScriptSession();
        var updates = await RunAsync(script, singleTransaction: true, "CREATE TABLE a", "INSERT a", "CREATE INDEX a");

        Assert.Equal(["CREATE TABLE a", "INSERT a", "CREATE INDEX a"], script.Executed);
        Assert.True(script.Committed);
        Assert.Equal(
            [(0, CopyStepState.Running), (0, CopyStepState.Done), (1, CopyStepState.Running), (1, CopyStepState.Done),
             (2, CopyStepState.Running), (2, CopyStepState.Done)],
            updates.Select(u => (u.Index, u.State)));
        Assert.Contains("3 row(s)", updates[3].Details);
    }

    [Fact]
    public async Task A_failure_in_a_transaction_marks_earlier_steps_rolled_back_and_later_ones_not_run()
    {
        var script = new FakeScriptSession { FailOn = "INSERT a" };
        var updates = new List<CopyStepUpdate>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(script, singleTransaction: true, updates, "CREATE TABLE a", "INSERT a", "CREATE INDEX a"));

        Assert.Contains("Step 2", error.Message);
        Assert.False(script.Committed);
        var final = FinalStates(updates);
        Assert.Equal(CopyStepState.RolledBack, final[0].State);
        Assert.Equal(CopyStepState.Failed, final[1].State);
        Assert.Equal("boom", final[1].Details);
        Assert.Equal(CopyStepState.NotRun, final[2].State);
    }

    [Fact]
    public async Task A_failure_step_by_step_keeps_earlier_steps_done()
    {
        var script = new FakeScriptSession { FailOn = "INSERT a" };
        var updates = new List<CopyStepUpdate>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(script, singleTransaction: false, updates, "CREATE TABLE a", "INSERT a", "CREATE INDEX a"));

        var final = FinalStates(updates);
        Assert.Equal(CopyStepState.Done, final[0].State);
        Assert.Equal(CopyStepState.Failed, final[1].State);
        Assert.Equal(CopyStepState.NotRun, final[2].State);
    }

    [Fact]
    public async Task On_MySQL_a_failure_in_a_transaction_only_marks_row_steps_after_the_last_schema_step_rolled_back()
    {
        var script = new FakeScriptSession { FailOn = "INSERT c" };
        var updates = new List<CopyStepUpdate>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(script, true, updates, SqlDialect.MySqlKey,
            [("INSERT a", CopyStepKind.Insert), ("CREATE TABLE b", CopyStepKind.Structure), ("INSERT b", CopyStepKind.Insert),
             ("INSERT c", CopyStepKind.Insert), ("CREATE INDEX c", CopyStepKind.Constraint)]));

        Assert.DoesNotContain("everything was rolled back", error.Message);
        var final = FinalStates(updates);
        Assert.Equal(CopyStepState.Done, final[0].State);
        Assert.Equal(CopyStepState.Done, final[1].State);
        Assert.Equal(CopyStepState.RolledBack, final[2].State);
        Assert.Equal(CopyStepState.Failed, final[3].State);
        Assert.Equal(CopyStepState.NotRun, final[4].State);
    }

    [Fact]
    public async Task On_MySQL_a_failing_schema_step_leaves_every_earlier_step_applied()
    {
        var script = new FakeScriptSession { FailOn = "CREATE INDEX a" };
        var updates = new List<CopyStepUpdate>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(script, true, updates, SqlDialect.MySqlKey,
            [("CREATE TABLE a", CopyStepKind.Structure), ("INSERT a", CopyStepKind.Insert), ("CREATE INDEX a", CopyStepKind.Constraint)]));

        var final = FinalStates(updates);
        Assert.Equal(CopyStepState.Done, final[0].State);
        Assert.Equal(CopyStepState.Done, final[1].State);
        Assert.Equal(CopyStepState.Failed, final[2].State);
    }

    [Fact]
    public void Create_database_statement_is_quoted_per_engine()
    {
        Assert.Equal("CREATE DATABASE [QR]]Register];", SqlDialect.SqlServer.CreateDatabase("QR]Register"));
        Assert.Equal("CREATE DATABASE \"QRRegister\";", SqlDialect.Postgres.CreateDatabase("QRRegister"));
    }

    [Fact]
    public async Task A_snapshot_view_shares_the_provider_without_disposing_it()
    {
        var provider = FakeProvider.Create(new FakeScriptSession(), out var state);
        var session = new DatabaseSession(new ConnectionProfile(), null!, provider, "1", Empty());
        var view = session.WithSnapshot(Empty());

        Assert.Same(session.Provider, view.Provider);
        await view.DisposeAsync();
        Assert.False(state.Disposed);
        await session.DisposeAsync();
        Assert.True(state.Disposed);
    }

    private static Dictionary<int, CopyStepUpdate> FinalStates(IEnumerable<CopyStepUpdate> updates) =>
        updates.GroupBy(u => u.Index).ToDictionary(g => g.Key, g => g.Last());

    private static async Task<List<CopyStepUpdate>> RunAsync(FakeScriptSession script, bool singleTransaction, params string[] sql)
    {
        var updates = new List<CopyStepUpdate>();
        await RunAsync(script, singleTransaction, updates, sql);
        return updates;
    }

    private static Task RunAsync(FakeScriptSession script, bool singleTransaction, List<CopyStepUpdate> updates, params string[] sql) =>
        RunAsync(script, singleTransaction, updates, SqlDialect.SqlServerKey, sql.Select(s => (s, CopyStepKind.Structure)).ToArray());

    private static async Task RunAsync(FakeScriptSession script, bool singleTransaction, List<CopyStepUpdate> updates,
        string providerKey, (string Sql, CopyStepKind Kind)[] steps)
    {
        var session = new DatabaseSession(new ConnectionProfile(), null!, FakeProvider.Create(script, out _), "1", Empty());
        var plan = new CopyPlan
        {
            Analysis = new CopyAnalysis
            {
                Source = new DbObject { Schema = "dbo", Name = "a", Type = DbObjectType.Table },
                TargetDatabase = "Target", TargetSchema = "dbo", TargetName = "a"
            },
            Action = CopyAction.CreateObject,
            Options = new CopyOptions { SingleTransaction = singleTransaction },
            TargetProviderKey = providerKey,
            Steps = steps.Select(s => new CopyStep(s.Sql, s.Kind, s.Sql)).ToList()
        };

        var service = new ObjectCopyService(new DefinitionService(), new QueryExecutionService());
        await service.ExecuteAsync(session, session, plan, stepProgress: new SyncProgress<CopyStepUpdate>(updates.Add));
    }

    private static MetadataSnapshot Empty() => new()
    {
        Objects = [], Columns = [], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
    };

    /// <summary>Reports synchronously, so the order of the updates is exactly the order of the calls.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeScriptSession : IScriptSession
    {
        public string? FailOn { get; init; }
        public List<string> Executed { get; } = [];
        public bool Committed { get; private set; }

        public Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default)
        {
            if (sql == FailOn) throw new InvalidOperationException("boom");
            Executed.Add(sql);
            return Task.FromResult(3);
        }

        public Task<QueryExecutionResult> QueryAsync(string sql, int timeoutSeconds, int maxRows = int.MaxValue,
            CancellationToken ct = default, ReadOnlyScript? readOnly = null) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken ct = default)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Only what running a plan needs: the provider key, a script session and disposal.</summary>
    public class FakeProvider : DispatchProxy
    {
        public sealed class State
        {
            public bool Disposed { get; set; }
        }

        private IScriptSession _script = null!;
        private State _state = null!;

        internal static IDatabaseProvider Create(IScriptSession script, out State state)
        {
            var provider = Create<IDatabaseProvider, FakeProvider>();
            var fake = (FakeProvider)(object)provider;
            fake._script = script;
            fake._state = state = new State();
            return provider;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_ProviderKey" => SqlDialect.SqlServerKey,
            nameof(IDatabaseProvider.BeginScriptSessionAsync) => Task.FromResult(_script),
            nameof(IAsyncDisposable.DisposeAsync) => Dispose(),
            _ => throw new NotSupportedException(method?.Name)
        };

        private ValueTask Dispose()
        {
            _state.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
