using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace DbExplorer.Desktop.Views;

/// <summary>Asks for the values of a query's parameters (undeclared @variables and :placeholders).</summary>
public sealed class ParametersWindow : Window
{
    public ParametersWindow(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> defaults)
    {
        Title = "Query parameters";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var boxes = new List<(string Name, TextBox Box)>();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (var i = 0; i < names.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = names[i], Margin = new Thickness(0, 0, 10, 6), VerticalAlignment = VerticalAlignment.Center, FontFamily = "Cascadia Mono, Consolas, Menlo, monospace" };
            var box = new TextBox { Margin = new Thickness(0, 0, 0, 6), Text = defaults.GetValueOrDefault(names[i], ""), Watermark = "value, NULL, 42, 2024-01-31…" };
            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
            boxes.Add((names[i], box));
        }

        var ok = new Button { Content = "Run", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => Close(boxes.ToDictionary(b => b.Name, b => b.Box.Text ?? "", StringComparer.OrdinalIgnoreCase) as IReadOnlyDictionary<string, string>);
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.75,
                    Text = "Numbers, NULL and TRUE/FALSE are used as typed; other values are quoted as text. Values are remembered for the next run."
                },
                grid,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } }
            }
        };
        Opened += (_, _) => boxes.FirstOrDefault().Box?.Focus();
    }
}
