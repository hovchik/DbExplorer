using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DbExplorer.Application.Documentation;
using DbExplorer.Desktop.Controls;

namespace DbExplorer.Desktop.Views;

public enum DocumentationFormat
{
    Html,
    Markdown
}

/// <summary>What the Export documentation dialog starts with: the server's databases, the one to preselect, and how to
/// list a database's schemas.</summary>
public sealed record DocumentationPrompt(
    IReadOnlyList<string> Databases, string? Database, string? Schema, Func<string?, Task<IReadOnlyList<string>>> SchemasOf);

/// <summary>The user's choices in the Export documentation dialog.</summary>
public sealed record DocumentationRequest(string? Database, DocumentationFormat Format, DocumentationOptions Options, bool OpenWhenDone);

/// <summary>Picks the database, schema, format and contents of a documentation export.</summary>
public sealed class ExportDocumentationWindow : Window
{
    private const string AllSchemas = "All schemas";
    private static DocumentationRequest? _last;

    public ExportDocumentationWindow(DocumentationPrompt prompt)
    {
        Title = "Export database documentation";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var last = _last;
        var database = new DatabasePicker
        {
            ItemsSource = prompt.Databases, SelectedItem = prompt.Database ?? prompt.Databases.FirstOrDefault(),
            PlaceholderText = "Database", HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var schema = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { AllSchemas }, SelectedIndex = 0 };
        var schemaHint = new TextBlock { Opacity = 0.7, FontSize = 12, Text = "Reading schemas…", IsVisible = false };

        var loadVersion = 0;
        async void LoadSchemas(string? wanted)
        {
            var version = ++loadVersion;
            schemaHint.IsVisible = true;
            schema.IsEnabled = false;
            IReadOnlyList<string> schemas;
            try
            {
                schemas = await prompt.SchemasOf(database.SelectedItem);
            }
            catch
            {
                schemas = [];
            }
            if (version != loadVersion) return;
            var items = new List<string> { AllSchemas };
            items.AddRange(schemas);
            schema.ItemsSource = items;
            var at = wanted is null ? -1 : items.FindIndex(s => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase));
            schema.SelectedIndex = at < 0 ? 0 : at;
            schema.IsEnabled = schemas.Count > 1 || at > 0;
            schemaHint.IsVisible = false;
        }
        database.PropertyChanged += (_, e) =>
        {
            if (e.Property == DatabasePicker.SelectedItemProperty) LoadSchemas(null);
        };
        LoadSchemas(prompt.Schema);

        var html = new RadioButton { Content = "HTML page (one file, with a clickable diagram)", GroupName = "docfmt", IsChecked = last?.Format != DocumentationFormat.Markdown };
        var markdown = new RadioButton { Content = "Markdown (for a wiki or a repository; Mermaid diagram)", GroupName = "docfmt", IsChecked = last?.Format == DocumentationFormat.Markdown };

        var defaults = new DocumentationOptions();
        var diagram = new CheckBox { Content = "Diagram of the tables", IsChecked = last?.Options.IncludeDiagram ?? defaults.IncludeDiagram };
        var source = new CheckBox { Content = "Source code of views, routines and triggers", IsChecked = last?.Options.IncludeSource ?? defaults.IncludeSource };
        var constraints = new CheckBox
        {
            Content = "Column defaults and check constraints", IsChecked = last?.Options.IncludeConstraints ?? defaults.IncludeConstraints
        };
        ToolTip.SetTip(constraints, "Read from the server one table at a time, so large databases take longer.");
        var rows = new CheckBox { Content = "Approximate row counts", IsChecked = last?.Options.IncludeRowCounts ?? defaults.IncludeRowCounts };
        var open = new CheckBox { Content = "Open the file when it is saved", IsChecked = last?.OpenWhenDone ?? true };

        var ok = new Button { Content = "Export…", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        ok.Click += (_, _) =>
        {
            var options = new DocumentationOptions
            {
                Schema = schema.SelectedItem as string is { } s && s != AllSchemas ? s : null,
                IncludeDiagram = diagram.IsChecked == true,
                IncludeSource = source.IsChecked == true,
                IncludeConstraints = constraints.IsChecked == true,
                IncludeRowCounts = rows.IsChecked == true
            };
            _last = new DocumentationRequest(database.SelectedItem, markdown.IsChecked == true ? DocumentationFormat.Markdown : DocumentationFormat.Html,
                options, open.IsChecked == true);
            Close(_last);
        };

        static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
                    Text = "One file that lists the tables with their columns, keys, indexes and comments, plus views and routines. Useful for onboarding and audits."
                },
                Label("Database"),
                database,
                Label("Schema"),
                schema,
                schemaHint,
                Label("Format"),
                html,
                markdown,
                Label("Include"),
                diagram,
                source,
                constraints,
                rows,
                new Separator { Margin = new Thickness(0, 4) },
                open,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0), Children = { cancel, ok }
                }
            }
        };
    }
}
