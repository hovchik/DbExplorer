using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>The command palette (Ctrl+K / Ctrl+P, Ctrl+Shift+P for commands only) and the F1 shortcut sheet.</summary>
public partial class MainWindowViewModel
{
    [RelayCommand]
    private void OpenCommandPalette() => ShowPalette(commandsOnly: false);

    [RelayCommand]
    private void OpenCommandList() => ShowPalette(commandsOnly: true);

    [RelayCommand]
    private void ShowShortcuts() => _dialogs.ShowShortcutSheet();

    private void ShowPalette(bool commandsOnly) =>
        _dialogs.ShowCommandPalette(new CommandPaletteViewModel(
            BuildPaletteItems(), _settings.RecentCommands, item => _settings.AddRecentCommand(item.Id), commandsOnly)
        {
            ShowShortcuts = ShowShortcuts
        });

    /// <summary>The keys of a command as set in the F1 sheet.</summary>
    private static string? K(string id) => Keymap.Current.Primary(id)?.ToString();

    internal IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();

        void Add(string category, string title, Action run, string? shortcut = null, string? subtitle = null, string? keywords = null, string? id = null) =>
            items.Add(new PaletteItem(title, subtitle, PaletteItemKind.Command, _ => run())
            {
                Category = category, Shortcut = shortcut, Keywords = keywords, Id = id ?? title
            });

        void Command(string category, string title, ICommand command, string? shortcut = null, string? subtitle = null, string? keywords = null)
        {
            if (command.CanExecute(null)) Add(category, title, () => command.Execute(null), shortcut, subtitle, keywords);
        }

        // ----- Query editor (the active Query tab; usable offline too, only running needs a connection) -----
        var queryUsable = IsConnected || !OfflineTabs.NeedsConnection(AppTab.Query);
        if (queryUsable && Query.SelectedDocument is { } doc)
        {
            void Editor(string title, EditorAction action, string? shortcut = null, string? keywords = null) =>
                Add("Query", title, () => RunInEditor(doc, action), shortcut, doc.Title, keywords);

            if (IsConnected) Editor("Run script or selection", EditorAction.RunScript, K(ShortcutCommands.RunScript), "execute go");
            if (IsConnected) Editor("Run statement at caret", EditorAction.RunStatement, K(ShortcutCommands.RunStatement), "execute current");
            if (IsConnected) Editor("Explain (execution plan)", EditorAction.Explain, K(ShortcutCommands.Explain), "plan showplan");
            if (IsConnected) Editor("Explain analyze (actual plan)", EditorAction.ExplainAnalyze, K(ShortcutCommands.ExplainAnalyze), "plan actual");
            Command("Query", "Cancel the running query", doc.CancelCommand, subtitle: doc.Title, keywords: "stop abort");
            Command("Query", "Commit transaction", doc.CommitCommand, subtitle: doc.Title);
            Command("Query", "Rollback transaction", doc.RollbackCommand, subtitle: doc.Title, keywords: "undo");
            Editor("Format SQL", EditorAction.Format, K(ShortcutCommands.Format), "beautify pretty indent");
            Editor("Comment or uncomment lines", EditorAction.ToggleComment, K(ShortcutCommands.ToggleComment), "toggle");
            Editor("Find", EditorAction.Find, "Ctrl+F", "search");
            Editor("Find and replace", EditorAction.Replace, K(ShortcutCommands.Replace), "search substitute");
            Editor("Go to line…", EditorAction.GoToLine, K(ShortcutCommands.GoToLine), "jump");
            Editor("Upper case", EditorAction.UpperCase, K(ShortcutCommands.UpperCase), "capitals");
            Editor("Lower case", EditorAction.LowerCase, K(ShortcutCommands.LowerCase));
            Editor("Duplicate line or selection", EditorAction.Duplicate, K(ShortcutCommands.Duplicate), "copy");
            Editor("Next SQL warning", EditorAction.NextWarning, K(ShortcutCommands.NextWarning), "problem inspection");
            Editor(_settings.EditorWordWrap ? "Turn word wrap off" : "Turn word wrap on", EditorAction.ToggleWordWrap, K(ShortcutCommands.WordWrap), "wrap lines");
            Editor("Zoom in", EditorAction.ZoomIn, K(ShortcutCommands.ZoomIn), "font bigger larger");
            Editor("Zoom out", EditorAction.ZoomOut, K(ShortcutCommands.ZoomOut), "font smaller");
            Editor("Reset zoom", EditorAction.ResetZoom, K(ShortcutCommands.ResetZoom), "font size");
            Editor("Maximize results", EditorAction.MaximizeResults, K(ShortcutCommands.MaximizeResults), "layout grid");
            Editor("Maximize editor", EditorAction.MaximizeEditor, K(ShortcutCommands.MaximizeEditor), "layout");
            Editor("Reset editor and results split", EditorAction.ResetLayout, keywords: "layout");
            Add("Query", _settings.SqlInspections ? "Turn SQL warnings off" : "Turn SQL warnings on",
                () => _settings.SetSqlInspections(!_settings.SqlInspections), keywords: "inspections lint problems", id: "toggle-sql-warnings");
            if (doc.IsAssistantAvailable)
                Command("Query", doc.IsAssistantOpen ? "Hide AI assistant" : "Show AI assistant", doc.ToggleAssistantCommand, keywords: "ai claude chat");
            Command("Query", "AI assistant settings…", doc.OpenAssistantSettingsCommand, keywords: "ai claude api key");
            Command("Query", "Close all pinned results", doc.CloseAllPinnedResultsCommand, keywords: "pins");
        }

