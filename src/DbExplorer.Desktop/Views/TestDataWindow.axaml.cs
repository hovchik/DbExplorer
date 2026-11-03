using Avalonia.Controls;
using Avalonia.Interactivity;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>Generate test data for one table; see <see cref="TestDataViewModel"/>. Closing it cancels (and rolls back)
/// an insert that is still running.</summary>
public partial class TestDataWindow : Window
{
    public TestDataWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as TestDataViewModel)?.Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
