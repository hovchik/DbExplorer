namespace DbExplorer.Application.Shortcuts;

/// <summary>Where a shortcut works. Names are what the F1 sheet shows as group titles.</summary>
public static class ShortcutAreas
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
    public const string Vim = "Vim mode (Query editor)";

    /// <summary>Whether a key in <paramref name="a"/> and the same key in <paramref name="b"/> can both be live at the same
    /// time: "Anywhere" overlaps everything, and the Query tab strip is active while its editor or its results have focus.</summary>
    public static bool Overlap(string a, string b)
    {
        if (a == b || a == Anywhere || b == Anywhere) return true;
        static bool InQueryTab(string area) => area is QueryEditor or ResultGrid;
        return (a == QueryTabs && InQueryTab(b)) || (b == QueryTabs && InQueryTab(a));
    }
}

/// <summary>A command whose keys the user can change in the F1 sheet.</summary>
public sealed record ShortcutCommand(string Id, string Area, string Action, IReadOnlyList<KeyChord> Defaults);

/// <summary>A key the sheet lists but that can't be changed (it belongs to a control, or only makes sense as it is).</summary>
public sealed record FixedShortcut(string Area, string Keys, string Action);

/// <summary>Every command whose keys can be changed, with its default keys, in the order the F1 sheet shows them. The
/// views look keys up by <see cref="ShortcutCommand.Id"/> through <see cref="KeymapService"/>, never by literal keys.</summary>
public static class ShortcutCommands
{
    public const string Palette = "app.palette";
    public const string CommandList = "app.commands";
    public const string ToggleNavigator = "app.navigator";
    public const string ShortcutSheet = "app.shortcuts";
    public const string Preferences = "app.preferences";
    public const string SearchNames = "app.search-names";

    public const string RunScript = "editor.run-script";
    public const string RunStatement = "editor.run-statement";
    public const string Explain = "editor.explain";
    public const string ExplainAnalyze = "editor.explain-analyze";
    public const string Complete = "editor.complete";
    public const string Format = "editor.format";
    public const string ToggleComment = "editor.comment";
    public const string Replace = "editor.replace";
    public const string GoToLine = "editor.go-to-line";
    public const string GoToDefinition = "editor.definition";
    public const string Rename = "editor.rename";
    public const string FindUsages = "editor.usages";
    public const string Duplicate = "editor.duplicate";
    public const string MoveLinesUp = "editor.lines-up";
    public const string MoveLinesDown = "editor.lines-down";
    public const string UpperCase = "editor.upper";
    public const string LowerCase = "editor.lower";
    public const string AddNextOccurrence = "editor.next-occurrence";
    public const string SelectAllOccurrences = "editor.all-occurrences";
    public const string ShowFixes = "editor.fixes";
    public const string NextWarning = "editor.next-warning";
    public const string PreviousWarning = "editor.previous-warning";
    public const string WordWrap = "editor.word-wrap";
    public const string ZoomIn = "editor.zoom-in";
    public const string ZoomOut = "editor.zoom-out";
    public const string ResetZoom = "editor.zoom-reset";
    public const string MaximizeResults = "editor.maximize-results";
    public const string MaximizeEditor = "editor.maximize-editor";
    public const string ResultsAsText = "editor.results-as-text";

    public const string NewTab = "tabs.new";
    public const string CloseTab = "tabs.close";
    public const string ReopenTab = "tabs.reopen";
    public const string OpenFile = "tabs.open";
    public const string Save = "tabs.save";
    public const string SaveAs = "tabs.save-as";
    public const string LocalHistory = "tabs.history";
    public const string PreviousTab = "tabs.previous";
    public const string NextTab = "tabs.next";
    public const string MoveTabLeft = "tabs.move-left";
    public const string MoveTabRight = "tabs.move-right";

    public const string GridCopyWithHeader = "grid.copy-with-header";
    public const string GridFilter = "grid.filter";
    public const string GridAddRow = "grid.add-row";
    public const string GridDeleteRows = "grid.delete-rows";

    public const string NextDifference = "comparer.next";
    public const string PreviousDifference = "comparer.previous";
    public const string CancelComparison = "comparer.cancel";

    public const string DebugContinue = "debugger.continue";
    public const string DebugStop = "debugger.stop";
    public const string DebugStepOver = "debugger.step-over";
    public const string DebugStepInto = "debugger.step-into";
    public const string DebugStepOut = "debugger.step-out";

    public static IReadOnlyList<ShortcutCommand> All { get; } = Build();

    private static readonly Dictionary<string, ShortcutCommand> ById = All.ToDictionary(c => c.Id);

    public static ShortcutCommand? Get(string id) => ById.GetValueOrDefault(id);

