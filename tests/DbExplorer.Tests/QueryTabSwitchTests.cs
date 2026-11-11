using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using DbExplorer.Application;
using DbExplorer.Desktop;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Tests;

/// <summary>
/// Switching and closing Query tabs while the editor has focus. On Windows the input method asks where the caret is drawn
/// each time it moves, and swapping a tab's document moves it; with the previous tab's folding still installed that threw
/// "Invalid document", left the editor drawing old text over new, and left closed tabs as empty slots in the strip.
/// </summary>
[Collection(nameof(HeadlessUiCollection))]
public class QueryTabSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-tabswitch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record Setup(ServiceProvider Provider, QueryWorkspaceViewModel Workspace, QueryWorkspaceView View, Window Window,
        TextEditor Editor, TabControl Tabs, List<Exception> Errors);

    private Setup Show()
    {
        var services = App.ConfigureServices();
        services.AddSingleton(new AppPaths(Path.Combine(_root, "data")));
        services.AddSingleton(DesktopTabTests.DialogFake.Create(out _)); // closing a tab with text asks first: yes
        var provider = services.BuildServiceProvider();
        var workspace = provider.GetRequiredService<QueryWorkspaceViewModel>();
        workspace.Documents[0].Document.Text = "SELECT 1\nFROM a\nWHERE (\n  x = 1\n);";
        for (var i = 2; i <= 4; i++) workspace.NewTab().Document.Text = $"SELECT {i}\nFROM t{i}\nWHERE (\n  y = {i}\n);";

        var view = new QueryWorkspaceView { DataContext = workspace };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        var errors = new List<Exception>();
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            errors.Add(e.Exception);
            e.Handled = true;
        };
        Pump(window, errors);

        var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
        // What the input method does while the editor has focus: ask where the caret is drawn whenever it moves.
        editor.TextArea.Caret.PositionChanged += (_, _) => _ = editor.TextArea.Caret.Position;
        return new Setup(provider, workspace, view, window, editor, view.FindControl<TabControl>("DocTabs")!, errors);
    }

    private static void Pump(Window window, List<Exception> errors)
    {
        Dispatcher.UIThread.RunJobs();
        try { window.UpdateLayout(); }
        catch (Exception ex) { errors.Add(ex); }
    }

    private static object? FoldingDocument(TextEditor editor)
    {
        var folding = ((IServiceProvider)editor.TextArea.TextView).GetService(typeof(FoldingManager));
        return folding?.GetType().GetProperty("Document", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(folding);
    }

    private static List<TabItem> TabHeaders(TabControl tabs) =>
        tabs.GetVisualDescendants().OfType<TabItem>().Where(t => ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(t), tabs)).ToList();

    [AvaloniaFact]
    public async Task Switching_tabs_with_the_caret_inside_the_text_keeps_the_editor_on_the_selected_tab()
    {
        var s = Show();
        await using var _ = s.Provider;

        foreach (var round in Enumerable.Range(0, 2))
        foreach (var doc in s.Workspace.Documents.Reverse().ToList())
        {
            s.Editor.CaretOffset = s.Editor.Document.TextLength - 3; // somewhere other than the start, so the swap moves it
            s.Workspace.SelectedDocument = doc;
            Pump(s.Window, s.Errors);

            Assert.Empty(s.Errors);
            Assert.Same(doc.Document, s.Editor.Document);
            Assert.Same(doc.Document, FoldingDocument(s.Editor));
            // The visible lines are the selected tab's, not ones left over from an earlier tab.
            Assert.All(s.Editor.TextArea.TextView.VisualLines, line => Assert.Same(doc.Document, line.Document));
        }

        // Typing after the switches still works and folds.
        s.Editor.Document.Insert(s.Editor.Document.TextLength, "\n-- more");
        Pump(s.Window, s.Errors);
        Assert.Empty(s.Errors);
        s.Window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_tabs_after_switching_leaves_no_empty_slot_in_the_strip()
    {
        var s = Show();
        await using var _ = s.Provider;

        s.Editor.CaretOffset = s.Editor.Document.TextLength - 3;
        s.Workspace.SelectedDocument = s.Workspace.Documents[1];
        Pump(s.Window, s.Errors);

        s.Editor.CaretOffset = s.Editor.Document.TextLength - 3;
        await s.Workspace.CloseTabCommand.ExecuteAsync(s.Workspace.SelectedDocument); // the selected one
        Pump(s.Window, s.Errors);
        s.Editor.CaretOffset = s.Editor.Document.TextLength - 3;
        await s.Workspace.CloseTabCommand.ExecuteAsync(s.Workspace.Documents[0]); // one beside it
        Pump(s.Window, s.Errors);

        Assert.Empty(s.Errors);
        Assert.Equal(2, s.Workspace.Documents.Count);
        // The remaining tabs close up: one header per tab, in order, side by side from the left.
        var headers = TabHeaders(s.Tabs).OrderBy(h => h.Bounds.X).ToList();
        Assert.Equal(s.Workspace.Documents, headers.Select(h => h.DataContext));
        Assert.Equal(0, headers[0].Bounds.X, 0.5);
        Assert.Equal(headers[0].Bounds.Right + headers[0].Margin.Right, headers[1].Bounds.X, 0.5);
        var editors = s.View.GetVisualDescendants().OfType<TextEditor>().ToList();
        Assert.Equal(1, editors.Count);
        Assert.Same(s.Workspace.SelectedDocument!.Document, editors[0].Document);
        Assert.Same(editors[0].Document, FoldingDocument(editors[0]));
        s.Window.Close();
    }

    [Theory]
    [InlineData(new[] { "a", "b", "c", "d" }, 2, "b")] // middle: its left neighbour
    [InlineData(new[] { "a", "b", "c", "d" }, 3, "c")] // last: the new last
    [InlineData(new[] { "a", "b", "c", "d" }, 0, "b")] // leftmost: the new leftmost
    [InlineData(new[] { "a" }, 0, null)] // only tab: none
    public void The_tab_left_of_a_closed_tab_gets_the_focus(string[] tabs, int closing, string? expected) =>
        Assert.Equal(expected, QueryWorkspaceViewModel.TabAfterClosing(tabs, closing));

    [AvaloniaFact]
    public async Task Closing_the_selected_tab_focuses_the_nearest_tab_on_its_left()
    {
        var s = Show();
        await using var _ = s.Provider;
        var docs = s.Workspace.Documents.ToList(); // four tabs

        s.Workspace.SelectedDocument = docs[2];
        Pump(s.Window, s.Errors);
        await s.Workspace.CloseTabCommand.ExecuteAsync(docs[2]);
        Pump(s.Window, s.Errors);
        Assert.Same(docs[1], s.Workspace.SelectedDocument);
        Assert.Same(docs[1], s.Tabs.SelectedItem);
        Assert.Same(docs[1].Document, s.Editor.Document);

        // The leftmost tab: the one that is now first.
        s.Workspace.SelectedDocument = docs[0];
        Pump(s.Window, s.Errors);
        await s.Workspace.CloseTabCommand.ExecuteAsync(docs[0]);
        Pump(s.Window, s.Errors);
        Assert.Same(docs[1], s.Workspace.SelectedDocument);
        Assert.Same(docs[1], s.Tabs.SelectedItem);

        // Closing a tab that is not selected keeps the selection where it is.
        await s.Workspace.CloseTabCommand.ExecuteAsync(docs[3]);
        Pump(s.Window, s.Errors);
        Assert.Same(docs[1], s.Workspace.SelectedDocument);

        Assert.Empty(s.Errors);
        s.Window.Close();
    }
}
