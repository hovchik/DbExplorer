using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.Converters;
using DbExplorer.Desktop.ViewModels;
using Path = Avalonia.Controls.Shapes.Path;

namespace DbExplorer.Desktop.Views;

/// <summary>One column / value pair of the row-details pane.</summary>
public sealed record RowDetailItem(string Name, string Value, bool IsNull, IBrush? Foreground)
{
    public FontStyle FontStyle => IsNull ? FontStyle.Italic : FontStyle.Normal;
}

/// <summary>Renders a <see cref="ResultSetView"/> as a DataGrid with dynamically generated columns
/// (AutoGenerateColumns doesn't work for row types with an indexer-only shape). The fetched rows
/// can be refined without re-running the query: a search box over all cells, per-column filters
/// (condition and value list) from each header's funnel, typed multi-column sort (click / Shift+click
/// a header), hidden / frozen / reordered columns, and a row-details pane. Copy and export work on
/// the rows and columns in view. When the result set knows its source tables, values can be edited in place (committed
/// or reverted from the edit bar) and foreign key values link to the row they reference.</summary>
public partial class ResultGridView : UserControl
{
    public static readonly StyledProperty<ResultSetView?> ResultSetProperty =
        AvaloniaProperty.Register<ResultGridView, ResultSetView?>(nameof(ResultSet));

    public ResultSetView? ResultSet
    {
        get => GetValue(ResultSetProperty);
        set => SetValue(ResultSetProperty, value);
    }

    private const int MaxDistinctValues = 1000;
    private const int NumericSampleRows = 200;
    private const int DetailValueMaxChars = 4000;
    private const string FunnelGeometry = "M0,0 L10,0 L6.2,4.6 L6.2,9.2 L3.8,10 L3.8,4.6 Z";

    private sealed class ColumnState(int index, string name, bool isNumeric)
    {
        public int Index { get; } = index;
        public string Name { get; } = name;
        public bool IsNumeric { get; } = isNumeric;
        public DataGridColumn Column { get; set; } = null!;
        public TextBlock SortGlyph { get; } = new() { FontSize = 10, Margin = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        public Path FilterIcon { get; } = new() { Data = Geometry.Parse(FunnelGeometry), Width = 10, Height = 10, Stretch = Stretch.Uniform };
        public Button FilterButton { get; } = new();
        public Flyout FilterFlyout { get; } = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    }

    /// <summary>What the user did to one result set; kept per result set so switching result tabs
    /// (which reuses this control) doesn't lose it.</summary>
    private sealed class ViewState
    {
        public string QuickFilter = "";
        public readonly Dictionary<int, ColumnFilter> Filters = [];
        public readonly List<ColumnSort> Sorts = [];
        public readonly HashSet<int> HiddenColumns = [];
        public int FrozenColumnCount;
        public bool PivotOn;
        public PivotSpec? Pivot;
    }

    private static readonly ConditionalWeakTable<ResultSetView, ViewState> States = new();

    private readonly List<ColumnState> _columns = [];
    private readonly DispatcherTimer _quickFilterTimer;
    private readonly Flyout _columnsFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private ViewState _state = new();
    private List<ResultRow> _viewRows = [];
    private int _viewVersion;

    /// <summary>Results at least this large are filtered and sorted off the UI thread.</summary>
    private const int BackgroundViewRows = 20_000;
    private KeyModifiers _headerModifiers;
    private bool _rebuilding;
    private double _detailsWidth = 340;

    // The cell last clicked, so context-menu actions target it rather than the keyboard's current cell.
    private ResultRow? _pressedRow;
    private int? _pressedColumn;

    // In-place editing: the grid only enters edit mode when asked to (F2, double-click, menu), never on a plain click.
    private bool _editRequested;
    private bool _isEditing;
    private bool _committing;
    private string? _retryText; // text of a rejected edit, put back when the editor reopens
    private string? _editMessage;
    private bool _editMessageIsError;
    private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#D13438"));
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private const string LinkClass = "fkLink";

    // A plain left press on a foreign key value; releasing over the same value opens the referenced row. (The grid
    // handles and captures the pointer, so the value's own Tapped event never comes.)
    private TextBlock? _pressedLink;

    private readonly MenuItem _openReferenceItem;
    private readonly MenuItem _editCellItem;
    private readonly MenuItem _setNullItem;
    private readonly MenuItem _revertCellItem;
    private readonly MenuItem _revertRowItem;
    private readonly MenuItem _addRowItem;
    private readonly MenuItem _deleteRowsItem;
    private readonly MenuItem _selectTextItem;
    private readonly Separator _editSeparator = new();

    public ResultGridView()
    {
        InitializeComponent();
        _quickFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _quickFilterTimer.Tick += (_, _) => FlushQuickFilter();

        Grid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        Grid.SelectionChanged += (_, _) => { UpdateAggregates(); UpdateDetails(); UpdateRowButtons(); };
        Grid.CurrentCellChanged += (_, _) => UpdateAggregates();
        Grid.DoubleTapped += OnGridDoubleTapped;
        Grid.Sorting += OnSorting;
        Grid.ColumnReordering += OnColumnReordering;
        Grid.CellPointerPressed += OnCellPointerPressed;
        Grid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Grid.BeginningEdit += (_, e) =>
        {
            if (!_editRequested) e.Cancel = true;
            _editRequested = false;
        };
        Grid.PreparingCellForEdit += OnPreparingCellForEdit;
        Grid.CellEditEnding += OnCellEditEnding;
        Grid.CellEditEnded += (_, _) => _isEditing = false;
        Grid.LoadingRow += (_, e) =>
        {
            e.Row.Header = (e.Row.Index + 1).ToString("N0");
            e.Row.Classes.Set("alt", e.Row.Index % 2 == 1);
        };

        FilterBox.TextChanged += (_, _) =>
        {
            if (_rebuilding) return;
            _state.QuickFilter = FilterBox.Text ?? "";
            _quickFilterTimer.Stop();
            _quickFilterTimer.Start();
        };
        FilterBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(FilterBox.Text)) { FilterBox.Text = ""; FlushQuickFilter(); e.Handled = true; }
            else if (e.Key == Key.Enter) { FlushQuickFilter(); e.Handled = true; }
            else if (e.Key == Key.Down) { Grid.Focus(); e.Handled = true; }
        };

        ColumnsButton.Flyout = _columnsFlyout;
        _columnsFlyout.Opening +=(_, _) => _columnsFlyout.Content = BuildColumnsPanel();
        DetailsToggle.IsCheckedChanged += (_, _) => SetDetailsVisible(DetailsToggle.IsChecked == true);
        DetailsFilterBox.TextChanged += (_, _) => UpdateDetails();

