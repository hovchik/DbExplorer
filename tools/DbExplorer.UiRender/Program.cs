using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Application;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Query;
using DbExplorer.Application.Search;
using DbExplorer.Application.Updates;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop;
using DbExplorer.Desktop.Controls;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;
using DbExplorer.UiRender;
using Microsoft.Extensions.DependencyInjection;

// Renders the desktop app's windows to PNGs with Avalonia's headless Skia backend (no display needed).
// See README.md next to this file.

var outDir = args.Length > 0 ? args[0] : Path.Combine(FindRepoRoot(), ".ui-renders");
var pg = (Environment.GetEnvironmentVariable("DBX_RENDER_PG") ?? "localhost;5432;postgres;postgres;uirender_shop").Split(';');
var mssql = Environment.GetEnvironmentVariable("DBX_RENDER_MSSQL")?.Split(';');   // host;port;user;password;database
var mysql = Environment.GetEnvironmentVariable("DBX_RENDER_MYSQL")?.Split(';');   // host;port;user;password;database
Directory.CreateDirectory(outDir);

var log = new CapturingLogSink();
Logger.Sink = log;

AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .WithInterFont()
    .SetupWithoutStarting();

var exitCode = 0;
var done = new CancellationTokenSource();
Dispatcher.UIThread.Post(async () =>
{
    try
    {
        var script = new RenderScript(outDir, pg, mssql, mysql);
        await script.RunAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Render failed: " + ex);
        exitCode = 1;
    }
    finally
    {
        File.WriteAllLines(Path.Combine(outDir, "avalonia-log.txt"), log.Entries);
        Console.WriteLine($"{log.Entries.Count} distinct Avalonia warning(s)/error(s) → {Path.Combine(outDir, "avalonia-log.txt")}");
        done.Cancel();
    }
});
Dispatcher.UIThread.MainLoop(done.Token);
return exitCode;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbExplorer.sln"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

namespace DbExplorer.UiRender
{
    /// <summary>Keeps every Warning-or-worse line Avalonia logs (binding errors, layout cycles), once each.</summary>
    internal sealed class CapturingLogSink : ILogSink
    {
        private readonly HashSet<string> _seen = [];
        public List<string> Entries { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Add(level, area, source, messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            var message = messageTemplate;
            foreach (var value in propertyValues)
            {
                var open = message.IndexOf('{');
                var close = open >= 0 ? message.IndexOf('}', open) : -1;
                if (close < 0) break;
                message = message[..open] + (value?.ToString() ?? "null") + message[(close + 1)..];
            }
            Add(level, area, source, message);
        }

        private void Add(LogEventLevel level, string area, object? source, string message)
        {
            var line = $"[{level}] {area} {source?.GetType().Name}: {message}";
            if (_seen.Add(line)) Entries.Add(line);
        }
    }

    /// <summary>
    /// Lists, for every rendered frame, the name a screen reader would read for each visible button and input
    /// (accessibility-names.txt) and the ones it would read as just "button" or "edit" (accessibility-missing.txt).
    /// </summary>
    internal static class Accessibility
    {
        private static readonly SortedSet<string> Names = new(StringComparer.Ordinal);
        private static readonly SortedSet<string> Missing = new(StringComparer.Ordinal);

        public static void Audit(TopLevel window, string shot)
        {
            foreach (var c in window.GetVisualDescendants().OfType<Control>().Where(AccessibleNames.IsLabelled))
            {
                if (!c.IsEffectivelyVisible || c.TemplatedParent is not null) continue;
                var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c);
                var name = peer.GetName();
                var view = c.GetVisualAncestors().OfType<UserControl>().FirstOrDefault()?.GetType().Name ?? window.GetType().Name;
                var content = (c as ContentControl)?.Content is string s ? s : "";
                var line = $"{view}: {c.GetType().Name} \"{name}\"" + (content.Length > 0 && content != name ? $"  [shows \"{content}\"]" : "")
                           + (peer.GetAcceleratorKey() is { Length: > 0 } k ? $"  [{k}]" : "");
                Names.Add(line);
                if (!AccessibleNames.HasWords(name)) Missing.Add($"{line}  (first seen in {shot})");
            }
        }

        public static void Write(string outDir)
        {
            File.WriteAllLines(Path.Combine(outDir, "accessibility-names.txt"), Names);
            File.WriteAllLines(Path.Combine(outDir, "accessibility-missing.txt"), Missing);
            Console.WriteLine($"Accessibility: {Names.Count} distinct control names, {Missing.Count} without a name → {outDir}");
        }
    }

    internal sealed class RenderScript(string outDir, string[] pg, string[]? mssql, string[]? mysql)
    {
        private MainWindow _window = null!;
        private MainWindowViewModel _vm = null!;
        private ServiceProvider _provider = null!;
        private readonly List<string> _failures = [];

        public async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "DbExplorer.UiRender", Guid.NewGuid().ToString("N"));
            var services = App.ConfigureServices();
            services.AddSingleton(new AppPaths(root)); // never the user's own connections and settings
            services.AddSingleton<IReleaseSource>(new SampleReleases()); // never GitHub
            _provider = services.BuildServiceProvider();

            _window = App.CreateMainWindow(_provider);
            _vm = (MainWindowViewModel)_window.DataContext!;
            _window.Width = 1400;
            _window.Height = 900;
            _window.Show();
            await _vm.InitializeAsync();

            var pgProfile = Profile("Shop (PostgreSQL)", SqlDialect.PostgresKey, pg, ConnectionEnvironment.Development, "Local");
            _vm.Profiles.Add(pgProfile);
            if (mssql is not null) _vm.Profiles.Add(Profile("Shop (SQL Server)", SqlDialect.SqlServerKey, mssql, ConnectionEnvironment.Test, "Local"));
            if (mysql is not null) _vm.Profiles.Add(Profile("Shop (MariaDB)", SqlDialect.MySqlKey, mysql, ConnectionEnvironment.Production, "Local"));
            _vm.Profiles.Add(new ConnectionProfile { Name = "Reports", Folder = "Clients/Acme", ProviderKey = SqlDialect.SqlServerKey, Host = "reports.acme.example", Database = "Reports", UserName = "reader", Environment = ConnectionEnvironment.Production });
            _vm.SelectedProfile = pgProfile;
            await Settle();
            await Shot(_window, "main-disconnected");
            await Step("errors", () => RenderConnectionErrorsAsync(pg));
            _vm.SelectedProfile = pgProfile;

