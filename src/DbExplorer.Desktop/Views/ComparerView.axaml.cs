using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ComparerView : UserControl
{
    public ComparerView()
    {
        InitializeComponent();
    }

    /// <summary>Opens the drop-down as soon as the box is focused, so the full object list can be
    /// browsed with a click alone (no typing required) instead of only filtering as-you-type.</summary>
    private void OnObjectPickerGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = true;
    }

    private async void OnExportSchemaHtml(object? sender, RoutedEventArgs e) => await ExportAsync(schema: true, html: true);
    private async void OnExportSchemaMarkdown(object? sender, RoutedEventArgs e) => await ExportAsync(schema: true, html: false);
    private async void OnExportDataHtml(object? sender, RoutedEventArgs e) => await ExportAsync(schema: false, html: true);
    private async void OnExportDataMarkdown(object? sender, RoutedEventArgs e) => await ExportAsync(schema: false, html: false);

    private async Task ExportAsync(bool schema, bool html)
    {
        if (DataContext is not ComparerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        try
        {
            var report = schema ? await vm.BuildSchemaReportAsync(html) : vm.BuildDataReport(html);
            if (report is null) return;

            var extension = html ? "html" : "md";
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save comparison report",
                SuggestedFileName = $"{vm.ReportFileStem}-{(schema ? "schema" : "data")}-diff.{extension}",
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(html ? "HTML" : "Markdown") { Patterns = ["*." + extension] }]
            });
            if (file is null) return;

            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(report);
            vm.Status = $"Report saved to {file.Name}.";
        }
        catch (Exception ex)
        {
            vm.Status = "Could not export report: " + ex.Message;
        }
    }
}
