using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>The database navigator: what the tree lists under each node (pure, from the cached catalog), the
/// scripts its menu opens, and the view model's connect / open / filter behaviour.</summary>
public class NavigatorTests
{
    private static DbObject Obj(string name, DbObjectType type = DbObjectType.Table, string schema = "dbo", string database = "Shop") =>
        new() { Database = database, Schema = schema, Name = name, Type = type };

    private static DbColumn Col(string table, string name, int ordinal, bool pk = false, string type = "int",
        bool identity = false, bool computed = false, string schema = "dbo", string database = "Shop") =>
        new()
        {
            Database = database, Schema = schema, Table = table, Name = name, Ordinal = ordinal, IsPrimaryKey = pk,
            DataType = type, BaseType = type, IsIdentity = identity, IsComputed = computed, IsNullable = !pk
        };

    private static MetadataSnapshot Snapshot(IEnumerable<DbObject> objects, IEnumerable<DbColumn>? columns = null,
        IEnumerable<DbForeignKey>? keys = null, IEnumerable<DbIndex>? indexes = null) => new()
    {
        Objects = objects.ToList(), Columns = (columns ?? []).ToList(), Modules = [],
        ForeignKeys = (keys ?? []).ToList(), Indexes = (indexes ?? []).ToList(), RefreshedAt = DateTimeOffset.Now
    };

    // ----- NavigatorTree -----

    [Fact]
    public void Databases_merge_the_server_list_with_the_catalog_and_mark_the_connection_database()
    {
        var snapshot = Snapshot([Obj("Orders"), Obj("Logs", database: "Audit")]);

        var databases = NavigatorTree.Databases(snapshot, ["master", "Shop", "Empty"], "Shop");

        Assert.Equal(["Audit", "Empty", "master", "Shop"], databases.Select(d => d.Name));
        Assert.True(databases.Single(d => d.Name == "Shop").IsConnectionDatabase);
        Assert.True(databases.Single(d => d.Name == "Audit").InCatalog);
        Assert.False(databases.Single(d => d.Name == "Empty").InCatalog);
    }

    [Fact]
    public void A_catalog_without_database_names_is_listed_under_the_connection_database()
    {
        var snapshot = Snapshot([Obj("orders", schema: "public", database: "")]);

        var db = Assert.Single(NavigatorTree.Databases(snapshot, [], "shop"));

        Assert.Equal("shop", db.Name);
        Assert.True(db.InCatalog);
        Assert.Single(NavigatorTree.ObjectsOf(snapshot, "shop", isConnectionDatabase: true));
        Assert.Empty(NavigatorTree.ObjectsOf(snapshot, "shop", isConnectionDatabase: false));
    }

    [Fact]
    public void Engine_schemas_come_after_the_users_schemas()
    {
        var schemas = NavigatorTree.Schemas([Obj("a", schema: "pg_catalog"), Obj("b", schema: "sales"), Obj("c", schema: "app"), Obj("d", schema: "app")]);

        Assert.Equal(["app", "sales", "pg_catalog"], schemas.Select(s => s.Name));
        Assert.Equal(2, schemas[0].ObjectCount);
    }

    [Fact]
    public void The_schema_level_is_skipped_on_mysql_and_when_it_only_repeats_the_database()
    {
        Assert.True(NavigatorTree.SkipSchemaLevel(SqlDialect.MySqlKey, "shop", [new("shop", 3)]));
        Assert.True(NavigatorTree.SkipSchemaLevel(SqlDialect.PostgresKey, "shop", [new("shop", 3)]));
        Assert.False(NavigatorTree.SkipSchemaLevel(SqlDialect.PostgresKey, "shop", [new("public", 3)]));
        Assert.False(NavigatorTree.SkipSchemaLevel(SqlDialect.SqlServerKey, "Shop", [new("dbo", 1), new("sales", 1)]));
    }

