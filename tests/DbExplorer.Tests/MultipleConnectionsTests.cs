using Avalonia.Headless.XUnit;
using DbExplorer.Application;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Application.Workspace;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop;
using DbExplorer.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Tests;

/// <summary>Several connections open at once: one active for the main tabs, each Query tab on its own connection, the
/// navigator showing every open one, and all of them reopened at start.</summary>
[Collection(nameof(HeadlessUiCollection))]
public class MultipleConnectionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-multi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static DatabaseSession Session(string name, string database = "Shop", params DbObject[] objects) =>
        new(new ConnectionProfile { Name = name, ProviderKey = SqlDialect.SqlServerKey, Database = database }, null!,
            DesktopTabTests.ScriptedProvider.Create(new(), SqlDialect.SqlServerKey), "1", new MetadataSnapshot
            {
                Objects = objects, Columns = [], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
            });

    private static DbObject Table(string name, string database = "Shop") =>
        new() { Database = database, Schema = "dbo", Name = name, Type = DbObjectType.Table };

    // ----- The list of open connections -----

    [Fact]
    public void Closing_the_active_connection_makes_the_one_used_before_it_active()
    {
        var open = new OpenConnections();
        var changes = 0;
        open.Changed += (_, _) => changes++;
        var (a, b, c) = (Session("A"), Session("B"), Session("C"));

        open.Add(a);
        open.Add(b);
        open.Add(c);
        Assert.Same(c, open.Active);
        Assert.True(open.Activate(a));
        Assert.Same(a, open.Active);
        Assert.Equal([a, b, c], open.All); // the opening order (and so the colour marks) never changes
        Assert.Equal(1, open.IndexOf(b.Profile.Id));

        Assert.Same(c, open.SuccessorOf(a)); // c was active before a
        Assert.Same(a, open.SuccessorOf(b)); // closing another one keeps the active one
        Assert.Same(c, open.Remove(a));
        Assert.Same(c, open.Remove(b));
        Assert.Null(open.Remove(c));
        Assert.Null(open.Active);
        Assert.Equal(7, changes);
    }

    [Fact]
    public void A_saved_connection_is_open_at_most_once()
    {
        var open = new OpenConnections();
        var a = Session("A");
        open.Add(a);

        Assert.Throws<InvalidOperationException>(() => open.Add(new DatabaseSession(a.Profile, null!, a.Provider, "1", a.Snapshot)));
        Assert.False(open.Activate(Session("not open")));
    }

    [Fact]
    public void A_new_tab_takes_the_selected_tabs_connection_while_it_is_open_else_the_active_one()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        bool IsOpen(Guid id) => id == a || id == b;

        Assert.Equal(a, QueryTabConnection.ForNewTab(a, IsOpen, active: b));
        Assert.Equal(b, QueryTabConnection.ForNewTab(Guid.NewGuid(), IsOpen, active: b)); // the selected tab's is closed
        Assert.Equal(b, QueryTabConnection.ForNewTab(null, IsOpen, active: b));
        Assert.Null(QueryTabConnection.ForNewTab(null, _ => false, active: null));
    }

    // ----- Start-up -----

    [Fact]
    public void The_other_open_connections_are_reopened_unless_they_are_production_or_need_a_password()
    {
        var active = new ConnectionProfile { Name = "Active", Password = "x" };
        var dev = new ConnectionProfile { Name = "Dev", Password = "x" };
        var prod = new ConnectionProfile { Name = "Prod", Password = "x", Environment = ConnectionEnvironment.Production };
        var noPassword = new ConnectionProfile { Name = "Ask" };
        var windows = new ConnectionProfile { Name = "Windows", IntegratedSecurity = true };
        var profiles = new[] { active, dev, prod, noPassword, windows };
        var state = new WorkspaceState
        {
            ConnectionId = active.Id, WasConnected = true,
            OtherOpenConnectionIds = [dev.Id, prod.Id, noPassword.Id, Guid.NewGuid(), windows.Id, active.Id]
        };

        Assert.Equal([dev, windows], StartupConnection.Others(state, profiles, reconnectOnStart: true));
        Assert.Empty(StartupConnection.Others(state, profiles, reconnectOnStart: false));
        state.WasConnected = false;
        Assert.Empty(StartupConnection.Others(state, profiles, reconnectOnStart: true));
    }

    // ----- Query tabs -----

    private (ServiceProvider Provider, QueryWorkspaceViewModel Workspace, OpenConnections Open) Workspace()
    {
        var services = App.ConfigureServices();
        services.AddSingleton(new AppPaths(Path.Combine(_root, "data")));
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<QueryWorkspaceViewModel>(), provider.GetRequiredService<OpenConnections>());
    }

    /// <summary>What the window does: register the connection, then make it active.</summary>
    private static void Open(QueryWorkspaceViewModel workspace, OpenConnections open, DatabaseSession session)
    {
        open.Add(session);
        workspace.Attach(session);
    }

    [AvaloniaFact]
    public async Task Tabs_keep_their_connection_when_another_one_becomes_active()
    {
        var (provider, workspace, open) = Workspace();
        await using var _ = provider;
        var (shop, reports) = (Session("Shop"), Session("Reports", "Reports"));
        workspace.Profiles = [shop.Profile, reports.Profile];

        var first = workspace.Documents.Single();
        Open(workspace, open, shop);
        Assert.Same(shop, first.AttachedSession);
        Assert.Equal(shop.Profile.Id, first.ConnectionId); // bound to the first connection it was attached to

        Open(workspace, open, reports);
        Assert.Same(shop, first.AttachedSession);
        workspace.OpenInNewTab("SELECT 1"); // from the Objects tab and the like: the active connection
        var second = workspace.SelectedDocument!;
        Assert.Same(reports, second.AttachedSession);

        workspace.SelectedDocument = first;
        var third = workspace.NewTab(); // ＋ New next to a Shop tab: Shop again
        Assert.Same(shop, third.AttachedSession);

        // The headers get the connection's colour once tabs are on two connections.
        Assert.All(workspace.Documents, d => Assert.True(d.ShowConnectionMark));
        Assert.Equal(ConnectionMarks.Brush(0), first.ConnectionMark);
        Assert.Equal(ConnectionMarks.Brush(1), second.ConnectionMark);
        Assert.Equal(["Shop", "Reports"], first.ConnectionChoices.Select(c => c.Name));
        Assert.True(first.ConnectionChoices.Single(c => c.Name == "Reports").IsActive);
    }

    [AvaloniaFact]
    public async Task Closing_a_connection_leaves_its_tabs_waiting_with_connect_and_use_the_active_one()
    {
        var (provider, workspace, open) = Workspace();
        await using var _ = provider;
        var (shop, reports) = (Session("Shop"), Session("Reports", "Reports"));
        workspace.Profiles = [shop.Profile, reports.Profile];
        Open(workspace, open, shop);
        var tab = workspace.Documents.Single();
        Open(workspace, open, reports);

        // The window closes the active connection: the main tabs move first, then it is removed.
        open.Remove(shop);
        Assert.Null(tab.AttachedSession);
        Assert.Equal(shop.Profile.Id, tab.ConnectionId);
        Assert.Equal("Connect to Shop", tab.ConnectLabel);
        Assert.Equal("Use Reports instead", tab.UseActiveLabel);
        Assert.StartsWith("Shop is not open.", tab.OfflineText);
        Assert.Equal("Connection: Shop (not open)", tab.ConnectionSummary);

        ConnectionProfile? asked = null;
        workspace.ConnectProfileRequested += p => { asked = p; return Task.CompletedTask; };
        tab.ConnectCommand!.Execute(null);
        Assert.Same(shop.Profile, asked);

        tab.UseActiveCommand!.Execute(null);
        Assert.Same(reports, tab.AttachedSession);
        Assert.Equal("", tab.ConnectLabel);

        // Opened again, a tab still bound to it attaches by itself.
        workspace.BindTab(tab, shop.Profile);
        Assert.Null(tab.AttachedSession);
        var again = new DatabaseSession(shop.Profile, null!, shop.Provider, "1", shop.Snapshot);
        Open(workspace, open, again);
        Assert.Same(again, tab.AttachedSession);
    }

    [AvaloniaFact]
    public async Task Picking_a_connection_in_the_tab_moves_it_there_and_the_binding_is_saved()
    {
        var (provider, workspace, open) = Workspace();
        await using var _ = provider;
        var (shop, reports) = (Session("Shop"), Session("Reports", "Reports"));
        workspace.Profiles = [shop.Profile, reports.Profile];
        Open(workspace, open, shop);
        Open(workspace, open, reports);
        var tab = workspace.Documents.Single();
        Assert.Same(shop, tab.AttachedSession);

        tab.SelectedConnection = tab.ConnectionChoices.Single(c => c.Name == "Reports");
        Assert.Same(reports, tab.AttachedSession);
        Assert.Equal("Reports", tab.CurrentDatabase);

        Assert.True(await workspace.SaveTabsAsync());
        var saved = await provider.GetRequiredService<ScriptStore>().LoadTabsAsync();
        Assert.Equal(reports.Profile.Id, Assert.Single(saved).ConnectionId);
    }

    [AvaloniaFact]
    public async Task Restored_tabs_wait_for_their_own_connection_and_older_ones_take_the_first_opened()
    {
        var (provider, workspace, open) = Workspace();
        await using var _ = provider;
        var (shop, reports) = (Session("Shop"), Session("Reports", "Reports"));
        await provider.GetRequiredService<ScriptStore>().SaveTabsAsync(
        [
            new QueryTabState { Title = "On reports", Sql = "SELECT 1", ConnectionId = reports.Profile.Id },
            new QueryTabState { Title = "Older file", Sql = "SELECT 2" }
        ]);
        await workspace.RestoreTabsAsync();
        workspace.Profiles = [shop.Profile, reports.Profile];
        var (onReports, older) = (workspace.Documents[0], workspace.Documents[1]);

        Open(workspace, open, shop);
        Assert.Null(onReports.AttachedSession);
        Assert.Equal("Connect to Reports", onReports.ConnectLabel);
        Assert.Same(shop, older.AttachedSession);

        Open(workspace, open, reports);
        Assert.Same(reports, onReports.AttachedSession);
        Assert.Same(shop, older.AttachedSession);
    }

    [AvaloniaFact]
    public async Task A_tab_with_an_open_transaction_cannot_move_to_another_connection()
    {
        var (provider, workspace, open) = Workspace();
        await using var _ = provider;
        var (shop, reports) = (Session("Shop"), Session("Reports", "Reports"));
        workspace.Profiles = [shop.Profile, reports.Profile];
        Open(workspace, open, shop);
        Open(workspace, open, reports);
        var tab = workspace.Documents.Single();
        tab.IsRunning = true;

        Assert.False(tab.CanChangeConnection);
        tab.SelectedConnection = tab.ConnectionChoices.Single(c => c.Name == "Reports");
        Assert.Same(shop, tab.AttachedSession);
        Assert.Contains("before moving the tab", tab.Status);
    }

    // ----- Navigator -----

    [Fact]
    public async Task The_navigator_browses_every_open_connection_and_its_rows_name_their_connection()
    {
        var open = new OpenConnections();
        var shop = Session("Shop", "Shop", Table("Orders"));
        var reports = Session("Reports", "Reports", Table("Sales", "Reports"));
        var closed = new ConnectionProfile { Name = "Closed", ProviderKey = SqlDialect.SqlServerKey };
        var vm = new NavigatorViewModel(null!, open) { Profiles = [shop.Profile, reports.Profile, closed] };
        var actions = new List<(NavigatorObjectAction Action, DbObject Object, DatabaseSession? Session)>();
        var scripts = new List<(string? Database, ConnectionProfile? Connection)>();
        vm.ObjectActionRequested += (action, obj, session) => actions.Add((action, obj, session));
        vm.OpenSqlRequested += (_, database, _, connection) => scripts.Add((database, connection));

        open.Add(shop);
        vm.Attach(shop);
        open.Add(reports);
        vm.Attach(reports);
        await Until(() => !vm.IsRestoring);

        var shopRow = vm.Roots.Single(r => r.Title == "Shop");
        var reportsRow = vm.Roots.Single(r => r.Title == "Reports");
        var closedRow = vm.Roots.Single(r => r.Title == "Closed");
        Assert.True(shopRow.IsExpanded);
        Assert.True(reportsRow.IsExpanded); // each opens on its own database when it is connected
        Assert.True(reportsRow.IsEmphasized);
        Assert.False(shopRow.IsEmphasized);
        Assert.Equal("active", reportsRow.Detail);
        Assert.NotNull(shopRow.MarkBrush);
        Assert.Null(closedRow.MarkBrush);
        Assert.True(NavigatorViewModel.OpensOnDoubleClick(closedRow));
        Assert.False(NavigatorViewModel.OpensOnDoubleClick(shopRow));

        vm.SelectedNode = shopRow;
        Assert.True(vm.SelectedIsOpenInactiveConnection);
        Assert.True(vm.SelectedHasDatabase);
        vm.SelectedNode = closedRow;
        Assert.True(vm.SelectedIsClosedConnection);
        Assert.False(vm.SelectedIsOpenConnection);

        // An object of the connection that is not active names its connection, so the window can activate it first.
        var shopDb = shopRow.Children.Single(c => c.Title == "Shop");
        var orders = shopDb.SelfAndDescendants().FirstOrDefault(n => n.Title == "Orders");
        if (orders is null)
        {
            foreach (var node in shopDb.SelfAndDescendants().ToList()) await node.LoadAsync();
            foreach (var node in shopDb.SelfAndDescendants().ToList()) await node.LoadAsync();
            orders = shopDb.SelfAndDescendants().First(n => n.Title == "Orders");
        }
        Assert.True(vm.Activate(orders));
        Assert.Same(shop, actions.Single().Session);
        vm.SelectRowsCommand.Execute(null);
        Assert.Equal(("Shop", shop.Profile), scripts.Single());

        // The filter searches every open connection, the active one first.
        vm.FilterText = "s";
        Assert.Equal(["Reports", "Shop"], vm.Roots.Where(r => r.Session is not null).Select(r => r.Title));
        Assert.Equal("2 matches", vm.FilterSummary);
        vm.FilterText = "";

        // Closing one takes it (and only it) out of the tree.
        open.Remove(shop);
        Assert.Null(vm.Roots.Single(r => r.Title == "Shop").Session);
        Assert.Null(vm.Roots.Single(r => r.Title == "Reports").MarkBrush); // one open: no marks
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not come true.");
            await Task.Delay(10);
        }
    }
}
