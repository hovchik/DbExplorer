using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Application;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Query;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop;
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
            await Step("small", RenderSmallAsync);
            await Step("toolbar", RenderToolbarStatesAsync);
            await Step("querytabs", RenderManyQueryTabsAsync);
            await Step("pins", RenderPinnedResultsAsync);
            await Step("dialogs", RenderDialogsAsync);
            await Step("leave", RenderLeavePromptAsync);
            await Step("localhistory", RenderLocalHistoryAsync);

            if (mssql is not null) await Step("sqlserver", () => RenderOtherEngineAsync("Shop (SQL Server)", "sqlserver"));
            if (mysql is not null) await Step("mysql", () => RenderOtherEngineAsync("Shop (MariaDB)", "mysql"));

            await _vm.ShutdownAsync();
            await _provider.DisposeAsync();
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
            await WaitFor(() => _vm.Search.Results.Count > 0, "search results");
            _vm.Search.SelectedResult = _vm.Search.Results.First(r => r.ObjectName == "order_total");
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

            _vm.SelectedTab = AppTab.SearchNamesAndCode;
            if (_vm.Search.Results.Count > 0)
            {
                _vm.Search.SelectedResult = _vm.Search.Results.First(r => r.ObjectName == "v_order_totals");
                _ = _vm.Search.OpenResultCommand.ExecuteAsync(null);
                await ShootOwned<SearchResultDetailWindow>("search-result-detail", 10);
            }

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

        /// <summary>A tab with a few kept versions (runs, a save, a closed tab), then its local history window.</summary>
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
        private async Task Shot(TopLevel window, string name)
        {
            foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
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