    [Fact]
    public void Folders_come_in_a_fixed_order_and_every_function_kind_shares_one()
    {
        var objects = new[]
        {
            Obj("seq", DbObjectType.Sequence), Obj("f1", DbObjectType.ScalarFunction), Obj("f2", DbObjectType.TableFunction),
            Obj("v", DbObjectType.View), Obj("t", DbObjectType.Table), Obj("p", DbObjectType.Procedure)
        };

        var folders = NavigatorTree.Folders(objects);

        Assert.Equal([NavigatorFolder.Tables, NavigatorFolder.Views, NavigatorFolder.Procedures, NavigatorFolder.Functions, NavigatorFolder.Sequences],
            folders.Select(f => f.Folder));
        Assert.Equal(2, folders.Single(f => f.Folder == NavigatorFolder.Functions).Count);
        Assert.Equal(["f1", "f2"], NavigatorTree.ObjectsIn(objects, NavigatorFolder.Functions).Select(o => o.Name));
    }

    [Fact]
    public void A_table_lists_only_the_detail_folders_it_has_and_a_self_reference_only_under_foreign_keys()
    {
        var employees = Obj("Employees");
        var snapshot = Snapshot([employees, Obj("Teams")],
            [Col("Employees", "Id", 1, pk: true), Col("Employees", "ManagerId", 2)],
            [
                new DbForeignKey { Database = "Shop", Schema = "dbo", Table = "Employees", Name = "FK_Manager", Columns = "ManagerId",
                    ReferencedSchema = "dbo", ReferencedTable = "Employees", ReferencedColumns = "Id" },
                new DbForeignKey { Database = "Shop", Schema = "dbo", Table = "Teams", Name = "FK_Lead", Columns = "LeadId",
                    ReferencedSchema = "dbo", ReferencedTable = "Employees", ReferencedColumns = "Id" }
            ]);

        var details = NavigatorTree.Details(snapshot, employees);

        Assert.Equal([(NavigatorDetail.Columns, 2), (NavigatorDetail.ForeignKeys, 1), (NavigatorDetail.References, 1)], details);
        var reference = Assert.Single(NavigatorTree.DetailItems(snapshot, employees, NavigatorDetail.References));
        Assert.Equal("dbo.Teams", reference.Name);
        var columns = NavigatorTree.DetailItems(snapshot, employees, NavigatorDetail.Columns);
        Assert.Equal(["Id", "ManagerId"], columns.Select(c => c.Name));
        Assert.True(columns[0].IsKey);
        Assert.Equal("int · PK", columns[0].Description);
        Assert.Equal("int · null", columns[1].Description);
        Assert.Empty(NavigatorTree.Details(snapshot, Obj("p", DbObjectType.Procedure)));
    }

    [Fact]
    public void Matches_rank_exact_then_prefix_then_contains_and_respect_the_limit()
    {
        var objects = new[] { Obj("BackOrders"), Obj("Orders"), Obj("OrderLines"), Obj("Customers"), Obj("Stuff", schema: "orders_archive") };

        Assert.Equal(["Orders", "BackOrders", "Stuff"], NavigatorTree.Matches(objects, " orders ").Select(o => o.Name));
        Assert.Equal(["OrderLines", "Orders", "BackOrders", "Stuff"], NavigatorTree.Matches(objects, "order").Select(o => o.Name));
        Assert.Equal(["Orders"], NavigatorTree.Matches(objects, "ORDERS", limit: 1).Select(o => o.Name));
        Assert.Empty(NavigatorTree.Matches(objects, "  "));
    }

    // ----- Scripts from the navigator's menu -----

    [Fact]
    public void Select_rows_names_every_column_and_limits_the_rows_per_engine()
    {
        var table = Obj("Orders");
        var columns = new[] { Col("Orders", "Total", 2), Col("Orders", "Id", 1, pk: true) };

        Assert.Equal("SELECT TOP (200)\n  [Id],\n  [Total]\nFROM [dbo].[Orders];\n",
            ObjectScripts.SelectScript(SqlDialect.SqlServerKey, table, columns));
        Assert.Equal("SELECT\n  \"Id\",\n  \"Total\"\nFROM \"dbo\".\"Orders\" LIMIT 200;\n",
            ObjectScripts.SelectScript(SqlDialect.PostgresKey, table, columns));
        Assert.Equal("SELECT * FROM [dbo].[Orders];\n", ObjectScripts.SelectScript(SqlDialect.SqlServerKey, table, [], 0)
            .Replace("TOP (0) ", ""));
    }

