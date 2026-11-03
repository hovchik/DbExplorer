using DbExplorer.Application;
using DbExplorer.Application.Export;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;
using DbExplorer.Desktop;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Tests;

/// <summary>A Query tab result cut off at the row limit: Fetch more and Export full result run the query again, and only
/// when the script only reads.</summary>
public sealed class QueryTabFullResultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-full-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A table of <paramref name="total"/> rows: scripts keep at most maxRows of them; streams get them all.</summary>
    private static IDatabaseProvider Table(int total, List<int> limits, List<string> streamed) =>
        DesktopTabTests.ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.ExecuteScriptAsync)] = args =>
            {
                var maxRows = (int)args[4]!;
                limits.Add(maxRows);
                var rows = Enumerable.Range(1, Math.Min(total, maxRows)).Select(i => (IReadOnlyList<object?>)[i, $"n{i}"]).ToList();
                return Task.FromResult(new QueryExecutionResult
                {
                    ResultSets = [new QueryResultSet { Columns = ["id", "name"], Rows = rows, TotalRowCount = total, IsTruncated = total > maxRows }]
                });
            },
            [nameof(IDatabaseProvider.StreamScriptAsync)] = args =>
            {
                streamed.Add((string)args[0]!);
                var stream = (ResultSetStream)args[2]!;
                stream.Begin(["id", "name"]);
                for (var i = 1; i <= total; i++) stream.Write([i, $"n{i}"]);
                return Task.FromResult(stream.RowCount);
            }
        });

    private QueryViewModel Tab(ServiceProvider services, IDatabaseProvider provider)
    {
        var tab = services.GetRequiredService<QueryViewModel>();
        tab.Attach(DesktopTabTests.Session(provider));
        return tab;
    }

    private ServiceProvider Services()
    {
        var services = App.ConfigureServices();
        services.AddSingleton(new AppPaths(_root));
        services.AddSingleton(DesktopTabTests.DialogFake.Create(out _));
        return services.BuildServiceProvider();
    }

    [Fact]
    public Task Fetch_more_runs_again_with_a_higher_limit_and_keeps_the_tab_limit() => UiThread.RunAsync(async () =>
    {
        var limits = new List<int>();
        await using var services = Services();
        var tab = Tab(services, Table(25, limits, []));
        tab.MaxRows = 10;
        tab.Sql = "SELECT id, name FROM items";

        await tab.ExecuteCommand.ExecuteAsync(null);
        var first = Assert.Single(tab.ResultSets);
        Assert.Equal(10, first.Rows.Count);
        var rerun = Assert.IsType<ResultRerun>(first.Rerun);
        Assert.Equal(10, rerun.Step);
        Assert.Equal(0, rerun.ResultSetIndex);

        await rerun.FetchAsync(20);
        var second = Assert.Single(tab.ResultSets);
        Assert.Equal(20, second.Rows.Count);
        Assert.NotNull(second.Rerun);

        await second.Rerun!.FetchAsync(int.MaxValue);
        var all = Assert.Single(tab.ResultSets);
        Assert.Equal(25, all.Rows.Count);
        Assert.False(all.IsTruncated);
        Assert.Null(all.Rerun);

        Assert.Equal([10, 20, int.MaxValue], limits);
        Assert.Equal(10, tab.MaxRows);
        // A normal run is back to the tab's limit.
        await tab.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(10, limits[^1]);
    });

    [Fact]
    public Task A_script_that_may_write_is_not_run_again() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var tab = Tab(services, Table(25, [], []));
        tab.MaxRows = 10;
        tab.Sql = "UPDATE items SET name = 'x' WHERE id = 0; SELECT id, name FROM items";

        await tab.ExecuteCommand.ExecuteAsync(null);
        var result = Assert.Single(tab.ResultSets);
        Assert.True(result.IsTruncated);
        Assert.Null(result.Rerun);
        Assert.Contains("may change data", result.RerunUnavailable);
    });

    [Fact]
    public Task Export_full_result_streams_every_row_and_marks_the_tab_busy() => UiThread.RunAsync(async () =>
    {
        var streamed = new List<string>();
        await using var services = Services();
        var tab = Tab(services, Table(25, [], streamed));
        tab.MaxRows = 10;
        tab.Sql = "SELECT id, name FROM items";
        await tab.ExecuteCommand.ExecuteAsync(null);
        var view = tab.ResultSets[0];

        using var ms = new MemoryStream();
        var wasRunning = false;
        var result = await view.Rerun!.ExportAsync((streamer, ct) =>
        {
            wasRunning = tab.IsRunning;
            var writer = ResultFileWriter.Create(ExportFormat.Csv, ms);
            return FullResultExport.RunAsync((s, t) => streamer(s, t), 0, view.Columns, writer, new ExportProjection([1]), null, ct);
        }, CancellationToken.None);

        Assert.True(wasRunning);
        Assert.False(tab.IsRunning);
        Assert.Equal(25, result.RowsWritten);
        Assert.Equal(["SELECT id, name FROM items"], streamed);
        var lines = System.Text.Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(26, lines.Length);
        Assert.Equal(["name", "n1"], lines[..2]);
        Assert.Contains("Exported 25", tab.Status);
        // The rows on screen are still the first 10.
        Assert.Equal(10, tab.ResultSets[0].Rows.Count);
    });

    [Fact]
    public void A_pinned_result_is_never_run_again()
    {
        var view = new ResultSetView("Result 1", ["id"], [new ResultRow([1])])
        {
            IsTruncated = true,
            Rerun = new ResultRerun
            {
                ResultSetIndex = 0, Step = 1,
                ExportAsync = (_, _) => throw new InvalidOperationException(),
                FetchAsync = _ => Task.CompletedTask
            }
        };
        var pinned = new ResultPins().Pin(view, "SELECT 1", DateTime.Now)!;
        Assert.Null(pinned.Rerun);
        Assert.Contains("snapshot", pinned.RerunUnavailable);
    }
}