            await _vm.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => _vm.IsConnected && !_vm.IsBusy, "connect", 60_000);
            Console.WriteLine("Connected: " + _vm.StatusText);

            await Step("navigator", RenderNavigatorAsync);
            await Step("tabs", RenderTabsAsync);
            await Step("designer", RenderTableDesignerAsync);
            await Step("small", RenderSmallAsync);
            await Step("toolbar", RenderToolbarStatesAsync);
            await Step("querytabs", RenderManyQueryTabsAsync);
            await Step("pins", RenderPinnedResultsAsync);
            await Step("dialogs", RenderDialogsAsync);
            await Step("leave", RenderLeavePromptAsync);
            await Step("localhistory", RenderLocalHistoryAsync);
            await Step("reopen", RenderReopenClosedTabsAsync);
            await Step("sqlfiles", RenderSqlFilesAsync);
            await Step("offline", RenderOfflineAsync);
            await Step("palette", RenderPaletteAsync);
            await Step("usages", RenderUsagesAsync);
            await Step("search", RenderSearchNamesAsync);
            await Step("import", RenderImportAsync);
            await Step("preferences", RenderPreferencesAsync);
            await Step("docs", RenderDocumentationAsync);
            await Step("testdata", RenderTestDataAsync);
            await Step("migration", RenderMigrationAsync);
            await Step("comparer", RenderComparerAsync);
            await Step("keys", RenderKeysAndVimAsync);
            await Step("fullresult", RenderFullResultAsync);
            await Step("inline", RenderInlineResultsAsync);
            await Step("datasearch", RenderDataSearchAsync);
            await Step("updates", RenderUpdatesAsync);
            await Step("charts", RenderChartsAsync);
            await Step("backup", RenderBackupAsync);
            await Step("jobs", () => RenderJobsAsync("postgres"));
            await Step("multi", RenderMultipleConnectionsAsync);
            await Step("builder", RenderQueryBuilderAsync);

            if (mssql is not null) await Step("sqlserver", () => RenderOtherEngineAsync("Shop (SQL Server)", "sqlserver"));
            if (mysql is not null) await Step("mysql", () => RenderOtherEngineAsync("Shop (MariaDB)", "mysql"));
            await Step("sqlite", () => RenderSqliteAsync(root));

            await _vm.ShutdownAsync();
            await _provider.DisposeAsync();
            Accessibility.Write(outDir);
            if (_failures.Count > 0)
            {
                Console.WriteLine("Steps that failed:");
                foreach (var f in _failures) Console.WriteLine("  " + f);
            }
        }

        private static ConnectionProfile Profile(string name, string providerKey, string[] parts, ConnectionEnvironment environment, string folder) => new()
        {
            Name = name,
            Folder = folder,
            ProviderKey = providerKey,
            Host = parts[0],
            Port = int.Parse(parts[1]),
            UserName = parts[2],
            Password = parts[3],
            Database = parts.Length > 4 ? parts[4] : "",
            SavePassword = true,
            Environment = environment,
            Encrypt = false
        };

        private static readonly HashSet<string> Only = (Environment.GetEnvironmentVariable("DBX_RENDER_ONLY") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        private async Task Step(string name, Func<Task> step)
        {
            if (Only.Count > 0 && !Only.Contains(name)) return;
            Console.WriteLine($"== {name}");
            try { await step(); }
            catch (Exception ex)
            {
                _failures.Add($"{name}: {ex.GetBaseException().Message}");
                Console.Error.WriteLine($"Step {name} failed: {ex}");
            }
        }

        // ------------------------------------------------------------------ main window tabs

        private DbObject Table(string name) =>
            _vm.Session!.Snapshot.Objects.First(o => o.Type == DbObjectType.Table && o.Name == name);

        /// <summary>The database navigator: the orders table open to its columns, its right-click menu, and a filter.</summary>
        private async Task RenderNavigatorAsync()
        {
            var nav = _vm.Navigator;
            _vm.ShowNavigator = true;
            _vm.SelectedTab = AppTab.Objects;
            await nav.RevealAsync(Table("orders"));
            var orders = nav.SelectedNode!;
            await orders.LoadAsync();
            orders.IsExpanded = true;
            foreach (var folder in orders.Children.Where(c => c.Title is "Columns" or "Foreign keys"))
            {
                await folder.LoadAsync();
                folder.IsExpanded = true;
            }
            nav.Activate(orders);
            await WaitFor(() => _vm.Objects.Columns.Count > 0, "objects columns");
            await Settle(4);
            await Shot(_window, "navigator");

            var tree = _window.GetVisualDescendants().OfType<TreeView>().First(t => t.Name == "Tree");
            tree.ContextMenu!.Open(tree);
            await Settle(4);
            await Shot(_window, "navigator-menu");
            tree.ContextMenu.Close();

            // Saved connections can be managed while connected: the menu on another (not open) connection.
            var reporting = new ConnectionProfile
            {
                Name = "Reporting", Folder = "Local", ProviderKey = SqlDialect.PostgresKey, Host = "reports.internal",
                Port = 5432, Database = "reports", UserName = "report", Environment = ConnectionEnvironment.Staging
            };
            _vm.Profiles.Add(reporting);
            await Settle(2);
            static IEnumerable<NavigatorNode> All(IEnumerable<NavigatorNode> nodes) => nodes.SelectMany(n => All(n.Children).Prepend(n));
            nav.SelectedNode = All(nav.Roots).First(n => n.Kind == NavigatorNodeKind.Connection && n.Profile?.Id == reporting.Id);
            await Settle(2);
            tree.ContextMenu.Open(tree);
            await Settle(4);
            await Shot(_window, "navigator-connection-menu");
            tree.ContextMenu.Close();
            _vm.Profiles.Remove(reporting);
            nav.SelectedNode = null;
            await Settle(2);

            nav.FilterText = "order";
            await Settle(6);
            await Shot(_window, "navigator-filter");
            nav.FilterText = "";
            await Settle();

            _window.Width = 1024;
            _window.Height = 700;
            _vm.SelectedTab = AppTab.Query;
            await Settle(6);
            await Shot(_window, "small-navigator-query");
            _window.Width = 1400;
            _window.Height = 900;
            _vm.SelectedTab = AppTab.Objects;
            await Settle();
        }

        /// <summary>Export documentation: the navigator menu on a schema row, then the dialog it opens.</summary>
        private async Task RenderDocumentationAsync()
        {
            var nav = _vm.Navigator;
            _vm.ShowNavigator = true;
            _vm.SelectedTab = AppTab.Objects;
            await nav.RevealAsync(Table("orders"));
            for (var n = nav.SelectedNode; n is not null; n = n.Parent)
                if (n.Kind == NavigatorNodeKind.Schema) { nav.SelectedNode = n; break; }
            await Settle(2);
            var tree = _window.GetVisualDescendants().OfType<TreeView>().First(t => t.Name == "Tree");
            tree.ContextMenu!.Open(tree);
            await Settle(4);
            await Shot(_window, "navigator-schema-menu");
            tree.ContextMenu.Close();

            nav.ExportDocumentationCommand.Execute(null);
            await ShootOwned<ExportDocumentationWindow>("export-documentation", 12);
            nav.SelectedNode = null;
            await Settle();
        }

        private async Task RenderTabsAsync()
        {
            // Query: a result grid, then an estimated plan
            _vm.SelectedTab = AppTab.Query;
            var doc = _vm.Query.SelectedDocument ?? _vm.Query.NewTab();
            doc.SetText("SELECT o.id, c.name AS customer, c.email, o.status, o.ordered_at, o.note, order_total(o.id) AS total,\n" +
                        "       c.is_active, oi.unit_price\n" +
                        "FROM orders o\n" +
                        "JOIN customers c ON c.id = o.customer_id\n" +
                        "LEFT JOIN order_items oi ON oi.order_id = o.id AND oi.id = (SELECT min(id) FROM order_items WHERE order_id = o.id)\n" +
                        "ORDER BY o.ordered_at DESC;", markClean: true);
            await Settle();
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning && doc.ResultSets.Count > 0, "query results");
            await Shot(_window, "query");

            await doc.ExplainCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning && doc.ResultSets.Any(r => !r.IsGrid), "plan");
            await Shot(_window, "plan");

            // A failed statement: the error placement
            _vm.Query.OpenInNewTab("SELECT * FROM no_such_table WHERE id = 1;", "Broken query");
            var errorDoc = _vm.Query.SelectedDocument!;
            await Settle();
            await errorDoc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !errorDoc.IsRunning, "error query");
            await Shot(_window, "query-error");
            _vm.Query.SelectedDocument = doc;

            // Objects: a table, then a function definition
            _vm.SelectedTab = AppTab.Objects;
            _vm.Objects.SelectedObject = _vm.Objects.Objects.First(o => o.Type == DbObjectType.Table && o.Name == "orders");
            await WaitFor(() => _vm.Objects.Columns.Count > 0, "objects columns");
            await Settle(4);
            await Shot(_window, "objects");
            _vm.Objects.SelectedObject = _vm.Objects.Objects.First(o => o.Name == "order_total");
            await WaitFor(() => _vm.Objects.Definition.Length > 0, "objects definition");
            await Shot(_window, "objects-function");

            // The right-click menu on a table, then Modify on the function: a Query tab with CREATE OR REPLACE
            _vm.Objects.SelectedObject = _vm.Objects.Objects.First(o => o.Type == DbObjectType.Table && o.Name == "orders");
            await Settle();
            var objectsGrid = _window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "ObjectsGrid");
            objectsGrid.ContextMenu!.Open(objectsGrid);
            await Settle(4);
            if (TopLevel.GetTopLevel(objectsGrid.ContextMenu) is { } menuRoot && menuRoot != _window) await Shot(menuRoot, "objects-menu");
            else await Shot(_window, "objects-menu");
            objectsGrid.ContextMenu.Close();
            _vm.Objects.SelectedObject = _vm.Objects.Objects.First(o => o.Name == "order_total");
            await _vm.Objects.ModifyCommand.ExecuteAsync(null);
            await Settle(4);
            await Shot(_window, "objects-modify");
            _vm.SelectedTab = AppTab.Objects;

            // Comparer: both sides on the current connection, one table compared
            _vm.SelectedTab = AppTab.Comparer;
            await Settle();
            await Shot(_window, "comparer-empty");
            await _vm.Comparer.UseCurrentAsLeftCommand.ExecuteAsync(null);
            await _vm.Comparer.UseCurrentAsRightCommand.ExecuteAsync(null);
            await WaitFor(() => _vm.Comparer.LeftObjects.Count > 0 && _vm.Comparer.RightObjects.Count > 0, "comparer objects");
            _vm.Comparer.SelectedTabIndex = ComparerViewModel.SchemaTab;
            _vm.Comparer.SelectedLeftObject = _vm.Comparer.LeftObjects.First(o => o.Name == "v_order_totals");
            await Settle(4);
            await _vm.Comparer.CompareSchemaCommand.ExecuteAsync(null);
            await WaitFor(() => !_vm.Comparer.IsBusy, "compare schema");
            await Shot(_window, "comparer");
            _vm.Comparer.CompareOverviewCommand.Execute(null);
            await WaitFor(() => !_vm.Comparer.IsBusy && _vm.Comparer.OverviewEntries.Count > 0, "compare overview", 30_000);
            _vm.Comparer.SelectedTabIndex = 0;
            await Settle(4);
            await Shot(_window, "comparer-overview");

            // Search names & code
            _vm.SelectedTab = AppTab.SearchNamesAndCode;
            await Settle();
            await Shot(_window, "search-names-empty");
            _vm.Search.Query = "order";
            await _vm.Search.SearchCommand.ExecuteAsync(null);
            await WaitFor(() => _vm.Search.Rows.Count > 0, "search results");
            _vm.Search.SelectedRow = _vm.Search.Rows.First(r => r.IsCode && r.Group.ObjectName == "order_total");
            await WaitFor(() => _vm.Search.PreviewText.Length > 0, "search preview");
            await Settle(4);
            await Shot(_window, "search-names");

            // Search data
            _vm.SelectedTab = AppTab.SearchData;
            await Settle();
            await Shot(_window, "search-data-empty");
            _vm.DataSearch.Term = "Customer 7";
            await _vm.DataSearch.StartCommand.ExecuteAsync(null);
            await WaitFor(() => !_vm.DataSearch.IsRunning && _vm.DataSearch.Matches.Count > 0, "data search", 60_000);
            _vm.DataSearch.SelectedMatch = _vm.DataSearch.Matches[0];
            await Shot(_window, "search-data");

            // Diagram
            _vm.SelectedTab = AppTab.Diagram;
            await Settle();
            await Shot(_window, "diagram-empty");
            _vm.Diagram.ShowTable(Table("orders"));
            await Settle(8);
            await Shot(_window, "diagram");

            // Table designer
            _vm.SelectedTab = AppTab.TableDesigner;
            await Settle();
            await Shot(_window, "table-designer-empty");
            await _vm.TableDesigner.OpenTableAsync(Table("order_items"));
            await Settle(4);
            await Shot(_window, "table-designer");

            // ER model
            _vm.SelectedTab = AppTab.ErModel;
            await Settle();
            await Shot(_window, "er-model-empty");
            await _vm.ErModel.ReadFromDatabaseCommand.ExecuteAsync(null);
            await WaitFor(() => !_vm.ErModel.IsBusy, "er model");
            await Settle(6);
            await Shot(_window, "er-model");

            // Query builder
            _vm.SelectedTab = AppTab.QueryBuilder;
            await Settle();
            await Shot(_window, "query-builder-empty");
            var orders = _vm.QueryBuilder.AddTable(Table("orders"), 30, 30);
            var customers = _vm.QueryBuilder.AddTable(Table("customers"), 420, 60);
            if (orders is not null) { _vm.QueryBuilder.ToggleColumn(orders, "id"); _vm.QueryBuilder.ToggleColumn(orders, "status"); }
            if (customers is not null) _vm.QueryBuilder.ToggleColumn(customers, "name");
            await Settle(6);
            await Shot(_window, "query-builder");

            // Indexes, Locks, Activity
            _vm.SelectedTab = AppTab.Indexes;
            await Settle();
            await Shot(_window, "indexes-empty");
            await _vm.Indexes.LoadCommand.ExecuteAsync(null);
            await Settle(4);
            await Shot(_window, "indexes");

            _vm.SelectedTab = AppTab.Locks;
            await Settle();
            await _vm.Locks.RefreshCommand.ExecuteAsync(null);
            await Settle(4);
            await Shot(_window, "locks");
            _vm.Locks.ShowRecorder = true;
            await Settle(4);
            await Shot(_window, "locks-recorder");
            _vm.Locks.ShowRecorder = false;

            _vm.SelectedTab = AppTab.Activity;
            await Settle();
            await Shot(_window, "activity-empty");
            await _vm.Activity.RefreshRequestsCommand.ExecuteAsync(null);
            await _vm.Activity.LoadTopQueriesCommand.ExecuteAsync(null);
            await Settle(4);
            await Shot(_window, "activity");

            _vm.SelectedTab = AppTab.Lab;
            await Settle(6);
            await Shot(_window, "lab");

            _vm.SelectedTab = AppTab.Security;
            await WaitFor(() => _vm.Security.Principals.Count > 0, "security", 30_000);
            _vm.Security.SelectedPrincipal = _vm.Security.Principals.FirstOrDefault(p => p.Name == "postgres") ?? _vm.Security.Principals[0];
            await Settle(4);
            await Shot(_window, "security");
        }

        /// <summary>The Table designer's states: a new table with keys and indexes, an open table with new, changed and
        /// dropped columns, each section tab, the column menu and the common-columns menu, and the 1024 px window.</summary>
        private async Task RenderTableDesignerAsync()
        {
            var designer = _vm.TableDesigner;
            _vm.SelectedTab = AppTab.TableDesigner;
            await Settle(4);
            TabControl Sections() => _window.GetVisualDescendants().OfType<TableDesignerView>().First().FindControl<TabControl>("Sections")!;

            designer.NewTableCommand.Execute(null);
            designer.TableName = "invoices";
            designer.AddCommonColumnCommand.Execute(designer.CommonColumnChoices.First(c => c.Label == "Created at"));
            designer.AddColumnCommand.Execute(null);
            var customer = designer.Columns[^1];
            customer.Name = "customer_id";
            customer.Type = "integer";
            customer.IsNullable = false;
            designer.AddColumnCommand.Execute(null);
            designer.Columns[^1].Name = "total";
            designer.Columns[^1].Type = "numeric";
            designer.Columns[^1].Size = "12,2";
            designer.AddForeignKeyCommand.Execute(null);
            designer.ForeignKeys[^1].Column = "customer_id";
            designer.ForeignKeys[^1].ReferencedTable = "public.customers";
            await Settle(6);
            await Shot(_window, "designer-new");
            Sections().SelectedIndex = 1;
            await Settle(4);
            await Shot(_window, "designer-foreign-keys");
            Sections().SelectedIndex = 0;

            await designer.OpenTableAsync(Table("order_items"));
            await Settle(4);
            designer.Columns.First(c => c.Name == "quantity").Type = "bigint";
            designer.Columns.First(c => c.Name == "unit_price").Name = "price";
            designer.SelectedColumn = designer.Columns[^1];
            designer.AddCommonColumnCommand.Execute(designer.CommonColumnChoices.First(c => c.Label == "Created at"));
            designer.RemoveColumnCommand.Execute(designer.Columns.First(c => c.Name == "product_id"));
            designer.IndexColumnCommand.Execute(designer.Columns.First(c => c.Name == "created_at"));
            await Settle(6);
            await Shot(_window, "designer-alter");
            Sections().SelectedIndex = 2;
            await Settle(4);
            await Shot(_window, "designer-indexes");
            Sections().SelectedIndex = 0;
            await Settle(4);

            var menuButton = _window.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Column actions");
            menuButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Settle(4);
            await Shot(_window, "designer-column-menu");
            CloseFlyouts();
            var common = _window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Common columns ▾"));
            common.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Settle(4);
            await Shot(_window, "designer-common-columns");
            CloseFlyouts();

            _window.Width = 1024;
            _window.Height = 700;
            await Settle(6);
            await Shot(_window, "small-designer-alter");
            var view = _window.GetVisualDescendants().OfType<TableDesignerView>().First();
            view.FindControl<TabControl>("Sections")!.SelectedItem = view.FindControl<TabItem>("SideTab");
            await Settle(6);
            await Shot(_window, "small-designer-script");
            Sections().SelectedIndex = 0;
            designer.TableName = "";
            await Settle(6);
            await Shot(_window, "small-designer-error");
            _window.Width = 1400;
            _window.Height = 900;
            designer.RevertChangesCommand.Execute(null);
            await Settle(4);
        }

        private void CloseFlyouts() => _window.GetVisualDescendants().OfType<TableDesignerView>().First().LastMenu?.Hide();

        /// <summary>The Query builder: empty, a three-table design (related tables listed first, columns with a total,
        /// a sort and a filter), its Joins panel, and the design at 1024×700. Writes builder-*.png.</summary>
        private async Task RenderQueryBuilderAsync()
        {
            var b = _vm.QueryBuilder;
            _vm.SelectedTab = AppTab.QueryBuilder;
            if (b.ClearCommand.CanExecute(null)) b.ClearCommand.Execute(null);
            await Settle(4);
            await Shot(_window, "builder-empty");

            var orders = b.AddTable(Table("orders"), null);
            await Settle(2);
            await Shot(_window, "builder-related");
            var customers = b.AddTable(Table("customers"), null);
            var items = b.AddTable(Table("order_items"), null);
            if (orders is null || customers is null || items is null) throw new InvalidOperationException("query builder tables");
            b.ToggleColumn(customers, "name");
            b.ToggleColumn(orders, "status");
            b.ToggleColumn(items, "quantity");
            var quantity = b.Columns.Last();
            quantity.Aggregate = DbExplorer.Application.QueryBuilder.ColumnAggregate.Sum;
            quantity.Sort = DbExplorer.Application.QueryBuilder.SortDirection.Descending;
            b.Columns.First(c => c.Column == "status").Filter = "<> 'cancelled'";
            b.SelectedTable = items;
            await Settle(6);
            await Shot(_window, "builder");

            var tabs = _window.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("design"));
            tabs.SelectedIndex = 1;
            await Settle(4);
            await Shot(_window, "builder-joins");
            tabs.SelectedIndex = 0;

            _window.Width = 1024;
            _window.Height = 700;
            await Settle(6);
            await Shot(_window, "small-builder");
            _window.Width = 1400;
            _window.Height = 900;
            if (b.ClearCommand.CanExecute(null)) b.ClearCommand.Execute(null);
            await Settle(2);
        }

        private async Task RenderSmallAsync()
        {
            _window.Width = 1024;
            _window.Height = 700;
            await Settle(6);
            foreach (var tab in Enum.GetValues<AppTab>())
            {
                _vm.SelectedTab = tab;
                await Settle(6);
                await Shot(_window, $"small-{tab.ToString().ToLowerInvariant()}");
            }
            _window.Width = 1400;
            _window.Height = 900;
            _vm.SelectedTab = AppTab.Query;
            await Settle(6);
        }

        /// <summary>The app bar's busier states at 1024 px: reading the catalog (progress and Cancel) and a Team badge.</summary>
        private async Task RenderToolbarStatesAsync()
        {
            _window.Width = 1024;
            _window.Height = 700;
            _vm.IsBusy = true;
            if (_vm.Team is { } team) team.ChangedCount = 3;
            await Settle(6);
            await Shot(_window, "small-toolbar-busy");
            _vm.IsBusy = false;
            if (_vm.Team is { } t) t.ChangedCount = 0;
            _window.Width = 1400;
            _window.Height = 900;
            await Settle(6);
        }

        /// <summary>More Query tabs than fit, some with long names, at both sizes: the strip must scroll, never run under ＋ New.</summary>
        private async Task RenderManyQueryTabsAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            var titles = new[]
            {
                "FoCurrency", "→ TransactionStatus", "Transactions", "DECLARE", "FinancialOrganizationEndpoint",
                "Transactions", "CREATE AuthorizationCodes", "SELECT customers JOIN orders JOIN order_items", "Report"
            };
            var added = new List<QueryViewModel>();
            foreach (var title in titles)
            {
                var doc = _vm.Query.NewTab();
                doc.Title = title;
                added.Add(doc);
            }
            _vm.Query.SelectedDocument = added[^3];
            foreach (var (width, height, prefix) in new[] { (1400, 900, ""), (1024, 700, "small-") })
            {
                _window.Width = width;
                _window.Height = height;
                await Settle(8);
                await Shot(_window, prefix + "query-many-tabs");
            }
            _vm.Query.SelectedDocument = added[0];
            await Settle(8);
            await Shot(_window, "small-query-many-tabs-first");
            foreach (var doc in added) _vm.Query.Documents.Remove(doc);
            _window.Width = 1400;
            _window.Height = 900;
            await Settle(6);
        }

        /// <summary>A result cut off at the row limit: the bar with Fetch more / Export full result, the export menus, a
        /// fetch, and a script that writes (not run again).</summary>
        private async Task RenderFullResultAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            _vm.Query.OpenInNewTab("SELECT g AS id, 'order ' || g AS label, round(g * 1.5, 2) AS amount\nFROM generate_series(1, 25000) g;", "Big result");
            var doc = _vm.Query.SelectedDocument!;
            await Settle();
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning && doc.ResultSets.Count > 0, "big result");
            await Settle(6);
            await Shot(_window, "fullresult-bar");

            ResultGridView Grid() => _window.GetVisualDescendants().OfType<ResultGridView>()
                .First(g => g.IsEffectivelyVisible && ReferenceEquals(g.ResultSet, doc.ResultSets[0]));
            var export = Grid().FindControl<Button>("ExportButton")!;
            export.Flyout!.ShowAt(export);
            await Settle(8);
            await Shot(_window, "fullresult-export-menu");
            export.Flyout.Hide();
            await Settle();
            var full = Grid().FindControl<Button>("ExportFullButton")!;
            full.Flyout!.ShowAt(full);
            await Settle(8);
            await Shot(_window, "fullresult-full-menu");
            full.Flyout.Hide();
            await Settle();

            await doc.ResultSets[0].Rerun!.FetchAsync(20_000);
            await WaitFor(() => !doc.IsRunning && doc.ResultSets.Count > 0, "fetch more");
            await Settle(6);
            await Shot(_window, "fullresult-fetched");

            _window.Width = 1024;
            _window.Height = 700;
            await Settle(6);
            await Shot(_window, "small-fullresult");
            _window.Width = 1400;
            _window.Height = 900;

            doc.SetText("INSERT INTO orders SELECT * FROM orders WHERE false;\nSELECT g AS id FROM generate_series(1, 25000) g;", markClean: true);
            doc.AutoCommit = false; // rolled back below
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning && doc.ResultSets.Count > 0, "write script");
            await Settle(6);
            await Shot(_window, "fullresult-unavailable");
            if (doc.HasOpenTransaction) await doc.RollbackCommand.ExecuteAsync(null);
            doc.AutoCommit = true;
            _vm.Query.Documents.Remove(doc);
            await Settle(4);
        }

        /// <summary>Results under their statements in the editor: a script of quick checks run once, then one statement
        /// run again, at both window sizes.</summary>
        private async Task RenderDataSearchAsync()
        {
            var ds = _vm.DataSearch;
            _vm.SelectedTab = AppTab.SearchData;
            await Settle();
            await Shot(_window, "datasearch-empty");

            // The scope pickers: the connection's schemas, then its tables with two ticked.
            var view = _window.GetVisualDescendants().OfType<DataSearchView>().First();
            Button PickerButton(string name) => (Button)view.FindControl<MultiNamePicker>(name)!.Content!;
            PickerButton("SchemaPicker").Flyout!.ShowAt(PickerButton("SchemaPicker"));
            await Settle(6);
            await Shot(_window, "datasearch-schemas");
            PickerButton("SchemaPicker").Flyout!.Hide();
            ds.TableChoices.First(c => c.Table == "customers").IsSelected = true;
            ds.TableChoices.First(c => c.Table == "products").IsSelected = true;
            PickerButton("TablePicker").Flyout!.ShowAt(PickerButton("TablePicker"));
            await Settle(6);
            await Shot(_window, "datasearch-tables");
            PickerButton("TablePicker").Flyout!.Hide();
            await Settle(4);
            await Shot(_window, "datasearch-scope");
            ds.ResetScopeCommand.Execute(null);

            // "12" is in names, emails, SKUs and notes; the size limit skips order_items so the skipped badge shows.
            ds.Term = "12";
            ds.MaxTableRows = 100;
            await ds.StartCommand.ExecuteAsync(null);
            await WaitFor(() => !ds.IsRunning && ds.Matches.Count > 0, "data search", 60_000);
            ds.SelectedMatch = ds.Matches.First(m => m.Match.Table == "customers" && m.Column == "email");
            await WaitFor(() => !ds.IsRecordLoading && ds.RecordFields.Count > 0, "found row");
            await Settle(8);
            await Shot(_window, "datasearch-results");

            ds.SelectedTable = ds.Tables.First(t => t.Table == "products");
            await WaitFor(() => !ds.IsRecordLoading && ds.RecordFields.Count > 0, "found row (products)");
            await Settle(8);
            await Shot(_window, "datasearch-table");

            ds.DetailTabIndex = 1;
            await Settle(4);
            await Shot(_window, "datasearch-skipped");
            await ds.RetrySkippedCommand.ExecuteAsync(null);
            await WaitFor(() => !ds.IsRunning, "retry skipped", 60_000);
            ds.DetailTabIndex = 0;
            ds.SelectedTable = ds.Tables[0];
            await WaitFor(() => !ds.IsRecordLoading, "found row (all)");
            await Settle(8);
            await Shot(_window, "datasearch-retried");

            _window.Width = 1024;
            _window.Height = 700;
            await Settle(8);
            await Shot(_window, "small-datasearch");
            _window.Width = 1400;
            _window.Height = 900;

            ds.MaxTableRows = null;
            ds.Term = "no such value anywhere";
            await ds.StartCommand.ExecuteAsync(null);
            await WaitFor(() => !ds.IsRunning, "data search (nothing)", 60_000);
            await Settle(8);
            await Shot(_window, "datasearch-nomatch");
        }

        private async Task RenderInlineResultsAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            const string script =
                "-- Quick checks before the release\n" +
                "SELECT count(*) AS orders, count(DISTINCT customer_id) AS customers FROM orders;\n\n" +
                "SELECT status, count(*) AS orders\nFROM orders\nGROUP BY status\nORDER BY status;\n\n" +
                "SET search_path TO public;\n\n" +
                "SELECT id, customer_id, status, note FROM orders ORDER BY id;\n\n" +
                "SELECT id, name, email FROM customers WHERE email IS NULL;\n";
            _vm.Query.OpenInNewTab(script, "Release checks");
            var doc = _vm.Query.SelectedDocument!;
            await Settle();
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning && doc.InlineResults.Count == 4, "inline results");
            doc.RequestEditorAction(EditorAction.MaximizeEditor);
            await Settle(8);
            await Shot(_window, "inline-results");

            // One statement again: its card is replaced, the others stay.
            var statement = "SELECT status, count(*) AS orders\nFROM orders\nGROUP BY status\nORDER BY status;";
            await doc.ExecuteCommand.ExecuteAsync(new QueryRun(statement, doc.Document.Text.IndexOf(statement, StringComparison.Ordinal)));
            await WaitFor(() => !doc.IsRunning, "one statement");
            await Settle(8);
            await Shot(_window, "inline-statement");

            _window.Width = 1024;
            _window.Height = 700;
            await Settle(8);
            await Shot(_window, "small-inline");
            _window.Width = 1400;
            _window.Height = 900;

            doc.RequestEditorAction(EditorAction.ResetLayout);
            _vm.Query.Documents.Remove(doc);
            await Settle(4);
        }

        /// <summary>Charts of query results: bars, a line split by a column (with the hover tooltip), area, pie and
        /// scatter, bars split by a column, and the Values picker; then the 1024 px window.</summary>
        private async Task RenderChartsAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            _vm.Query.OpenInNewTab(
                "SELECT p.name AS product, sum(oi.quantity * oi.unit_price) AS revenue, sum(oi.quantity) AS units\n" +
                "FROM order_items oi JOIN products p ON p.id = oi.product_id\nGROUP BY p.name ORDER BY revenue DESC LIMIT 12;\n" +
                "SELECT d::date AS week, s AS status, 20 + abs(hashtext(d::text || s)) % 40 + (extract(month FROM d)::int * 3) AS orders\n" +
                "FROM generate_series('2026-01-05'::date, '2026-10-26'::date, '1 week') d CROSS JOIN unnest(ARRAY['new','paid','shipped']) s;\n" +
                "SELECT status, count(*) AS orders FROM orders GROUP BY status;\n" +
                "SELECT name, price, stock FROM products;\n" +
                "SELECT p.name AS product, o.status, sum(oi.quantity) AS units FROM order_items oi\n" +
                "JOIN orders o ON o.id = oi.order_id JOIN products p ON p.id = oi.product_id\n" +
                "WHERE p.id <= 6 GROUP BY p.id, p.name, o.status ORDER BY p.id, o.status;", "Charts");
            var doc = _vm.Query.SelectedDocument!;
            await Settle();
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning && doc.ResultSets.Count == 5, "chart queries");
            await Settle(4);

            ResultGridView Grid() => _window.GetVisualDescendants().OfType<ResultGridView>().First(g => g.IsEffectivelyVisible);
            async Task Show(int result, ChartSpec? spec, string name)
            {
                doc.SelectedResult = doc.ResultSets[result];
                await Settle(4);
                var grid = Grid();
                grid.ShowChart(true);
                if (spec is not null) grid.SetChart(spec);
                await Settle(6);
                await Shot(_window, name);
            }

            await Show(0, null, "chart-bar"); // the default: bars of revenue by product
            await Show(4, new ChartSpec(ChartKind.Bar, 0, [2], PivotAggregate.Sum, SeriesField: 1), "chart-bar-split");
            var values = Grid().FindControl<Button>("ChartValuesButton")!;
            values.Flyout!.ShowAt(values);
            await Settle(8);
            await Shot(_window, "chart-values-menu");
            values.Flyout.Hide();
            await Settle();

            await Show(1, new ChartSpec(ChartKind.Line, 0, [2], PivotAggregate.Sum, SeriesField: 1), "chart-line");
            var chart = Grid().FindControl<DbExplorer.Desktop.Controls.ChartControl>("ChartView")!;
            var at = chart.TranslatePoint(new Point(chart.Bounds.Width * 0.55, chart.Bounds.Height * 0.5), _window)!.Value;
            _window.MouseMove(at);
            await Settle(4);
            await Shot(_window, "chart-line-hover");
            _window.MouseMove(new Point(2, 2));
            await Show(1, new ChartSpec(ChartKind.Area, 0, [2], PivotAggregate.Sum), "chart-area");
            await Show(2, new ChartSpec(ChartKind.Pie, 0, [1], PivotAggregate.Sum), "chart-pie");
            await Show(3, new ChartSpec(ChartKind.Scatter, 1, [2], PivotAggregate.Sum), "chart-scatter");

            _window.Width = 1024;
            _window.Height = 700;
            await Show(1, new ChartSpec(ChartKind.Line, 0, [2], PivotAggregate.Sum, SeriesField: 1), "small-chart-line");
            await Show(2, new ChartSpec(ChartKind.Pie, 0, [1], PivotAggregate.Sum), "small-chart-pie");
            _window.Width = 1400;
            _window.Height = 900;
            _vm.Query.Documents.Remove(doc);
            await Settle(4);
        }

        /// <summary>Pinned results: a run's result pinned, then the same query run again after a change, so both are on screen.</summary>
        private async Task RenderPinnedResultsAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            _vm.Query.OpenInNewTab("SELECT status, count(*) AS orders FROM orders GROUP BY status ORDER BY status;\n" +
                                   "SELECT id, status, note FROM orders WHERE status = 'new' ORDER BY id LIMIT 10;", "Order status");
            var doc = _vm.Query.SelectedDocument!;
            await Settle();
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning && doc.ResultSets.Count == 2, "status counts");
            await Shot(_window, "pins-before");

            // Ctrl+T: the second result as aligned text, then back to the grid.
            doc.SelectedResult = doc.ResultSets[1];
            doc.ResultsAsText = true;
            await Settle(4);
            await Shot(_window, "results-as-text");
            doc.ResultsAsText = false;
            doc.SelectedResult = doc.ResultSets[0];
            await Settle(2);

            doc.PinResultCommand.Execute(doc.ResultSets[0]);
            await Settle(4);
            await Shot(_window, "pins-pinned");

            doc.SetText("UPDATE orders SET status = 'shipped' WHERE id IN (SELECT id FROM orders WHERE status = 'paid' ORDER BY id LIMIT 7);", markClean: true);
            doc.AutoCommit = false; // rolled back below, so the sample database stays as seeded
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning, "update");
            doc.SetText("SELECT status, count(*) AS orders FROM orders GROUP BY status ORDER BY status;", markClean: true);
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning && doc.ResultSets.Count == 1, "status counts again");
            await Settle(4);
            await Shot(_window, "pins-compare");
            doc.SelectedResult = doc.Pins.Items[0];
            await Settle(4);
            await Shot(_window, "pins-open");

            await doc.RollbackCommand.ExecuteAsync(null);
            doc.AutoCommit = true;
            _vm.Query.Documents.Remove(doc);
            await Settle(4);
        }

        // ------------------------------------------------------------------ dialogs

        private async Task RenderDialogsAsync()
        {
            var registry = _provider.GetRequiredService<ProviderRegistry>();
            var tunnels = _provider.GetRequiredService<SshTunnelService>();

            var draft = new ConnectionProfile { ProviderKey = registry.All[0].Key };
            await ShowAndShoot(new ConnectionDialog { DataContext = new ConnectionDialogViewModel(registry, draft, ["Local", "Clients/Acme"], tunnels), Title = "New connection" }, "connection-dialog");

            // A new connection to another machine: encrypted and verified by default; Production shows the
            // certificate warning once Trust certificate is ticked.
            var remoteVm = new ConnectionDialogViewModel(registry, new ConnectionProfile { ProviderKey = registry.All[0].Key }, ["Local"], tunnels, isNew: true)
            {
                Host = "db.example.com"
            };
            await ShowAndShoot(new ConnectionDialog { DataContext = remoteVm, Title = "New connection" }, "connection-dialog-remote");
            remoteVm.Environment = ConnectionEnvironment.Production;
            remoteVm.TrustServerCertificate = true;
            await ShowAndShoot(new ConnectionDialog { DataContext = remoteVm, Title = "New connection" }, "connection-dialog-production-warning");

            // Connection string: the clipboard offer on a new connection, a filled-in string, and a string that can't be read.
            var pasteVm = new ConnectionDialogViewModel(registry, new ConnectionProfile { ProviderKey = registry.All[0].Key }, ["Local"], tunnels, isNew: true);
            pasteVm.OfferClipboard("postgresql://app:secret@db.example.com:6543/shop?sslmode=require&application_name=web");
            await ShowAndShoot(new ConnectionDialog { DataContext = pasteVm, Title = "New connection" }, "connection-string-offer");
            var filledVm = new ConnectionDialogViewModel(registry, new ConnectionProfile { ProviderKey = registry.All[0].Key }, ["Local"], tunnels, isNew: true);
            filledVm.Fill("jdbc:sqlserver://sql01.corp\\SALES:1450;databaseName=Orders;user=report;password=pw;encrypt=true;loginTimeout=30;applicationIntent=ReadOnly", clearBox: true);
            await ShowAndShoot(new ConnectionDialog { DataContext = filledVm, Title = "New connection" }, "connection-string-filled");
            var badVm = new ConnectionDialogViewModel(registry, new ConnectionProfile { ProviderKey = registry.All[0].Key }, ["Local"], tunnels, isNew: true)
            {
                ConnectionStringText = "Server=db01;Port=fifty"
            };
            badVm.FillFromConnectionStringCommand.Execute(null);
            await ShowAndShoot(new ConnectionDialog { DataContext = badVm, Title = "New connection" }, "connection-string-error");

            // Microsoft Entra ID: the offer on an Azure host, browser sign-in on Azure SQL, and an app on Azure PostgreSQL.
            var sqlKey = registry.All.First(p => p.Key == "SqlServer");
            var azureVm = new ConnectionDialogViewModel(registry, new ConnectionProfile { ProviderKey = sqlKey.Key }, ["Local"], tunnels, isNew: true)
            {
                Host = "shop-prod.database.windows.net", Database = "shop"
            };
            await ShowAndShoot(new ConnectionDialog { DataContext = azureVm, Title = "New connection" }, "entra-offer");
            azureVm.UserName = "ann@contoso.com";
            azureVm.UseEntraSignInCommand.Execute(null);
            await ShowAndShoot(new ConnectionDialog { DataContext = azureVm, Title = "New connection" }, "entra-interactive");
            var pgAppVm = new ConnectionDialogViewModel(registry, new ConnectionProfile { ProviderKey = "Postgres" }, ["Local"], tunnels, isNew: true)
            {
                Host = "pg-prod.postgres.database.azure.com", Database = "shop"
            };
            pgAppVm.SelectedSignIn = pgAppVm.SignInOptions.First(o => o.Method == SignInMethod.EntraServicePrincipal);
            pgAppVm.UserName = "reporting-app";
            pgAppVm.ClientId = "6f1c2b0e-4d1a-4c55-9a7e-2f0b8c1d3e42";
            pgAppVm.Password = "secret";
            pgAppVm.TenantId = "contoso.onmicrosoft.com";
            await ShowAndShoot(new ConnectionDialog { DataContext = pgAppVm, Title = "New connection" }, "entra-postgres-app");

            var sshDraft = _vm.Profiles[0].Clone();
            sshDraft.Ssh.Enabled = true;
            sshDraft.Ssh.Host = "bastion.example.com";
            sshDraft.Ssh.UserName = "deploy";
            var sshDialog = new ConnectionDialog { DataContext = new ConnectionDialogViewModel(registry, sshDraft, ["Local"], tunnels), Title = "Edit connection" };
            await ShowAndShoot(sshDialog, "connection-dialog-ssh", d => ((ConnectionDialog)d).ShowSshTab = true);

            _vm.OpenCommandPaletteCommand.Execute(null);
            await ShootOwned<CommandPaletteWindow>("command-palette");

            _vm.OpenTeamCommand.Execute(null);
            await ShootOwned<TeamWindow>("team", 10);

            await ShowAndShoot(new ConfirmWindow(
                "Run DELETE FROM orders WHERE status = 'cancelled' on PRODUCTION? 31 row(s) match.",
                "Run on production", requiredText: "PRODUCTION", banner: "PRODUCTION · Shop (PostgreSQL) · changes here affect live data",
                details: "DELETE FROM orders WHERE status = 'cancelled';\n-- the statement has no transaction around it"), "confirm");
            await ShowAndShoot(new ConfirmWindow("Discard the 3 edited row(s) that are not committed yet?", "Discard"), "confirm-plain");

            _vm.SelectedTab = AppTab.Objects;
            _vm.Objects.SelectedObject = _vm.Objects.Objects.First(o => o.Name == "order_total");
            await Settle(4);
            _vm.Objects.ExecuteSelectedCommand.Execute(null);
            await ShootOwned<RoutineExecutionWindow>("routine-execution", 10);

            _vm.Objects.SelectedObject = _vm.Objects.Objects.First(o => o.Name == "mark_shipped");
            await Settle(4);
            _vm.Objects.DebugSelectedCommand.Execute(null);
            await ShootOwned<RoutineDebuggerWindow>("routine-debugger", 15);

            _vm.Objects.SelectedObject = _vm.Objects.Objects.First(o => o.Type == DbObjectType.Table && o.Name == "orders");
            await Settle(4);
            _vm.Objects.GetDataCommand.Execute(null);
            await ShootOwned<GetDataWindow>("get-data", 15);

            await ShowAndShoot(new ValueViewerWindow("note (row 3)", string.Join("\n", Enumerable.Range(1, 12).Select(i => $"Line {i}: {{\"order\": {i}, \"status\": \"shipped\", \"note\": \"Lorem ipsum dolor sit amet, consectetur adipiscing elit.\"}}"))), "value-viewer");

            _vm.SelectedTab = AppTab.Query;
            var doc = _vm.Query.SelectedDocument!;
            _ = doc.OpenAssistantSettingsCommand.ExecuteAsync(null);
            await ShootOwned<AssistantSettingsWindow>("assistant-settings");

            _ = _vm.ExportConnectionsCommand.ExecuteAsync(null);
            await ShootOwned<ExportConnectionsWindow>("export-connections");

            await ShowAndShoot(new ImportPasswordWindow("team-connections.dbxc", "That is not the password this file was exported with."), "import-password");
            await ShowAndShoot(new ImportConflictWindow(["Shop (PostgreSQL)", "Reports"]), "import-conflict");
            await ShowAndShoot(new LoginPromptWindow("New login", "The login is created on the server with the password below.", null, "Also create a user for it in uirender_shop"), "login-prompt");
            await ShowAndShoot(new TextPromptWindow("Rename tab", "A name for this query tab.", "Name", "Orders by customer", "e.g. Monthly totals"), "text-prompt");
            await ShowAndShoot(new ParametersWindow(["@from", "@to", "@status"], new Dictionary<string, string> { ["@from"] = "2026-01-01", ["@to"] = "", ["@status"] = "shipped" }), "parameters");

            _vm.SelectedTab = AppTab.SearchData;
            if (_vm.DataSearch.Matches.Count > 0)
            {
                _vm.DataSearch.ShowRelationsCommand.Execute(null);
                await ShootOwned<DataRelationsWindow>("data-relations", 20);
            }
            _vm.SelectedTab = AppTab.Query;
        }

        /// <summary>Connects to another engine and renders the tabs whose code paths differ by engine.</summary>
        private async Task RenderOtherEngineAsync(string profileName, string suffix)
        {
            await _vm.DisconnectCommand.ExecuteAsync(null);
            await WaitFor(() => !_vm.IsConnected && !_vm.IsBusy, "disconnect");
            _vm.SelectedProfile = _vm.Profiles.First(p => p.Name == profileName);
            await _vm.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => _vm.IsConnected && !_vm.IsBusy, "connect " + suffix, 90_000);
            if (!_vm.IsConnected) throw new InvalidOperationException(_vm.StatusText);

            await RenderJobsAsync(suffix);

            _vm.SelectedTab = AppTab.Security;
            await WaitFor(() => _vm.Security.Principals.Count > 0, "security " + suffix, 30_000);
            _vm.Security.SelectedPrincipal = _vm.Security.Principals.FirstOrDefault(); // a server with only built-in accounts shows the empty state
            await Settle(4);
            await Shot(_window, $"security-{suffix}");

            _vm.SelectedTab = AppTab.Query;
            _vm.Query.OpenInNewTab("SELECT c.name, count(*) AS orders FROM customers c JOIN orders o ON o.customer_id = c.id GROUP BY c.name ORDER BY orders DESC;", $"Top customers ({suffix})");
            var doc = _vm.Query.SelectedDocument!;
            await Settle();
            await doc.ExplainCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning, "plan " + suffix);
            await Shot(_window, $"plan-{suffix}");

            _vm.SelectedTab = AppTab.Objects;
            _vm.Objects.SelectedObject = _vm.Objects.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table && o.Name.Equals("orders", StringComparison.OrdinalIgnoreCase));
            await Settle(6);
            await Shot(_window, $"objects-{suffix}");

            _vm.Objects.SelectedObject = _vm.Objects.Objects.FirstOrDefault(o => o.IsRoutine);
            await Settle(4);
            _vm.Objects.DebugSelectedCommand.Execute(null);
            await ShootOwned<RoutineDebuggerWindow>($"routine-debugger-{suffix}", 10);

            _vm.SelectedTab = AppTab.Locks;
            await _vm.Locks.RefreshCommand.ExecuteAsync(null);
            await Settle(4);
            await Shot(_window, $"locks-{suffix}");

            _vm.SelectedTab = AppTab.Activity;
            await _vm.Activity.RefreshRequestsCommand.ExecuteAsync(null);
            await _vm.Activity.LoadTopQueriesCommand.ExecuteAsync(null);
            await Settle(4);
            await Shot(_window, $"activity-{suffix}");
        }

        /// <summary>
        /// SQLite needs no server: a sample shop file is made in the temp root. Renders the file connection dialog, the
        /// catalog, a query and its plan, and the tabs that explain what a file doesn't have (locks, activity, security).
        /// </summary>
        private async Task RenderSqliteAsync(string root)
        {
            var file = Path.Combine(root, "samples", "shop.db");
            await DbExplorer.Providers.Sqlite.SqliteProviderFactory.CreateFileAsync(file);
            var profile = new ConnectionProfile { Name = "Shop (SQLite)", Folder = "Local", ProviderKey = SqlDialect.SqliteKey, Host = file };
            await using (var seed = new DbExplorer.Providers.Sqlite.SqliteProviderFactory().Create(profile))
                await seed.ExecuteScriptAsync(SqliteSeed, null, 60);
            _vm.Profiles.Add(profile);

            var registry = _provider.GetRequiredService<ProviderRegistry>();
            var tunnels = _provider.GetRequiredService<SshTunnelService>();
            await ShowAndShoot(new ConnectionDialog { DataContext = new ConnectionDialogViewModel(registry, profile.Clone(), ["Local"], tunnels), Title = "Edit connection" },
                "connection-dialog-sqlite");
            var newVm = new ConnectionDialogViewModel(registry, new ConnectionProfile { ProviderKey = SqlDialect.SqliteKey }, ["Local"], tunnels, isNew: true);
            await ShowAndShoot(new ConnectionDialog { DataContext = newVm, Title = "New connection" }, "connection-dialog-sqlite-new");

            await _vm.DisconnectCommand.ExecuteAsync(null);
            await WaitFor(() => !_vm.IsConnected && !_vm.IsBusy, "disconnect");
            _vm.SelectedProfile = profile;
            await _vm.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => _vm.IsConnected && !_vm.IsBusy, "connect sqlite", 30_000);
            if (!_vm.IsConnected) throw new InvalidOperationException(_vm.StatusText);
            Console.WriteLine("Connected: " + _vm.StatusText);

            _vm.SelectedTab = AppTab.Objects;
            _vm.Objects.SelectedObject = _vm.Objects.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table && o.Name == "orders");
            await Settle(8);
            await Shot(_window, "objects-sqlite");

            _vm.SelectedTab = AppTab.Query;
            _vm.Query.OpenInNewTab("SELECT c.name, c.city, count(*) AS orders, round(sum(o.total), 2) AS total\nFROM customers c\nJOIN orders o ON o.customer_id = c.id\nWHERE o.status <> 'cancelled'\nGROUP BY c.id\nORDER BY total DESC;", "Top customers (SQLite)");
            var doc = _vm.Query.SelectedDocument!;
            await Settle();
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning, "query sqlite");
            await Settle(4);
            await Shot(_window, "query-sqlite");
            await doc.ExplainCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning, "plan sqlite");
            await Settle(4);
            await Shot(_window, "plan-sqlite");

            _vm.SelectedTab = AppTab.TableDesigner;
            await Settle(6);
            await Shot(_window, "table-designer-sqlite");

            _vm.SelectedTab = AppTab.Locks;
            await Settle(4);
            await Shot(_window, "locks-sqlite");
            _vm.SelectedTab = AppTab.Activity;
            await Settle(4);
            await Shot(_window, "activity-sqlite");
            _vm.SelectedTab = AppTab.Security;
            await Settle(6);
            await Shot(_window, "security-sqlite");

            _window.Width = 1024; _window.Height = 700;
            _vm.SelectedTab = AppTab.Query;
            await Settle(4);
            await Shot(_window, "small-query-sqlite");
            _window.Width = 1400; _window.Height = 900;
            await Settle();
        }

        private const string SqliteSeed = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT UNIQUE, city TEXT, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE products (id INTEGER PRIMARY KEY, sku TEXT NOT NULL UNIQUE, name TEXT NOT NULL, price NUMERIC(10,2) NOT NULL CHECK (price >= 0));
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                customer_id INTEGER NOT NULL REFERENCES customers (id),
                status TEXT NOT NULL DEFAULT 'new' CHECK (status IN ('new', 'paid', 'shipped', 'cancelled')),
                total REAL NOT NULL DEFAULT 0,
                ordered_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                note TEXT
            );
            CREATE TABLE order_items (
                order_id INTEGER NOT NULL REFERENCES orders (id) ON DELETE CASCADE,
                product_id INTEGER NOT NULL REFERENCES products (id),
                qty INTEGER NOT NULL CHECK (qty > 0),
                PRIMARY KEY (order_id, product_id)
            ) WITHOUT ROWID;
            CREATE INDEX ix_orders_customer ON orders (customer_id, ordered_at DESC);
            CREATE INDEX ix_orders_open ON orders (status) WHERE status <> 'shipped';
            CREATE VIEW v_order_totals AS
                SELECT o.id, c.name AS customer, o.status, sum(i.qty * p.price) AS total
                FROM orders o JOIN customers c ON c.id = o.customer_id
                JOIN order_items i ON i.order_id = o.id JOIN products p ON p.id = i.product_id
                GROUP BY o.id;
            CREATE TRIGGER trg_order_items_total AFTER INSERT ON order_items BEGIN
                UPDATE orders SET total = total + NEW.qty * (SELECT price FROM products WHERE id = NEW.product_id) WHERE id = NEW.order_id;
            END;
            INSERT INTO customers (name, email, city) VALUES
                ('Acme Corp', 'orders@acme.example', 'Oslo'), ('Globex', 'buy@globex.example', 'Rome'), ('Initech', NULL, 'Austin'),
                ('Umbrella', 'shop@umbrella.example', 'Berlin'), ('Hooli', 'it@hooli.example', 'Palo Alto'), ('Vehement', NULL, 'Lyon');
            INSERT INTO products (sku, name, price) VALUES
                ('KB-01', 'Keyboard', 49.90), ('MS-02', 'Mouse', 19.50), ('MN-27', 'Monitor 27"', 279.00), ('CB-10', 'USB-C cable', 9.99), ('DK-04', 'Dock', 129.00);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 60)
            INSERT INTO orders (customer_id, status, ordered_at, note)
            SELECT 1 + (i * 7) % 6, CASE i % 5 WHEN 0 THEN 'cancelled' WHEN 1 THEN 'new' WHEN 2 THEN 'paid' ELSE 'shipped' END,
                   datetime('2026-10-01', '+' || (i * 11 % 30) || ' days', '+' || (i * 37 % 24) || ' hours'),
                   CASE WHEN i % 9 = 0 THEN 'Leave at the door' END
            FROM n;
            INSERT INTO order_items (order_id, product_id, qty) SELECT id, 1 + id % 5, 1 + id % 3 FROM orders;
            INSERT INTO order_items (order_id, product_id, qty) SELECT id, 1 + (id + 2) % 5, 1 FROM orders WHERE id % 2 = 0;
            ANALYZE;
            """;

        /// <summary>The Jobs tab with sample jobs (one that failed, one disabled, one with a run history) and the new-job form.</summary>
        private async Task RenderJobsAsync(string suffix)
        {
            var provider = _vm.Session!.Provider;
            var catalog = await DbExplorer.Application.Jobs.JobCatalogLoader.LoadAsync(provider, _vm.Session.Profile.Database);
            if (!catalog.Available)
            {
                _vm.SelectedTab = AppTab.Jobs;
                await Settle(6);
                await Shot(_window, $"jobs-unavailable-{suffix}");
                return;
            }
            await SeedJobsAsync(provider, catalog);
            _vm.SelectedTab = AppTab.Jobs;
            await _vm.Jobs.RefreshCommand.ExecuteAsync(null);
            await WaitFor(() => _vm.Jobs.Jobs.Count > 0 && !_vm.Jobs.IsBusy, "jobs " + suffix, 30_000);
            _vm.Jobs.SelectedJob = _vm.Jobs.Jobs.FirstOrDefault(j => j.IsFailed) ?? _vm.Jobs.Jobs[0];
            await WaitFor(() => !_vm.Jobs.HistoryStatus.StartsWith("Reading", StringComparison.Ordinal), "job history " + suffix);
            await Settle(6);
            await Shot(_window, $"jobs-{suffix}");

            if (_vm.Jobs.Jobs.FirstOrDefault(j => j.Job.Steps.Count > 1) is { } multi)
            {
                _vm.Jobs.SelectedJob = multi;
                await WaitFor(() => !_vm.Jobs.HistoryStatus.StartsWith("Reading", StringComparison.Ordinal), "job history " + suffix);
                await Settle(6);
                await Shot(_window, $"jobs-steps-{suffix}");
            }

            _vm.Jobs.NewJobCommand.Execute(null);
            _vm.Jobs.EditName = "Archive old orders";
            _vm.Jobs.EditKind = _vm.Jobs.ScheduleKinds.First(k => k.Kind == DbExplorer.Application.Jobs.ScheduleKind.Weekly);
            _vm.Jobs.EditTime = "01:30";
            _vm.Jobs.EditSaturday = true;
            _vm.Jobs.EditMonday = false;
            _vm.Jobs.EditDescription = "Moves orders older than two years to orders_archive";
            _vm.Jobs.EditSql = "INSERT INTO orders_archive SELECT * FROM orders WHERE ordered_at < CURRENT_DATE - 730;\nDELETE FROM orders WHERE ordered_at < CURRENT_DATE - 730;";
            await Settle(6);
            await Shot(_window, $"jobs-new-{suffix}");
            _vm.Jobs.CancelEditCommand.Execute(null);
            await Settle();
        }

        /// <summary>Creates the render's sample jobs again (through the Jobs tab's own scripts), with some run history.</summary>
        private static async Task SeedJobsAsync(DbExplorer.Core.Abstractions.IDatabaseProvider provider, DbExplorer.Application.Jobs.JobCatalog catalog)
        {
            string[] names = ["Nightly vacuum", "Purge old sessions", "Refresh sales report", "Weekly summary", "Nightly index maintenance", "Load and copy",
                              "purge_sessions", "daily_rollup", "year_end_close"];
            foreach (var old in catalog.Jobs.Where(j => names.Contains(j.Name)))
                await DbExplorer.Application.Jobs.JobScriptBuilder.RunAsync(provider, DbExplorer.Application.Jobs.JobScriptBuilder.Delete(catalog, old), 30);
            catalog = await DbExplorer.Application.Jobs.JobCatalogLoader.LoadAsync(provider, null);
            var now = catalog.ServerNow ?? DateTime.Now;
            async Task Create(string name, string command, DbExplorer.Application.Jobs.JobScheduleSpec schedule, bool enabled = true, string? database = null, string? description = null)
            {
                var draft = new DbExplorer.Application.Jobs.JobDraft
                {
                    Name = name, Command = command, Schedule = schedule, Enabled = enabled, Description = description,
                    Database = database ?? (catalog.Engine == DbExplorer.Application.Jobs.JobEngine.PgCron ? "uirender_shop" : null)
                };
                await DbExplorer.Application.Jobs.JobScriptBuilder.RunAsync(provider, DbExplorer.Application.Jobs.JobScriptBuilder.Create(catalog, draft, now), 30);
            }
            static DbExplorer.Application.Jobs.JobScheduleSpec S(DbExplorer.Application.Jobs.ScheduleKind kind, int interval = 1, int hour = 3, int minute = 0, string? cron = null,
                DayOfWeek day = DayOfWeek.Monday) =>
                new() { Kind = kind, Interval = interval, At = new TimeOnly(hour, minute), Cron = cron, Days = [day] };

            switch (catalog.Engine)
            {
                case DbExplorer.Application.Jobs.JobEngine.PgCron:
                    await Create("Nightly vacuum", "VACUUM (ANALYZE) orders;", S(DbExplorer.Application.Jobs.ScheduleKind.Daily, hour: 3));
                    await Create("Purge old sessions", "SELECT count(*) FROM customers;", S(DbExplorer.Application.Jobs.ScheduleKind.Cron, cron: "1 seconds"));
                    await Create("Refresh sales report", "REFRESH MATERIALIZED VIEW sales_report;", S(DbExplorer.Application.Jobs.ScheduleKind.Cron, cron: "1 seconds"));
                    await Create("Weekly summary", "SELECT 1;", S(DbExplorer.Application.Jobs.ScheduleKind.Weekly, hour: 6, day: DayOfWeek.Monday), enabled: false);
                    await Task.Delay(3500);
                    catalog = await DbExplorer.Application.Jobs.JobCatalogLoader.LoadAsync(provider, null);
                    foreach (var (name, spec) in new[] { ("Purge old sessions", S(DbExplorer.Application.Jobs.ScheduleKind.EveryMinutes, 15)), ("Refresh sales report", S(DbExplorer.Application.Jobs.ScheduleKind.EveryHours, minute: 5)) })
                    {
                        var job = catalog.Jobs.First(j => j.Name == name);
                        await DbExplorer.Application.Jobs.JobScriptBuilder.RunAsync(provider,
                            DbExplorer.Application.Jobs.JobScriptBuilder.Update(catalog, job, DbExplorer.Application.Jobs.JobScriptBuilder.DraftOf(job) with { Schedule = spec }, now), 30);
                    }
                    break;
                case DbExplorer.Application.Jobs.JobEngine.SqlServerAgent:
                    await Create("Nightly index maintenance", "ALTER INDEX ALL ON dbo.orders REORGANIZE;", S(DbExplorer.Application.Jobs.ScheduleKind.Daily, hour: 2), database: "uirender_shop",
                        description: "Reorganizes the order indexes every night");
                    await Create("Purge old sessions", "SELECT 1/0;", S(DbExplorer.Application.Jobs.ScheduleKind.EveryMinutes, 15), database: "uirender_shop");
                    await Create("Weekly summary", "SELECT 1;", S(DbExplorer.Application.Jobs.ScheduleKind.Weekly, hour: 6), enabled: false, database: "uirender_shop");
                    await using (var run = await provider.BeginScriptSessionAsync(null, false))
                        await run.ExecuteAsync("""
                            DECLARE @id uniqueidentifier;
                            EXEC msdb.dbo.sp_add_job @job_name = N'Load and copy', @job_id = @id OUTPUT;
                            EXEC msdb.dbo.sp_add_jobstep @job_id = @id, @step_name = N'Load staging', @subsystem = N'TSQL', @database_name = N'uirender_shop', @command = N'SELECT COUNT(*) FROM dbo.orders;', @on_success_action = 3;
                            EXEC msdb.dbo.sp_add_jobstep @job_id = @id, @step_name = N'Copy export', @subsystem = N'CmdExec', @command = N'cmd /c copy C:\exports\orders.csv \\fileserver\drop\';
                            EXEC msdb.dbo.sp_add_jobserver @job_id = @id;
                            """, 30);
                    catalog = await DbExplorer.Application.Jobs.JobCatalogLoader.LoadAsync(provider, null);
                    foreach (var job in catalog.Jobs.Where(j => j.Name is "Nightly index maintenance" or "Purge old sessions"))
                        await DbExplorer.Application.Jobs.JobScriptBuilder.RunAsync(provider, DbExplorer.Application.Jobs.JobScriptBuilder.RunNow(job), 30);
                    await Task.Delay(4000);
                    break;
                default:
                    await Create("purge_sessions", "DELETE FROM orders WHERE id < 0", S(DbExplorer.Application.Jobs.ScheduleKind.EveryMinutes, 15), database: "uirender_shop");
                    await Create("daily_rollup", "INSERT INTO orders_daily SELECT CURDATE(), COUNT(*) FROM orders;\nDELETE FROM orders_daily WHERE day < CURDATE() - INTERVAL 1 YEAR;",
                        S(DbExplorer.Application.Jobs.ScheduleKind.Daily, hour: 1), database: "uirender_shop", description: "Totals per day, kept for a year");
                    await Create("year_end_close", "UPDATE orders SET status = status WHERE id < 0",
                        new DbExplorer.Application.Jobs.JobScheduleSpec { Kind = DbExplorer.Application.Jobs.ScheduleKind.Once, OnceAt = new DateTime(now.Year, 12, 31, 23, 0, 0) },
                        enabled: false, database: "uirender_shop");
                    break;
            }
        }

        /// <summary>Disconnected while on Objects: the window moves to the Query tab, which still edits (with names from
        /// the saved catalog) but cannot run; reconnecting goes back to Objects.</summary>
        private async Task RenderOfflineAsync()
        {
            _vm.SelectedTab = AppTab.Objects;
            await Settle();
            // Earlier steps leave tables on the Query builder canvas; Disconnect would ask about them and wait forever here.
            if (_vm.QueryBuilder.ClearCommand.CanExecute(null)) _vm.QueryBuilder.ClearCommand.Execute(null);
            await DisconnectAnsweringPromptsAsync();
            await WaitFor(() => !_vm.IsConnected && !_vm.IsBusy, "disconnect");
            await WaitFor(() => _vm.Query.SelectedDocument?.CompletionInfo.StartsWith("Offline", StringComparison.Ordinal) == true, "offline catalog");
            Console.WriteLine($"Offline: tab {_vm.SelectedTab}, hint '{_vm.Query.SelectedDocument?.CompletionInfo}'");
            _vm.Query.OpenInNewTab("-- written offline\nSELECT c.name, o.total\nFROM customers c\nJOIN orders o ON o.customer_id = c.id;", "Offline draft");
            await Settle(4);
            await Shot(_window, "query-offline");
            _window.Width = 1024; _window.Height = 700;
            await Settle(4);
            await Shot(_window, "small-query-offline");
            _window.Width = 1400; _window.Height = 900;

            // The draft was added while offline, so connecting stays on the Query tab to run it.
            await _vm.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => _vm.IsConnected && !_vm.IsBusy, "reconnect", 60_000);
            Console.WriteLine($"Reconnected: tab {_vm.SelectedTab}");
        }

        /// <summary>A tab with a few kept versions (runs, a save, a closed tab), then its local history window.</summary>
        /// <summary>Recently closed Query tabs in the History flyout, and a tab brought back to its place.</summary>
        private async Task RenderReopenClosedTabsAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            var query = _vm.Query;
            foreach (var d in query.Documents.Skip(1).ToList()) query.Documents.Remove(d);
            var dir = Directory.CreateTempSubdirectory("dbx-reopen-").FullName;
            (string Name, string Sql)[] files =
            [
                ("monthly-revenue.sql", "select date_trunc('month', ordered_at) as month, sum(total)\nfrom orders\ngroup by 1\norder by 1;"),
                ("top-customers.sql", "select c.name, sum(o.total) as total\nfrom orders o join customers c on c.id = o.customer_id\ngroup by c.name order by total desc limit 10;"),
                ("stale-carts.sql", "delete from carts where updated_at < now() - interval '30 days';"),
            ];
            foreach (var (name, sql) in files)
            {
                var path = Path.Combine(dir, name);
                await File.WriteAllTextAsync(path, sql);
                await query.OpenFileAsync(path, name);
            }
            await Settle(4);
            await Shot(_window, "reopen-before");
            var view = _window.GetVisualDescendants().OfType<QueryWorkspaceView>().First();
            await query.CloseTabCommand.ExecuteAsync(query.Documents.First(d => d.Title == "top-customers.sql"));
            await query.CloseTabCommand.ExecuteAsync(query.Documents.First(d => d.Title == "stale-carts.sql"));
            await query.CloseTabCommand.ExecuteAsync(query.Documents.First(d => d.Title == "monthly-revenue.sql"));
            await Settle(4);

            var button = view.FindControl<Button>("HistoryButton")!;
            button.Flyout!.ShowAt(button);
            await Settle(8);
            await Shot(_window, "reopen-flyout");
            button.Flyout.Hide();
            await Settle();

            var tab = view.GetVisualDescendants().OfType<TabItem>().First();
            if (tab.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.ContextMenu is not null) is { } header)
            {
                header.ContextMenu!.Open(header);
                await Settle(6);
                if (TopLevel.GetTopLevel(header.ContextMenu) is { } menuRoot && menuRoot != _window) await Shot(menuRoot, "reopen-menu");
                else await Shot(_window, "reopen-menu");
                header.ContextMenu.Close();
            }

            await query.ReopenClosedTabCommand.ExecuteAsync(null);
            await query.ReopenClosedTabCommand.ExecuteAsync(null);
            await Settle(4);
            await Shot(_window, "reopen-after");
            Console.WriteLine("  tabs: " + string.Join(", ", query.Documents.Select(d => d.Title)) + " · still closed: " +
                              string.Join(", ", query.RecentlyClosed.Select(c => c.Title)));
        }

        /// <summary>.sql files: the Recent list under Open, and the bar shown when another program changed a file the tab has edits in.</summary>
        private async Task RenderSqlFilesAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            var query = _vm.Query;
            foreach (var d in query.Documents.Skip(1).ToList()) query.Documents.Remove(d);
            var dir = Directory.CreateTempSubdirectory("dbx-sqlfiles-").FullName;
            string[] names = ["daily-orders.sql", "refund-audit.sql", "customer-cleanup.sql"];
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                await File.WriteAllTextAsync(path, $"-- {name}\nselect id, status, total\nfrom orders\nwhere ordered_at >= current_date - 7;");
                await _vm.OpenFilesAsync([path]);
            }
            await Settle(4);
            var view = _window.GetVisualDescendants().OfType<QueryWorkspaceView>().First();
            var open = view.FindControl<SplitButton>("OpenButton")!;
            open.Flyout!.ShowAt(open);
            await Settle(8);
            await Shot(_window, "sqlfiles-recent");
            open.Flyout.Hide();
            await Settle();

            var tab = query.Documents.First(d => d.Title == "daily-orders.sql");
            query.SelectedDocument = tab;
            tab.Document.Insert(tab.Document.TextLength, "\n-- my edit");
            var file = tab.FilePath!;
            await File.WriteAllTextAsync(file, "-- changed by a teammate\nselect 1;");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            await query.CheckFilesOnDiskAsync();
            await Settle(4);
            await Shot(_window, "sqlfiles-changed");
            Console.WriteLine("  disk notice: " + tab.DiskNotice);
        }

        private async Task RenderLocalHistoryAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            var query = _vm.Query;
            var closed = query.NewTab();
            closed.Title = "Old report";
            closed.SetText("select status, count(*)\nfrom orders\ngroup by status;", markClean: false);
            await query.RecordAsync(closed, LocalHistoryReason.Closed);

            var tab = query.NewTab();
            tab.Title = "Orders by customer";
            string[] versions =
            [
                "select *\nfrom orders;",
                "select o.id, o.total\nfrom orders o\nwhere o.total > 100;",
                "select c.name, count(*) as orders, sum(o.total) as total\nfrom orders o\njoin customers c on c.id = o.customer_id\nwhere o.total > 100\ngroup by c.name\norder by total desc;",
            ];
            var reasons = new[] { LocalHistoryReason.Run, LocalHistoryReason.Run, LocalHistoryReason.Saved };
            for (var i = 0; i < versions.Length; i++)
            {
                tab.SetText(versions[i], markClean: false);
                await query.RecordAsync(tab, reasons[i]);
                await Task.Delay(20);
            }
            tab.SetText(versions[^1].Replace("> 100", "> 250").Replace("order by total desc;", "order by total desc\nlimit 20;"), markClean: false);
            await Settle();

            query.ShowLocalHistoryCommand.Execute(tab);
            await WaitFor(() => _window.OwnedWindows.OfType<LocalHistoryWindow>().Any(), "open local history", 10_000);
            var window = _window.OwnedWindows.OfType<LocalHistoryWindow>().First();
            var hvm = (LocalHistoryViewModel)window.DataContext!;
            await WaitFor(() => hvm.Entries.Count > 0, "local history entries", 5_000);
            hvm.SelectedEntry = hvm.Entries.FirstOrDefault(e => e.Version.Reason == LocalHistoryReason.Saved) ?? hvm.SelectedEntry;
            await Settle(6);
            await Shot(window, "local-history");

            hvm.SelectedEntry = hvm.Entries.Skip(1).FirstOrDefault() ?? hvm.SelectedEntry;
            hvm.CompareWithPrevious = true;
            await Settle(4);
            await Shot(window, "local-history-previous");

            hvm.CompareWithPrevious = false;
            hvm.ShowAll = true;
            await WaitFor(() => hvm.Entries.Any(e => e.Version.Reason == LocalHistoryReason.Closed), "all scripts", 5_000);
            hvm.SelectedEntry = hvm.Entries.First(e => e.Version.Reason == LocalHistoryReason.Closed);
            await Settle(4);
            await Shot(window, "local-history-all");
            window.Close();
            await Settle();

            var empty = query.NewTab();
            query.ShowLocalHistoryCommand.Execute(empty);
            await ShootOwned<LocalHistoryWindow>("local-history-empty", 8);

            _vm.OpenCommandPaletteCommand.Execute(null);
            await WaitFor(() => _window.OwnedWindows.OfType<CommandPaletteWindow>().Any(), "palette", 10_000);
            var palette = _window.OwnedWindows.OfType<CommandPaletteWindow>().First();
            ((CommandPaletteViewModel)palette.DataContext!).Query = "local history";
            await Settle(4);
            await Shot(palette, "local-history-palette");
            palette.Close();
            await Settle();
        }

        /// <summary>Rename (Shift+F6) on an alias, with a clash warning, the result, and Find usages (Alt+F7) of a table
        /// and a column from the editor and from the Objects tab.</summary>
        private async Task RenderUsagesAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            const string sql =
                "with open_orders as (\n    select o.id, o.customer_id, o.status\n    from orders o\n    where o.status <> 'cancelled'\n)\n" +
                "select c.name, count(*) as order_count\nfrom open_orders b\njoin customers c on c.id = b.customer_id\n" +
                "group by c.name\norder by order_count desc;";
            _vm.Query.OpenInNewTab(sql, "Open orders by customer");
            var doc = _vm.Query.SelectedDocument!;
            await WaitFor(() => doc.Inspector is not null, "catalog for symbols", 20_000);
            await Settle(6);
            var view = _window.GetVisualDescendants().OfType<QueryView>().First(v => v.IsEffectivelyVisible);
            var editor = view.FindControl<AvaloniaEdit.TextEditor>("Editor")!;
            var popup = view.FindControl<Popup>("RenamePopup")!;
            var box = view.FindControl<TextBox>("RenameBox")!;
            void Call(string method) => typeof(QueryView).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(view, null);
            async Task CallAsync(string method) => await (Task)typeof(QueryView).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(view, null)!;
            async Task ShotWithPopup(string name)
            {
                await Settle(6);
                await Shot(_window, name);
                if (popup.Child is { } child && TopLevel.GetTopLevel(child) is { } root && root != _window) await Shot(root, name + "-popup");
            }

            editor.Focus();
            editor.CaretOffset = sql.IndexOf("o.status <>", StringComparison.Ordinal);
            Call("StartRename");
            await Settle(4);
            box.Text = "ord";
            await ShotWithPopup("rename-alias");
            box.Text = "c";
            await ShotWithPopup("rename-warning");
            box.Text = "ord";
            await Settle(2);
            Call("ApplyRename");
            await Settle(6);
            await Shot(_window, "rename-done");

            editor.CaretOffset = editor.Document.Text.IndexOf("as order_count", StringComparison.Ordinal) + 4;
            Call("StartRename");
            box.Text = "orders";
            await ShotWithPopup("rename-column");
            popup.IsOpen = false;
            await Settle(2);

            editor.CaretOffset = editor.Document.Text.IndexOf("from orders", StringComparison.Ordinal) + 6;
            await CallAsync("FindUsagesAsync");
            await ShootOwned<UsagesWindow>("usages-table", 8);

            editor.CaretOffset = editor.Document.Text.IndexOf(".status <>", StringComparison.Ordinal) + 1;
            await CallAsync("FindUsagesAsync");
            await ShootOwned<UsagesWindow>("usages-column", 8);

            _vm.SelectedTab = AppTab.Objects;
            _vm.Objects.SelectedObject = _vm.Objects.Objects.First(o => o.Type == DbObjectType.Table && o.Name == "customers");
            await Settle(2);
            await _vm.Objects.FindUsagesCommand.ExecuteAsync(null);
            await WaitFor(() => _window.OwnedWindows.OfType<UsagesWindow>().Any(), "objects usages", 10_000);
            var usages = _window.OwnedWindows.OfType<UsagesWindow>().First();
            var uvm = (UsagesViewModel)usages.DataContext!;
            Console.WriteLine("  " + uvm.Subtitle);
            uvm.Open(uvm.Rows.FirstOrDefault(r => r.CanOpen));
            await Settle(8);
            await Shot(usages, "usages-objects");
            usages.Close();
            await Settle(4);
            await Shot(_window, "usages-opened");
        }

        /// <summary>Back up and restore: the Back up tab, a real pg_dump of the sample, and the Restore tab reading that
        /// file (nothing is restored).</summary>
        private async Task RenderBackupAsync()
        {
            var database = _vm.Session!.Profile.Database;
            _vm.OpenBackupRestore(database, restore: false);
            await WaitFor(() => _window.OwnedWindows.OfType<BackupRestoreWindow>().Any(), "backup window", 10_000);
            var window = _window.OwnedWindows.OfType<BackupRestoreWindow>().First();
            var bvm = (BackupRestoreViewModel)window.DataContext!;
            await WaitFor(() => bvm.Support is not null, "backup support", 20_000);
            var dir = Path.Combine(Path.GetTempPath(), "dbx-render-backup");
            Directory.CreateDirectory(dir);
            bvm.BackupPath = Path.Combine(dir, "uirender_shop_20261103_0715.dump");
            Console.WriteLine("  " + bvm.ToolText + bvm.ProblemText);
            await Settle(8);
            await Shot(window, "backup");
            if (bvm.IsAvailable)
            {
                if (File.Exists(bvm.BackupPath)) File.Delete(bvm.BackupPath);
                await bvm.StartBackupCommand.ExecuteAsync(null);
                Console.WriteLine("  " + bvm.Status);
                await Settle(8);
                await Shot(window, "backup-done");

                bvm.SelectedTabIndex = 1;
                bvm.RestorePath = bvm.BackupPath;
                await bvm.ReadFileCommand.ExecuteAsync(null);
                bvm.RestoreToNew = true;
                bvm.NewDatabaseName = database + "_copy";
                await Settle(8);
                await Shot(window, "restore");
                bvm.RestoreToNew = false;
                await Settle(4);
                await Shot(window, "restore-existing");
                window.Width = 680;
                window.Height = 560;
                await Settle(6);
                await Shot(window, "small-restore");
            }
            window.Close();
            await Settle(2);
        }

        /// <summary>The Generate test data window on a parent table and on a child table with foreign keys.</summary>
        private async Task RenderTestDataAsync()
        {
            _vm.SelectedTab = AppTab.Objects;
            foreach (var name in new[] { "customers", "orders" })
            {
                _vm.Objects.SelectedObject = _vm.Objects.Objects.First(o => o.Type == DbObjectType.Table && o.Name == name);
                await Settle(2);
                _vm.Objects.GenerateTestDataCommand.Execute(null);
                await WaitFor(() => _window.OwnedWindows.OfType<TestDataWindow>().Any(), "test data window", 10_000);
                var window = _window.OwnedWindows.OfType<TestDataWindow>().First();
                var tvm = (TestDataViewModel)window.DataContext!;
                await WaitFor(() => !tvm.IsBusy, "test data plan", 20_000);
                tvm.Seed = 12345;
                Console.WriteLine("  " + tvm.Status);
                await Settle(8);
                await Shot(window, "testdata-" + name);
                window.Close();
                await Settle(2);
            }
        }

        /// <summary>The command palette (recent commands, "&gt;" commands only, a key search), a command run through it
        /// against the real editor, and the F1 shortcut sheet.</summary>
        /// <summary>The Preferences window (Ctrl+,) at the top and scrolled to its last sections, and the theme menu
        /// that now leads to it.</summary>
        private async Task RenderPreferencesAsync()
        {
            var themeButton = _window.GetVisualDescendants().OfType<Button>()
                .First(b => ToolTip.GetTip(b) as string == "Theme and preferences");
            themeButton.Flyout!.ShowAt(themeButton);
            await Settle(8);
            await Shot(_window, "preferences-theme-menu");
            themeButton.Flyout.Hide();
            await Settle();

            _vm.OpenPreferencesCommand.Execute(null);
            await WaitFor(() => _window.OwnedWindows.OfType<PreferencesWindow>().Any(), "open preferences", 10_000);
            var window = _window.OwnedWindows.OfType<PreferencesWindow>().First();
            await Settle(8);
            await Shot(window, "preferences");

            var prefs = (PreferencesViewModel)window.DataContext!;
            prefs.SoftwareRendering = true; // shows the restart note
            prefs.NotifyLongRuns = false;
            var list = window.FindControl<ListBox>("SectionList")!;
            list.SelectedIndex = 2;
            await Settle(8);
            await Shot(window, "preferences-middle");
            list.SelectedIndex = list.ItemCount - 2;
            await Settle(8);
            await Shot(window, "preferences-scrolled");
            prefs.SoftwareRendering = false;
            prefs.NotifyLongRuns = true;

            window.Width = 600;
            window.Height = 480;
            list.SelectedIndex = 0;
            await Settle(8);
            await Shot(window, "preferences-small");
            list.SelectedIndex = 3;
            await Settle(8);
            await Shot(window, "preferences-small-notifications");
            window.Close();
            await Settle();
        }

        /// <summary>A sample release list instead of GitHub's, so the update bar has something to offer.</summary>
        private sealed class SampleReleases : IReleaseSource
        {
            public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ReleaseInfo>>(
            [
                new ReleaseInfo(AppVersion.Parse("1.1.0"), "v1.1.0", "DB Explorer 1.1.0", "https://github.com/hovchik/DbExplorer/releases/tag/v1.1.0",
                    "https://github.com/hovchik/DbExplorer/releases/download/v1.1.0/DbExplorer-Setup-1.1.0.exe", 48_600_000,
                    new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), "", false)
            ]);
        }

        /// <summary>The update bar under the toolbar (both sizes), the palette entries and Preferences' Updates section
        /// after Check now.</summary>
        private async Task RenderUpdatesAsync()
        {
            _window.Width = 1400;
            _window.Height = 900;
            _vm.SelectedTab = AppTab.Query;
            _vm.OpenLink = _ => { };
            var result = await _vm.CheckForUpdatesNowAsync();
            Console.WriteLine("Update check: " + result?.Describe());
            await Settle(6);
            await Shot(_window, "update-bar");
            _window.Width = 1024;
            _window.Height = 700;
            await Settle(6);
            await Shot(_window, "small-update-bar");
            _window.Width = 1400;
            _window.Height = 900;

            _vm.OpenCommandPaletteCommand.Execute(null);
            await WaitFor(() => _window.OwnedWindows.OfType<CommandPaletteWindow>().Any(), "palette", 10_000);
            var palette = _window.OwnedWindows.OfType<CommandPaletteWindow>().First();
            ((CommandPaletteViewModel)palette.DataContext!).Query = "update";
            await Settle(6);
            await Shot(palette, "update-palette");
            palette.Close();
            await Settle();

            _vm.OpenPreferencesCommand.Execute(null);
            await WaitFor(() => _window.OwnedWindows.OfType<PreferencesWindow>().Any(), "open preferences", 10_000);
            var window = _window.OwnedWindows.OfType<PreferencesWindow>().First();
            var prefs = (PreferencesViewModel)window.DataContext!;
            await prefs.CheckUpdatesNowCommand.ExecuteAsync(null);
            var list = window.FindControl<ListBox>("SectionList")!;
            list.SelectedIndex = list.Items.Cast<ListBoxItem>().ToList().FindIndex(i => (string?)i.Tag == "UpdatesSection");
            await Settle(8);
            await Shot(window, "preferences-updates");
            window.Close();
            _vm.DismissUpdateCommand.Execute(null);
            await Settle();
        }

        private async Task RenderPaletteAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            _vm.Query.OpenInNewTab("select id,name from customers where id=1", "Palette check");
            var doc = _vm.Query.SelectedDocument!;
            await Settle(6);

            async Task<CommandPaletteWindow> OpenPalette(System.Windows.Input.ICommand command)
            {
                command.Execute(null);
                await WaitFor(() => _window.OwnedWindows.OfType<CommandPaletteWindow>().Any(), "palette", 10_000);
                return _window.OwnedWindows.OfType<CommandPaletteWindow>().First();
            }

            // Run "Format SQL" through the palette: it must reach the editor view and change the text.
            var palette = await OpenPalette(_vm.OpenCommandPaletteCommand);
            var pvm = (CommandPaletteViewModel)palette.DataContext!;
            pvm.Query = "format sql";
            await Settle(2);
            if (pvm.Selected?.Title != "Format SQL") throw new InvalidOperationException("Format SQL not first: " + pvm.Selected?.Title);
            pvm.Accept(PaletteModifier.None);
            await Settle(6);
            var formatted = doc.Document.Text;
            Console.WriteLine("Formatted via palette: " + formatted.Replace("\n", " ⏎ "));
            if (formatted == "select id,name from customers where id=1") throw new InvalidOperationException("palette Format did not reach the editor");

            palette = await OpenPalette(_vm.OpenCommandPaletteCommand);
            pvm = (CommandPaletteViewModel)palette.DataContext!;
            pvm.Query = "theme";
            await Settle(2);
            pvm.Selected = pvm.Results.First(r => r.Title == "Theme: Light");
            pvm.Accept(PaletteModifier.None);
            await Settle(4);

            palette = await OpenPalette(_vm.OpenCommandPaletteCommand);
            await Settle(4);
            await Shot(palette, "palette-recent");
            palette.Close();
            await Settle();

            palette = await OpenPalette(_vm.OpenCommandListCommand);
            await Settle(4);
            await Shot(palette, "palette-commands");
            palette.Close();
            await Settle();

            palette = await OpenPalette(_vm.OpenCommandPaletteCommand);
            ((CommandPaletteViewModel)palette.DataContext!).Query = "ctrl+l";
            await Settle(4);
            await Shot(palette, "palette-keys");
            palette.Close();
            await Settle();

            _vm.ShowShortcutsCommand.Execute(null);
            await ShootOwned<ShortcutSheetWindow>("shortcuts", 8);
            _vm.ShowShortcutsCommand.Execute(null);
            await WaitFor(() => _window.OwnedWindows.OfType<ShortcutSheetWindow>().Any(), "sheet", 10_000);
            var sheet = _window.OwnedWindows.OfType<ShortcutSheetWindow>().First();
            ((ShortcutSheetViewModel)sheet.DataContext!).Filter = "run";
            await Settle(6);
            await Shot(sheet, "shortcuts-filtered");
            sheet.Close();
            await Settle();
        }

        /// <summary>The F1 sheet while a key is changed (with a conflict), after the change with Vim mode on, and the
        /// query editor in Vim mode driven by real key presses: normal, visual, a : command and its result.</summary>
        private async Task RenderKeysAndVimAsync()
        {
            var keymap = _provider.GetRequiredService<DbExplorer.Application.Shortcuts.KeymapService>();
            _vm.ShowShortcutsCommand.Execute(null);
            await WaitFor(() => _window.OwnedWindows.OfType<ShortcutSheetWindow>().Any(), "sheet", 10_000);
            var sheet = _window.OwnedWindows.OfType<ShortcutSheetWindow>().First();
            var sheetVm = (ShortcutSheetViewModel)sheet.DataContext!;
            await Settle(4);
            var format = sheetVm.Groups.SelectMany(g => g.Items).Single(i => i.Id == DbExplorer.Application.Shortcuts.ShortcutCommands.Format);
            sheetVm.StartEditCommand.Execute(format);
            await Settle(2);
            sheet.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
            await Settle(6);
            await Shot(sheet, "shortcuts-editing");
            sheet.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Settle(2);
            sheetVm.VimMode = true;
            sheetVm.Filter = "format";
            await Settle(6);
            await Shot(sheet, "shortcuts-changed");
            sheetVm.Filter = "";
            await Settle(2);
            var scroller = sheet.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            await Settle(6);
            await Shot(sheet, "shortcuts-vim");
            sheet.Close();
            await Settle();

            _vm.SelectedTab = AppTab.Query;
            const string sql = "select c.name, count(*) as orders\nfrom customers c\njoin orders o on o.customer_id = c.id\nwhere o.status = 'open'\ngroup by c.name;";
            _vm.Query.OpenInNewTab(sql, "Vim mode");
            await Settle(6);
            var view = _window.GetVisualDescendants().OfType<QueryView>().First(v => v.IsEffectivelyVisible);
            var editor = view.FindControl<AvaloniaEdit.TextEditor>("Editor")!;
            editor.TextArea.Focus();
            editor.CaretOffset = 0;
            await Settle(2);
            void Type(string text)
            {
                foreach (var ch in text) _window.KeyTextInput(ch.ToString());
            }
            Type("jw");
            await Settle(4);
            await Shot(_window, "vim-normal");
            Type("vee");
            await Settle(4);
            await Shot(_window, "vim-visual");
            _window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Type(":%s/c\\.name/c.full_name/g");
            await Settle(4);
            await Shot(_window, "vim-command");
            _window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Type("Gcwhaving");
            await Settle(4);
            await Shot(_window, "vim-insert");
            _window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await Settle(2);
            Console.WriteLine("  vim text: " + editor.Document.Text.Replace("\n", " | "));

            keymap.ResetAll();
            keymap.SetVimMode(false);
            await Settle(2);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The prompt before closing with an open transaction and a Query builder canvas: built from the
        /// real tab state, so it also checks what the main window collects.</summary>
        /// <summary>
        /// A failed connect (wrong password, then nothing listening on the port): the failure card with Edit / Retry, the
        /// red status line, and the status history opened from it, at both sizes.
        /// </summary>
        private async Task RenderConnectionErrorsAsync(string[] pg)
        {
            var wrong = Profile("Shop (wrong password)", SqlDialect.PostgresKey, pg, ConnectionEnvironment.Development, "Local");
            wrong.Password = "not-the-password";
            var closed = Profile("Billing", SqlDialect.PostgresKey, pg, ConnectionEnvironment.Staging, "Clients/Acme");
            closed.Host = "127.0.0.1";
            closed.Port = 1;
            _vm.Profiles.Add(wrong);
            _vm.Profiles.Add(closed);

            _vm.SelectedProfile = closed;
            await _vm.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => !_vm.IsBusy, "refused connect");
            _vm.SelectedProfile = wrong;
            await _vm.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => !_vm.IsBusy && _vm.ShowConnectionFailure, "failed connect");
            Console.WriteLine("  " + _vm.StatusText);
            await Shot(_window, "error-login");

            var status = _window.FindControl<Button>("StatusButton")!;
            status.Flyout!.ShowAt(status);
            await Settle(6);
            await Shot(_window, "error-history");
            status.Flyout.Hide();

            _window.Width = 1024;
            _window.Height = 700;
            _vm.SelectedProfile = closed;
            await _vm.ConnectCommand.ExecuteAsync(null);
            await WaitFor(() => !_vm.IsBusy, "refused connect");
            await Settle(6);
            await Shot(_window, "small-error-refused");
            _window.Width = 1400;
            _window.Height = 900;

            _vm.DismissConnectionFailureCommand.Execute(null);
            _vm.Profiles.Remove(wrong);
            _vm.Profiles.Remove(closed);
            await Settle();
        }

        /// <summary>The Comparer's sections: setup steps, the Overview of the shop against its older copy, and one object's diffs.</summary>
        private async Task RenderComparerAsync()
        {
            var c = _vm.Comparer;
            _vm.SelectedTab = AppTab.Comparer;
            if (c.LeftSession is not null) await c.DisconnectLeftCommand.ExecuteAsync(null);
            if (c.RightSession is not null) await c.DisconnectRightCommand.ExecuteAsync(null);
            c.SelectedTabIndex = ComparerViewModel.OverviewTab;
            await Settle(4);
            await Shot(_window, "comparer-setup");
            await c.UseCurrentAsLeftCommand.ExecuteAsync(null);
            await Settle(4);
            await Shot(_window, "comparer-setup-left");

            await c.UseCurrentAsRightCommand.ExecuteAsync(null);
            await WaitFor(() => c.RightDatabases.Contains("uirender_shop_old"), "right databases");
            c.SelectedRightDatabase = "uirender_shop_old";
            await WaitFor(() => !c.IsBusy && c.RightObjects.Any(o => o.Name == "legacy_import") && c.OverviewEntries.Count > 0,
                "overview against the old shop", 30_000);
            await Settle(4);
            await Shot(_window, "comparer-overview-diff");

            c.ShowOverviewIdentical = true;
            c.SelectedOverviewType = DbExplorer.Application.Compare.OverviewTypeGroup.Tables;
            c.SelectedOverviewEntry = c.OverviewEntries.First(e => e.Name == "customers");
            c.SelectedOverviewEntries = [c.SelectedOverviewEntry];
            await Settle(4);
            await Shot(_window, "comparer-overview-tables");

            await c.OpenOverviewEntryCommand.ExecuteAsync(null);
            await WaitFor(() => !c.IsBusy, "structure of customers");
            await Settle(4);
            await Shot(_window, "comparer-structure");

            c.SelectedTabIndex = ComparerViewModel.SchemaTab;
            c.SelectedLeftObject = c.LeftObjects.First(o => o.Name == "v_order_totals");
            await Settle(2);
            await c.CompareSchemaCommand.ExecuteAsync(null);
            await WaitFor(() => !c.IsBusy, "definition of v_order_totals");
            c.ShowOnlySchemaChanges = false;
            await Settle(4);
            await Shot(_window, "comparer-definition");

            c.SelectedTabIndex = ComparerViewModel.OverviewTab;
            c.ShowOverviewIdentical = false;
            c.SelectedOverviewType = DbExplorer.Application.Compare.OverviewTypeGroup.AllTypes;
            _window.Width = 1024;
            _window.Height = 700;
            await Settle(6);
            await Shot(_window, "small-comparer-overview");
            c.SelectedTabIndex = ComparerViewModel.SchemaTab;
            await Settle(4);
            await Shot(_window, "small-comparer-definition");
            _window.Width = 1400;
            _window.Height = 900;
            c.SelectedTabIndex = ComparerViewModel.OverviewTab;
            c.SelectedRightDatabase = "uirender_shop";
            await Settle();
        }

        /// <summary>The Comparer's Migration tab: the shop against its older copy (uirender_shop_old from the seed script).</summary>
        /// <summary>Search names &amp; code: the empty help, a search with a column hit in the preview, filters (only
        /// code, only functions), a collapsed object, a regular expression, nothing found, and the 1024 px window.</summary>
        private async Task RenderSearchNamesAsync()
        {
            var s = _vm.Search;
            _vm.SelectedTab = AppTab.SearchNamesAndCode;
            s.Query = "";
            await Settle(4);
            await Shot(_window, "search-names-empty");

            async Task Search(string text)
            {
                s.Query = text;
                await s.SearchCommand.ExecuteAsync(null);
                await WaitFor(() => !s.IsSearching, "search " + text);
                await Settle(4);
            }

            async Task Select(Func<MetadataSearchRow, bool> which, string what)
            {
                s.SelectedRow = s.Rows.First(which);
                await WaitFor(() => s.PreviewText.Length > 0 || s.PreviewNote.Length > 0, "preview of " + what);
                await Settle(4);
            }

            await Search("order");
            await Select(r => r.IsCode && r.Group.ObjectName == "v_order_totals", "view line");
            await Shot(_window, "search-names");
            await Select(r => r.Kind == MetadataSearchRowKind.Column && r.Group.ObjectName == "order_items", "column");
            await Shot(_window, "search-names-column");

            await Search("customer_id");
            await Select(r => r.IsObject, "first object");
            await Shot(_window, "search-names-columns");

            await Search("order");
            s.ShowNames = false;
            s.ShowColumns = false;
            s.SelectedTypeOption = s.TypeOptions.First(o => o.Type == MetadataTypeFilter.Routines);
            await Settle(4);
            await Shot(_window, "search-names-code-only");
            s.ShowNames = s.ShowColumns = true;
            s.SelectedTypeOption = s.TypeOptions[0];
            s.CollapseAllCommand.Execute(null);
            await Settle(4);
            await Shot(_window, "search-names-collapsed");
            s.ExpandAllCommand.Execute(null);

            s.UseRegex = true;
            await Search(@"^v_|_seq$");
            await Shot(_window, "search-names-regex");
            s.UseRegex = false;

            await Search("no_such_thing_anywhere");
            await Shot(_window, "search-names-nothing");

            await Search("total");
            await Select(r => r.IsCode && r.Group.ObjectName == "order_total", "function line");
            _window.Width = 1024;
            _window.Height = 700;
            await Settle(6);
            await Shot(_window, "small-search-names");
            _window.Width = 1400;
            _window.Height = 900;
            await Settle();
        }

        private async Task RenderMigrationAsync()
        {
            var c = _vm.Comparer;
            _vm.SelectedTab = AppTab.Comparer;
            if (c.LeftSession is null) await c.UseCurrentAsLeftCommand.ExecuteAsync(null);
            if (c.RightSession is null) await c.UseCurrentAsRightCommand.ExecuteAsync(null);
            await WaitFor(() => c.RightDatabases.Contains("uirender_shop_old"), "right databases");
            c.SelectedTabIndex = ComparerViewModel.MigrationTab;
            await Settle(4);
            await Shot(_window, "migration-empty");
            c.SelectedRightDatabase = "uirender_shop_old";
            await WaitFor(() => !c.IsBusy && c.RightObjects.Any(o => o.Name == "legacy_import"), "old shop catalog", 30_000);
            await c.GenerateMigrationCommand.ExecuteAsync(null);
            await WaitFor(() => !c.IsBusy && c.HasMigration, "migration script", 30_000);
            await Settle(4);
            await Shot(_window, "migration");
            c.MigrationDropExtras = true;
            await c.GenerateMigrationCommand.ExecuteAsync(null);
            await WaitFor(() => !c.IsBusy, "migration script with drops", 30_000);
            await Shot(_window, "migration-drops");
            _window.Width = 1024;
            _window.Height = 700;
            await Settle(6);
            await Shot(_window, "small-migration");
            _window.Width = 1400;
            _window.Height = 900;
            c.MigrationDropExtras = false;
            await Settle();
        }

        private async Task RenderLeavePromptAsync()
        {
            _vm.SelectedTab = AppTab.Query;
            _vm.Query.OpenInNewTab("UPDATE orders SET note = note WHERE id = 1;", "Orders fix");
            var doc = _vm.Query.SelectedDocument!;
            doc.AutoCommit = false;
            await Settle();
            await doc.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !doc.IsRunning && doc.HasOpenTransaction, "open transaction");
            _vm.QueryBuilder.AddTable(Table("orders"), 40, 40);
            _vm.QueryBuilder.AddTable(Table("customers"), 300, 40);

            foreach (var reason in Enum.GetValues<LeaveReason>())
                Console.WriteLine($"{reason}: {string.Join(" | ", _vm.DescribeUnsavedWork(reason))}");
            var lines = _vm.DescribeUnsavedWork(LeaveReason.Quit);
            if (lines.Count != 2) throw new InvalidOperationException($"expected 2 lines, got {lines.Count}");
            await ShowAndShoot(new ConfirmWindow(UnsavedWork.Message(LeaveReason.Quit, _vm.Session!.Profile.DisplayName, lines),
                UnsavedWork.ConfirmText(LeaveReason.Quit)), "leave-quit");
            await ShowAndShoot(new ConfirmWindow(UnsavedWork.Message(LeaveReason.Disconnect, _vm.Session!.Profile.DisplayName,
                    [.. lines, "Security: 2 unapplied changes are discarded", "Data window for public.orders: uncommitted edits are discarded"]),
                UnsavedWork.ConfirmText(LeaveReason.Disconnect)), "leave-disconnect");

            await doc.EndTransactionAsync(commit: false, reason: "render done");
        }

        /// <summary>Import data: a CSV into an existing table (one bad row), and a semicolon file with decimal commas
        /// into a new table with guessed types.</summary>
        private async Task RenderImportAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "dbx-render-import");
            Directory.CreateDirectory(dir);
            var products = Path.Combine(dir, "products.csv");
            File.WriteAllText(products, "SKU,Name,Price,Stock\nSKU-9001,Desk lamp,24.90,12\nSKU-9002,\"Chair, oak\",129.00,4\nSKU-9003,Monitor arm,59.5,lots\nSKU-9004,Cable tray,9.99,40\n");
            var sales = Path.Combine(dir, "Sales 2024 (final).csv");
            File.WriteAllText(sales, "Region;Order date;Amount;Paid;Customer ref\nNorth;2024-01-03;1.234,50;yes;C-0042\nSouth;2024-01-04;99,90;no;C-0007\nEast;2024-01-09;12,00;yes;C-0815\n");

            _vm.SelectedTab = AppTab.Objects;
            _vm.Objects.SelectedObject = Table("products");
            await Settle(4);
            _vm.Objects.ImportDataCommand.Execute(null);
            await WaitFor(() => _window.OwnedWindows.OfType<ImportDataWindow>().Any(), "import window", 10_000);
            var window = _window.OwnedWindows.OfType<ImportDataWindow>().First();
            var vm = (ImportDataViewModel)window.DataContext!;
            await vm.OpenAsync(products);
            await WaitFor(() => vm.CheckSummary.Contains("rows have", StringComparison.Ordinal) || vm.CheckSummary.StartsWith("All", StringComparison.Ordinal), "import check", 10_000);
            await Settle(10);
            await Shot(window, "import-existing");
            window.Close();
            await Settle();

            _vm.Objects.ImportNewTableCommand.Execute(null);
            await WaitFor(() => _window.OwnedWindows.OfType<ImportDataWindow>().Any(), "import window", 10_000);
            window = _window.OwnedWindows.OfType<ImportDataWindow>().First();
            vm = (ImportDataViewModel)window.DataContext!;
            await vm.OpenAsync(sales);
            await WaitFor(() => vm.CheckSummary.StartsWith("All", StringComparison.Ordinal), "import check", 10_000);
            await Settle(10);
            await Shot(window, "import-new");
            window.Width = 980;
            window.Height = 660;
            await Settle(6);
            await Shot(window, "small-import-new");
            window.Close();
            await Settle();
        }

        /// <summary>Two connections open at once: the shop and its older copy, a Query tab on each, the toolbar and the
        /// navigator showing both, switching the active one, and a tab left waiting when its connection closes.</summary>
        private async Task RenderMultipleConnectionsAsync()
        {
            var shop = _vm.Session!;
            var archive = Profile("Shop archive", SqlDialect.PostgresKey, [.. pg[..4], "uirender_shop_old"], ConnectionEnvironment.Staging, "Local");
            _vm.Profiles.Add(archive);
            _vm.ShowNavigator = true;
            _vm.SelectedTab = AppTab.Query;
            var query = _vm.Query;
            var before = query.Documents.ToList();
            query.OpenInNewTab("SELECT id, customer_id, status, ordered_at\nFROM orders\nORDER BY id DESC\nLIMIT 20;", "Orders today");
            var onShop = query.SelectedDocument!;

            await _vm.ConnectProfileAsync(archive);
            await WaitFor(() => _vm.OpenConnections.Count == 2 && !_vm.IsBusy, "second connection", 60_000);
            Console.WriteLine($"  open: {string.Join(", ", _vm.OpenConnections.All.Select(o => o.Profile.Name))}; active {_vm.Session?.Profile.Name}");
            if (!ReferenceEquals(onShop.AttachedSession, shop)) _failures.Add("multi: the Shop tab moved to the archive");

            query.OpenInNewTab("SELECT table_name, table_type\nFROM information_schema.tables\nWHERE table_schema = 'public'\nORDER BY table_name;", "Archive tables");
            var onArchive = query.SelectedDocument!;
            await onArchive.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !onArchive.IsRunning && onArchive.ResultSets.Count > 0, "archive results");
            await onShop.ExecuteCommand.ExecuteAsync(null);
            await WaitFor(() => !onShop.IsRunning && onShop.ResultSets.Count > 0, "shop results");
            Console.WriteLine($"  archive tab: {onArchive.AttachedSession?.Profile.Name} {onArchive.Status}; shop tab: {onShop.AttachedSession?.Profile.Name} {onShop.Status}");
            await Settle(6);
            await Shot(_window, "multi-query");

            var picker = _window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "ConnectionPicker" && c.IsEffectivelyVisible);
            picker.IsDropDownOpen = true;
            await Settle(6);
            await Shot(_window, "multi-tab-picker");
            picker.IsDropDownOpen = false;
            await Settle();

            var main = _window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Classes.Contains("bare"));
            main.IsDropDownOpen = true;
            await Settle(6);
            await Shot(_window, "multi-toolbar-picker");
            main.IsDropDownOpen = false;
            await Settle();

            // Picking the shop in the toolbar makes it active again: the Objects tab shows it, the archive tab stays.
            _vm.SelectedProfile = _vm.Profiles.First(p => p.Id == shop.Profile.Id);
            await WaitFor(() => ReferenceEquals(_vm.Session, shop), "activate shop");
            _vm.SelectedTab = AppTab.Objects;
            await Settle(8);
            await Shot(_window, "multi-objects");
            if (!ReferenceEquals(onArchive.AttachedSession?.Profile.Id, archive.Id) && onArchive.AttachedSession?.Profile.Id != archive.Id)
                _failures.Add("multi: the archive tab left its connection");

            // Closing the archive: its tab waits with Connect, or runs on the shop instead.
            await _vm.CloseConnectionAsync(_vm.OpenConnections.Find(archive.Id)!);
            _vm.SelectedTab = AppTab.Query;
            query.SelectedDocument = onArchive;
            await Settle(8);
            await Shot(_window, "multi-closed-tab");

            foreach (var doc in query.Documents.Except(before).ToList())
            {
                doc.SetText("", markClean: true);
                await query.CloseTabCommand.ExecuteAsync(doc);
            }
            _vm.Profiles.Remove(archive);
            await Settle();
        }

        private async Task ShowAndShoot(Window dialog, string name, Action<Window>? afterShow = null)
        {
            dialog.Show(_window);
            afterShow?.Invoke(dialog);
            await Settle(6);
            await Shot(dialog, name);
            dialog.Close();
            await Settle();
        }

        private async Task ShootOwned<T>(string name, int settleFrames = 4) where T : Window
        {
            await WaitFor(() => _window.OwnedWindows.OfType<T>().Any(), "open " + typeof(T).Name, 10_000);
            var dialog = _window.OwnedWindows.OfType<T>().First();
            await Settle(settleFrames);
            await Shot(dialog, name);
            dialog.Close();
            await Settle();
        }

        /// <summary>Captures <paramref name="window"/> in the light and the dark theme.</summary>
        private static readonly HashSet<string> ContrastShots =
            ["main-disconnected", "objects", "query", "pins-before", "results-as-text", "diagram", "plan", "small-objects"];

        private async Task Shot(TopLevel window, string name)
        {
            Accessibility.Audit(window, name);
            var variants = new List<(ThemeVariant, string)> { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") };
            // A few views also in high contrast, to check it without tripling the run.
            if (ContrastShots.Contains(name)) variants.Add((AppThemes.HighContrast, "contrast"));
            foreach (var (variant, suffix) in variants)
            {
                Avalonia.Application.Current!.RequestedThemeVariant = variant;
                await Settle(4);
                using var frame = window.CaptureRenderedFrame();
                if (frame is null)
                {
                    Console.WriteLine($"  {name}-{suffix}: no frame");
                    continue;
                }
                var path = Path.Combine(outDir, $"{name}-{suffix}.png");
                frame.Save(path);
                Console.WriteLine($"  {name}-{suffix}.png ({window.ClientSize.Width}x{window.ClientSize.Height})");
            }
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            await Settle();
        }

        private static async Task Settle(int frames = 2)
        {
            for (var i = 0; i < frames; i++)
            {
                await Task.Delay(40);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        /// <summary>
        /// Disconnect, answering the "you will lose…" prompt if one opens. Earlier steps leave work behind (a table
        /// design, grid edits, an open transaction, depending on which steps ran), and a modal dialog nobody answers made
        /// a full run stall here; the run now logs what was pending and goes ahead, as a user choosing Disconnect would.
        /// </summary>
        private async Task DisconnectAnsweringPromptsAsync()
        {
            foreach (var line in _vm.DescribeUnsavedWork(LeaveReason.Disconnect)) Console.WriteLine($"  pending before disconnect: {line}");
            var disconnect = _vm.DisconnectCommand.ExecuteAsync(null);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!disconnect.IsCompleted && DateTime.UtcNow < deadline)
            {
                if (_window.OwnedWindows.OfType<ConfirmWindow>().FirstOrDefault() is { } prompt)
                {
                    Console.WriteLine("  answered the disconnect prompt");
                    prompt.Close(true);
                }
                await Settle(1);
            }
            if (!disconnect.IsCompleted)
            {
                Console.WriteLine("  (disconnect did not finish; open windows: " +
                                  string.Join(", ", _window.OwnedWindows.Select(w => w.GetType().Name)) + ")");
                foreach (var w in _window.OwnedWindows.ToList()) w.Close();
            }
            await disconnect.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
        }

        private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 20_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Console.WriteLine($"  (timed out waiting for {what})");
                    return;
                }
                await Settle(1);
            }
            await Settle(2);
        }
    }
}