    [Fact]
    public void Insert_and_update_templates_leave_out_identity_and_computed_columns_and_key_the_update()
    {
        var table = Obj("Orders");
        var columns = new[]
        {
            Col("Orders", "Id", 1, pk: true, identity: true), Col("Orders", "Note", 2, type: "text"),
            Col("Orders", "Total", 3, computed: true), Col("Orders", "Status", 4, type: "text")
        };

        var insert = ObjectScripts.InsertTemplate(SqlDialect.PostgresKey, table, columns);
        var update = ObjectScripts.UpdateTemplate(SqlDialect.PostgresKey, table, columns);

        Assert.Contains("INSERT INTO \"dbo\".\"Orders\" (\n  \"Note\",\n  \"Status\"\n) VALUES (\n  <Note>,\n  <Status>\n);", insert);
        Assert.Contains("UPDATE \"dbo\".\"Orders\"\nSET \"Note\" = <Note>,\n    \"Status\" = <Status>\nWHERE \"Id\" = <Id>;", update);
        Assert.DoesNotContain("Total", insert + update.Replace("-- Update rows", ""));
    }

    // ----- NavigatorViewModel -----

    private static (NavigatorViewModel Vm, DatabaseSession Session, ConnectionProfile Other) Connected()
    {
        var session = new DatabaseSession(
            new ConnectionProfile { Name = "Shop", ProviderKey = SqlDialect.SqlServerKey, Database = "Shop", Folder = "Local" },
            null!, DesktopTabTests.ScriptedProvider.Create(new(), SqlDialect.SqlServerKey), "1",
            Snapshot([Obj("Orders"), Obj("Customers"), Obj("GetOrders", DbObjectType.Procedure), Obj("Rates", schema: "sales")],
                [Col("Orders", "Id", 1, pk: true), Col("Orders", "CustomerId", 2)]));
        var other = new ConnectionProfile { Name = "Reports", ProviderKey = SqlDialect.SqlServerKey };
        var vm = new NavigatorViewModel(null!) { Profiles = [session.Profile, other] };
        return (vm, session, other);
    }

    private static NavigatorNode Child(NavigatorNode node, string title) => node.Children.Single(c => c.Title == title);

    [Fact]
    public void Disconnected_it_lists_saved_connections_by_folder_and_double_click_connects()
    {
        var (vm, session, other) = Connected();
        (NavigatorConnectionAction, ConnectionProfile)? asked = null;
        vm.ConnectionActionRequested += (action, profile) => asked = (action, profile);

        Assert.Equal(["Reports", "Local"], vm.Roots.Select(r => r.Title));
        var shop = Child(vm.Roots[1], "Shop");
        Assert.True(vm.Activate(shop));
        Assert.Equal((NavigatorConnectionAction.Connect, session.Profile), asked);
        Assert.False(vm.Activate(vm.Roots[1]));
        Assert.NotSame(other, asked?.Item2);
    }

    [Fact]
    public async Task Connected_it_opens_on_the_connection_database_and_walks_down_to_columns()
    {
        var (vm, session, _) = Connected();

        vm.Attach(session);
        var connection = Child(vm.Roots.Single(r => r.Title == "Local"), "Shop");
        await Until(() => connection.IsLoaded && connection.Children.Any(c => c.Title == "Shop" && c.IsLoaded));

        Assert.True(connection.IsExpanded);
        var database = Child(connection, "Shop");
        Assert.True(database.IsExpanded);
        Assert.Equal(["dbo", "sales"], database.Children.Select(c => c.Title));
        var dbo = Child(database, "dbo");
        await dbo.LoadAsync();
        Assert.Equal(["Tables", "Procedures"], dbo.Children.Select(c => c.Title));
        var tables = Child(dbo, "Tables");
        await tables.LoadAsync();
        Assert.Equal(["Customers", "Orders"], tables.Children.Select(c => c.Title));
        var orders = Child(tables, "Orders");
        Assert.True(orders.InSessionCatalog);
        await orders.LoadAsync();
        var columns = Child(orders, "Columns");
        await columns.LoadAsync();
        Assert.Equal(["Id", "CustomerId"], columns.Children.Select(c => c.Title));
        Assert.False(Child(tables, "Customers").Children.Any()); // no columns cached: no expander
    }

