using System.Diagnostics;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>
/// "Relations &amp; combined data" froze the app on a value found in hundreds of tables: the diagram, the generated SQL
/// and every query's rows were built on the UI thread. These check the work leaves the caller's thread, shows progress,
/// runs a few queries at a time and can be stopped.
/// </summary>
public sealed class DataRelationsLoadingTests
{
    private static DataMatch Match(string schema, string table, string column, string id) => new()
    {
        Schema = schema, Table = table, Column = column, Value = id, RowKey = $"{column}={id}",
        KeyValues = [new KeyValuePair<string, string?>(column, id)]
    };

    private static readonly IReadOnlyList<DataMatch> Matches =
    [
        Match("dbo", "Customers", "CustomerId", "1"),
        Match("sales", "Orders", "OrderId", "42"),
        Match("dbo", "Products", "ProductId", "7")
    ];

    private static DataRelationsViewModel Create(Func<CancellationToken, Task<QueryExecutionResult>> execute)
    {
        var provider = DesktopTabTests.ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.QuoteIdentifier)] = a => "[" + a[0] + "]",
            [nameof(IDatabaseProvider.ExecuteScriptAsync)] = a => execute((CancellationToken)a[3]!)
        }, "SqlServer");
        var session = new DatabaseSession(new ConnectionProfile { ProviderKey = "SqlServer", Name = "fake", Database = "main" },
            null!, provider, "1", TestSnapshots.Shop());
        return new DataRelationsViewModel(new QueryExecutionService(), session, "x", Matches);
    }

    private static QueryExecutionResult OneRow() => new()
    {
        ResultSets = [new QueryResultSet { Columns = ["Id"], Rows = [[1]], TotalRowCount = 1, TotalRowCountIsExact = true }]
    };

    [Fact]
    public async Task Opening_does_no_work_until_loaded_and_load_runs_off_the_callers_thread()
    {
        var marker = new MarkerContext();
        var sawMarker = false;
        using var release = new ManualResetEventSlim();
        var vm = Create(_ =>
        {
            if (SynchronizationContext.Current is MarkerContext) sawMarker = true;
            release.Wait(TimeSpan.FromSeconds(10)); // a slow, blocking read of rows
            return Task.FromResult(OneRow());
        });
        Assert.Empty(vm.Diagram.Tables);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(marker);
        Task load;
        var sw = Stopwatch.StartNew();
        try
        {
            load = vm.LoadAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "LoadAsync blocked its caller");
        Assert.True(vm.IsRunning);

        release.Set();
        await load.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(sawMarker);
        Assert.NotEmpty(vm.Diagram.Tables);
        Assert.NotEmpty(vm.Results);
        Assert.Equal(vm.Results.Count, vm.ProgressMaximum);
        Assert.Equal(vm.Results.Count, vm.Progress);
        Assert.All(vm.Results, r => Assert.True(r.HasView, r.Title + ": " + r.Message));
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Queries_run_a_few_at_a_time()
    {
        var running = 0;
        var peak = 0;
        var vm = Create(async ct =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
            return OneRow();
        });
        await vm.LoadAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(vm.Results.Count > vm.Parallelism);
        Assert.InRange(peak, 1, vm.Parallelism);
        Assert.Equal(vm.Results.Count, vm.Progress);
    }

    [Fact]
    public async Task Stop_cancels_the_queries_left_and_keeps_the_window_usable()
    {
        var started = new TaskCompletionSource();
        var vm = Create(async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return OneRow();
        });
        var load = vm.LoadAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.StopCommand.CanExecute(null));

        vm.StopCommand.Execute(null);
        await load.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(vm.IsRunning);
        Assert.StartsWith("Stopped", vm.Status);
        Assert.True(vm.LoadCommand.CanExecute(null));
        Assert.Contains(vm.Results, r => r.Message == "Stopped.");
    }

    [Fact]
    public async Task A_failing_query_shows_its_error_and_the_rest_still_load()
    {
        var calls = 0;
        var vm = Create(_ => Interlocked.Increment(ref calls) == 1
            ? Task.FromException<QueryExecutionResult>(new InvalidOperationException("boom"))
            : Task.FromResult(OneRow()));
        await vm.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(vm.Results, r => r.Message == "Error: boom");
        Assert.Equal(vm.Results.Count - 1, vm.Results.Count(r => r.HasView));
        Assert.DoesNotContain("Stopped", vm.Status);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private sealed class MarkerContext : SynchronizationContext;
}
