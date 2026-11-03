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
        if (_vm is not null)
        {
            _vm.CloseRequested -= OnCloseRequested;
            _vm.CopyRequested -= OnCopyRequested;
        }
        _vm = DataContext as ConnectionDialogViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += OnCloseRequested;
            _vm.CopyRequested += OnCopyRequested;
        }
    }

    /// <summary>A new connection offers a connection string the user just copied (only offered, never used unasked).</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            if (_vm is not null && Clipboard is { } clipboard && await clipboard.GetTextAsync() is { } text)
                _vm.OfferClipboard(text);
        }
        catch (Exception)
        {
            // The clipboard can be locked by another app; the offer is only a convenience.
        }
    }

    private async void OnCopyRequested(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    private void OnCloseRequested(bool ok) => Close(ok);

    /// <summary>Opens on the SSH tab (asked for a password on connect when only the SSH one is missing).</summary>
    public bool ShowSshTab
    {
        set { if (value) Tabs.SelectedIndex = 1; }
    }

    private static readonly FilePickerFileType SqliteFiles = new("SQLite database")
    {
        Patterns = ["*.db", "*.sqlite", "*.sqlite3", "*.db3", "*.s3db", "*.sl3"]
    };

    /// <summary>The folder of the file already chosen, so the picker opens next to it.</summary>
    private async Task<IStorageFolder?> CurrentFolderAsync()
    {
        var folder = _vm is { Host: { Length: > 0 } host } ? Path.GetDirectoryName(host.Trim().Trim('"')) : null;
        return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a SQLite database",
            AllowMultiple = false,
            FileTypeFilter = [SqliteFiles, FilePickerFileTypes.All],
            SuggestedStartLocation = await CurrentFolderAsync()
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _vm.Host = path;
            if (string.IsNullOrWhiteSpace(_vm.Name)) _vm.Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    private async void OnNewFile(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create a SQLite database",
            DefaultExtension = "db",
            SuggestedFileName = string.IsNullOrWhiteSpace(_vm.Name) ? "database.db" : _vm.Name.Trim() + ".db",
            FileTypeChoices = [SqliteFiles],
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await CurrentFolderAsync()
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        // The picker asked before overwriting: an existing file is replaced by an empty database.
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _vm.Message = "✗ " + ex.Message;
            return;
        }
        await _vm.CreateFileAsync(path);
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