    [Fact]
    public async Task Objects_open_in_the_objects_tab_and_select_rows_opens_a_query_in_their_database()
    {
        var (vm, session, _) = Connected();
        vm.Attach(session);
        var objectActions = new List<(NavigatorObjectAction, DbObject)>();
        var scripts = new List<(string Sql, string? Database, string? Title)>();
        vm.ObjectActionRequested += (action, obj, _) => objectActions.Add((action, obj));
        vm.OpenSqlRequested += (sql, database, title, _) => scripts.Add((sql, database, title));

        await vm.RevealAsync(Obj("Orders"));
        var orders = vm.SelectedNode!;
        Assert.Equal("Orders", orders.Title);

        Assert.True(vm.Activate(orders));
        Assert.Equal(NavigatorObjectAction.Open, Assert.Single(objectActions).Item1);
        Assert.True(vm.SelectedIsTable);
        Assert.True(vm.SelectedHasCatalogActions);

        vm.SelectRowsCommand.Execute(null);
        var script = Assert.Single(scripts);
        Assert.Equal("SELECT TOP (200)\n  [Id],\n  [CustomerId]\nFROM [dbo].[Orders];\n", script.Sql);
        Assert.Equal("Shop", script.Database);
        Assert.Equal("Select Orders", script.Title);

        vm.DropTableCommand.Execute(null);
        Assert.Equal(NavigatorObjectAction.DropTable, objectActions[^1].Item1);
    }

    [Fact]
    public void The_filter_shows_matching_objects_under_their_open_path_and_counts_them()
    {
        var (vm, session, _) = Connected();
        vm.Attach(session);

        vm.FilterText = "order";

        Assert.Equal("2 matches", vm.FilterSummary);
        var connection = vm.Roots.Single(r => r.Kind == NavigatorNodeKind.Connection && r.Title == "Shop");
        var objects = connection.SelfAndDescendants().Where(n => n.Kind == NavigatorNodeKind.Object).Select(n => n.Title);
        Assert.Equal(["Orders", "GetOrders"], objects);
        Assert.All(connection.SelfAndDescendants().Where(n => n.Kind is NavigatorNodeKind.Database or NavigatorNodeKind.Schema or NavigatorNodeKind.Folder),
            n => Assert.True(n.IsExpanded));

        vm.FilterText = "reports";
        Assert.Contains(vm.Roots, r => r.Title == "Reports");
        Assert.Equal("No object matches in the connection's catalog", vm.FilterSummary);
    }

    [Fact]
    public void Disconnecting_returns_to_the_saved_connections()
    {
        var (vm, session, _) = Connected();
        vm.Attach(session);
        vm.Attach(null);

        var shop = Child(vm.Roots.Single(r => r.Title == "Local"), "Shop");
        Assert.True(shop.IsLoaded);
        Assert.Empty(shop.Children);
        Assert.False(shop.IsDimmed);
    }

    [Fact]
    public async Task A_second_rebuild_while_the_first_still_restores_keeps_the_open_nodes()
    {
        var (vm, session, _) = Connected();

        vm.Attach(session);   // opens the connection and its database, reading the server's database list first
        vm.Rebuild();         // e.g. DatabasesChanged right after SnapshotChanged
        await Until(() => !vm.IsRestoring);

        var connection = Child(vm.Roots.Single(r => r.Title == "Local"), "Shop");
        Assert.True(connection.IsExpanded);
        Assert.True(Child(connection, "Shop").IsExpanded);
    }

