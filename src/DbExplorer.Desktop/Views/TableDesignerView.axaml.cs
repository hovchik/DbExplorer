using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class TableDesignerView : UserControl
{
    private TableDesignerViewModel? _vm;

    /// <summary>The menu a ⋯, ＋ or Common columns button showed last (the headless renders close it).</summary>
    public MenuFlyout? LastMenu { get; private set; }

    public TableDesignerView()
    {
        InitializeComponent();
        Keymap.Bind(this, () => DataContext is not TableDesignerViewModel vm ? [] :
        [
            (ShortcutCommands.DesignerExecute, vm.ExecuteCommand),
            (ShortcutCommands.DesignerAddColumn, vm.AddColumnCommand),
            (ShortcutCommands.DesignerRemoveColumn, vm.RemoveColumnCommand),
            (ShortcutCommands.DesignerColumnUp, vm.MoveColumnUpCommand),
            (ShortcutCommands.DesignerColumnDown, vm.MoveColumnDownCommand),
            (ShortcutCommands.DesignerCopyScript, vm.CopyScriptCommand)
        ]);
        Body.SizeChanged += (_, e) => SetNarrow(e.NewSize.Width < NarrowWidth);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.FocusColumnRequested -= FocusColumn;
            _vm = DataContext as TableDesignerViewModel;
            if (_vm is not null) _vm.FocusColumnRequested += FocusColumn;
        };
    }

    /// <summary>Below this width the editor needs the whole width; the script and suggestions get a tab of their own.</summary>
    private const double NarrowWidth = 1000;

    public bool IsNarrow { get; private set; }

    private void SetNarrow(bool narrow)
    {
        if (narrow == IsNarrow) return;
        IsNarrow = narrow;
        if (narrow)
        {
            SideHost.Child = null;
            SideTabHost.Child = SidePanel;
        }
        else
        {
            if (Sections.SelectedItem == SideTab) Sections.SelectedIndex = 0;
            SideTabHost.Child = null;
            SideHost.Child = SidePanel;
        }
        SideTab.IsVisible = narrow;
        // On its own tab the panel shares the height with the script, so the list scrolls sooner.
        SuggestionScroll.MaxHeight = narrow ? 180 : 300;
        SideHost.IsVisible = !narrow;
        SideSplitter.IsVisible = !narrow;
        var columns = Body.ColumnDefinitions;
        columns[1].Width = new GridLength(narrow ? 0 : 6);
        columns[2].MinWidth = narrow ? 0 : 260;
        columns[2].Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
    }

    /// <summary>The "to fix first" button: shows the suggestions, which in a narrow window are on their own tab.</summary>
    private void OnShowSuggestionsClick(object? sender, RoutedEventArgs e)
    {
        if (IsNarrow) Sections.SelectedItem = SideTab;
    }

    /// <summary>Type, schema and table pickers open their list on focus, so the choices show before anything is typed.</summary>
    private void OnPickerFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = true;
    }

    private void OnOpenTableKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || _vm is null) return;
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = false;
        _vm.OpenExistingTableCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>The row with the caret is the one the move and remove keys act on.</summary>
    private void OnColumnRowFocus(object? sender, GotFocusEventArgs e)
    {
        if (_vm is not null && sender is Control { DataContext: ColumnRow row }) _vm.SelectedColumn = row;
    }

    /// <summary>Star-sized editor columns need a width to share: the viewport's, but never less than the editor's
    /// MinWidth, below which it scrolls sideways instead of squeezing the boxes.</summary>
    private void OnEditorSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer { Content: Control content })
            content.Width = Math.Max(e.NewSize.Width - 14, content.MinWidth);
    }

    /// <summary>A new column: the Columns tab, scrolled to it, with the caret in its name.</summary>
    private void FocusColumn(ColumnRow row)
    {
        Sections.SelectedIndex = 0;
        Dispatcher.UIThread.Post(() =>
        {
            if (ColumnList.ContainerFromItem(row) is not Control container) return;
            container.BringIntoView();
            container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
        }, DispatcherPriority.Background);
    }

    private void OnCommonColumnsClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Control button) return;
        var menu = new MenuFlyout();
        foreach (var choice in _vm.CommonColumnChoices)
        {
            var item = new MenuItem
            {
                Header = $"{choice.Label}  ·  {choice.Column.Name} {choice.Column.FullType}".Replace("_", "__"),
                Command = _vm.AddCommonColumnCommand,
                CommandParameter = choice
            };
            ToolTip.SetTip(item, choice.Hint);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "The table has all of them already", IsEnabled = false });
        LastMenu = menu;
        menu.ShowAt(button);
    }

    private void OnColumnMenuClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Control { DataContext: ColumnRow row } button) return;
        _vm.SelectedColumn = row;
        MenuItem Item(string header, System.Windows.Input.ICommand command, string? keys = null)
        {
            var item = new MenuItem { Header = header, Command = command, CommandParameter = row };
            if (keys is not null) Keymap.SetCommand(item, keys);
            return item;
        }
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Move up", _vm.MoveColumnUpCommand, ShortcutCommands.DesignerColumnUp));
        menu.Items.Add(Item("Move down", _vm.MoveColumnDownCommand, ShortcutCommands.DesignerColumnDown));
        menu.Items.Add(Item("Index this column", _vm.IndexColumnCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove column", _vm.RemoveColumnCommand, ShortcutCommands.DesignerRemoveColumn));
        LastMenu = menu;
        menu.ShowAt(button);
    }

    private void OnIndexColumnClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Control { DataContext: IndexRow row } button) return;
        var used = row.ToDesign().Columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var menu = new MenuFlyout();
        foreach (var name in _vm.ColumnNames.Where(n => !used.Contains(n)))
        {
            var column = name;
            var item = new MenuItem { Header = column.Replace("_", "__") };
            item.Click += (_, _) => _vm.AppendIndexColumn(row, column);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Every column is in the index", IsEnabled = false });
        LastMenu = menu;
        menu.ShowAt(button);
    }
}
