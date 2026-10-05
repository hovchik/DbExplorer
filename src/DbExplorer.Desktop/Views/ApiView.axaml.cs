using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ApiView : UserControl
{
    private static readonly FilePickerFileType Collections = new("Postman / Insomnia export") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = ["*"] };

    public ApiView() => InitializeComponent();

    private ApiViewModel? Vm => DataContext as ApiViewModel;

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import collection or environment", AllowMultiple = true, FileTypeFilter = [Collections, AllFiles]
        });
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path) await vm.ImportFileAsync(path);
        }
    }

    /// <summary>Only edits made by the user count: bindings also raise these events when another request is selected.</summary>
    private void OnEdited(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { IsKeyboardFocusWithin: true }) Vm?.MarkEdited();
    }

    private void OnCellEdited(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit) Vm?.MarkEdited();
    }
}