    [Fact]
    public void Filter_results_keep_the_best_match_first_across_every_database()
    {
        // A server-wide catalog: 600 partial matches in Audit, one exact match in Shop.
        var objects = Enumerable.Range(0, 600).Select(i => Obj($"Old{i:000}Orders", database: "Audit"))
            .Append(Obj("BackOrders")).Append(Obj("Orders")).ToList();
        var session = new DatabaseSession(new ConnectionProfile { Name = "Server", ProviderKey = SqlDialect.SqlServerKey },
            null!, DesktopTabTests.ScriptedProvider.Create(new(), SqlDialect.SqlServerKey), "1", Snapshot(objects));
        var vm = new NavigatorViewModel(null!) { Profiles = [session.Profile] };
        vm.Attach(session);

        vm.FilterText = "Orders";

        Assert.Equal($"First {NavigatorViewModel.MaxMatches} of 602 matches", vm.FilterSummary);
        var shop = vm.Roots[0].Children.Single(d => d.Title == "Shop");
        var tables = shop.SelfAndDescendants().Single(n => n.Kind == NavigatorNodeKind.Folder);
        Assert.Equal(["Orders", "BackOrders"], tables.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Scripts_use_the_columns_of_the_same_database_when_two_have_the_same_table()
    {
        var session = new DatabaseSession(new ConnectionProfile { Name = "Server", ProviderKey = SqlDialect.SqlServerKey },
            null!, DesktopTabTests.ScriptedProvider.Create(new(), SqlDialect.SqlServerKey), "1",
            Snapshot([Obj("Orders", database: "Audit"), Obj("Orders")],
                [Col("Orders", "AuditId", 1, pk: true, database: "Audit"), Col("Orders", "OrderId", 1, pk: true)]));
        var vm = new NavigatorViewModel(null!) { Profiles = [session.Profile] };
        string? sql = null;
        vm.OpenSqlRequested += (text, _, _, _) => sql = text;
        vm.Attach(session);

        await vm.RevealAsync(Obj("Orders"));
        vm.ScriptDeleteCommand.Execute(null);

        Assert.Contains("[OrderId] = <OrderId>", sql);
        Assert.DoesNotContain("AuditId", sql);
    }

    [Fact]
    public async Task Open_on_a_detail_folder_opens_its_table_and_new_query_on_the_connection_uses_its_database()
    {
        var (vm, session, _) = Connected();
        vm.Attach(session);
        var opened = new List<DbObject>();
        var scripts = new List<string?>();
        var hidden = false;
        vm.ObjectActionRequested += (_, obj, _) => opened.Add(obj);
        vm.OpenSqlRequested += (_, database, _, _) => scripts.Add(database);
        vm.HideRequested += () => hidden = true;

        await vm.RevealAsync(Obj("Orders"));
        var orders = vm.SelectedNode!;
        await orders.LoadAsync();
        vm.SelectedNode = Child(orders, "Columns");
        vm.OpenCommand.Execute(null);
        Assert.Equal("Orders", Assert.Single(opened).Name);

        await Until(() => !vm.IsRestoring);
        vm.SelectedNode = Child(vm.Roots.Single(r => r.Title == "Local"), "Shop");
        vm.NewQueryCommand.Execute(null);
        Assert.Equal("Shop", Assert.Single(scripts));

        vm.HideCommand.Execute(null);
        Assert.True(hidden);
    }

    [Fact]
    public void Double_click_shows_objects_and_columns_but_opens_and_closes_containers()
    {
        Assert.True(NavigatorViewModel.OpensOnDoubleClick(new NavigatorNode(NavigatorNodeKind.Object, "t", "k")));
        Assert.True(NavigatorViewModel.OpensOnDoubleClick(new NavigatorNode(NavigatorNodeKind.Detail, "c", "k")));
        Assert.True(NavigatorViewModel.OpensOnDoubleClick(new NavigatorNode(NavigatorNodeKind.Connection, "saved", "k")));
        Assert.False(NavigatorViewModel.OpensOnDoubleClick(new NavigatorNode(NavigatorNodeKind.Connection, "connected", "k") { Session = Connected().Session }));
        Assert.False(NavigatorViewModel.OpensOnDoubleClick(new NavigatorNode(NavigatorNodeKind.Folder, "Tables", "k")));
        Assert.False(NavigatorViewModel.OpensOnDoubleClick(new NavigatorNode(NavigatorNodeKind.DetailFolder, "Columns", "k")));
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(1_234, "1.2k")]
    [InlineData(45_000, "45k")]
    [InlineData(3_400_000, "3.4M")]
    public void Row_counts_are_short(long rows, string expected) =>
        Assert.Equal(expected, NavigatorViewModel.ShortCount(rows).Replace(',', '.'));

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
