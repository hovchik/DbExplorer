using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class QueryWorkspaceView : UserControl
{
    private static readonly FilePickerFileType SqlFiles = new("SQL script") { Patterns = ["*.sql"] };
    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = ["*"] };

    public QueryWorkspaceView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private QueryWorkspaceViewModel? Vm => DataContext as QueryWorkspaceViewModel;

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.N: e.Handled = true; vm.NewTab(); break;
            case Key.W: e.Handled = true; await vm.CloseTabCommand.ExecuteAsync(null); break;
            case Key.O: e.Handled = true; await OpenAsync(); break;
            case Key.S: e.Handled = true; await SaveAsync(saveAs: shift); break;
        }
    }

    private async void OnOpen(object? sender, RoutedEventArgs e) => await OpenAsync();
    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveAsync(saveAs: false);
    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    private async Task OpenAsync()
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL script", AllowMultiple = true, FileTypeFilter = [SqlFiles, AllFiles]
        });
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;
            try { await vm.OpenFileAsync(path, file.Name); }
            catch (Exception ex) { if (vm.SelectedDocument is { } doc) doc.Status = $"Could not open {file.Name}: {ex.Message}"; }
        }
    }

    private async Task SaveAsync(bool saveAs)
    {
        if (Vm is not { SelectedDocument: { } doc } vm) return;
        var path = doc.FilePath;
        var name = doc.Title;
        if (saveAs || path is null)
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save SQL script",
                SuggestedFileName = (doc.FilePath is null ? doc.Title : Path.GetFileName(doc.FilePath)).Replace(' ', '_').TrimEnd('.') +
                                    (doc.FilePath is null ? ".sql" : ""),
                DefaultExtension = "sql", ShowOverwritePrompt = true, FileTypeChoices = [SqlFiles, AllFiles]
            });
            if (file?.TryGetLocalPath() is not { } chosen) return;
            path = chosen;
            name = file.Name;
        }

        try
        {
            await vm.SaveAsync(doc, path, name);
            doc.Status = $"Saved to {path}";
        }
        catch (Exception ex)
        {
            doc.Status = "Could not save: " + ex.Message;
        }
    }

    private void OnHistoryOpened(object? sender, EventArgs e) => Vm?.LoadHistoryCommand.Execute(null);

    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { } vm && HistoryList.SelectedItem is HistoryItem item) vm.OpenHistoryCommand.Execute(item);
    }
}
