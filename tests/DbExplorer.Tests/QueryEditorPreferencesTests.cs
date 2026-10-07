using DbExplorer.Application;
using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

/// <summary>The user's saved snippets and the SQL editor's zoom / word wrap preferences.</summary>
public class QueryEditorPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-editor-prefs-" + Guid.NewGuid().ToString("N"));

    public QueryEditorPreferencesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Saved_snippets_are_sorted_and_survive_a_restart()
    {
        var library = new SnippetLibrary(new AppPaths(_root));
        var changes = 0;
        library.Changed += () => changes++;

        library.Save("  Top customers ", "SELECT TOP (10) * FROM dbo.Customers ORDER BY Total DESC;");
        library.Save("Active orders", "SELECT * FROM dbo.Orders WHERE Status = 'Active';");

        Assert.Equal(2, changes);
        var reloaded = new SnippetLibrary(new AppPaths(_root)).Items;
        Assert.Equal(["Active orders", "Top customers"], reloaded.Select(s => s.Name));
        Assert.StartsWith("SELECT TOP (10)", reloaded[1].Sql);
    }

    [Fact]
    public void Saving_under_an_existing_name_replaces_it_ignoring_case()
    {
        var library = new SnippetLibrary(new AppPaths(_root));
        library.Save("Orders", "SELECT 1;");
        library.Save("ORDERS", "SELECT 2;");

        var only = Assert.Single(library.Items);
        Assert.Equal("ORDERS", only.Name);
        Assert.Equal("SELECT 2;", only.Sql);
        Assert.True(library.Contains("orders"));
    }

    [Fact]
    public void Removing_a_snippet_deletes_it_from_disk()
    {
        var library = new SnippetLibrary(new AppPaths(_root));
        library.Save("A", "SELECT 1;");
        library.Save("B", "SELECT 2;");

        Assert.True(library.Remove("a"));
        Assert.False(library.Remove("missing"));
        Assert.Equal(["B"], new SnippetLibrary(new AppPaths(_root)).Items.Select(s => s.Name));
    }

    [Fact]
    public void A_snippet_needs_a_name_and_sql()
    {
        var library = new SnippetLibrary(new AppPaths(_root));
        Assert.Throws<ArgumentException>(() => library.Save(" ", "SELECT 1;"));
        Assert.Throws<ArgumentException>(() => library.Save("Empty", "  "));
        Assert.Empty(library.Items);
    }

    [Fact]
    public void An_unreadable_snippet_file_starts_an_empty_library()
    {
        File.WriteAllText(Path.Combine(_root, "snippets.json"), "{ not json");
        Assert.Empty(new SnippetLibrary(new AppPaths(_root)).Items);
    }

    [Fact]
    public void Completion_offers_saved_snippets_at_the_start_of_a_statement()
    {
        var library = new SnippetLibrary(new AppPaths(_root));
        var engine = new SqlCompletionEngine(TestSnapshots.Shop(), id => "[" + id + "]") { UserSnippets = () => library.Items };
        library.Save("Monthly revenue", "SELECT SUM(Total) FROM dbo.Orders WHERE OrderDate >= @from || 'x';");

        var result = engine.Complete("Month", 5);

        var item = Assert.Single(result.Items, i => i.Label == "Monthly revenue");
        Assert.Equal(CompletionKind.Snippet, item.Kind);
        Assert.Equal("my snippet", item.Detail);
        Assert.Contains("||", item.InsertText);
        Assert.Null(item.CaretOffset);
    }

    [Fact]
    public void Editor_zoom_and_word_wrap_are_remembered_and_clamped()
    {
        var settings = new AppSettingsService(new AppPaths(_root));
        Assert.Equal(AppSettingsService.DefaultEditorFontSize, settings.EditorFontSize);
        Assert.False(settings.EditorWordWrap);

        var changes = 0;
        settings.EditorChanged += () => changes++;
        settings.SetEditorFontSize(16);
        settings.SetEditorWordWrap(true);
        settings.SetEditorWordWrap(true);

        Assert.Equal(2, changes);
        var reloaded = new AppSettingsService(new AppPaths(_root));
        Assert.Equal(16, reloaded.EditorFontSize);
        Assert.True(reloaded.EditorWordWrap);

        reloaded.SetEditorFontSize(500);
        Assert.Equal(AppSettingsService.MaxEditorFontSize, reloaded.EditorFontSize);
        reloaded.SetEditorFontSize(1);
        Assert.Equal(AppSettingsService.MinEditorFontSize, reloaded.EditorFontSize);
    }

    [Fact]
    public void Settings_written_before_editor_preferences_keep_the_theme_and_default_zoom()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{ \"Theme\": 2 }");
        var settings = new AppSettingsService(new AppPaths(_root));

        Assert.Equal((DbExplorer.Core.Models.AppThemeMode)2, settings.Theme);
        Assert.Equal(AppSettingsService.DefaultEditorFontSize, settings.EditorFontSize);
    }
}
