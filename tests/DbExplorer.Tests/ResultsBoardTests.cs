using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Application;
using DbExplorer.Desktop;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Tests;

/// <summary>The Side by side board: results of different Query tabs shown next to each other, as read-only snapshots.</summary>
[Collection(nameof(HeadlessUiCollection))]
public sealed class ResultsBoardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-board-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime At = new(2026, 11, 17, 11, 20, 5);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static ResultSetView View(string title, params int[] ids) =>
        new(title, ["id", "name"], ids.Select(i => new ResultRow([i, "row " + i])).ToList())
        {
            CommitEdits = (_, _) => Task.FromResult("saved"),
            OpenReference = (_, _, _) => { }
        };

    private static BoardSource Source(string tab, string connection = "Shop", string? database = "shop") =>
        new(tab, connection, database, At, "SELECT id, name FROM t");

    // ----- The board's list -----

    [Fact]
    public void A_result_on_the_board_is_a_read_only_snapshot_that_edits_in_the_tab_do_not_change()
    {
        var board = new ResultsBoard();
        var run = View("Result 1", 1, 2);

        var item = board.Add(run, Source("Orders"))!;

        Assert.NotSame(run, item.View);
        Assert.Null(item.View.CommitEdits);
        Assert.Null(item.View.OpenReference);
        Assert.Null(item.View.Rerun);
        Assert.False(item.View.CanEditRows);
        Assert.False(item.View.IsPinned);
        Assert.Equal("SELECT id, name FROM t", item.View.Query);

        // Editing the tab's grid afterwards leaves the snapshot as it was.
        run.Rows[0].SetValue(1, "changed");
        Assert.Equal("row 1", item.View.Rows[0].Values[1]);
    }

    [Fact]
    public void Results_with_edits_duplicates_and_more_than_the_limit_are_refused()
    {
        var board = new ResultsBoard();
        var edited = View("Result 1", 1);
        edited.Rows[0].SetValue(1, "x");
        Assert.Contains("Commit or discard", board.WhyNot(edited));
        Assert.Null(board.Add(edited, Source("A")));

        var run = View("Result 1", 1);
        Assert.NotNull(board.Add(run, Source("A")));
        Assert.Contains("already", board.WhyNot(run));

        for (var i = board.Count; i < ResultsBoard.MaxResults; i++) Assert.NotNull(board.Add(View("R", i), Source("T" + i)));
        Assert.Contains($"At most {ResultsBoard.MaxResults}", board.WhyNot(View("R", 99)));
    }

    [Fact]
    public void Results_can_be_moved_and_removed()
    {
        var board = new ResultsBoard();
        var a = board.Add(View("A", 1), Source("A"))!;
        var b = board.Add(View("B", 2), Source("B"))!;
        var c = board.Add(View("C", 3), Source("C"))!;

        Assert.False(board.Move(a, -1));
        Assert.True(board.Move(a, +1));
        Assert.Equal([b, a, c], board.Items);
        Assert.True(board.Move(c, -2));
        Assert.Equal([c, b, a], board.Items);
        Assert.False(board.CanMove(a, +1));

        Assert.True(board.Remove(b));
        Assert.Equal([c, a], board.Items);
        board.Clear();
        Assert.Empty(board.Items);
    }

    [Fact]
    public void The_header_says_which_tab_connection_database_and_time_a_result_came_from()
    {
        var item = new ResultsBoard().Add(View("Result 2", 1, 2, 3), Source("Orders today", "Shop archive", "shop_old"))!;

        Assert.Equal("Orders today · Result 2", item.Title);
        Assert.Equal("Shop archive · shop_old · 11:20:05 · 3 row(s)", item.Detail);
        Assert.Contains("SELECT id, name FROM t", item.ToolTip);

        // A tab named like its result is not repeated; the database is left out when it is the connection's name.
        var same = new ResultsBoard().Add(View("Orders", 1), new BoardSource("Orders", "shop", "shop", At))!;
        Assert.Equal("Orders", same.Title);
        Assert.Equal("shop · 11:20:05 · 1 row(s)", same.Detail);
    }

    // ----- The view model -----

    [Fact]
    public void The_view_model_reports_what_happened_and_tells_the_view_to_lay_out_again()
    {
        var vm = new ResultsBoardViewModel();
        var changes = 0;
        vm.Changed += () => changes++;
        Assert.Equal("Side by side", vm.Header);
        Assert.False(vm.ClearCommand.CanExecute(null));

        Assert.Contains("Add another result", vm.Add(View("Result 1", 1), Source("Orders")));
        Assert.Contains("2 results side by side", vm.Add(View("Result 1", 2), Source("Archive")));
        Assert.Equal("Side by side (2)", vm.Header);
        Assert.Equal(2, changes);

        vm.MoveRightCommand.Execute(vm.Items[0]);
        Assert.Equal(["Archive · Result 1", "Orders · Result 1"], vm.Items.Select(i => i.Title));

        vm.Layout = BoardLayout.Rows;
        Assert.True(vm.IsRows);
        Assert.Equal(4, changes);

        vm.RemoveCommand.Execute(vm.Items[0]);
        Assert.Single(vm.Items);
        vm.ClearCommand.Execute(null);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.ClearCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_query_tab_sends_its_result_with_where_it_came_from()
    {
        var services = App.ConfigureServices();
        services.AddSingleton(new AppPaths(_root));
        await using var provider = services.BuildServiceProvider();
        var workspace = provider.GetRequiredService<QueryWorkspaceViewModel>();
        var tab = workspace.SelectedDocument!;
        tab.Title = "Orders today";
        var run = View("Result 1", 1);
        tab.ResultSets = [run];

        (ResultSetView View, BoardSource Source)? sent = null;
        workspace.SideBySideRequested += (v, s) => { sent = (v, s); return "on the board"; };
        tab.ViewSideBySideCommand.Execute(run);

        Assert.NotNull(sent);
        Assert.Same(run, sent.Value.View);
        Assert.Equal("Orders today", sent.Value.Source.Tab);
        Assert.True(DateTime.Now - sent.Value.Source.RanAt < TimeSpan.FromMinutes(1));
        Assert.Equal("on the board", tab.Status);

        // A result that is not on screen in this tab is ignored.
        sent = null;
        tab.ViewSideBySideCommand.Execute(View("Other", 2));
        Assert.Null(sent);
    }

    // ----- The views -----

    [AvaloniaFact]
    public void The_result_grid_offers_side_by_side_only_where_the_host_handles_it()
    {
        var grid = new ResultGridView { ResultSet = View("Result 1", 1, 2) };
        var window = new Window { Width = 900, Height = 500, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var button = grid.FindControl<Button>("SideBySideButton")!;
        Assert.False(button.IsVisible);

        var raised = 0;
        grid.ViewSideBySideRequested += (_, _) => raised++;
        grid.RequestSideBySide();
        Assert.Equal(0, raised);

        grid.CanViewSideBySide = true;
        Assert.True(button.IsVisible);
        grid.RequestSideBySide();
        Assert.Equal(1, raised);
        window.Close();
    }

    [AvaloniaFact]
    public void The_board_lays_results_out_as_columns_or_rows_with_splitters()
    {
        var vm = new ResultsBoardViewModel();
        var view = new ResultsBoardView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var board = view.FindControl<Grid>("Board")!;
        Assert.Empty(board.Children);

        vm.Add(View("Result 1", 1, 2), Source("Orders"));
        vm.Add(View("Result 1", 3), Source("Archive"));
        vm.Add(View("Result 1", 4), Source("Reports"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5, board.ColumnDefinitions.Count); // three results, two splitters
        Assert.Equal(2, board.Children.OfType<GridSplitter>().Count());
        var grids = view.GetVisualDescendants().OfType<ResultGridView>().ToList();
        Assert.Equal(3, grids.Count);
        Assert.All(grids, g => Assert.False(g.CanViewSideBySide));

        var firstGrid = grids[0];
        vm.Layout = BoardLayout.Rows;
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(board.ColumnDefinitions);
        Assert.Equal(5, board.RowDefinitions.Count);
        // The cards are kept, so each grid keeps its filters and sort.
        Assert.Same(firstGrid, view.GetVisualDescendants().OfType<ResultGridView>().First());

        vm.RemoveCommand.Execute(vm.Items[1]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, board.RowDefinitions.Count);
        window.Close();
    }

    [Fact]
    public void The_data_grid_still_has_the_internal_scroll_handlers_scroll_together_needs() =>
        // Avalonia's DataGrid has no public way to scroll to an offset; if an upgrade renames these, this fails.
        Assert.True(ResultGridView.CanScrollTo);

    [AvaloniaFact]
    public void Scrolling_one_result_scrolls_the_others_when_scroll_together_is_on()
    {
        var vm = new ResultsBoardViewModel();
        var view = new ResultsBoardView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 600, Content = view };
        window.Show();
        vm.Add(View("Result 1", [.. Enumerable.Range(1, 400)]), Source("Orders"));
        vm.Add(View("Result 1", [.. Enumerable.Range(1, 400)]), Source("Archive"));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var grids = view.GetVisualDescendants().OfType<ResultGridView>().ToList();
        foreach (var g in grids) g.WatchScrolling();
        Dispatcher.UIThread.RunJobs();

        // What the user's wheel or drag does: the grid moves its scroll bar.
        var bar = grids[0].GetVisualDescendants().OfType<ScrollBar>().First(b => b.Name == "PART_VerticalScrollbar");
        bar.Value = 600;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(600, grids[1].ScrollOffset.Vertical, 0);
        // The grid itself moved, not just its scroll bar: its first row on screen is no longer row 1.
        var firstShown = grids[1].GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible).Min(r => r.Index);
        Assert.True(firstShown > 5, $"first row shown is {firstShown}");

        vm.SyncScroll = false;
        bar.Value = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(600, grids[1].ScrollOffset.Vertical, 0);
        window.Close();
    }
}
