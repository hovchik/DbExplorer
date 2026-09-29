using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaloniaEdit;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.Views;

/// <summary>Shows one cell value in full (JSON/XML indented), with copy.</summary>
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
}
