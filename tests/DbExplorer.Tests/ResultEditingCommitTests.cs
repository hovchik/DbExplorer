using System.Reflection;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>Saving grid edits: a failure part-way must leave the rows so that saving again runs only what did not run.</summary>
public class ResultEditingCommitTests
{
    private static readonly ResultSource Customers =
        ResultSourceResolver.Resolve("SELECT * FROM dbo.Customers", ["CustomerId", "Name", "Email"], TestSnapshots.Shop(), "SqlServer", null)!;

    [Fact]
    public async Task In_the_open_transaction_rows_whose_statements_ran_are_marked_saved_when_a_later_one_fails()
    {
        var tx = new FakeScript { FailOn = "[CustomerId] = 3" };
        var (deleted, edited, failing) = Rows();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ResultEditing.CommitAsync(Session(tx), Customers, [deleted, edited, failing], tx, Dialogs.Create()));

        Assert.Equal(ResultRowState.Removed, deleted.State);
        Assert.False(edited.HasChanges);
        Assert.True(failing.IsModified);

        // Saving again runs only the row that failed: nothing is deleted or updated twice.
        tx.FailOn = null;
        tx.Executed.Clear();
        await ResultEditing.CommitAsync(Session(tx), Customers, [deleted, edited, failing], tx, Dialogs.Create());
        Assert.Contains("[CustomerId] = 3", Assert.Single(tx.Executed));
        Assert.False(failing.HasChanges);
    }

    [Fact]
    public async Task In_a_transaction_of_its_own_a_failure_saves_nothing_and_keeps_every_change()
    {
        var own = new FakeScript { FailOn = "[CustomerId] = 3" };
        var (deleted, edited, failing) = Rows();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ResultEditing.CommitAsync(Session(own), Customers, [deleted, edited, failing], openTransaction: null, Dialogs.Create()));

        Assert.False(own.Committed);
        Assert.True(deleted.IsDeleted);
        Assert.True(edited.IsModified);
        Assert.True(failing.IsModified);
    }

    private static (ResultRow Deleted, ResultRow Edited, ResultRow Failing) Rows()
    {
        var deleted = new ResultRow(new object?[] { 1, "a", "a@x" });
        deleted.MarkDeleted();
        var edited = new ResultRow(new object?[] { 2, "b", "b@x" });
        edited.SetValue(1, "B");
        var failing = new ResultRow(new object?[] { 3, "c", "c@x" });
        failing.SetValue(1, "C");
        return (deleted, edited, failing);
    }

    private static DatabaseSession Session(FakeScript script) =>
        new(new ConnectionProfile { ProviderKey = "SqlServer", Host = "fake", Name = "fake" }, null!, Provider.Create(script), "16.0",
            TestSnapshots.Shop());

    /// <summary>Counts every statement as one affected row, except those containing <see cref="FailOn"/> (no row matched).</summary>
    private sealed class FakeScript : IScriptSession
    {
        public string? FailOn { get; set; }
        public List<string> Executed { get; } = [];
        public bool Committed { get; private set; }

        public Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default)
        {
            Executed.Add(sql);
            return Task.FromResult(FailOn is not null && sql.Contains(FailOn, StringComparison.Ordinal) ? 0 : 1);
        }

        public Task<QueryExecutionResult> QueryAsync(string sql, int timeoutSeconds, int maxRows = int.MaxValue, CancellationToken ct = default,
            ReadOnlyScript? readOnly = null)
        {
            Executed.Add(sql);
            return Task.FromResult(new QueryExecutionResult());
        }

        public Task CommitAsync(CancellationToken ct = default)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A SQL Server-like provider whose script sessions are <see cref="FakeScript"/>.</summary>
    public class Provider : DispatchProxy
    {
        private object? _script;

        internal static IDatabaseProvider Create(object script)
        {
            var provider = DispatchProxy.Create<IDatabaseProvider, Provider>();
            ((Provider)(object)provider)._script = script;
            return provider;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_ProviderKey" => "SqlServer",
            nameof(IDatabaseProvider.QuoteIdentifier) => "[" + ((string)args![0]!).Replace("]", "]]") + "]",
            nameof(IDatabaseProvider.BeginScriptSessionAsync) => Task.FromResult((IScriptSession)_script!),
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }

    /// <summary>Confirms every question.</summary>
    public class Dialogs : DispatchProxy
    {
        internal static IDialogService Create() => DispatchProxy.Create<IDialogService, Dialogs>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IDialogService.ConfirmAsync) ? Task.FromResult(true) : throw new NotSupportedException(targetMethod?.Name);
    }
}
