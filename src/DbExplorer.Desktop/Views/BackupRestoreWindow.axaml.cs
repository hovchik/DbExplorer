using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>Back up and restore databases of one connection; see <see cref="BackupRestoreViewModel"/>. Closing it while
/// a backup or restore runs asks first, then cancels it.</summary>
public partial class BackupRestoreWindow : Window
{
    private bool _confirmedClose;

    public BackupRestoreWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Closed += (_, _) => (DataContext as BackupRestoreViewModel)?.Close();
        // Follow the output as it grows.
        LogBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                Dispatcher.UIThread.Post(() => LogBox.CaretIndex = LogBox.Text?.Length ?? 0, DispatcherPriority.Background);
        };
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_confirmedClose || DataContext is not BackupRestoreViewModel { IsRunning: true }) return;
        e.Cancel = true;
        var confirm = new ConfirmWindow("A backup or restore is still running. Stop it and close?", "Stop and close");
        if (await confirm.ShowDialog<bool>(this))
        {
            _confirmedClose = true;
            Close();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
