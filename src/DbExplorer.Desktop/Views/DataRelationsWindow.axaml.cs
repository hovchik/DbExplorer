using Avalonia.Controls;
using Avalonia.Interactivity;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class DataRelationsWindow : Window
{
    public DataRelationsWindow()
    {
        InitializeComponent();
        Canvas.TableActivated += table => (DataContext as DataRelationsViewModel)?.Activate(table);
    }

    private async void OnCopyMermaid(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DataRelationsViewModel vm || Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(vm.ToMermaid());
        vm.Status = "Mermaid diagram copied to the clipboard.";
    }
}
