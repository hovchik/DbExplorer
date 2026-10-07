using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DbExplorer.Application.Connections;

namespace DbExplorer.Desktop.Views;

/// <summary>Asks what to do with imported connections whose names are already used. Closes with null when cancelled.</summary>
public sealed class ImportConflictWindow : Window
{
    public ImportConflictWindow(IReadOnlyList<string> names)
    {
        Title = "Import connections";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        Button Choice(string text, ImportConflictChoice choice, bool accent = false)
        {
            var button = new Button { Content = text, IsDefault = accent };
            if (accent) button.Classes.Add("accent");
            button.Click += (_, _) => Close(choice);
            return button;
        }

        var cancel = new Button { Content = "Cancel", IsCancel = true };
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
                    Text = names.Count == 1
                        ? "A connection with this name already exists:"
                        : $"{names.Count} connections with these names already exist:"
                },
                new ScrollViewer
                {
                    MaxHeight = 200,
                    Content = new TextBlock { Text = string.Join(Environment.NewLine, names), FontWeight = FontWeight.SemiBold }
                },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
                    Text = "Skip keeps yours, Overwrite replaces yours with the imported one, Keep both adds the imported one with a number after its name."
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Children =
                    {
                        cancel,
                        Choice("Skip", ImportConflictChoice.Skip),
                        Choice("Overwrite", ImportConflictChoice.Overwrite),
                        Choice("Keep both", ImportConflictChoice.KeepBoth, accent: true)
                    }
                }
            }
        };
    }
}
