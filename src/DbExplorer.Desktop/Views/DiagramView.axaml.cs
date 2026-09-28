using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class DiagramView : UserControl
{
    public DiagramView()
    {
        InitializeComponent();
        Canvas.TableActivated += table => (DataContext as DiagramViewModel)?.Refocus(table);
        TablePicker.GotFocus += (_, _) => TablePicker.IsDropDownOpen = true;
    }

    private async void OnCopyMermaid(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagramViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(vm.ToMermaid());
        vm.Status = "Mermaid diagram copied to the clipboard.";
    }

    private async void OnSavePng(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagramViewModel { Diagram.Tables.Count: > 0 } vm ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save diagram",
            SuggestedFileName = (vm.FocusTable?.FullName ?? "schema") + "-diagram.png",
            DefaultExtension = "png",
            FileTypeChoices = [FilePickerFileTypes.ImagePng]
        });
        if (file is null) return;

        try
        {
            // 2x via the control's own zoom at 96 DPI: a high-DPI RenderTargetBitmap scales text twice.
            const double scale = 2.0;
            var size = new PixelSize((int)Math.Ceiling(vm.Diagram.Width * scale), (int)Math.Ceiling(vm.Diagram.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            var zoom = vm.Zoom;
            vm.Zoom = scale;
            Canvas.Measure(Size.Infinity);
            Canvas.Arrange(new Rect(Canvas.DesiredSize));
            bitmap.Render(Canvas);
            vm.Zoom = zoom;

            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            bitmap.Save(stream);
            vm.Status = $"Saved {file.Name}.";
        }
        catch (Exception ex)
        {
            vm.Status = "Could not save image: " + ex.Message;
        }
    }
}
