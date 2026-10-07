using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DbExplorer.Desktop.Views;

/// <summary>The export password typed on import; <see cref="Password"/> is null to import without passwords.</summary>
public sealed record ImportPasswordAnswer(string? Password);

/// <summary>Asks for the password a connections file was exported with. Closes with null when cancelled.</summary>
public sealed class ImportPasswordWindow : Window
{
    public ImportPasswordWindow(string fileName, string? error)
    {
        Title = "Import connections";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var box = new TextBox { PasswordChar = '•', Watermark = "Export password" };
        var ok = new Button { Content = "Import", IsDefault = true, Classes = { "accent" } };
        var skip = new Button { Content = "Import without passwords" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => Close(new ImportPasswordAnswer(box.Text ?? ""));
        skip.Click += (_, _) => Close(new ImportPasswordAnswer(null));
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
                    Text = $"{fileName} includes encrypted passwords. Enter the password it was exported with, or import the connections without their passwords."
                },
                box,
                new TextBlock { Text = error, Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = error is not null },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, skip, ok } }
            }
        };
        Opened += (_, _) => box.Focus();
    }
}
