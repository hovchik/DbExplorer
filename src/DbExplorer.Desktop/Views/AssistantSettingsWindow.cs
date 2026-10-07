using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DbExplorer.Application.Assistant;

namespace DbExplorer.Desktop.Views;

/// <summary>Sets or removes the Anthropic API key of the AI assistant, and says what is sent.</summary>
public sealed class AssistantSettingsWindow : Window
{
    public AssistantSettingsWindow(AssistantSettings settings)
    {
        Title = "AI assistant settings";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var box = new TextBox
        {
            PasswordChar = '•',
            Watermark = settings.IsConfigured ? "Saved: " + AssistantSettings.Mask(settings.ApiKey) + " (type a new key to replace it)" : "sk-ant-…",
            FontFamily = "Cascadia Mono, Consolas, Menlo, monospace"
        };
        var save = new Button { Content = "Save", IsDefault = true, Classes = { "accent" } };
        var remove = new Button { Content = "Remove key", IsVisible = settings.IsConfigured };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text)) settings.SetApiKey(box.Text);
            Close();
        };
        remove.Click += (_, _) =>
        {
            settings.SetApiKey(null);
            Close();
        };
        cancel.Click += (_, _) => Close();

        var storage = settings.CanPersistKey
            ? "The key is stored encrypted for your Windows user account (DPAPI) and is only sent to api.anthropic.com."
            : "This system has no secret store the app can use, so the key is kept only until the app closes.";

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "The AI assistant in the Query tab writes SQL from a sentence, explains a query and fixes a failed one, " +
                           $"using Claude ({settings.Model}) with your own Anthropic API key. Get a key at console.anthropic.com.",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "What is sent: your request, the query text in the editor (and its error message when fixing), and the " +
                           "names and types of the tables, views, columns, keys and routines of the tab's database. " +
                           "Row data and routine bodies are never sent. Generated SQL is put in the editor and never run automatically.",
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.8
                },
                new TextBlock { Text = storage, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                new TextBlock { Text = "Anthropic API key", FontWeight = FontWeight.SemiBold },
                box,
                new DockPanel
                {
                    Children =
                    {
                        remove,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                            Children = { cancel, save }
                        }
                    }
                }
            }
        };
        Opened += (_, _) => box.Focus();
    }
}
