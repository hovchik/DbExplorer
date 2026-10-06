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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DiagramViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DiagramViewModel.ShowSuggestions)) ShowPanel(vm.ShowSuggestions);
                };
        };
    }

    private GridLength _panelWidth = new(480);

    /// <summary>The panel column is resizable with the splitter; hidden, it takes no room and keeps its last width.</summary>
    private void ShowPanel(bool show)
    {
        var column = Body.ColumnDefinitions[2];
        if (show)
        {
            column.Width = _panelWidth;
            column.MinWidth = 300;
        }
        else
        {
            if (column.Width.Value > 0) _panelWidth = column.Width;
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
    }

    private void OnFit(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagramViewModel { Diagram.Tables.Count: > 0 } vm) return;
        var view = Viewport.Bounds.Size;
        var zoom = Math.Min((view.Width - 4) / vm.Diagram.Width, (view.Height - 4) / vm.Diagram.Height);
        vm.Zoom = Math.Clamp(Math.Floor(zoom * 20) / 20, 0.3, 1.5);
    }

    private async void OnCopyMermaid(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagramViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(vm.ToMermaid());
        vm.Status = "Mermaid diagram copied to the clipboard.";
    }

    private async void OnCopyScript(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagramViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(vm.SuggestionScript);
        vm.Status = "Script copied to the clipboard. Nothing has been run.";
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
