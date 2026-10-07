using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.Views;

/// <summary>Shows one cell value in full (JSON/XML indented, pictures as pictures), with copy.</summary>
public sealed class ValueViewerWindow : Window
{
    public ValueViewerWindow(string title, string value)
    {
        var (text, kind) = ValueFormatter.Pretty(value);
        Title = $"{title} · {kind} · {value.Length:N0} characters";
        Width = 760;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var editor = new TextEditor
        {
            Text = text, IsReadOnly = true, ShowLineNumbers = true, WordWrap = kind == "Text",
            FontFamily = "Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace", FontSize = 13,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) => { if (Clipboard is { } c) await c.SetTextAsync(text); };
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { copy, close } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(10), Children = { buttons, editor } };
    }

    /// <summary>A binary value that is a picture, shown as one; null when it cannot be decoded.</summary>
    public static ValueViewerWindow? TryImage(string title, byte[] bytes)
    {
        if (ImageSniffer.Detect(bytes) is not { } format) return null;
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception)
        {
            return null;
        }
        return new ValueViewerWindow(title, bytes, format, bitmap);
    }

    private ValueViewerWindow(string title, byte[] bytes, string format, Bitmap bitmap)
    {
        Title = $"{title} · {format} image · {bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} · {bytes.Length:N0} bytes";
        Width = Math.Clamp(bitmap.PixelSize.Width + 60, 360, 1100);
        Height = Math.Clamp(bitmap.PixelSize.Height + 110, 240, 800);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var image = new Image { Source = bitmap, Stretch = Avalonia.Media.Stretch.None };
        var scroller = new ScrollViewer
        {
            Content = image,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var fit = new CheckBox { Content = "Fit to window" };
        fit.IsCheckedChanged += (_, _) =>
        {
            var on = fit.IsChecked == true;
            image.Stretch = on ? Avalonia.Media.Stretch.Uniform : Avalonia.Media.Stretch.None;
            scroller.HorizontalScrollBarVisibility = on ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            scroller.VerticalScrollBarVisibility = on ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        };
        var copy = new Button { Content = "Copy as hex" };
        copy.Click += async (_, _) => { if (Clipboard is { } c) await c.SetTextAsync("0x" + Convert.ToHexString(bytes)); };
        var save = new Button { Content = "Save…" };
        save.Click += async (_, _) =>
        {
            var extension = ImageSniffer.Extension(format);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save image", SuggestedFileName = title + "." + extension, DefaultExtension = extension
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
        };
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();
        Closed += (_, _) => bitmap.Dispose();

        var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { copy, save, close } };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(fit);
        DockPanel.SetDock(bar, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(10), Children = { bar, scroller } };
    }
}
