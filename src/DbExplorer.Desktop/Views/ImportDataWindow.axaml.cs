using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ImportDataWindow : Window
{
    public ImportDataWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnPickerFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = true;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>A running import keeps the window open until it is cancelled; the background check just stops.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.CloseReason == WindowCloseReason.WindowClosing && DataContext is ImportDataViewModel { IsImporting: true } vm)
        {
            e.Cancel = true;
            vm.Status = "An import is running. Cancel it first (nothing is kept), then close.";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as ImportDataViewModel)?.Cancel();
    }

    private static string? DroppedPath(DragEventArgs e) =>
        e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedPath(e) is not null && DataContext is ImportDataViewModel { IsIdle: true } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DroppedPath(e) is { } path && DataContext is ImportDataViewModel { IsIdle: true } vm)
        {
            e.Handled = true;
            await vm.OpenAsync(path);
        }
    }
}