        // ----- Query tabs -----
        if (queryUsable)
        {
            Add("Query tabs", "New query tab", () => { SelectedTab = AppTab.Query; Query.NewTab(); }, K(ShortcutCommands.NewTab), keywords: "new script sql editor");
            Add("Query tabs", "Open SQL file…", () => RunInWorkspace(QueryFileAction.Open), K(ShortcutCommands.OpenFile), keywords: "load script");
            foreach (var recent in Query.RecentFiles)
            {
                Add("Query tabs", $"Open recent: {recent.Name}", () =>
                {
                    SelectedTab = AppTab.Query;
                    Query.OpenRecentCommand.Execute(recent);
                }, subtitle: recent.Folder, keywords: "recent file sql script mru", id: "open-recent:" + recent.Path);
            }
            // Closed Query tabs: the newest first (Ctrl+Shift+T), then each one by name.
            foreach (var (closed, i) in Query.RecentlyClosed.Select((c, i) => (c, i)))
            {
                Add("Query tabs", i == 0 ? $"Reopen closed tab: {closed.Title}" : $"Reopen {closed.Title}", () =>
                {
                    SelectedTab = AppTab.Query;
                    Query.ReopenTabCommand.Execute(closed);
                }, i == 0 ? K(ShortcutCommands.ReopenTab) : null, $"closed at {closed.When}", "restore undo close", id: i == 0 ? "reopen-closed-tab" : null);
            }
            if (Query.SelectedDocument is { } tab)
            {
                Add("Query tabs", "Save", () => RunInWorkspace(QueryFileAction.Save), K(ShortcutCommands.Save), tab.Title, "write file");
                Add("Query tabs", "Save as…", () => RunInWorkspace(QueryFileAction.SaveAs), K(ShortcutCommands.SaveAs), tab.Title, "write file");
                Command("Query tabs", "Close query tab", Query.CloseTabCommand, K(ShortcutCommands.CloseTab), tab.Title);
                Command("Query tabs", "Close other query tabs", Query.CloseOtherTabsCommand, subtitle: tab.Title);
                Command("Query tabs", "Close all query tabs", Query.CloseAllTabsCommand);
                Command("Query tabs", "Duplicate query tab", Query.DuplicateTabCommand, subtitle: tab.Title, keywords: "copy clone");
                Command("Query tabs", "Rename query tab…", Query.RenameTabCommand, subtitle: tab.Title);
                Command("Query tabs", "Local history…", Query.ShowLocalHistoryCommand, K(ShortcutCommands.LocalHistory), $"earlier versions of {tab.Title}", "versions restore diff");
                if (tab.FilePath is not null)
                    Command("Query tabs", "Show file in folder", Query.ShowInFolderCommand, subtitle: tab.FilePath, keywords: "explorer reveal finder");
            }
        }

