using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DbExplorer.Desktop.Controls;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>
/// The Side by side tab: one card per result (where it came from, then its grid), laid out as columns or rows with
/// splitters between them. Cards are kept while their result stays on the board, so a grid keeps its filters, sort and
/// scroll position when the layout changes or another result is added.
/// </summary>
public partial class ResultsBoardView : UserControl
{
    /// <summary>A result's card and its parts that change with its position.</summary>
    private sealed class Pane
    {
        public required BoardResult Item { get; init; }
        public required Border Root { get; init; }
        public required ResultGridView Grid { get; init; }
        public required TextBlock Number { get; init; }
        public required Button Back { get; init; }
        public required Button Forward { get; init; }
        public required StrokeIcon BackIcon { get; init; }
        public required StrokeIcon ForwardIcon { get; init; }
    }

    private const double MinPaneSize = 220;

    private readonly Dictionary<BoardResult, Pane> _panes = new(ReferenceEqualityComparer.Instance);
    private ResultsBoardViewModel? _vm;

    public ResultsBoardView()
    {
        InitializeComponent();
        ColumnsButton.Click += (_, _) => { if (_vm is not null) _vm.Layout = BoardLayout.Columns; };
        RowsButton.Click += (_, _) => { if (_vm is not null) _vm.Layout = BoardLayout.Rows; };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.Changed -= Arrange;
        _vm = DataContext as ResultsBoardViewModel;
        if (_vm is not null) _vm.Changed += Arrange;
        Arrange();
    }

    /// <summary>Lays the cards out again: new ones built, removed ones dropped, all in the board's order.</summary>
    private void Arrange()
    {
        var items = _vm?.Items.ToList() ?? [];
        foreach (var gone in _panes.Keys.Where(k => !items.Contains(k)).ToList()) _panes.Remove(gone);

        Board.Children.Clear();
        Board.ColumnDefinitions.Clear();
        Board.RowDefinitions.Clear();
        if (items.Count == 0) return;

        var columns = _vm!.Layout == BoardLayout.Columns;
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                // A splitter between two cards; the grip comes from the app-wide GridSplitter style.
                if (columns) Board.ColumnDefinitions.Add(new ColumnDefinition(8, GridUnitType.Pixel));
                else Board.RowDefinitions.Add(new RowDefinition(8, GridUnitType.Pixel));
                var splitter = new GridSplitter
                {
                    ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                    Background = Brushes.Transparent
                };
                ToolTip.SetTip(splitter, "Drag to give a result more room");
                Place(splitter, (columns ? Board.ColumnDefinitions.Count : Board.RowDefinitions.Count) - 1, columns);
                Board.Children.Add(splitter);
            }
            if (columns) Board.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = MinPaneSize });
            else Board.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 160 });

            var pane = PaneFor(items[i]);
            Update(pane, i, items.Count, columns);
            Place(pane.Root, (columns ? Board.ColumnDefinitions.Count : Board.RowDefinitions.Count) - 1, columns);
            Board.Children.Add(pane.Root);
        }
    }

    private static void Place(Control control, int index, bool columns)
    {
        Grid.SetColumn(control, columns ? index : 0);
        Grid.SetRow(control, columns ? 0 : index);
    }

    private void Update(Pane pane, int index, int count, bool columns)
    {
        pane.Number.Text = (index + 1).ToString();
        pane.Back.IsEnabled = index > 0;
        pane.Forward.IsEnabled = index < count - 1;
        pane.BackIcon.Data = columns ? Icons.ChevronLeft : Icons.ChevronUp;
        pane.ForwardIcon.Data = columns ? Icons.ChevronRight : Icons.ChevronDown;
        var back = columns ? "Move left" : "Move up";
        var forward = columns ? "Move right" : "Move down";
        ToolTip.SetTip(pane.Back, back);
        ToolTip.SetTip(pane.Forward, forward);
        AutomationProperties.SetName(pane.Back, back);
        AutomationProperties.SetName(pane.Forward, forward);
    }

    private Pane PaneFor(BoardResult item)
    {
        if (_panes.TryGetValue(item, out var pane)) return pane;

        var number = new TextBlock();
        var badge = new Border { Classes = { "number" }, Child = number, Margin = new Thickness(0, 0, 8, 0) };

        var title = new TextBlock
        {
            Text = item.Title,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var detail = new TextBlock { Text = item.Detail, Classes = { "hint" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } };
        Grid.SetColumn(text, 1);

        var backIcon = new StrokeIcon { Width = 14, Height = 14 };
        var forwardIcon = new StrokeIcon { Width = 14, Height = 14 };
        var back = Tool(backIcon);
        var forward = Tool(forwardIcon);
        var remove = Tool(new StrokeIcon { Data = Icons.Close, Width = 12, Height = 12 });
        ToolTip.SetTip(remove, "Remove from the board");
        AutomationProperties.SetName(remove, "Remove from the board");
        back.Click += (_, _) => _vm?.MoveLeftCommand.Execute(item);
        forward.Click += (_, _) => _vm?.MoveRightCommand.Execute(item);
        remove.Click += (_, _) => _vm?.RemoveCommand.Execute(item);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 0, 0, 0), Children = { back, forward, remove } };
        Grid.SetColumn(tools, 2);

        var header = new Border
        {
            Classes = { "paneHeader" },
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { badge, text, tools } }
        };
        ToolTip.SetTip(header, item.ToolTip);
        DockPanel.SetDock(header, Dock.Top);

        var grid = new ResultGridView { ResultSet = item.View, Margin = new Thickness(6, 6, 6, 4) };
        AutomationProperties.SetName(grid, item.Title);
        grid.Scrolled += OnGridScrolled;
        grid.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(grid.WatchScrolling, DispatcherPriority.Loaded);

        var root = new Border { Classes = { "pane" }, Child = new DockPanel { Children = { header, grid } } };
        pane = new Pane { Item = item, Root = root, Grid = grid, Number = number, Back = back, Forward = forward, BackIcon = backIcon, ForwardIcon = forwardIcon };
        _panes[item] = pane;
        return pane;
    }

    private static Button Tool(StrokeIcon icon) => new() { Classes = { "paneTool" }, Content = icon };

    /// <summary>"Scroll together": the other grids follow the one the user scrolled.</summary>
    private void OnGridScrolled(ResultGridView source, double horizontal, double vertical)
    {
        if (_vm?.SyncScroll != true) return;
        foreach (var pane in _panes.Values)
            if (!ReferenceEquals(pane.Grid, source)) pane.Grid.ScrollTo(horizontal, vertical);
    }
}