    /// <summary>Keys that can't be changed, in sheet order within their area. Changeable keys can't take them.</summary>
    public static IReadOnlyList<FixedShortcut> Fixed { get; } =
    [
        new(ShortcutAreas.Palette, ">", "Type first to list commands only"),
        new(ShortcutAreas.Palette, "Enter", "Open or run the selected item"),
        new(ShortcutAreas.Palette, "Shift+Enter", "Show the table in the diagram"),
        new(ShortcutAreas.Palette, "Ctrl+Enter", "Get data from a table or view, execute a routine"),
        new(ShortcutAreas.Palette, "Esc", "Close"),

        new(ShortcutAreas.QueryEditor, "Ctrl+F", "Find"),
        new(ShortcutAreas.QueryEditor, "Ctrl+Z / Ctrl+Y", "Undo, redo"),
        new(ShortcutAreas.QueryEditor, "Esc", "Leave multi-caret mode, close popups"),

        new(ShortcutAreas.QueryTabs, "Middle-click", "Close the tab"),

        new(ShortcutAreas.ResultGrid, "Ctrl+C", "Copy the cell, or the selected rows"),
        new(ShortcutAreas.ResultGrid, "F2", "Edit the cell (or select its text when read-only)"),
        new(ShortcutAreas.ResultGrid, "Enter", "Show the whole cell value"),
        new(ShortcutAreas.ResultGrid, "Esc", "Clear the quick filter"),

        new(ShortcutAreas.Navigator, "Enter", "Open the selected object"),
        new(ShortcutAreas.Navigator, "F5", "Refresh"),
        new(ShortcutAreas.Navigator, "Ctrl+C", "Copy the name"),
        new(ShortcutAreas.Navigator, "Esc", "Clear the filter"),

        new(ShortcutAreas.ErModel, "Ctrl+Z", "Undo"),
        new(ShortcutAreas.ErModel, "Ctrl+Y / Ctrl+Shift+Z", "Redo"),
        new(ShortcutAreas.ErModel, "Delete", "Delete the selected table"),
    ];

    /// <summary>The Vim keys the Query editor understands in Vim mode (a reference card; they are not changeable).</summary>
    public static IReadOnlyList<FixedShortcut> Vim { get; } =
    [
        new(ShortcutAreas.Vim, "Esc", "Back to normal mode (from insert, visual, : or /)"),
        new(ShortcutAreas.Vim, "i a I A o O", "Insert before, after, at line start or end, on a new line below or above"),
        new(ShortcutAreas.Vim, "v V", "Visual mode by character or by line"),
        new(ShortcutAreas.Vim, "h j k l", "Left, down, up, right (arrows too)"),
        new(ShortcutAreas.Vim, "w b e W B E ge", "Next word, previous word, word end (W: words split only by spaces)"),
        new(ShortcutAreas.Vim, "0 ^ $", "Line start, first non-blank, line end"),
        new(ShortcutAreas.Vim, "gg G 5G", "First line, last line, line 5"),
        new(ShortcutAreas.Vim, "f t F T ; ,", "To or before a character on the line; repeat forward or back"),
        new(ShortcutAreas.Vim, "% { }", "Matching bracket, previous or next paragraph"),
        new(ShortcutAreas.Vim, "d c y > < gu gU g~", "Delete, change, yank, indent, outdent, case: then a motion or text object"),
        new(ShortcutAreas.Vim, "dd cc yy >> <<", "The same on whole lines (3dd: three lines)"),
        new(ShortcutAreas.Vim, "iw aw i( a( i' a' i\" ip", "Text objects: word, brackets, quotes, paragraph (also [ { <)"),
        new(ShortcutAreas.Vim, "x X D C s S Y", "Delete character, before, to line end; change to end, character, line; yank line"),
        new(ShortcutAreas.Vim, "p P", "Put after or before (whole lines go below or above)"),
        new(ShortcutAreas.Vim, "r J ~", "Replace a character, join lines, toggle case"),
        new(ShortcutAreas.Vim, "u Ctrl+R .", "Undo, redo, repeat the last change"),
        new(ShortcutAreas.Vim, "/ ? n N * #", "Search forward or back (plain text, smart case), next, previous, word under caret"),
        new(ShortcutAreas.Vim, ":42 :w :q :wq", "Go to line 42, save, close the tab, save and close"),
        new(ShortcutAreas.Vim, ":%s/a/b/g", "Replace with a regular expression (\\1 and & work; :'<,'> from visual)"),
    ];

