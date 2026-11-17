using Avalonia;
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

/// <summary>Compare: another Query tab's result (or another result of the same tab) shown next to a tab's own results.</summary>
[Collection(nameof(HeadlessUiCollection))]
public sealed class CompareResultsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-compare-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static ResultSetView View(string title, params int[] ids) =>
        new(title, ["id", "name"], ids.Select(i => new ResultRow([i, "row " + i])).ToList());

    private (ServiceProvider Provider, QueryWorkspaceViewModel Workspace, QueryViewModel Orders, QueryViewModel Accounts) Tabs()
    {
        var services = App.ConfigureServices();
        services.AddSingleton(new AppPaths(_root));
        services.AddSingleton(DesktopTabTests.DialogFake.Create(out _));
        var provider = services.BuildServiceProvider();
        var workspace = provider.GetRequiredService<QueryWorkspaceViewModel>();
        var accounts = workspace.SelectedDocument!;
        accounts.Title = "Accounts top 10";
        accounts.ResultSets = [View("Result 1", 1, 2, 3)];
        var orders = workspace.NewTab();
        orders.Title = "Orders";
        orders.ResultSets = [View("Result 1", 10, 11), View("Result 2", 12)];
        return (provider, workspace, orders, accounts);
    }

    [Fact]
    public async Task The_list_offers_other_tabs_results_first_then_this_tabs_other_results()
    {
        var (provider, _, orders, accounts) = Tabs();
        await using var _ = provider;

        var choices = orders.CompareChoices();

        Assert.Equal(["Accounts top 10", "Orders · Result 2"], choices.Select(c => c.Title));
        Assert.Same(accounts, choices[0].Tab);
        Assert.Contains("3 row(s)", choices[0].Detail);
        // The result on screen in this tab is not offered: it is already there.
        Assert.DoesNotContain(choices, c => ReferenceEquals(c.Current, orders.SelectedResult));
    }

    [Fact]
    public async Task The_compared_result_follows_its_tab_and_closes_with_it()
    {
        var (provider, workspace, orders, accounts) = Tabs();
        await using var _ = provider;
        var changed = new List<string?>();
        orders.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        orders.CompareWithCommand.Execute(orders.CompareChoices()[0]);
        Assert.True(orders.HasCompare);
        Assert.Same(accounts.ResultSets[0], orders.CompareResult);
        Assert.Equal("Accounts top 10", orders.CompareTitle);

        // The Accounts query runs again: the pane shows the new rows.
        var rerun = View("Result 1", 7);
        accounts.ResultSets = [rerun];
        Assert.Same(rerun, orders.CompareResult);
        Assert.Contains(nameof(QueryViewModel.CompareResult), changed);

        // A run with no result leaves the pane open, saying so.
        accounts.ResultSets = [];
        Assert.False(orders.HasCompareResult);
        Assert.Contains("no such result", orders.CompareEmptyText);
        accounts.ResultSets = [View("Result 1", 8)];
        Assert.True(orders.HasCompareResult);

        orders.ToggleCompareLayoutCommand.Execute(null);
        Assert.True(orders.CompareBelow);

        workspace.Documents.Remove(accounts);
        Assert.False(orders.HasCompare);
        Assert.Null(orders.CompareResult);
    }

    [Fact]
    public async Task A_pinned_result_stays_compared_while_the_tab_runs_again()
    {
        var (provider, _, orders, accounts) = Tabs();
        await using var _ = provider;
        accounts.PinResultCommand.Execute(accounts.ResultSets[0]);
        var pinned = accounts.Pins.Items[0];

        orders.CompareWithCommand.Execute(orders.CompareChoices().Single(c => ReferenceEquals(c.Pinned, pinned)));
        accounts.ResultSets = [View("Result 1", 99)];
        Assert.Same(pinned, orders.CompareResult);

        accounts.ClosePinnedResultCommand.Execute(pinned);
        Assert.False(orders.HasCompareResult);
        orders.CloseCompareCommand.Execute(null);
        Assert.False(orders.HasCompare);
    }

    [AvaloniaFact]
    public void The_compared_result_sits_beside_or_below_the_results_and_scrolls_with_them()
    {
        var (provider, workspace, orders, accounts) = Tabs();
        using var _ = provider;
        orders.ResultSets = [View("Result 1", [.. Enumerable.Range(1, 400)])];
        accounts.ResultSets = [View("Result 1", [.. Enumerable.Range(1, 400)])];
        var view = new QueryWorkspaceView { DataContext = workspace };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var queryView = view.GetVisualDescendants().OfType<QueryView>().Single(v => ReferenceEquals(v.DataContext, orders));
        var area = queryView.FindControl<Grid>("ResultsArea")!;
        Assert.Equal(0, area.ColumnDefinitions[2].Width.Value);
        var own = queryView.GetVisualDescendants().OfType<ResultGridView>().First(g => g.CanCompare);
        Assert.True(own.FindControl<Button>("CompareButton")!.IsVisible);

        orders.CompareWithCommand.Execute(orders.CompareChoices()[0]);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(area.ColumnDefinitions[2].Width.IsStar);
        var compared = queryView.FindControl<ResultGridView>("CompareGrid")!;
        Assert.Same(accounts.ResultSets[0], compared.ResultSet);
        Assert.False(compared.CanCompare);
        Assert.True(compared.Bounds.Width > 300);
        var at = compared.TranslatePoint(default, queryView)!.Value;
        Assert.True(at.X > 500, $"compared grid at {at}"); // beside, on the right

        // Scroll together: scrolling this tab's grid moves the compared one to the same row.
        Assert.True(ResultGridView.CanScrollTo);
        var bar = own.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Name == "PART_VerticalScrollbar");
        bar.Value = 600;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(600, compared.ScrollOffset.Vertical, 0);

        orders.CompareBelow = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, area.ColumnDefinitions[2].Width.Value);
        Assert.True(area.RowDefinitions[2].Height.IsStar);

        orders.CloseCompareCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, area.RowDefinitions[2].Height.Value);
        Assert.False(queryView.FindControl<Border>("ComparePane")!.IsVisible);
        window.Close();
    }
}