        _openReferenceItem = Item("Open referenced row", () => OpenReference(TargetCell()));
        _editCellItem = Item("Edit value", () => BeginCellEdit(TargetCell()));
        _editCellItem.InputGesture = new KeyGesture(Key.F2);
        _setNullItem = Item("Set to NULL", () => EditTarget((row, column) => row.SetValue(column, null)));
        _revertCellItem = Item("Revert this value", () => EditTarget((row, column) => row.RevertCell(column)));
        _revertRowItem = Item("Revert this row", () => RevertRow(TargetCell()?.Row));
        _addRowItem = Item("Add row", AddRow);
        _addRowItem.InputGesture = new KeyGesture(Key.Insert, KeyModifiers.Alt);
        _deleteRowsItem = Item("Delete selected rows", ToggleDeleteSelected);
        _deleteRowsItem.InputGesture = new KeyGesture(Key.Delete, KeyModifiers.Control);
        _selectTextItem = Item("Select text in cell", () => BeginCellEdit(TargetCell()));
        _selectTextItem.InputGesture = new KeyGesture(Key.F2);
        if (Grid.ContextMenu is { } menu)
        {
            var items = new Control[] { _openReferenceItem, _editCellItem, _setNullItem, _revertCellItem, _revertRowItem, _addRowItem, _deleteRowsItem, _editSeparator };
            for (var i = 0; i < items.Length; i++) menu.Items.Insert(i, items[i]);
            var view = menu.Items.OfType<MenuItem>().First(m => m.Header as string == "View cell value…");
            menu.Items.Insert(menu.Items.IndexOf(view) + 1, _selectTextItem);
            menu.Opening += (_, _) => UpdateCellMenu();
        }
        InitPivot();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResultSetProperty)
            Rebuild(change.GetNewValue<ResultSetView?>());
    }

    // ---------------------------------------------------------------- building

    private void Rebuild(ResultSetView? resultSet)
    {
        _rebuilding = true;
        try
        {
            _quickFilterTimer.Stop();
            Grid.FrozenColumnCount = 0;
            Grid.Columns.Clear();
            _columns.Clear();
            _pressedRow = null;
            _pressedColumn = null;
            _editMessage = null;
            _isEditing = false;

            if (resultSet is null)
            {
                _state = new ViewState();
                _viewRows = [];
                Grid.ItemsSource = null;
                FilterBox.Text = "";
                RowCountText.Text = "";
                AggregateText.Text = "";
                UpdateChips();
                UpdateDetails();
                UpdateEditBar();
                UpdateEditInfo();
                UpdateRowButtons();
                SyncPivotControls();
                return;
            }

            _state = States.GetValue(resultSet, _ => new ViewState());
            for (var i = 0; i < resultSet.Columns.Count; i++)
            {
                var index = i;
                var sample = resultSet.Rows.Take(NumericSampleRows).Select(r => index < r.Values.Count ? r.Values[index] : null);
                var state = new ColumnState(index, resultSet.Columns[index], ResultViewQuery.IsNumericColumn(sample));
                var column = new DataGridTemplateColumn
                {
                    Header = BuildHeader(state),
                    CellTemplate = new FuncDataTemplate<ResultRow>((_, _) => BuildCell(index, state.IsNumeric), supportsRecycling: true),
                    CellEditingTemplate = new FuncDataTemplate<ResultRow>((row, _) => BuildEditor(row, index, state.IsNumeric), supportsRecycling: false),
                    IsReadOnly = false, // read-only cells still open a read-only editor, to select part of the text
                    SortMemberPath = $"Values[{index}]",
                    CanUserSort = true,
                    Width = DataGridLength.Auto,
                    MinWidth = 56,
                    MaxWidth = 600,
                    IsVisible = !_state.HiddenColumns.Contains(index),
                    Tag = index
                };
                column.HeaderPointerPressed += (_, e) => _headerModifiers = e.KeyModifiers;
                state.Column = column;
                _columns.Add(state);
                Grid.Columns.Add(column);
            }
            Grid.FrozenColumnCount = Math.Min(_state.FrozenColumnCount, _columns.Count);
            FilterBox.Text = _state.QuickFilter;
            UpdateEditBar();
            UpdateEditInfo();
            UpdateRowButtons();
            SyncPivotControls();
        }
        finally
        {
            _rebuilding = false;
        }
        ApplyView();
    }

    /// <summary>A cell: the value coloured by type, tinted when edited and not committed yet, underlined when it is a
    /// foreign key value that opens the row it references.</summary>
    private Control BuildCell(int index, bool numeric)
    {
        var text = new TextBlock
        {
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var cell = new Border { Background = Brushes.Transparent, Child = text };

        // Cells are recycled and rows change when edited, so re-read the row each time; rebind brushes only when they change.
        string? colorKey = null, backgroundKey = null;
        bool? linked = null, struck = null;
        IDisposable? stateBackground = null;
        void Refresh()
        {
            var row = cell.DataContext as ResultRow;
            var value = row is null ? null : Cell(row, index);
            text.Text = ResultCellTextConverter.Instance.Convert(value, typeof(string), null, CultureInfo.CurrentCulture) as string;
            var key = CellValueColors.ResourceKey(value);
            text.FontStyle = key == CellValueColors.Null ? FontStyle.Italic : FontStyle.Normal;
            if (key != colorKey)
            {
                colorKey = key;
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(key));
            }

            // Deleted and new rows are tinted whole; otherwise only edited values are.
            var isEdited = row?.IsCellModified(index) == true;
            var stateKey = row?.State switch
            {
                ResultRowState.Deleted => "AppCellDeletedBrush",
                ResultRowState.New => "AppCellNewBrush",
                _ => isEdited ? "AppCellEditedBrush" : null
            };
            if (stateKey != backgroundKey)
            {
                backgroundKey = stateKey;
                stateBackground?.Dispose();
                stateBackground = stateKey is null ? null : cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable(stateKey));
                if (stateKey is null) cell.Background = Brushes.Transparent;
            }
            ToolTip.SetTip(cell, row?.State switch
            {
                ResultRowState.Deleted => "Deleted when you commit. Undo delete (Ctrl+Delete) keeps the row.",
                ResultRowState.New => "New row, inserted when you commit." + (ResultSet?.Source?.CanSetInNewRow(index) == true
                    ? isEdited ? "" : " Empty: the column's default is used."
                    : " This column is set by the database."),
                _ => isEdited ? "Edited, not committed yet. Was: " + OriginalText(row!.OriginalValue(index)) : null
            });
            var isStruck = row?.IsDeleted == true;
            if (isStruck != struck)
            {
                struck = isStruck;
                text.TextDecorations = isStruck ? TextDecorations.Strikethrough : linked == true ? TextDecorations.Underline : null;
            }

            var reference = value is null or DBNull ? null : ResultSet?.ReferenceOf(index);
            var isLinked = reference is not null;
            if (isLinked != linked)
            {
                linked = isLinked;
                text.TextDecorations = struck == true ? TextDecorations.Strikethrough : isLinked ? TextDecorations.Underline : null;
                text.Cursor = isLinked ? HandCursor : null;
                // Only the value itself is the link, so clicking the rest of the cell still just selects the row.
                text.HorizontalAlignment = !isLinked ? HorizontalAlignment.Stretch : numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                text.Classes.Set(LinkClass, isLinked);
            }
            ToolTip.SetTip(text, reference is null ? null : $"Click to open the {reference.TargetName} row it references");
        }

        // Bound to the row's value list, which is replaced on every edit, revert and commit.
        cell.Bind(TagProperty, new Binding(nameof(ResultRow.Values)) { Mode = BindingMode.OneWay });
        cell.PropertyChanged += (_, e) =>
        {
            if (e.Property == TagProperty || e.Property == DataContextProperty) Refresh();
        };
        return cell;
    }

    private static string OriginalText(object? value) =>
        value is null or DBNull ? "NULL" : ResultCellTextConverter.Instance.Convert(value, typeof(string), null, CultureInfo.CurrentCulture) as string ?? "";

    /// <summary>The in-place editor: a text box with the value as typed text (read-only for cells that cannot be edited,
    /// so part of the text can still be selected and copied).</summary>
    private Control BuildEditor(ResultRow? row, int index, bool numeric)
    {
        var value = row is null ? null : Cell(row, index);
        var editable = row is not null && CanEditCell(row, index);
        var initial = value is null or DBNull && !editable ? "NULL" : ResultEditSql.EditText(value);
        var box = new TextBox
        {
            Text = initial,
            Tag = initial,
            IsReadOnly = !editable,
            Padding = new Thickness(7, 0),
            MinHeight = 0,
            MinWidth = 0, // the theme's minimum would widen an auto-sized column, and that re-layout ends the edit
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left,
            Watermark = value is null or DBNull && editable ? "NULL" : null
        };
        ToolTip.SetTip(box, editable
            ? "Enter keeps the new value (Commit writes it to the database) · Esc cancels"
            : "Read-only · select text and press Ctrl+C to copy it · Esc closes");
        return box;
    }

    private Control BuildHeader(ColumnState c)
    {
        var name = new TextBlock
        {
            Text = c.Name,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        c.FilterButton.Classes.Add("headerFilter");
        c.FilterButton.Content = c.FilterIcon;
        c.FilterButton.Flyout = c.FilterFlyout;
        ToolTip.SetTip(c.FilterButton, "Filter and sort this column");
        c.FilterFlyout.Opening += (_, _) => c.FilterFlyout.Content = BuildFilterPanel(c);

        var panel = new DockPanel { Background = Brushes.Transparent };
        DockPanel.SetDock(c.FilterButton, Dock.Right);
        DockPanel.SetDock(c.SortGlyph, Dock.Right);
        panel.Children.Add(c.FilterButton);
        panel.Children.Add(c.SortGlyph);
        panel.Children.Add(name);
        var about = ResultSet is { } rs
            ? (rs.CanEdit(c.Index) ? "\n✎ Editable (double-click or F2)" : "") +
              (rs.ReferenceOf(c.Index) is { } reference ? $"\n↗ References {reference.TargetName}: click a value to open its row" : "")
            : "";
        ToolTip.SetTip(panel, $"{c.Name}{about}\nClick to sort · Shift+Click to add to the sort · right-click for more");
        panel.ContextMenu = BuildHeaderMenu(c);
        return panel;
    }

    /// <summary>The grid's drag indicator copies the header's content, and our header content is a control that is
    /// already shown in the real header, so dragging would add it to a second parent and throw. Give the indicator its
    /// own label instead.</summary>
    private void OnColumnReordering(object? sender, DataGridColumnReorderingEventArgs e)
    {
        if (e.DragIndicator is not ContentControl indicator) return;
        var state = _columns.FirstOrDefault(c => c.Column == e.Column);
        indicator.Content = new TextBlock
        {
            Text = state?.Name ?? "",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        indicator.ContentTemplate = null;
    }

    private ContextMenu BuildHeaderMenu(ColumnState c)
    {
        var menu = new ContextMenu();
        var unfreeze = Item("Unfreeze columns", () => SetFrozen(0));
        var clearSort = Item("Clear sort", () => { _state.Sorts.RemoveAll(s => s.Column == c.Index); ApplyView(); });
        var clearFilter = Item("Clear filter", () => SetFilter(c.Index, null));
        menu.Opening += (_, _) =>
        {
            unfreeze.IsEnabled = Grid.FrozenColumnCount > 0;
            clearSort.IsEnabled = _state.Sorts.Any(s => s.Column == c.Index);
            clearFilter.IsEnabled = _state.Filters.ContainsKey(c.Index);
        };
        menu.Items.Add(Item("Sort ascending", () => SetSingleSort(c.Index, descending: false)));
        menu.Items.Add(Item("Sort descending", () => SetSingleSort(c.Index, descending: true)));
        menu.Items.Add(clearSort);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Filter…", () => c.FilterFlyout.ShowAt(c.FilterButton)));
        menu.Items.Add(clearFilter);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Hide column", () => SetColumnVisible(c, false)));
        menu.Items.Add(Item("Freeze columns up to this one", () => SetFrozen(c.Column.DisplayIndex + 1)));
        menu.Items.Add(unfreeze);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy column name", () => _ = SetClipboardAsync(c.Name)));
        menu.Items.Add(Item("Copy column values in view", () => _ = SetClipboardAsync(string.Join(Environment.NewLine,
            _viewRows.Select(r => ResultExporter.FormatInvariant(Cell(r, c.Index)).ReplaceLineEndings(" "))))));
        return menu;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static object? Cell(ResultRow row, int column) => column < row.Values.Count ? row.Values[column] : null;

    // ---------------------------------------------------------------- column filter flyout

    private Control BuildFilterPanel(ColumnState c)
    {
        if (ResultSet is not { } rs) return new TextBlock();
        var flyout = c.FilterFlyout;
        var existing = _state.Filters.GetValueOrDefault(c.Index);
        var root = new StackPanel { Width = 310, Spacing = 6 };

        root.Children.Add(new TextBlock { Text = c.Name, Classes = { "section" }, Margin = new Thickness(0), TextTrimming = TextTrimming.CharacterEllipsis });

        var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sortRow.Children.Add(CompactButton("↑ Ascending", () => { SetSingleSort(c.Index, false); flyout.Hide(); }));
        sortRow.Children.Add(CompactButton("↓ Descending", () => { SetSingleSort(c.Index, true); flyout.Hide(); }));
        if (_state.Sorts.Any(s => s.Column == c.Index))
            sortRow.Children.Add(CompactButton("No sort", () => { _state.Sorts.RemoveAll(s => s.Column == c.Index); ApplyView(); flyout.Hide(); }));
        root.Children.Add(sortRow);

        // Condition
        root.Children.Add(new TextBlock { Text = "Condition", Classes = { "hint" }, Margin = new Thickness(0, 4, 0, 0) });
        var operators = Enum.GetValues<ColumnFilterOperator>();
        var opBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "(any value)" }.Concat(operators.Select(ColumnFilter.Describe)).ToList(),
            SelectedIndex = existing?.Operator is { } op ? Array.IndexOf(operators, op) + 1 : 0,
            MaxDropDownHeight = 360
        };
        var valueBox = new TextBox { Watermark = c.IsNumeric ? "Number" : "Text, number or date", Text = existing?.Value ?? "" };
        ColumnFilterOperator? SelectedOperator() => opBox.SelectedIndex > 0 ? operators[opBox.SelectedIndex - 1] : null;
        void SyncValueEnabled() => valueBox.IsEnabled = SelectedOperator() is not { } o || ColumnFilter.NeedsValue(o);
        opBox.SelectionChanged += (_, _) => SyncValueEnabled();
        valueBox.TextChanged += (_, _) =>
        {
            // Typing a value with no condition picked means the natural one for the column.
            if (opBox.SelectedIndex == 0 && !string.IsNullOrEmpty(valueBox.Text))
                opBox.SelectedIndex = Array.IndexOf(operators, c.IsNumeric ? ColumnFilterOperator.Equals : ColumnFilterOperator.Contains) + 1;
        };
        SyncValueEnabled();
        root.Children.Add(opBox);
        root.Children.Add(valueBox);

        // Value list (Excel-style), counted over rows that pass the other filters
        root.Children.Add(new TextBlock { Text = "Values", Classes = { "hint" }, Margin = new Thickness(0, 4, 0, 0) });
        var others = _state.Filters.Values.Where(f => f.Column != c.Index).ToList();
        var candidates = ResultViewQuery.Apply(rs.Rows, r => r.Values, _state.QuickFilter, others, []);
        var distinct = ResultViewQuery.DistinctValues(candidates, r => r.Values, c.Index, MaxDistinctValues, out var truncated);

        var searchBox = new TextBox { Watermark = "Search values…", MinHeight = 26, Padding = new Thickness(6, 2), FontSize = 12 };
        var selectAll = new CheckBox { Content = "(Select all)", Margin = new Thickness(0), FontWeight = FontWeight.SemiBold };
        var list = new StackPanel();
        var entries = new List<(string? Value, string Label, CheckBox Box)>();
        var syncing = false;

        void SyncSelectAll()
        {
            var visible = entries.Where(e => e.Box.IsVisible).ToList();
            syncing = true;
            selectAll.IsChecked = visible.All(e => e.Box.IsChecked == true) ? true
                : visible.All(e => e.Box.IsChecked != true) ? false : null;
            syncing = false;
        }

        foreach (var (value, count) in distinct)
        {
            var label = ValueLabel(value);
            var box = new CheckBox
            {
                Content = $"{label}   ({count:N0})",
                IsChecked = IsIncluded(existing, value),
                Margin = new Thickness(0),
                MinHeight = 24,
                FontStyle = value is null or "" ? FontStyle.Italic : FontStyle.Normal
            };
            box.IsCheckedChanged += (_, _) => { if (!syncing) SyncSelectAll(); };
            entries.Add((value, label, box));
            list.Children.Add(box);
        }
        selectAll.Click += (_, _) =>
        {
            var visible = entries.Where(e => e.Box.IsVisible).ToList();
            var target = !visible.All(e => e.Box.IsChecked == true);
            syncing = true;
            foreach (var entry in visible) entry.Box.IsChecked = target;
            syncing = false;
            SyncSelectAll();
        };
        searchBox.TextChanged += (_, _) =>
        {
            var term = searchBox.Text?.Trim() ?? "";
            foreach (var entry in entries)
                entry.Box.IsVisible = term.Length == 0 || entry.Label.Contains(term, StringComparison.OrdinalIgnoreCase);
            SyncSelectAll();
        };
        SyncSelectAll();

        root.Children.Add(searchBox);
        root.Children.Add(selectAll);
        root.Children.Add(new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = this.FindResource("AppBorderBrush") as IBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            Child = new ScrollViewer { MaxHeight = 240, Content = list }
        });
        if (truncated)
            root.Children.Add(new TextBlock
            {
                Text = $"Showing the {MaxDistinctValues:N0} most frequent values; values not listed stay included.",
                Classes = { "hint" }, FontSize = 11, TextWrapping = TextWrapping.Wrap
            });

        void Apply()
        {
            var op = SelectedOperator();
            var value = valueBox.Text ?? "";
            if (op is { } o && ColumnFilter.NeedsValue(o) && value.Length == 0) op = null;

            var included = entries.Where(e => e.Box.IsChecked == true).Select(e => e.Value).ToList();
            var excluded = entries.Where(e => e.Box.IsChecked != true).Select(e => e.Value).ToList();
            IReadOnlySet<string?>? allowedSet = null, excludedSet = null;
            if (excluded.Count > 0)
            {
                // Values beyond a truncated list must stay in, so only an exclusion list is exact there.
                if (truncated || excluded.Count <= included.Count) excludedSet = excluded.ToHashSet();
                else allowedSet = included.ToHashSet();
            }
            SetFilter(c.Index, new ColumnFilter(c.Index)
            {
                Operator = op,
                Value = op is null ? "" : value,
                AllowedValues = allowedSet,
                ExcludedValues = excludedSet
            });
            flyout.Hide();
        }

        valueBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        valueBox.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => valueBox.Focus(), DispatcherPriority.Loaded);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        var apply = CompactButton("Apply", Apply);
        apply.Classes.Add("accent");
        buttons.Children.Add(apply);
        buttons.Children.Add(CompactButton("Clear filter", () => { SetFilter(c.Index, null); flyout.Hide(); }));
        buttons.Children.Add(CompactButton("Hide column", () => { SetColumnVisible(c, false); flyout.Hide(); }));
        root.Children.Add(buttons);
        return root;
    }

    private static bool IsIncluded(ColumnFilter? filter, string? value) =>
        filter?.AllowedValues?.Contains(value) != false && filter?.ExcludedValues?.Contains(value) != true;

    private static string ValueLabel(string? value)
    {
        if (value is null) return "(NULL)";
        if (value.Trim().Length == 0) return "(empty)";
        var single = value.ReplaceLineEndings(" ↵ ");
        return single.Length > 80 ? single[..80] + "…" : single;
    }

    private static Button CompactButton(string content, Action action)
    {
        var button = new Button { Content = content, Classes = { "compact" } };
        button.Click += (_, _) => action();
        return button;
    }

    // ---------------------------------------------------------------- columns flyout

    private Control BuildColumnsPanel()
    {
        var root = new StackPanel { Width = 290, Spacing = 6 };
        var search = new TextBox { Watermark = "Find column… (Enter jumps to it)", MinHeight = 26, Padding = new Thickness(6, 2), FontSize = 12 };
        var summary = new TextBlock { Classes = { "hint" }, FontSize = 11 };
        var list = new StackPanel();
        var rows = new List<(ColumnState Column, CheckBox Box, Control Row)>();

        void UpdateSummary() =>
            summary.Text = $"{_columns.Count(c => c.Column.IsVisible):N0} of {_columns.Count:N0} columns shown" +
                           (Grid.FrozenColumnCount > 0 ? $" · {Grid.FrozenColumnCount:N0} frozen" : "");

        foreach (var c in _columns.OrderBy(c => c.Column.DisplayIndex))
        {
            var box = new CheckBox { Content = c.Name, IsChecked = c.Column.IsVisible, Margin = new Thickness(0), MinHeight = 24 };
            box.IsCheckedChanged += (_, _) => { SetColumnVisible(c, box.IsChecked == true); UpdateSummary(); };
            var go = new Button { Content = "→", Classes = { "compact" }, Padding = new Thickness(6, 0), MinHeight = 22 };
            ToolTip.SetTip(go, "Scroll to this column");
            go.Click += (_, _) => { JumpToColumn(c); _columnsFlyout.Hide(); };
            var row = new DockPanel();
            DockPanel.SetDock(go, Dock.Right);
            row.Children.Add(go);
            row.Children.Add(box);
            rows.Add((c, box, row));
            list.Children.Add(row);
        }

        search.TextChanged += (_, _) =>
        {
            var term = search.Text?.Trim() ?? "";
            foreach (var (c, _, row) in rows)
                row.IsVisible = term.Length == 0 || c.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
        };
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var match = rows.FirstOrDefault(r => r.Row.IsVisible);
            if (match.Column is null) return;
            JumpToColumn(match.Column);
            _columnsFlyout.Hide();
            e.Handled = true;
        };
        search.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(CompactButton("Show all", () => { foreach (var r in rows) r.Box.IsChecked = true; }));
        buttons.Children.Add(CompactButton("Hide all", () => { foreach (var r in rows.Where(r => r.Row.IsVisible)) r.Box.IsChecked = false; }));
        buttons.Children.Add(CompactButton("Unfreeze", () => { SetFrozen(0); UpdateSummary(); }));

        root.Children.Add(search);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { MaxHeight = 380, Content = list });
        root.Children.Add(summary);
        UpdateSummary();
        return root;
    }

    private void JumpToColumn(ColumnState c)
    {
        SetColumnVisible(c, true);
        var item = Grid.SelectedItem ?? _viewRows.FirstOrDefault();
        if (item is not null)
            Dispatcher.UIThread.Post(() => Grid.ScrollIntoView(item, c.Column), DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- view state

    private void FlushQuickFilter()
    {
        _quickFilterTimer.Stop();
        ApplyView();
    }

    /// <summary>Re-filters and re-sorts the fetched rows into the grid and refreshes everything that describes the view.
    /// Large results are filtered on a background thread so typing in a filter never freezes the window; only the
    /// latest request is shown.</summary>
    private async void ApplyView()
    {
        if (ResultSet is not { } rs) return;
        var version = ++_viewVersion;
        var quick = _state.QuickFilter;
        var filters = _state.Filters.Values.ToList();
        var sorts = _state.Sorts.ToList();
        // Rows added in the grid stay at the end, whatever the filters and sort, so they can be filled in.
        var added = rs.Rows.Where(r => r.IsNew).ToList();
        var read = added.Count == 0 ? rs.Rows : rs.Rows.Where(r => !r.IsNew).ToList();
        List<ResultRow> rows;
        if (rs.Rows.Count < BackgroundViewRows)
            rows = ResultViewQuery.Apply(read, r => r.Values, quick, filters, sorts);
        else
        {
            RowCountText.Text = $"Filtering {rs.Rows.Count:N0} row(s)…";
            try
            {
                rows = await Task.Run(() => ResultViewQuery.Apply(read, r => r.Values, quick, filters, sorts));
            }
            catch (Exception ex)
            {
                if (version == _viewVersion) RowCountText.Text = "Could not filter the rows: " + ex.Message;
                return;
            }
            if (version != _viewVersion || !ReferenceEquals(rs, ResultSet)) return;
        }
        rows.AddRange(added);
        _viewRows = rows;
        Grid.ItemsSource = _viewRows;
        AggregateText.Text = "";
        UpdateHeaders();
        UpdateChips();
        UpdateCountText();
        UpdateDetails();
        UpdatePivot();
    }

    private void SetFilter(int column, ColumnFilter? filter)
    {
        if (filter is { IsActive: true }) _state.Filters[column] = filter;
        else _state.Filters.Remove(column);
        ApplyView();
    }

    private void SetSingleSort(int column, bool descending)
    {
        _state.Sorts.Clear();
        _state.Sorts.Add(new ColumnSort(column, descending));
        ApplyView();
    }

    /// <summary>Header click cycles ascending → descending → unsorted. Plain click sorts by this
    /// column only; Shift+click adds it to (or cycles it within) the existing sort.</summary>
    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true; // the grid's own sort can't compare mixed / NULL values; ours can
        if (e.Column.Tag is not int column) return;

        var additive = _headerModifiers.HasFlag(KeyModifiers.Shift);
        _headerModifiers = KeyModifiers.None;
        var position = _state.Sorts.FindIndex(s => s.Column == column);
        var current = position >= 0 ? _state.Sorts[position] : null;
        ColumnSort? next = current switch
        {
            null => new ColumnSort(column, false),
            { Descending: false } => current with { Descending = true },
            _ => null
        };

        if (!additive)
        {
            _state.Sorts.Clear();
            if (next is not null) _state.Sorts.Add(next);
        }
        else if (position < 0) _state.Sorts.Add(next!);
        else if (next is null) _state.Sorts.RemoveAt(position);
        else _state.Sorts[position] = next;
        ApplyView();
    }

    private void SetColumnVisible(ColumnState c, bool visible)
    {
        if (c.Column.IsVisible == visible) return;
        c.Column.IsVisible = visible;
        if (visible) _state.HiddenColumns.Remove(c.Index);
        else _state.HiddenColumns.Add(c.Index);
        UpdateCountText();
        UpdateDetails();
    }

    private void SetFrozen(int count)
    {
        count = Math.Clamp(count, 0, _columns.Count);
        Grid.FrozenColumnCount = count;
        _state.FrozenColumnCount = count;
    }

    private void OnClearAll(object? sender, RoutedEventArgs e)
    {
        _state.Filters.Clear();
        _state.Sorts.Clear();
        _rebuilding = true;
        FilterBox.Text = "";
        _rebuilding = false;
        _state.QuickFilter = "";
        FlushQuickFilter();
    }

    private void UpdateHeaders()
    {
        var multi = _state.Sorts.Count > 1;
        foreach (var c in _columns)
        {
            var position = _state.Sorts.FindIndex(s => s.Column == c.Index);
            c.SortGlyph.Text = position < 0 ? "" : (_state.Sorts[position].Descending ? "▼" : "▲") + (multi ? (position + 1).ToString() : "");

            var filtered = _state.Filters.ContainsKey(c.Index);
            c.FilterIcon.Bind(Shape.FillProperty, c.FilterIcon.GetResourceObservable(
                filtered ? "SystemControlHighlightAccentBrush" : "AppHintForegroundBrush"));
            c.FilterIcon.Opacity = filtered ? 1 : 0.45;
            ToolTip.SetTip(c.FilterButton, filtered
                ? "Filtered: " + _state.Filters[c.Index].Summary(c.Name) + "\nClick to change"
                : "Filter and sort this column");
        }
    }

    private void UpdateChips()
    {
        FilterChips.Children.Clear();
        foreach (var filter in _state.Filters.Values.OrderBy(f => f.Column < _columns.Count ? _columns[f.Column].Column.DisplayIndex : f.Column))
        {
            if (filter.Column >= _columns.Count) continue;
            var c = _columns[filter.Column];
            var edit = new Button
            {
                Content = new TextBlock { Text = filter.Summary(c.Name), FontSize = 12, MaxWidth = 380, TextTrimming = TextTrimming.CharacterEllipsis },
                Padding = new Thickness(0)
            };
            ToolTip.SetTip(edit, "Edit this filter");
            edit.Click += (_, _) => c.FilterFlyout.ShowAt(edit);
            var remove = new Button { Content = "✕" };
            ToolTip.SetTip(remove, "Remove this filter");
            remove.Click += (_, _) => SetFilter(c.Index, null);
            var chip = new Border
            {
                Classes = { "chip" },
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { edit, remove } }
            };
            FilterChips.Children.Add(chip);
        }
        FilterChips.IsVisible = FilterChips.Children.Count > 0;
    }

    private void UpdateCountText()
    {
        if (ResultSet is not { } rs)
        {
            RowCountText.Text = "";
            ClearAllButton.IsVisible = false;
            return;
        }
        var rows = _viewRows.Count == rs.Rows.Count
            ? $"{rs.Rows.Count:N0} row(s)"
            : $"{_viewRows.Count:N0} of {rs.Rows.Count:N0} row(s)";
        var visible = _columns.Count(c => c.Column.IsVisible);
        var columns = visible == _columns.Count ? $"{_columns.Count:N0} column(s)" : $"{visible:N0} of {_columns.Count:N0} column(s)";
        var truncated = rs.IsTruncated ? $" · first {rs.Rows.Count:N0} of {rs.TotalRowsText} (row limit)" : "";
        var sorted = _state.Sorts.Count == 0 ? "" : " · sorted by " + string.Join(", ",
            _state.Sorts.Where(s => s.Column < _columns.Count).Select(s => $"{_columns[s.Column].Name} {(s.Descending ? "↓" : "↑")}"));
        RowCountText.Text = $"{rows} · {columns}{truncated}{sorted}";
        ClearAllButton.IsVisible = _state.Filters.Count > 0 || _state.Sorts.Count > 0 || _state.QuickFilter.Trim().Length > 0;
    }

    private IReadOnlyList<ColumnState> VisibleColumns() =>
        _columns.Where(c => c.Column.IsVisible).OrderBy(c => c.Column.DisplayIndex).ToList();

    // ---------------------------------------------------------------- row details

    private void SetDetailsVisible(bool visible)
    {
        var column = BodyGrid.ColumnDefinitions[2];
        if (!visible && column.ActualWidth > 0) _detailsWidth = column.ActualWidth;
        column.Width = new GridLength(visible ? Math.Max(_detailsWidth, 200) : 0);
        DetailsPanel.IsVisible = visible;
        DetailsSplitter.IsVisible = visible;
        UpdateDetails();
    }

    private void OnCloseDetails(object? sender, RoutedEventArgs e) => DetailsToggle.IsChecked = false;

    private void UpdateDetails()
    {
        if (!DetailsPanel.IsVisible) return;
        if (Grid.SelectedItem is not ResultRow row || ResultSet is null)
        {
            DetailsTitle.Text = "Select a row";
            DetailsList.ItemsSource = null;
            return;
        }

        DetailsTitle.Text = $"Row {Grid.SelectedIndex + 1:N0} of {_viewRows.Count:N0}";
        var term = DetailsFilterBox.Text?.Trim() ?? "";
        DetailsList.ItemsSource = VisibleColumns()
            .Select(c =>
            {
                var value = Cell(row, c.Index);
                var text = value is byte[] bytes
                    ? CellValueConverter.Instance.Convert(bytes, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string
                    : ResultViewQuery.DisplayText(value);
                if (text is { Length: > DetailValueMaxChars }) text = text[..DetailValueMaxChars] + "… (double-click the cell to see all)";
                var brush = this.TryFindResource(CellValueColors.ResourceKey(value), ActualThemeVariant, out var found) ? found as IBrush : null;
                return new RowDetailItem(c.Name, text ?? "NULL", text is null, brush);
            })
            .Where(d => term.Length == 0 ||
                        d.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        d.Value.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ---------------------------------------------------------------- cell interaction

    private int? CurrentColumnIndex => Grid.CurrentColumn?.Tag as int?;

    /// <summary>The cell a context-menu action applies to: the one right-clicked, else the keyboard's current cell.</summary>
    private (ResultRow Row, int Column)? TargetCell()
    {
        var row = _pressedRow ?? Grid.SelectedItem as ResultRow;
        var column = _pressedColumn ?? CurrentColumnIndex;
        return row is not null && column is { } index && index < _columns.Count ? (row, index) : null;
    }

    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        _pressedRow = e.Row.DataContext as ResultRow;
        _pressedColumn = e.Column.Tag as int?;
        var press = e.PointerPressedEventArgs;
        _pressedLink = press.KeyModifiers == KeyModifiers.None && press.GetCurrentPoint(Grid).Properties.IsLeftButtonPressed && !_isEditing
            ? LinkAt(e.Cell, press)
            : null;
        if (e.PointerPressedEventArgs.GetCurrentPoint(Grid).Properties.IsRightButtonPressed &&
            _pressedRow is not null && !Grid.SelectedItems.Contains(_pressedRow))
            Grid.SelectedItem = _pressedRow;
    }

    /// <summary>The cell's foreign key value when the pointer is over the value itself (not the rest of the cell).</summary>
    private static TextBlock? LinkAt(Visual cell, PointerEventArgs e) =>
        cell.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains(LinkClass)) is { } link &&
        new Rect(link.Bounds.Size).Contains(e.GetPosition(link))
            ? link
            : null;

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var link = _pressedLink;
        _pressedLink = null;
        if (link is null || e.InitialPressMouseButton != MouseButton.Left || !link.Classes.Contains(LinkClass) ||
            link.DataContext is not ResultRow row || _pressedColumn is not { } column || !ReferenceEquals(row, _pressedRow))
            return;
        if (!new Rect(link.Bounds.Size).Contains(e.GetPosition(link))) return;
        OpenReference((row, column));
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_isEditing || (e.Source as Visual)?.FindAncestorOfType<DataGridCell>(includeSelf: true) is null) return;
        if (TargetCell() is ({ } row, var column) && CanEditCell(row, column)) BeginCellEdit((row, column));
        else ViewCell(TargetCell());
    }

    private void UpdateAggregates()
    {
        var selected = Grid.SelectedItems.OfType<ResultRow>().ToList();
        if (selected.Count < 2 || CurrentColumnIndex is not { } column || column >= _columns.Count)
        {
            AggregateText.Text = "";
            return;
        }
        AggregateText.Text = $"{_columns[column].Name}: " + SelectionStatistics.Summarize(selected.Select(r => Cell(r, column)));
    }

    private void OnViewCell(object? sender, RoutedEventArgs e) => ViewCell(TargetCell());

    /// <summary>Opens a cell in a viewer (full text, JSON/XML indented).</summary>
    private void ViewCell((ResultRow Row, int Column)? target)
    {
        if (target is not ({ } row, var column)) return;
        var value = Cell(row, column);
        if (value is byte[] picture && ValueViewerWindow.TryImage(_columns[column].Name, picture) is { } imageWindow)
        {
            ShowViewer(imageWindow);
            return;
        }
        var text = value switch
        {
            null or DBNull => "NULL",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            _ => ResultViewQuery.DisplayText(value) ?? ""
        };
        ShowViewer(new ValueViewerWindow(_columns[column].Name, text));
    }

    private void ShowViewer(Window window)
    {
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }

    private void OnViewRowJson(object? sender, RoutedEventArgs e)
    {
        var columns = VisibleColumns();
        if (TargetCell() is not ({ } row, _) || columns.Count == 0) return;
        var json = ResultExporter.ToJsonObject(Names(columns), Project([row], columns)[0]);
        ShowViewer(new ValueViewerWindow("Row", json));
    }

    private void OnCopySelectedJson(object? sender, RoutedEventArgs e)
    {
        var columns = VisibleColumns();
        var rows = SelectedRows();
        if (rows.Count == 0 || columns.Count == 0) return;
        _ = SetClipboardAsync(rows.Count == 1
            ? ResultExporter.ToJsonObject(Names(columns), Project(rows, columns)[0])
            : ResultExporter.ToJson(Names(columns), Project(rows, columns)));
    }

    private void OnFilterByCell(object? sender, RoutedEventArgs e)
    {
        if (TargetCell() is not ({ } row, var column)) return;
        SetFilter(column, new ColumnFilter(column) { AllowedValues = new HashSet<string?> { ResultViewQuery.DisplayText(Cell(row, column)) } });
    }

    private void OnExcludeCell(object? sender, RoutedEventArgs e)
    {
        if (TargetCell() is not ({ } row, var column)) return;
        var text =ResultViewQuery.DisplayText(Cell(row, column));
        var existing = _state.Filters.GetValueOrDefault(column) ?? new ColumnFilter(column);
        var excluded = new HashSet<string?>(existing.ExcludedValues ?? new HashSet<string?>()) { text };
        var allowed = existing.AllowedValues?.Where(v => v != text).ToHashSet();
        SetFilter(column, existing with { ExcludedValues = excluded, AllowedValues = allowed });
    }

    private void OnClearCellColumnFilter(object? sender, RoutedEventArgs e)
    {
        if (TargetCell() is (_, var column)) SetFilter(column, null);
    }

    private void OnSortCellColumnAsc(object? sender, RoutedEventArgs e)
    {
        if (TargetCell() is (_, var column)) SetSingleSort(column, false);
    }

    private void OnSortCellColumnDesc(object? sender, RoutedEventArgs e)
    {
        if (TargetCell() is (_, var column)) SetSingleSort(column, true);
    }

    private void OnHideCellColumn(object? sender, RoutedEventArgs e)
    {
        if (TargetCell() is (_, var column)) SetColumnVisible(_columns[column], false);
    }

    private void OnFreezeCellColumn(object? sender, RoutedEventArgs e)
    {
        if (TargetCell() is (_, var column)) SetFrozen(_columns[column].Column.DisplayIndex + 1);
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (_isEditing)
        {
            // The editor keeps its own keys (Enter / Esc end the edit, Ctrl+C copies its selected text); Ctrl+S keeps the
            // value being typed and commits.
            if (e.Key == Key.S && ctrl && Grid.CommitEdit() && HasPendingEdits())
            {
                _ = CommitEditsAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                // Keep the value and stay on the row; a value that does not parse leaves the editor open.
                Grid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
                e.Handled = true;
            }
            return;
        }
        _pressedRow = null;
        _pressedColumn = null;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.C && ctrl)
        {
            // One row: the current cell's value. Several rows (or Ctrl+Shift+C): the rows.
            var rows = SelectedRows();
            if (!shift && rows.Count <= 1 && TargetCell() is ({ } row, var column))
                _ = SetClipboardAsync(ResultExporter.FormatInvariant(Cell(row, column)));
            else
                _ = CopyTsvAsync(VisibleColumns(), rows, includeHeader: shift);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None)
        {
            BeginCellEdit(TargetCell());
            e.Handled = true;
        }
        else if (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Alt && ResultSet?.CanEditRows == true)
        {
            AddRow();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && ctrl && ResultSet?.CanEditRows == true)
        {
            ToggleDeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.S && ctrl && HasPendingEdits())
        {
            _ = CommitEditsAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F && ctrl)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            ViewCell(TargetCell());
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- editing and references

    /// <summary>The cell can be changed: an editable column of the result, a value the editor understands, and a row whose
    /// key is known (a LEFT JOIN may leave it NULL).</summary>
    private bool CanEditCell(ResultRow row, int column)
    {
        if (row.IsDeleted || column >= row.Values.Count) return false;
        if (row.IsNew) return ResultSet is { CanEditRows: true, Source: { } added } && added.CanSetInNewRow(column);
        if (ResultSet is not { Source: { } source } rs || !rs.CanEdit(column)) return false;
        if (!ResultEditSql.IsEditableValue(row.OriginalValue(column)) || !ResultEditSql.IsEditableValue(row.Values[column])) return false;
        return source.Tables[source.Columns[column]!.Table].Key.All(k => row.OriginalValue(k.ResultColumn) is not (null or DBNull));
    }

    private bool HasPendingEdits() => ResultSet?.Rows.Any(r => r.HasChanges) == true;

    /// <summary>Opens the in-place editor on a cell (read-only for cells that cannot be edited).</summary>
    private void BeginCellEdit((ResultRow Row, int Column)? target)
    {
        if (_isEditing || target is not ({ } row, var column) || column >= _columns.Count) return;
        var gridColumn = _columns[column].Column;
        if (!gridColumn.IsVisible || !_viewRows.Contains(row)) return;
        try
        {
            if (!ReferenceEquals(Grid.SelectedItem, row) || Grid.SelectedItems.Count != 1) Grid.SelectedItem = row;
            if (!ReferenceEquals(Grid.CurrentColumn, gridColumn)) Grid.CurrentColumn = gridColumn;
            Grid.ScrollIntoView(row, gridColumn);
            _editRequested = true;
            Grid.BeginEdit();
        }
        catch (InvalidOperationException)
        {
            // The grid had no current row yet; the next F2 / double-click works.
        }
        finally
        {
            _editRequested = false;
        }
        if (!_isEditing && row.IsDeleted)
            ShowEditMessage("This row is deleted when you commit. Undo delete (Ctrl+Delete) to edit it.", error: true);
        else if (!_isEditing && row.IsNew && !CanEditCell(row, column))
            ShowEditMessage("This column of a new row is set by the database (identity, computed, or not a column of " +
                            $"{ResultSet?.Source?.RowTable?.Table.FullName ?? "the table"}).", error: true);
        else if (!_isEditing && !row.IsNew && ResultSet?.CanEdit(column) == true && !CanEditCell(row, column))
            ShowEditMessage("This value cannot be edited: " + (row.Values[column] is { } v && !ResultEditSql.IsEditableValue(v)
                ? $"values of type {v.GetType().Name} are read-only here."
                : "the row has no key value (e.g. the missing side of an outer join)."), error: true);
    }

    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        _isEditing = true;
        if (e.EditingElement is not TextBox box) return;
        if (_retryText is { } retry && !box.IsReadOnly) box.Text = retry;
        _retryText = null;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Takes the typed text as the cell's new value, converted to the column's type; a value that does not parse
    /// keeps the editor open with the reason in the edit bar.</summary>
    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.EditingElement is not TextBox { IsReadOnly: false } box ||
            e.Row.DataContext is not ResultRow row || e.Column.Tag is not int column || !CanEditCell(row, column))
            return;
        var text = box.Text ?? "";
        if (text == box.Tag as string) return;
        try
        {
            var value = ResultEditSql.ParseValue(text, row.OriginalValue(column), ResultSet?.Source?.ColumnSource(column)?.Column);
            row.SetValue(column, value);
            if (_editMessageIsError) _editMessage = null;
            UpdateEditBar();
            UpdateDetails();
        }
        catch (FormatException ex)
        {
            // The grid may close the editor anyway (Enter): reopen it with the typed text so it can be corrected.
            e.Cancel = true;
            ShowEditMessage(ex.Message + " Fix it, or press Esc to cancel the edit.", error: true);
            Dispatcher.UIThread.Post(() =>
            {
                if (_isEditing) return;
                _retryText = text;
                BeginCellEdit((row, column));
                _retryText = null;
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>Applies a change to the targeted cell when it is editable (Set to NULL, Revert…).</summary>
    private void EditTarget(Action<ResultRow, int> change)
    {
        if (TargetCell() is not ({ } row, var column)) return;
        if (_isEditing) Grid.CancelEdit();
        change(row, column);
        UpdateEditBar();
        UpdateDetails();
    }

    private void OpenReference((ResultRow Row, int Column)? target)
    {
        if (target is not ({ } row, var column) || ResultSet is not { OpenReference: { } open } rs || rs.ReferenceOf(column) is not { } reference) return;
        if (Cell(row, column) is null or DBNull) return;
        open(rs, reference, row);
    }

    private void UpdateCellMenu()
    {
        var target = TargetCell();
        var rs = ResultSet;
        var row = target?.Row;
        var column = target?.Column ?? -1;

        var reference = row is null ? null : rs?.ReferenceOf(column);
        _openReferenceItem.IsVisible = reference is not null;
        _openReferenceItem.IsEnabled = row is not null && Cell(row, column) is not (null or DBNull);
        _openReferenceItem.Header = reference is null ? "Open referenced row" : $"Open referenced {reference.TargetName} row";

        var editableResult = rs?.Source?.HasEditableColumns == true && rs.CommitEdits is not null;
        var editable = row is not null && CanEditCell(row, column);
        foreach (var item in new[] { _editCellItem, _setNullItem, _revertCellItem, _revertRowItem }) item.IsVisible = editableResult;
        _editCellItem.IsEnabled = editable;
        _setNullItem.IsEnabled = editable && Cell(row!, column) is not (null or DBNull) &&
                                 rs?.Source?.ColumnSource(column)?.Column.IsNullable != false;
        _revertCellItem.IsEnabled = row?.IsCellModified(column) == true;
        _revertRowItem.IsEnabled = row?.HasChanges == true;
        _revertRowItem.Header = row?.IsNew == true ? "Remove this new row" : "Revert this row";
        var rowsEditable = rs?.CanEditRows == true;
        _addRowItem.IsVisible = _deleteRowsItem.IsVisible = rowsEditable;
        var selected = SelectedRows();
        _deleteRowsItem.IsEnabled = selected.Count > 0;
        _deleteRowsItem.Header = DeleteHeader(selected);
        _editSeparator.IsVisible = reference is not null || editableResult;
        _selectTextItem.IsVisible = !editable;
        _selectTextItem.IsEnabled = row is not null;
    }

    private void UpdateEditInfo()
    {
        var rs = ResultSet;
        var editable = rs?.Source?.HasEditableColumns == true && rs.CommitEdits is not null;
        var links = rs?.OpenReference is not null && rs.Source?.HasReferences == true;
        if (rs?.Source is null || (rs.CommitEdits is null && !links))
        {
            EditInfoText.IsVisible = false;
            return;
        }

        var tables = string.Join(", ", rs.Source.Tables.Where(t => t.IsEditable).Select(t => t.Table.FullName));
        EditInfoText.Text = (editable ? "✎ Editable" : "Read-only") + (links ? " · ↗ links" : "");
        ToolTip.SetTip(EditInfoText, string.Join("\n", new[]
        {
            editable
                ? $"Double-click or F2 edits a value of {tables}. Edits are written to the database only when you press Commit (Ctrl+S); Revert discards them." +
                  (rs.CanEditRows ? " Alt+Insert adds a row and Ctrl+Delete deletes the selected ones; the SQL is shown for review before it runs." : "")
                : rs.Source.ReadOnlyReason,
            links ? "Underlined values reference a row of another table: click one to open that row." : null
        }.Where(t => t is not null)));
        EditInfoText.IsVisible = true;
    }

    private void ShowEditMessage(string? message, bool error = false)
    {
        _editMessage = message;
        _editMessageIsError = error && message is not null;
        UpdateEditBar();
    }

    private void UpdateEditBar()
    {
        var all = ResultSet?.Rows ?? [];
        var edited = all.Where(r => r.State == ResultRowState.Unchanged && r.IsModified).ToList();
        var cells = edited.Sum(r => r.ModifiedColumns.Count);
        var added = all.Count(r => r.IsNew);
        var deleted = all.Count(r => r.IsDeleted);
        var pending = cells + added + deleted > 0;

        CommitEditsButton.IsVisible = RevertEditsButton.IsVisible = pending;
        CommitEditsButton.IsEnabled = RevertEditsButton.IsEnabled = !_committing && ResultSet?.CommitEdits is not null;
        DismissEditMessageButton.IsVisible = !pending && _editMessage is not null;
        var parts = new List<string>();
        if (cells > 0) parts.Add($"{cells:N0} edited value(s) in {edited.Count:N0} row(s)");
        if (added > 0) parts.Add($"{added:N0} new row(s)");
        if (deleted > 0) parts.Add($"{deleted:N0} row(s) to delete");
        EditStatusText.Text = pending
            ? $"✎ {string.Join(", ", parts)}, not committed yet" + (_editMessage is null ? "" : " · " + _editMessage)
            : _editMessage;
        if (_editMessageIsError) EditStatusText.Foreground = ErrorBrush;
        else EditStatusText.ClearValue(TextBlock.ForegroundProperty);
        EditBar.IsVisible = pending || _editMessage is not null;
    }

    private void OnCommitEdits(object? sender, RoutedEventArgs e) => _ = CommitEditsAsync();

    /// <summary>Writes every edited row of the result set (also those hidden by filters) through the host.</summary>
    private async Task CommitEditsAsync()
    {
        if (_committing || ResultSet is not { CommitEdits: { } commit } rs) return;
        if (_isEditing) Grid.CommitEdit();
        var rows = rs.Rows.Where(r => r.HasChanges).ToList();
        if (rows.Count == 0) return;

        _committing = true;
        ShowEditMessage("Saving…");
        try
        {
            var message = await commit(rs, rows);
            if (rs.RemoveRows(r => r.State == ResultRowState.Removed) > 0 && ReferenceEquals(rs, ResultSet)) ApplyView();
            if (ReferenceEquals(rs, ResultSet)) ShowEditMessage(message);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(rs, ResultSet)) ShowEditMessage("Not saved: " + ex.Message, error: true);
        }
        finally
        {
            _committing = false;
            UpdateEditBar();
            UpdateRowButtons();
            UpdateDetails();
        }
    }

    private void OnRevertEdits(object? sender, RoutedEventArgs e)
    {
        if (ResultSet is not { } rs) return;
        if (_isEditing) Grid.CancelEdit();
        var hadNew = rs.Rows.Any(r => r.IsNew);
        var count = ResultEditing.Revert(rs);
        if (hadNew) ApplyView();
        ShowEditMessage(count > 0 ? $"Reverted the changes of {count:N0} row(s)." : null);
        UpdateRowButtons();
        UpdateDetails();
    }

    // ---------------------------------------------------------------- adding and deleting rows

    private void OnAddRow(object? sender, RoutedEventArgs e) => AddRow();

    private void OnDeleteRows(object? sender, RoutedEventArgs e) => ToggleDeleteSelected();

    /// <summary>Adds an empty row at the end of the grid and opens the editor on its first cell that takes a value.</summary>
    private void AddRow()
    {
        if (ResultSet is not { } rs || _committing) return;
        if (_isEditing && !Grid.CommitEdit()) return;
        if (rs.AddRow() is not { } row)
        {
            ShowEditMessage(rs.Source?.RowEditReason ?? "Rows cannot be added to this result.", error: true);
            return;
        }
        ApplyView();
        UpdateEditBar();
        var first = _columns.FirstOrDefault(c => c.Column.IsVisible && rs.Source!.CanSetInNewRow(c.Index));
        Dispatcher.UIThread.Post(() =>
        {
            if (!_viewRows.Contains(row)) return;
            Grid.SelectedItem = row;
            Grid.ScrollIntoView(row, first?.Column ?? _columns.FirstOrDefault()?.Column);
            if (first is not null) BeginCellEdit((row, first.Index));
        }, DispatcherPriority.Background);
    }

    /// <summary>Marks the selected rows for deletion, or, when every selected row is already marked, undoes it. New rows are
    /// simply removed.</summary>
    private void ToggleDeleteSelected()
    {
        if (ResultSet is not { CanEditRows: true } rs || _committing) return;
        var rows = SelectedRows();
        if (rows.Count == 0 && TargetCell() is ({ } target, _)) rows = [target];
        if (rows.Count == 0) return;
        if (_isEditing) Grid.CancelEdit();

        var removedNew = rs.RemoveRows(r => r.IsNew && rows.Contains(r));
        var existing = rows.Where(r => !r.IsNew).ToList();
        if (existing.Count > 0 && existing.All(r => r.IsDeleted))
            foreach (var row in existing) row.UndoDelete();
        else
        {
            var keyless = existing.Where(r => !r.IsDeleted && !HasKey(rs, r)).ToList();
            foreach (var row in existing.Except(keyless)) row.MarkDeleted();
            if (keyless.Count > 0)
                ShowEditMessage($"{keyless.Count:N0} row(s) have no key value and were not marked for deletion.", error: true);
        }
        if (removedNew > 0) ApplyView();
        UpdateEditBar();
        UpdateRowButtons();
        UpdateDetails();
    }

    private static bool HasKey(ResultSetView rs, ResultRow row) =>
        rs.Source?.RowTable?.Key.All(k => row.OriginalValue(k.ResultColumn) is not (null or DBNull)) == true;

    /// <summary>"Discards" a new row; reverts the edits and the deletion of any other.</summary>
    private void RevertRow(ResultRow? row)
    {
        if (row is null || ResultSet is not { } rs) return;
        if (_isEditing) Grid.CancelEdit();
        if (row.IsNew)
        {
            rs.RemoveRows(r => ReferenceEquals(r, row));
            ApplyView();
        }
        else row.Revert();
        UpdateEditBar();
        UpdateRowButtons();
        UpdateDetails();
    }

    private static string DeleteHeader(IReadOnlyList<ResultRow> selected) =>
        selected.Count > 0 && selected.All(r => r.IsDeleted) ? "Undo delete"
        : selected.Count > 0 && selected.All(r => r.IsNew) ? "Remove new row(s)"
        : selected.Count > 1 ? $"Delete {selected.Count:N0} selected rows" : "Delete row";

    /// <summary>Shows the add / delete buttons on results whose rows can be edited, disabled with the reason when rows of
    /// this one cannot be added or deleted.</summary>
    private void UpdateRowButtons()
    {
        var rs = ResultSet;
        var shown = rs is { CommitEdits: not null, Source: { } source } && (source.HasEditableColumns || source.RowTable is not null);
        AddRowButton.IsVisible = DeleteRowsButton.IsVisible = shown;
        if (!shown) return;

        var canEdit = rs!.CanEditRows;
        var reason = rs.Source!.RowEditReason;
        AddRowButton.IsEnabled = canEdit && !_committing;
        ToolTip.SetTip(AddRowButton, canEdit
            ? $"Add a row to {rs.Source.RowTable!.Table.FullName} (Alt+Insert). It is inserted when you commit, after you review the SQL."
            : reason);
        var selected = SelectedRows();
        DeleteRowsButton.IsEnabled = canEdit && !_committing && selected.Count > 0;
        DeleteRowsButton.Content = selected.Count > 0 && selected.All(r => r.IsDeleted) ? "↶ Undo delete" : "− Delete";
        ToolTip.SetTip(DeleteRowsButton, !canEdit
            ? reason
            : selected.Count == 0
                ? "Select the rows to delete first."
                : "Mark the selected rows for deletion, or undo it (Ctrl+Delete). Rows are deleted by key when you commit, after you review the SQL.");
    }

    private void OnDismissEditMessage(object? sender, RoutedEventArgs e) => ShowEditMessage(null);

    // ---------------------------------------------------------------- copy / export

    /// <summary>Selected rows in view order (selection order is click order).</summary>
    private IReadOnlyList<ResultRow> SelectedRows()
    {
        var selected = Grid.SelectedItems.OfType<ResultRow>().ToHashSet(ReferenceEqualityComparer.Instance);
        return selected.Count == 0 ? [] : _viewRows.Where(selected.Contains).ToList();
    }

    private static IReadOnlyList<IReadOnlyList<object?>> Project(IEnumerable<ResultRow> rows, IReadOnlyList<ColumnState> columns) =>
        rows.Select(r => (IReadOnlyList<object?>)columns.Select(c => Cell(r, c.Index)).ToArray()).ToList();

    private static IReadOnlyList<string> Names(IReadOnlyList<ColumnState> columns) => columns.Select(c => c.Name).ToList();

    private void OnCopyAll(object? sender, RoutedEventArgs e) => _ = CopyTsvAsync(VisibleColumns(), _viewRows, includeHeader: true);

    private void OnCopyEverything(object? sender, RoutedEventArgs e)
    {
        if (ResultSet is { } rs && rs.Rows.Count > 0)
            _ = SetClipboardAsync(ResultExporter.ToTsv(rs.Columns, rs.Rows.Select(r => r.Values), includeHeader: true));
    }

    private void OnCopySelected(object? sender, RoutedEventArgs e) => _ = CopyTsvAsync(VisibleColumns(), SelectedRows(), includeHeader: false);

    private void OnCopySelectedWithHeader(object? sender, RoutedEventArgs e) => _ = CopyTsvAsync(VisibleColumns(), SelectedRows(), includeHeader: true);

    private void OnCopyAllInsert(object? sender, RoutedEventArgs e) => _ = CopyInsertAsync(_viewRows);

    private void OnCopySelectedInsert(object? sender, RoutedEventArgs e) => _ = CopyInsertAsync(SelectedRows());

    private void OnCopyCell(object? sender, RoutedEventArgs e)
    {
        if (TargetCell() is ({ } row, var column))
            _ = SetClipboardAsync(ResultExporter.FormatInvariant(Cell(row, column)));
    }

    private void OnExportCsv(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Csv);
    private void OnExportExcel(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Excel);
    private void OnExportJson(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Json);
    private void OnExportMarkdown(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Markdown);
    private void OnExportInsert(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Insert);
    private void OnExportHtml(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Html);

    private Task CopyTsvAsync(IReadOnlyList<ColumnState> columns, IReadOnlyList<ResultRow> rows, bool includeHeader)
    {
        if (ResultSet is null || rows.Count == 0 || columns.Count == 0) return Task.CompletedTask;
        return SetClipboardAsync(ResultExporter.ToTsv(Names(columns), Project(rows, columns), includeHeader));
    }

    private Task CopyInsertAsync(IReadOnlyList<ResultRow> rows)
    {
        var columns = VisibleColumns();
        if (ResultSet is not { } rs || rows.Count == 0 || columns.Count == 0) return Task.CompletedTask;
        return SetClipboardAsync(BuildInsert(rs, Names(columns), Project(rows, columns)));
    }

    private static string BuildInsert(ResultSetView rs, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows) =>
        ResultExporter.ToInsertStatements(columns, rows, rs.SourceTable ?? rs.Quote("target_table"), rs.Dialect, rs.Quote);

    private async Task SetClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private async Task ExportAsync(ExportFormat format)
    {
        if (ResultSet is not { } rs || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var extension = ResultExporter.FileExtension(format);
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export results",
            SuggestedFileName = SuggestedName(rs) + "." + extension,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(format.ToString()) { Patterns = ["*." + extension] }]
        });
        if (file is null) return;

        var visible = VisibleColumns();
        var columns = Names(visible);
        var rows = Project(_viewRows, visible);
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            if (format == ExportFormat.Excel)
            {
                ResultExporter.WriteXlsx(stream, columns, rows, rs.Title);
                RowCountText.Text = $"Exported {rows.Count:N0} row(s) to {file.Name}";
                return;
            }

            var text = format switch
            {
                ExportFormat.Csv => ResultExporter.ToCsv(columns, rows),
                ExportFormat.Json => ResultExporter.ToJson(columns, rows),
                ExportFormat.Markdown => ResultExporter.ToMarkdown(columns, rows),
                ExportFormat.Html => ResultExporter.ToHtml(columns, rows, new HtmlExportInfo(rs.SourceTable ?? rs.Title, DateTimeOffset.Now)
                {
                    Source = rs.SourceTable is null ? rs.Title : rs.SourceTable,
                    Connection = rs.Connection,
                    FetchedRowCount = rs.Rows.Count,
                    FetchedColumnCount = _columns.Count,
                    IsTruncated = rs.IsTruncated,
                    TotalRowCount = rs.TotalRowCount,
                    TotalRowCountIsExact = rs.TotalRowCountIsExact
                }),
                _ => BuildInsert(rs, columns, rows)
            };
            // BOM so Excel detects UTF-8 when opening CSV directly.
            var encoding = format == ExportFormat.Csv ? new UTF8Encoding(true) : new UTF8Encoding(false);
            await using var writer = new StreamWriter(stream, encoding);
            await writer.WriteAsync(text);
            RowCountText.Text = $"Exported {rows.Count:N0} row(s) to {file.Name}";
        }
        catch (Exception ex)
        {
            RowCountText.Text = "Export failed: " + ex.Message;
        }
    }

    private static string SuggestedName(ResultSetView rs)
    {
        var name = rs.SourceTable ?? rs.Title;
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(['[', ']', '"', ' ']).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('_');
        return cleaned.Length == 0 ? "results" : cleaned;
    }
}
