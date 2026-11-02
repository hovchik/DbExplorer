namespace DbExplorer.Desktop.ViewModels;

/// <summary>One keyboard shortcut: where it works, the keys, and what it does.</summary>
public sealed record ShortcutInfo(string Area, string Keys, string Action);

/// <summary>Every keyboard shortcut in the app, in the order the F1 sheet shows them. The key handlers live in the
/// views; a test checks that every KeyBinding and every "(Ctrl+…)" tooltip in the XAML is listed here.</summary>
public static class ShortcutCatalog
{
    public const string Anywhere = "Anywhere";
    public const string QueryEditor = "Query editor";
    public const string QueryTabs = "Query tabs";
    public const string ResultGrid = "Result grid";
    public const string Navigator = "Database navigator";
    public const string Comparer = "Comparer";
    public const string ErModel = "ER model";
    public const string Debugger = "Routine debugger";
    public const string Palette = "Command palette";

    public static IReadOnlyList<ShortcutInfo> All { get; } =
    [
        new(Anywhere, "Ctrl+K", "Command palette: go to an object, tab or command"),
        new(Anywhere, "Ctrl+P", "Command palette (same as Ctrl+K)"),
        new(Anywhere, "Ctrl+Shift+P", "Command palette, commands only"),
        new(Anywhere, "Ctrl+B", "Show or hide the database navigator"),
        new(Anywhere, "F1", "Keyboard shortcuts (this sheet)"),

        new(Palette, ">", "Type first to list commands only"),
        new(Palette, "Enter", "Open or run the selected item"),
        new(Palette, "Shift+Enter", "Show the table in the diagram"),
        new(Palette, "Ctrl+Enter", "Get data from a table or view, execute a routine"),
        new(Palette, "Esc", "Close"),

        new(QueryEditor, "F5", "Run the selection, or the whole script"),
        new(QueryEditor, "Ctrl+E", "Run the selection, or the whole script"),
        new(QueryEditor, "Ctrl+Enter", "Run the statement at the caret"),
        new(QueryEditor, "Ctrl+L", "Execution plan of the statement at the caret"),
        new(QueryEditor, "Ctrl+Shift+L", "Execution plan with actual run (EXPLAIN ANALYZE)"),
        new(QueryEditor, "Ctrl+Space", "Completion: names, keywords, snippets"),
        new(QueryEditor, "Ctrl+Shift+F", "Format the selection, or the whole script"),
        new(QueryEditor, "Ctrl+/", "Comment or uncomment the lines"),
        new(QueryEditor, "Ctrl+F", "Find"),
        new(QueryEditor, "Ctrl+H", "Find and replace"),
        new(QueryEditor, "Ctrl+G", "Go to line"),
        new(QueryEditor, "F12", "Go to the definition of the name at the caret"),
        new(QueryEditor, "Ctrl+D", "Duplicate the line or selection"),
        new(QueryEditor, "Alt+↑ / Alt+↓", "Move the lines up or down"),
        new(QueryEditor, "Ctrl+Shift+U", "Selection to UPPER case"),
        new(QueryEditor, "Ctrl+Alt+U", "Selection to lower case"),
        new(QueryEditor, "Alt+J", "Add a caret at the next occurrence"),
        new(QueryEditor, "Ctrl+Alt+Shift+J", "Add carets at every occurrence"),
        new(QueryEditor, "Alt+Enter", "Fixes for the SQL warning at the caret"),
        new(QueryEditor, "F8 / Shift+F8", "Next or previous SQL warning"),
        new(QueryEditor, "Alt+Z", "Word wrap on or off"),
        new(QueryEditor, "Ctrl+= / Ctrl+-", "Zoom the editor in or out (also Ctrl+wheel)"),
        new(QueryEditor, "Ctrl+0", "Reset the editor zoom"),
        new(QueryEditor, "Ctrl+Shift+↑", "Give the results most of the space"),
        new(QueryEditor, "Ctrl+Shift+↓", "Give the editor most of the space"),
        new(QueryEditor, "Ctrl+Z / Ctrl+Y", "Undo, redo"),
        new(QueryEditor, "Esc", "Leave multi-caret mode, close popups"),

        new(QueryTabs, "Ctrl+N", "New query tab"),
        new(QueryTabs, "Ctrl+W", "Close the query tab"),
        new(QueryTabs, "Ctrl+Shift+T", "Reopen the last closed tab"),
        new(QueryTabs, "Ctrl+O", "Open a .sql file"),
        new(QueryTabs, "Ctrl+S", "Save the tab to its file"),
        new(QueryTabs, "Ctrl+Shift+S", "Save the tab to a new file"),
        new(QueryTabs, "Ctrl+Shift+H", "Local history of the tab"),
        new(QueryTabs, "Ctrl+PgUp", "Previous query tab"),
        new(QueryTabs, "Ctrl+PgDn", "Next query tab"),
        new(QueryTabs, "Ctrl+Shift+PgUp", "Move the tab left"),
        new(QueryTabs, "Ctrl+Shift+PgDn", "Move the tab right"),
        new(QueryTabs, "Middle-click", "Close the tab"),

        new(ResultGrid, "Ctrl+C", "Copy the cell, or the selected rows"),
        new(ResultGrid, "Ctrl+Shift+C", "Copy the selected rows with column names"),
        new(ResultGrid, "Ctrl+F", "Quick filter on the rows"),
        new(ResultGrid, "F2", "Edit the cell (or select its text when read-only)"),
        new(ResultGrid, "Enter", "Show the whole cell value"),
        new(ResultGrid, "Alt+Insert", "Add a row"),
        new(ResultGrid, "Ctrl+Delete", "Delete the selected rows"),
        new(ResultGrid, "Ctrl+S", "Save edits to the database"),
        new(ResultGrid, "Esc", "Clear the quick filter"),

        new(Navigator, "Enter", "Open the selected object"),
        new(Navigator, "F5", "Refresh"),
        new(Navigator, "Ctrl+C", "Copy the name"),
        new(Navigator, "Esc", "Clear the filter"),

        new(Comparer, "F8 / Shift+F8", "Next or previous difference"),
        new(Comparer, "Esc", "Cancel the comparison"),

        new(ErModel, "Ctrl+Z", "Undo"),
        new(ErModel, "Ctrl+Y / Ctrl+Shift+Z", "Redo"),
        new(ErModel, "Delete", "Delete the selected table"),

        new(Debugger, "F5", "Start or continue"),
        new(Debugger, "Shift+F5", "Stop (rolls back)"),
        new(Debugger, "F9", "Toggle a breakpoint on the caret line"),
        new(Debugger, "F10", "Step over"),
        new(Debugger, "F11", "Step into"),
        new(Debugger, "Shift+F11", "Step out"),
    ];

    /// <summary>The keys listed for an action in an area, for showing next to a palette command.</summary>
    public static string? KeysFor(string area, string action) =>
        All.FirstOrDefault(s => s.Area == area && s.Action == action)?.Keys;

    /// <summary>Areas in sheet order, each with the shortcuts that match <paramref name="filter"/> (keys or text).</summary>
    public static IReadOnlyList<ShortcutGroup> Grouped(string? filter)
    {
        var terms = (filter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return All
            .Where(s => terms.All(t => s.Keys.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                       s.Action.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                       s.Area.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(s => s.Area)
            .Select(g => new ShortcutGroup(g.Key, g.ToList()))
            .ToList();
    }
}

public sealed record ShortcutGroup(string Area, IReadOnlyList<ShortcutInfo> Items);