    private static List<ShortcutCommand> Build()
    {
        var list = new List<ShortcutCommand>();
        void Add(string id, string area, string action, params string[] keys) =>
            list.Add(new ShortcutCommand(id, area, action, keys.Select(KeyChord.Parse).ToList()));

        var a = ShortcutAreas.Anywhere;
        Add(Palette, a, "Command palette: go to an object, tab or command", "Ctrl+K", "Ctrl+P");
        Add(CommandList, a, "Command palette, commands only", "Ctrl+Shift+P");
        Add(ToggleNavigator, a, "Show or hide the database navigator", "Ctrl+B");
        Add(ShortcutSheet, a, "Keyboard shortcuts (this sheet)", "F1");
        Add(Preferences, a, "Preferences", "Ctrl+,");
        Add(SearchNames, a, "Search names & code: find a table, column or text in any routine", "Ctrl+Shift+N");

        var e = ShortcutAreas.QueryEditor;
        Add(RunScript, e, "Run the selection, or the whole script", "F5", "Ctrl+E");
        Add(RunStatement, e, "Run the statement at the caret", "Ctrl+Enter");
        Add(Explain, e, "Execution plan of the statement at the caret", "Ctrl+L");
        Add(ExplainAnalyze, e, "Execution plan with actual run (EXPLAIN ANALYZE)", "Ctrl+Shift+L");
        Add(Complete, e, "Completion: names, keywords, snippets", "Ctrl+Space");
        Add(Format, e, "Format the selection, or the whole script", "Ctrl+Shift+F");
        Add(ToggleComment, e, "Comment or uncomment the lines", "Ctrl+/");
        Add(Replace, e, "Find and replace", "Ctrl+H");
        Add(GoToLine, e, "Go to line", "Ctrl+G");
        Add(GoToDefinition, e, "Go to the definition of the name at the caret", "F12");
        Add(Rename, e, "Rename the alias, CTE, column or variable at the caret", "Shift+F6", "F2");
        Add(FindUsages, e, "Find usages of the name at the caret", "Alt+F7");
        Add(Duplicate, e, "Duplicate the line or selection", "Ctrl+D");
        Add(MoveLinesUp, e, "Move the lines up", "Alt+↑");
        Add(MoveLinesDown, e, "Move the lines down", "Alt+↓");
        Add(UpperCase, e, "Selection to UPPER case", "Ctrl+Shift+U");
        Add(LowerCase, e, "Selection to lower case", "Ctrl+Alt+U");
        Add(AddNextOccurrence, e, "Add a caret at the next occurrence", "Alt+J");
        Add(SelectAllOccurrences, e, "Add carets at every occurrence", "Ctrl+Alt+Shift+J");
        Add(ShowFixes, e, "Fixes for the SQL warning at the caret", "Alt+Enter");
        Add(NextWarning, e, "Next SQL warning", "F8");
        Add(PreviousWarning, e, "Previous SQL warning", "Shift+F8");
        Add(WordWrap, e, "Word wrap on or off", "Alt+Z");
        Add(ZoomIn, e, "Zoom the editor in (also Ctrl+wheel)", "Ctrl+=");
        Add(ZoomOut, e, "Zoom the editor out", "Ctrl+-");
        Add(ResetZoom, e, "Reset the editor zoom", "Ctrl+0");
        Add(MaximizeResults, e, "Give the results most of the space", "Ctrl+Shift+↑");
        Add(ResultsAsText, e, "Show results as text or as a grid", "Ctrl+T");
        Add(MaximizeEditor, e, "Give the editor most of the space", "Ctrl+Shift+↓");

        var t = ShortcutAreas.QueryTabs;
        Add(NewTab, t, "New query tab", "Ctrl+N");
        Add(CloseTab, t, "Close the query tab", "Ctrl+W");
        Add(ReopenTab, t, "Reopen the last closed tab", "Ctrl+Shift+T");
        Add(OpenFile, t, "Open a .sql file", "Ctrl+O");
        Add(Save, t, "Save the tab to its file", "Ctrl+S");
        Add(SaveAs, t, "Save the tab to a new file", "Ctrl+Shift+S");
        Add(LocalHistory, t, "Local history of the tab", "Ctrl+Shift+H");
        Add(PreviousTab, t, "Previous query tab", "Ctrl+PgUp");
        Add(NextTab, t, "Next query tab", "Ctrl+PgDn");
        Add(MoveTabLeft, t, "Move the tab left", "Ctrl+Shift+PgUp");
        Add(MoveTabRight, t, "Move the tab right", "Ctrl+Shift+PgDn");

        var g = ShortcutAreas.ResultGrid;
        Add(GridCopyWithHeader, g, "Copy the selected rows with column names", "Ctrl+Shift+C");
        Add(GridFilter, g, "Quick filter on the rows", "Ctrl+F");
        Add(GridAddRow, g, "Add a row", "Alt+Insert");
        Add(GridDeleteRows, g, "Delete the selected rows", "Ctrl+Delete");

        var c = ShortcutAreas.Comparer;
        Add(NextDifference, c, "Next difference", "F8");
        Add(PreviousDifference, c, "Previous difference", "Shift+F8");
        Add(CancelComparison, c, "Cancel the comparison", "Esc");

        var d = ShortcutAreas.Debugger;
        Add(DebugContinue, d, "Start or continue", "F5");
        Add(DebugStop, d, "Stop (rolls back)", "Shift+F5");
        Add(DebugStepOver, d, "Step over", "F10");
        Add(DebugStepInto, d, "Step into", "F11");
        Add(DebugStepOut, d, "Step out", "Shift+F11");
        return list;
    }
}
