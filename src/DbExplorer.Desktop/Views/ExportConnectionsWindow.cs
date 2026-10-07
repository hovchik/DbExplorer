using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DbExplorer.Core.Connections;

namespace DbExplorer.Desktop.Views;

/// <summary>The connections to export and, optionally, the password that encrypts their saved passwords.</summary>
public sealed record ConnectionExportRequest(IReadOnlyList<ConnectionProfile> Profiles, string? Password);

/// <summary>Picks which connections to export, with or without their passwords.</summary>
public sealed class ExportConnectionsWindow : Window
{
    public ExportConnectionsWindow(IReadOnlyList<ConnectionProfile> profiles)
    {
        Title = "Export connections";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var boxes = profiles.Select(p => new CheckBox { Content = p.ToString(), IsChecked = true, Tag = p }).ToList();
        var list = new StackPanel { Spacing = 2 };
        foreach (var box in boxes) list.Children.Add(box);

        var all = new CheckBox { Content = "All", IsChecked = true, FontWeight = FontWeight.SemiBold };
        all.IsCheckedChanged += (_, _) =>
        {
            foreach (var box in boxes) box.IsChecked = all.IsChecked == true;
        };

        var include = new CheckBox { Content = "Include passwords" };
        var password = new TextBox { PasswordChar = '•', Watermark = "Export password" };
        var confirm = new TextBox { PasswordChar = '•', Watermark = "Repeat the export password" };
        var passwordPanel = new StackPanel
        {
            Spacing = 6, IsVisible = false, Margin = new Thickness(22, 0, 0, 0),
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
                    Text = "Saved passwords are encrypted with this password (AES-256). You will need it to import them; it is not stored anywhere."
                },
                password,
                confirm
            }
        };
        include.IsCheckedChanged += (_, _) => passwordPanel.IsVisible = include.IsChecked == true;

        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var ok = new Button { Content = "Export…", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        ok.Click += (_, _) =>
        {
            var selected = boxes.Where(b => b.IsChecked == true).Select(b => (ConnectionProfile)b.Tag!).ToList();
            void Fail(string message)
            {
                error.Text = message;
                error.IsVisible = true;
            }

            if (selected.Count == 0)
            {
                Fail("Pick at least one connection.");
                return;
            }

            if (include.IsChecked != true)
            {
                Close(new ConnectionExportRequest(selected, null));
                return;
            }

            if (string.IsNullOrEmpty(password.Text))
            {
                Fail("Enter an export password, or untick Include passwords.");
                return;
            }
            if (password.Text != confirm.Text)
            {
                Fail("The two passwords are different.");
                return;
            }
            Close(new ConnectionExportRequest(selected, password.Text));
        };

        var missing = profiles.Count(p => !p.IntegratedSecurity && string.IsNullOrEmpty(p.Password));
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Connections", FontWeight = FontWeight.SemiBold },
                all,
                new Border
                {
                    BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Padding = new Thickness(8, 4),
                    Child = new ScrollViewer { MaxHeight = 280, Content = list }
                },
                include,
                passwordPanel,
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.75, IsVisible = missing > 0,
                    Text = $"{missing} connection(s) have no saved password; they are exported without one and ask for it on connect."
                },
                error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } }
            }
        };
    }
}
