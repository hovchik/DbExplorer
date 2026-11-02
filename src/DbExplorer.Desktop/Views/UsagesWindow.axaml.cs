using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>Find usages results; see <see cref="UsagesViewModel"/>. It stays open beside the editor while the places are visited.</summary>
public partial class UsagesWindow : Window
{
    public UsagesWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is UsagesViewModel vm && vm.Selected is null) vm.Selected = vm.Rows.FirstOrDefault(r => r.CanOpen);
            RowList.Focus();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private UsagesViewModel? ViewModel => DataContext as UsagesViewModel;

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) => ViewModel?.Open(ViewModel.Selected);

    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ViewModel?.Open(ViewModel.Selected);
        e.Handled = true;
    }

    private void OnGoTo(object? sender, RoutedEventArgs e) => ViewModel?.Open(ViewModel.Selected);

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Clipboard is { } clipboard) await clipboard.SetTextAsync(vm.AsText());
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
