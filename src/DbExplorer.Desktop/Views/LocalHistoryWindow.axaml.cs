using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>The local history of a query tab: kept versions, a diff, Restore. See <see cref="LocalHistoryViewModel"/>.</summary>
public partial class LocalHistoryWindow : Window
{
    public LocalHistoryWindow()
    {
        InitializeComponent();
        // The tab may have been edited while the window was in the background: compare with its text as it is now.
        Activated += (_, _) => (DataContext as LocalHistoryViewModel)?.UpdateDiff();
        Opened += async (_, _) =>
        {
            if (DataContext is LocalHistoryViewModel vm) await vm.LoadAsync();
            VersionList.Focus();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
