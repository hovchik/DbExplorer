using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>Compare: another result shown beside or below this tab's results, with a splitter between them.</summary>
public partial class QueryView
{
    private QueryViewModel? _compareVm;

    /// <summary>The list of results that can be shown next to this tab's, under <paramref name="anchor"/>.</summary>
    private void ShowCompareChoices(Control anchor)
    {
        if (DataContext is not QueryViewModel vm) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var choices = vm.CompareChoices();
        if (choices.Count == 0)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = "No other results yet. Run a query in another Query tab, then pick its result here.",
                IsEnabled = false
            });
        }
        QueryViewModel? lastTab = null;
        foreach (var choice in choices)
        {
            if (lastTab is not null && !ReferenceEquals(lastTab, choice.Tab)) flyout.Items.Add(new Separator());
            lastTab = choice.Tab;
            var title = new TextBlock { Text = choice.Title, FontWeight = Avalonia.Media.FontWeight.SemiBold };
            var detail = new TextBlock { Text = choice.Detail, FontSize = 11, Classes = { "hint" } };
            var item = new MenuItem
            {
                Header = new StackPanel { Spacing = 1, Children = { title, detail } },
                Command = vm.CompareWithCommand,
                CommandParameter = choice,
                IsChecked = choice.SameChoice(vm.Compared),
                ToggleType = choice.SameChoice(vm.Compared) ? MenuItemToggleType.Radio : MenuItemToggleType.None
            };
            Avalonia.Automation.AutomationProperties.SetName(item, $"{choice.Title}, {choice.Detail}");
            flyout.Items.Add(item);
        }
        if (vm.HasCompare)
        {
            flyout.Items.Add(new Separator());
            flyout.Items.Add(new MenuItem { Header = "Close the compared result", Command = vm.CloseCompareCommand });
        }
        flyout.ShowAt(anchor);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_compareVm is not null) _compareVm.PropertyChanged -= OnCompareVmChanged;
        _compareVm = DataContext as QueryViewModel;
        if (_compareVm is not null) _compareVm.PropertyChanged += OnCompareVmChanged;
        ArrangeCompare();
    }

    private void OnCompareVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueryViewModel.HasCompare) or nameof(QueryViewModel.CompareBelow)) ArrangeCompare();
    }

    /// <summary>Results alone; or results | splitter | compared result (beside), or the same stacked (below). The grid
    /// keeps three columns and three rows (unused ones at zero size): Avalonia's Grid misses a child's new column when
    /// its definitions were added in the same pass.</summary>
    private void ArrangeCompare()
    {
        var on = _compareVm?.HasCompare == true;
        var below = _compareVm?.CompareBelow == true;
        var beside = on && !below;
        var stacked = on && below;
        var cols = ResultsArea.ColumnDefinitions;
        var rows = ResultsArea.RowDefinitions;
        cols[0].Width = GridLength.Star;
        cols[1].Width = new GridLength(beside ? 8 : 0);
        cols[2].Width = beside ? GridLength.Star : new GridLength(0);
        cols[0].MinWidth = cols[2].MinWidth = beside ? 260 : 0;
        rows[0].Height = GridLength.Star;
        rows[1].Height = new GridLength(stacked ? 8 : 0);
        rows[2].Height = stacked ? GridLength.Star : new GridLength(0);
        rows[0].MinHeight = rows[2].MinHeight = stacked ? 120 : 0;
        Place(ResultTabs, 0, below);
        Place(CompareSplitter, 1, below);
        Place(ComparePane, 2, below);
        CompareSplitter.ResizeDirection = below ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        if (on) Dispatcher.UIThread.Post(() => WatchOwnGrid(), DispatcherPriority.Loaded);
    }

    private static void Place(Control control, int index, bool below)
    {
        Grid.SetColumn(control, below ? 0 : index);
        Grid.SetRow(control, below ? index : 0);
    }

    /// <summary>The grid showing this tab's selected result.</summary>
    private ResultGridView? OwnGrid() =>
        ResultTabs.GetVisualDescendants().OfType<ResultGridView>().FirstOrDefault(g => g.IsEffectivelyVisible);

    private ResultGridView? _watchedOwnGrid;

    /// <summary>Scroll together: the compared grid follows this tab's grid, and the other way round.</summary>
    private void OnCompareScrolled(ResultGridView source, double horizontal, double vertical)
    {
        if (_compareVm?.CompareScrollTogether != true) return;
        var own = WatchOwnGrid();
        if (ReferenceEquals(source, CompareGrid)) own?.ScrollTo(horizontal, vertical);
        else CompareGrid.ScrollTo(horizontal, vertical);
    }

    /// <summary>Starts following this tab's grid (it is created by the result tabs' template, so found lazily).</summary>
    private ResultGridView? WatchOwnGrid()
    {
        var own = OwnGrid();
        if (own is not null && !ReferenceEquals(own, _watchedOwnGrid))
        {
            if (_watchedOwnGrid is not null) _watchedOwnGrid.Scrolled -= OnCompareScrolled;
            _watchedOwnGrid = own;
            own.Scrolled += OnCompareScrolled;
            own.WatchScrolling();
        }
        return own;
    }
}