        // ----- Connection -----
        Command("Connection", "Connect", ConnectCommand, subtitle: SelectedProfile?.ToString(), keywords: "open login");
        Command("Connection", "Disconnect", DisconnectCommand, subtitle: Session?.Profile.ToString(), keywords: "close logout");
        Command("Connection", "Refresh metadata", RefreshMetadataCommand, subtitle: "re-read the catalog", keywords: "reload schema objects");
        Command("Connection", "Cancel loading", CancelLoadCommand, keywords: "stop abort");
        Command("Connection", "New connection…", NewConnectionCommand, keywords: "add server profile");
        Command("Connection", "Edit connection…", EditConnectionCommand, subtitle: SelectedProfile?.ToString(), keywords: "change settings password");
        Command("Connection", "Duplicate connection…", DuplicateConnectionCommand, subtitle: SelectedProfile?.ToString(), keywords: "copy clone");
        Command("Connection", "Delete connection…", DeleteConnectionCommand, subtitle: SelectedProfile?.ToString(), keywords: "remove");
        Command("Connection", "Export connections…", ExportConnectionsCommand, subtitle: "with or without passwords", keywords: "backup share file");
        Command("Connection", "Import connections…", ImportConnectionsCommand, keywords: "load file");
        Command("Connection", "Export database documentation…", ExportDocumentationCommand,
            subtitle: "tables, columns, keys, comments and diagram as HTML or Markdown", keywords: "docs data dictionary schema report wiki");
        Command("Connection", "Back up a database…", BackupDatabaseCommand,
            subtitle: "to a .bak file on the server, or a pg_dump / mysqldump file here", keywords: "backup dump export save copy bak pg_dump mysqldump");
        Command("Connection", "Restore a database…", RestoreDatabaseCommand,
            subtitle: "from a backup file, into a new or an existing database", keywords: "restore load import bak dump pg_restore");
        Command("Connection", "Team sharing…", OpenTeamCommand, subtitle: "share connections, queries and snippets through a shared folder", keywords: "sync");
        if (!IsBusy)
        {
            foreach (var profile in Profiles.ToList())
            {
                if (Session?.Profile.Id == profile.Id) continue;
                // Same path as the navigator: a switch asks first when it would lose unsaved work.
                var action = IsConnected ? NavigatorConnectionAction.Switch : NavigatorConnectionAction.Connect;
                Add("Connection", $"{(IsConnected ? "Switch to" : "Connect to")} {profile}",
                    () => _ = OnNavigatorConnectionActionAsync(action, profile),
                    subtitle: profile.Host, keywords: "connect switch", id: "connect:" + profile.Id);
            }
        }

        // ----- Other tabs -----
        if (IsConnected)
        {
            Command("Navigator", "Collapse the navigator tree", Navigator.CollapseAllCommand, keywords: "tree fold");
            if (Objects.SelectedObject is { Type: DbObjectType.Table } table)
                Command("Data", $"Import data into {table.FullName}…", Objects.ImportDataCommand, keywords: "csv excel xlsx tsv load file rows");
            Command("Data", "Import a CSV or Excel file…", Objects.ImportNewTableCommand, subtitle: "into a new or an existing table",
                keywords: "csv excel xlsx tsv load file rows new table");
            Command("Comparer", "Compare schemas", Comparer.CompareSchemaCommand, keywords: "diff");
            Command("Comparer", "Compare data", Comparer.CompareDataCommand, keywords: "diff rows");
            Command("Locks", "Refresh locks", Locks.RefreshCommand, keywords: "blocking");
            Command("Activity", "Refresh activity", Activity.RefreshRequestsCommand, keywords: "sessions requests");
        }
        Command("ER model", "New ER model", ErModel.NewModelCommand, keywords: "diagram design");
        Command("ER model", "Open ER model…", ErModel.OpenModelCommand, keywords: "dbxmodel file");
        Command("ER model", "Save ER model", ErModel.SaveModelCommand, keywords: "dbxmodel file");

