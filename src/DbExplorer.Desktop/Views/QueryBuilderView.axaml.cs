using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DbExplorer.Core.Models;
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

        // Drag a table from the list onto the canvas; tunnel so the ListBox's own selection handling still runs.
        TableList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        TableList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        TableList.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel);
    }

    private void ApplyHighlighting() =>
        SqlEditor.SyntaxHighlighting = SqlHighlighting.Get(dark: ActualThemeVariant == ThemeVariant.Dark);

    // The editor's text is not bindable, so it follows the view model's Sql by hand.
    private void BindSql()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as QueryBuilderViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelPropertyChanged;
        SqlEditor.Text = _vm?.Sql ?? "";
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryBuilderViewModel.Sql) && _vm is not null && SqlEditor.Text != _vm.Sql) SqlEditor.Text = _vm.Sql;
    }

    private void OnTableDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is DbObject table)
            _vm?.AddTable(table, null);
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
        if (e.Source is not Visual source || source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not DbObject table) return;

        var data = new DataObject();
        data.Set(QueryCanvasControl.TableFormat, table);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    private async void OnCopySql(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(_vm.Sql);
        _vm.Status = "SQL copied to the clipboard.";
    }
}
