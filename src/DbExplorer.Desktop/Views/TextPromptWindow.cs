using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DbExplorer.Desktop.Views;

/// <summary>Asks for one line of text (e.g. the condition that identifies an expected row).</summary>
public sealed class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string message, string label, string initial, string? watermark)
    {
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var box = new TextBox { Text = initial, Watermark = watermark, FontFamily = "Cascadia Mono, Consolas, Menlo, monospace" };
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => Close(box.Text);
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 },
                new TextBlock { Text = label, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } }
            }
        };
        Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }
}