        // ----- View and settings -----
        Add("View", ShowNavigator ? "Hide the database navigator" : "Show the database navigator", () => ShowNavigator = !ShowNavigator,
            K(ShortcutCommands.ToggleNavigator), keywords: "tree sidebar explorer", id: "toggle-navigator");
        foreach (var theme in ThemeModes)
            if (theme != SelectedTheme)
                Add("View", $"Theme: {theme}", () => SelectedTheme = theme, keywords: "color colour appearance dark light");
        Add("Settings", ReconnectOnStart ? "Don't reconnect at start" : "Reconnect at start",
            () => ReconnectOnStart = !ReconnectOnStart, keywords: "restore workspace startup", id: "toggle-reconnect");
        Add("Settings", SoftwareRendering ? "Use GPU rendering (after restart)" : "Use software rendering (after restart)",
            () => SoftwareRendering = !SoftwareRendering, keywords: "blank window graphics", id: "toggle-software-rendering");
        Add("Settings", "Preferences…", OpenPreferences, K(ShortcutCommands.Preferences), keywords: "settings options configure theme font row limit auto-commit notifications");
        if (_updates is not null)
            Add("Help", "Check for updates", () => _ = CheckForUpdatesNowAsync(), keywords: "update upgrade new version release download latest",
                id: "check-for-updates");
        if (AvailableUpdate is { } update)
            Add("Help", $"Download DB Explorer {update.Version}", () => DownloadUpdateCommand.Execute(null), keywords: "update upgrade install",
                id: "download-update");
        Add("Help", "Keyboard shortcuts", ShowShortcuts, K(ShortcutCommands.ShortcutSheet), keywords: "keys hotkeys keybindings cheat sheet help customize change remap");
        Add("Settings", Keymap.Current.VimMode ? "Turn Vim keys off" : "Turn Vim keys on", () => Keymap.Current.SetVimMode(!Keymap.Current.VimMode),
            subtitle: "Query editor", keywords: "vim vi modal normal insert editor keys", id: "toggle-vim");

        // ----- Tabs -----
        foreach (var tab in Enum.GetValues<AppTab>())
        {
            if (!IsConnected && OfflineTabs.NeedsConnection(tab)) continue;
            var title = Converters.HumanizeConverter.Instance.Convert(tab, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? tab.ToString();
            items.Add(new PaletteItem(title.Replace("Search names and code", "Search names & code").Replace("Er model", "ER model"), "tab", PaletteItemKind.Tab, _ => SelectedTab = tab));
        }

        // ----- Objects -----
        if (Session is { } session)
        {
            var multipleDatabases = session.Snapshot.Objects.Select(o => o.Database).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            foreach (var obj in session.Snapshot.Objects.Where(o => o.Type != DbObjectType.Trigger))
            {
                var title = multipleDatabases && !string.IsNullOrEmpty(obj.Database) ? $"{obj.Database}.{obj.FullName}" : obj.FullName;
                var subtitle = obj.Type + (obj.RowCount is long rows ? $" · {rows:N0} rows" : "");
                items.Add(new PaletteItem(title, subtitle, PaletteItemKind.Object, modifier => OpenObject(obj, modifier)));
            }
        }

        return items;
    }

    /// <summary>Shows the Query tab, then lets its editor run the action once the view is bound to the tab.</summary>
    private void RunInEditor(QueryViewModel doc, EditorAction action)
    {
        SelectedTab = AppTab.Query;
        Query.SelectedDocument = doc;
        Dispatcher.UIThread.Post(() => doc.RequestEditorAction(action), DispatcherPriority.Background);
    }

    private void RunInWorkspace(QueryFileAction action)
    {
        SelectedTab = AppTab.Query;
        Dispatcher.UIThread.Post(() => _ = Query.RequestFileActionAsync(action), DispatcherPriority.Background);
    }
}
