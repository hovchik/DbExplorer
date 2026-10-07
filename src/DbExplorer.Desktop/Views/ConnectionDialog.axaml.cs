using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ConnectionDialog : Window
{
    private ConnectionDialogViewModel? _vm;

    public ConnectionDialog()
    {
        InitializeComponent();
        // Typing in the list filters it; picking a database fills the box and closes the list.
        DatabaseList.Picked += name =>
        {
            if (_vm is not null) _vm.Database = name;
            BrowseDatabases.Flyout?.Hide();
            DatabaseBox.Focus();
        };
        DatabaseList.Dismissed += () => BrowseDatabases.Flyout?.Hide();
        if (BrowseDatabases.Flyout is { } flyout) flyout.Opened += (_, _) => DatabaseList.Reset(_vm?.Database);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.CloseRequested -= OnCloseRequested;
        _vm = DataContext as ConnectionDialogViewModel;
        if (_vm is not null) _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(bool ok) => Close(ok);

    /// <summary>Opens on the SSH tab (asked for a password on connect when only the SSH one is missing).</summary>
    public bool ShowSshTab
    {
        set { if (value) Tabs.SelectedIndex = 1; }
    }

    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var sshFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var start = Directory.Exists(sshFolder) ? await StorageProvider.TryGetFolderFromPathAsync(sshFolder) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the SSH private key",
            AllowMultiple = false,
            SuggestedStartLocation = start
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) _vm.SshKeyPath = path;
    }
}
