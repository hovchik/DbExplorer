using DbExplorer.Desktop.Services;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Desktop.Controls;
using DbExplorer.Desktop.Editor;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class QueryBuilderView : UserControl
{
    private QueryBuilderViewModel? _vm;
    private Point? _dragStart;

    public QueryBuilderView()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
        DataContextChanged += (_, _) => BindSql();
        ApplyHighlighting();
        Keymap.Bind(this, () => DataContext is not QueryBuilderViewModel vm ? [] :
        [
            (ShortcutCommands.BuilderRun, vm.RunCommand),
            (ShortcutCommands.BuilderOpen, vm.OpenInQueryTabCommand),
            (ShortcutCommands.BuilderArrange, vm.ArrangeCommand)
        ]);
        Keymap.Changed += ShowKeys;
        ShowKeys();

        MainArea.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) ApplyLayout(MainArea.Bounds.Width);
        };

        // Drag a table from the list onto the canvas; tunnel so the ListBox's own selection handling still runs.
        TableList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        TableList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        TableList.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel);
    }

    /// <summary>Below this width the SQL moves from beside the canvas to a tab next to Columns and Joins, so the canvas
    /// keeps room for two tables side by side and the column rows keep the full width.</summary>
    internal const double WideLayoutWidth = 760;

    private bool? _wide;

    private void ApplyLayout(double width)
    {
        if (width <= 0) return;
        var wide = width >= WideLayoutWidth;
        var sqlWidth = wide ? new GridLength(Math.Round(Math.Clamp(width * 0.36, 300, 460))) : new GridLength(0);
        // Keep a width the user dragged the splitter to; only set it when the layout changes.
        if (_wide == wide) return;
        _wide = wide;
        MainArea.ColumnDefinitions[2].Width = sqlWidth;
        SqlCard.IsVisible = SqlSplitter.IsVisible = wide;
        Grid.SetColumnSpan(CanvasCard, wide ? 1 : 3);
        SqlTab.IsVisible = !wide;
        if (wide && DesignTabs.SelectedItem == SqlTab) DesignTabs.SelectedIndex = 0;
    }

    /// <summary>The buttons' tooltips name the keys in use now (they can be changed in the F1 sheet).</summary>
    private void ShowKeys()
    {
        static string With(string text, string id) => Keymap.Display(id) is { } keys ? $"{text} ({keys})" : text;
        ToolTip.SetTip(RunButton, With("Open the SQL in a new Query tab and run it there", ShortcutCommands.BuilderRun));
        ToolTip.SetTip(OpenButton, With("Open the SQL in a new Query tab without running it", ShortcutCommands.BuilderOpen));
        ToolTip.SetTip(ArrangeButton, With("Line the tables up along their joins", ShortcutCommands.BuilderArrange));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Keymap.Changed -= ShowKeys;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Keymap.Changed -= ShowKeys;
        Keymap.Changed += ShowKeys;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Undo/redo of the design, unless a text box handled the keys for its own text.
        if (e.Handled || _vm is not { } vm || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.Z && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) vm.UndoCommand.Execute(null);
        else if (e.Key == Key.Y || e.Key == Key.Z) vm.RedoCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    private void ApplyHighlighting() =>
        SqlEditor.SyntaxHighlighting = SqlTabEditor.SyntaxHighlighting = SqlHighlighting.Get(dark: AppThemes.IsDark(ActualThemeVariant));

    // The editor's text is not bindable, so it follows the view model's Sql by hand.
    private void BindSql()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.CopyRequested -= OnCopyRequested;
        }
        _vm = DataContext as QueryBuilderViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.CopyRequested += OnCopyRequested;
        }
        SqlEditor.Text = SqlTabEditor.Text = _vm?.Sql ?? "";
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QueryBuilderViewModel.Sql) || _vm is null) return;
        if (SqlEditor.Text != _vm.Sql) SqlEditor.Text = _vm.Sql;
        if (SqlTabEditor.Text != _vm.Sql) SqlTabEditor.Text = _vm.Sql;
    }

    private async void OnCopyRequested(string sql)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(sql);
    }

    private static TableChoice? ChoiceOf(object? source) =>
        source is Visual visual ? visual.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TableChoice : null;

    private void OnTableDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (ChoiceOf(e.Source) is { } choice) _vm?.AddTable(choice.Object, null);
    }

    private void OnAddTableClick(object? sender, RoutedEventArgs e)
    {
        if (ChoiceOf(sender) is { } choice) _vm?.AddTable(choice.Object, null);
        e.Handled = true;
    }

    private void OnTableListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || _vm?.SelectedChoice is not { } choice) return;
        _vm.AddTable(choice.Object, null);
        e.Handled = true;
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = e.GetCurrentPoint(TableList).Properties.IsLeftButtonPressed ? e.GetPosition(TableList) : null;
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || !e.GetCurrentPoint(TableList).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(TableList);
        if (Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        if (ChoiceOf(e.Source) is not { } choice) return;

        var data = new DataObject();
        data.Set(QueryCanvasControl.TableFormat, choice.Object);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }
}
