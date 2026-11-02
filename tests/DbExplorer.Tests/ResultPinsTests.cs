using DbExplorer.Application;
using DbExplorer.Desktop;
using DbExplorer.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Tests;

/// <summary>Pinned results: a pinned result tab stays, read-only, when the query runs again.</summary>
public sealed class ResultPinsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-pins-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime At = new(2026, 11, 2, 19, 48, 5);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static ResultSetView View(string title, params int[] ids) =>
        new(title, ["id"], ids.Select(i => new ResultRow([i])).ToList())
        {
            CommitEdits = (_, _) => Task.FromResult("saved"),
            OpenReference = (_, _, _) => { }
        };

    [Fact]
    public void A_pinned_result_is_a_read_only_copy_shown_before_the_run_results()
    {
        var pins = new ResultPins();
        var run = View("Result 1", 1, 2);

        var pinned = pins.Pin(run, "SELECT id FROM t", At)!;

        Assert.True(pinned.IsPinned);
        Assert.Equal(At, pinned.PinnedAt);
        Assert.Equal("SELECT id FROM t", pinned.Query);
        Assert.Null(pinned.CommitEdits);
        Assert.Null(pinned.OpenReference);
        Assert.False(pinned.CanEditRows);
        Assert.Null(pinned.AddRow());
        Assert.Equal(2, pinned.Rows.Count);
        Assert.Equal("Result 1 · 19:48:05", pinned.Header);
        Assert.Contains("SELECT id FROM t", pinned.HeaderTip);

        var next = View("Result 1", 3);
        Assert.Equal([pinned, next], pins.Arrange([next]));
    }

    [Fact]
    public void Results_with_uncommitted_edits_or_already_pinned_cannot_be_pinned()
    {
        var pins = new ResultPins();
        var edited = View("Result 1", 1);
        edited.Rows[0].SetValue(0, 5);

        Assert.NotNull(pins.WhyNot(edited));
        Assert.Null(pins.Pin(edited, null, At));

        var pinned = pins.Pin(View("Result 2", 1), null, At)!;
        Assert.Null(pins.Pin(pinned, null, At));
        Assert.Equal(1, pins.Count);
    }

    [Fact]
    public void Pins_are_limited()
    {
        var pins = new ResultPins();
        for (var i = 0; i < ResultPins.MaxPinned; i++) Assert.NotNull(pins.Pin(View($"R{i}", i), null, At));
        Assert.Null(pins.Pin(View("one more", 1), null, At));
        Assert.Contains("At most", pins.WhyNot(View("one more", 1)));
    }

    [Fact]
    public void Rename_and_remove_work_on_the_pinned_instance()
    {
        var pins = new ResultPins();
        var pinned = pins.Pin(View("Result 1", 1), null, At)!;

        var renamed = pins.Rename(pinned, "  Before the fix ")!;
        Assert.Equal("Before the fix", renamed.Title);
        Assert.False(pins.Contains(pinned));
        Assert.True(pins.Contains(renamed));
        Assert.Null(pins.Rename(View("not pinned", 1), "x"));

        Assert.False(pins.Remove(View("not pinned", 1)));
        Assert.True(pins.Remove(renamed));
        Assert.Equal(0, pins.Count);
    }

    [Fact]
    public void Selection_prefers_the_new_run_unless_a_pinned_tab_is_open()
    {
        var pins = new ResultPins();
        var pinned = pins.Pin(View("Result 1", 1), null, At)!;
        var oldRun = new List<ResultSetView> { View("Result 1", 1), View("Result 2", 2) };
        var newRun = new List<ResultSetView> { View("Result 1", 1), View("Result 2", 2) };

        // A new run shows its first result.
        Assert.Same(newRun[0], pins.ChooseSelection(null, oldRun[1], oldRun, newRun, keepPosition: false));
        // Auto refresh keeps the tab the user is on.
        Assert.Same(newRun[1], pins.ChooseSelection(null, oldRun[1], oldRun, newRun, keepPosition: true));
        // A new run shows its result even when a pinned tab was open; auto refresh leaves the reader on the pinned tab.
        Assert.Same(newRun[0], pins.ChooseSelection(null, pinned, oldRun, newRun, keepPosition: false));
        Assert.Same(pinned, pins.ChooseSelection(null, pinned, oldRun, newRun, keepPosition: true));
        // An explicit choice wins.
        Assert.Same(newRun[1], pins.ChooseSelection(newRun[1], pinned, oldRun, newRun, keepPosition: false));
        // No run results: the pinned ones.
        Assert.Same(pinned, pins.ChooseSelection(null, null, oldRun, [], keepPosition: false));
    }

    [Fact]
    public async Task Query_tab_keeps_pinned_results_across_runs_and_selects_them()
    {
        var services = App.ConfigureServices();
        services.AddSingleton(new AppPaths(_root));
        await using var provider = services.BuildServiceProvider();
        var tab = provider.GetRequiredService<QueryViewModel>();

        var first = new List<ResultSetView> { View("Result 1", 1), View("Result 2", 2) };
        tab.ResultSets = first;
        Assert.Same(first[0], tab.SelectedResult);

        tab.PinResultCommand.Execute(first[1]);
        var pinned = Assert.Single(tab.Pins.Items);
        Assert.Equal("Result 2", pinned.Title);
        Assert.Equal([pinned, first[0]], tab.ShownResults);
        Assert.Same(pinned, tab.SelectedResult);
        Assert.True(tab.HasPinnedResults);

        // The next run replaces only the run's results; the pinned tab stays and the new result is shown.
        var second = new List<ResultSetView> { View("Result 1", 9) };
        tab.ResultSets = second;
        Assert.Equal([pinned, second[0]], tab.ShownResults);
        Assert.Same(second[0], tab.SelectedResult);
        tab.SelectedResult = pinned;

        tab.ClosePinnedResultCommand.Execute(pinned);
        Assert.Equal([second[0]], tab.ShownResults);
        Assert.Same(second[0], tab.SelectedResult);
        Assert.False(tab.CloseAllPinnedResultsCommand.CanExecute(null));

        // Moving the tab to no connection clears the run's results, not the pins.
        tab.PinResultCommand.Execute(second[0]);
        tab.Attach(null);
        Assert.Single(tab.ShownResults);
        tab.CloseAllPinnedResultsCommand.Execute(null);
        Assert.Empty(tab.ShownResults);
    }
}
