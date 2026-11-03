using DbExplorer.Application.Copy;
using DbExplorer.Application.QueryBuilder;
using DbExplorer.Application.Sessions;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>The redesigned Query builder: related tables listed first, placing beside the linked table, arranging,
/// undo/redo, select-all per table, and Run.</summary>
public class QueryBuilderDesignTests
{
    // ----- Layout and related tables (Application) -----

    [Fact]
    public void Arrange_puts_each_table_one_column_right_of_the_table_it_joins()
    {
        var spots = QueryLayout.Arrange(
            [new LayoutBox("c", 100), new LayoutBox("o", 120), new LayoutBox("ol", 80), new LayoutBox("p", 60)],
            [("c", "o"), ("o", "ol"), ("p", "ol")], width: 240);

        Assert.Equal(spots["c"].X, QueryLayout.Margin);
        Assert.True(spots["o"].X > spots["c"].X);
        Assert.True(spots["ol"].X > spots["o"].X);
        Assert.True(spots["p"].X > spots["ol"].X);
        Assert.All(spots.Values, s => Assert.Equal(QueryLayout.Margin, s.Y));
    }

    [Fact]
    public void Arrange_stacks_siblings_and_gives_unjoined_tables_their_own_column()
    {
        var spots = QueryLayout.Arrange(
            [new LayoutBox("o", 100), new LayoutBox("c", 100), new LayoutBox("ol", 100), new LayoutBox("a", 50)],
            [("o", "c"), ("o", "ol")], width: 240);

        Assert.Equal(spots["c"].X, spots["ol"].X);
        Assert.Equal(QueryLayout.Margin + 100 + QueryLayout.RowGap, spots["ol"].Y);
        Assert.True(spots["a"].X > spots["c"].X);
    }

    [Fact]
    public void Related_lists_tables_linked_either_way_with_the_alias_they_join()
    {
        var related = JoinSuggester.Related(TestSnapshots.Shop(), "", [new QueryTable("o", "sales", "Orders")]);

        Assert.Equal("o", related[("dbo", "Customers")]);
        Assert.Equal("o", related[("SALES", "orderlines")]);
        Assert.False(related.ContainsKey(("dbo", "Products")));
        Assert.False(related.ContainsKey(("dbo", "Audit")));
    }

    // ----- View model -----

    [Fact]
    public void Tables_a_foreign_key_links_to_the_query_are_listed_first()
    {
        var vm = Builder();
        Assert.All(vm.TableChoices, c => Assert.False(c.IsRelated));

        vm.AddTable(Table("sales", "Orders"), null);

        Assert.Equal(["dbo.Customers", "sales.OrderLines"], vm.TableChoices.Take(2).Select(c => c.FullName).Order());
        Assert.Equal("joins o", vm.TableChoices[0].Hint);
        Assert.Equal("on canvas", vm.TableChoices.Single(c => c.FullName == "sales.Orders").Hint);
        Assert.True(vm.HasRelatedTables);
    }

    [Fact]
    public void A_table_added_without_a_position_lands_right_of_the_table_it_joins()
    {
        var vm = Builder();
        var customers = vm.AddTable(Table("dbo", "Customers"), 400, 300)!;
        var orders = vm.AddTable(Table("sales", "Orders"), null)!;

        Assert.Equal(customers.X + QueryTableItem.Width + QueryLayout.ColumnGap, orders.X);
        var audit = vm.AddTable(Table("dbo", "Audit"), null)!;
        Assert.True(audit.X >= orders.X + QueryTableItem.Width);
    }

    [Fact]
    public void Undo_and_redo_step_through_the_design()
    {
        var vm = Builder();
        Assert.False(vm.CanUndo);
        var orders = vm.AddTable(Table("sales", "Orders"), null)!;
        vm.AddTable(Table("dbo", "Customers"), null);
        vm.ToggleColumn(orders, "OrderDate");
        var withColumn = vm.Sql;

        vm.Undo();
        Assert.Empty(vm.Columns);
        Assert.Equal(2, vm.Tables.Count);
        Assert.Single(vm.Joins);

        vm.Undo();
        Assert.Single(vm.Tables);
        Assert.Empty(vm.Joins);
        Assert.True(vm.CanRedo);

        vm.Redo();
        vm.Redo();
        Assert.Equal(withColumn, vm.Sql);
        Assert.False(vm.CanRedo);

        // A new change drops what could be redone.
        vm.Undo();
        vm.Distinct = true;
        Assert.False(vm.CanRedo);
    }

    [Fact]
    public void Typing_into_one_cell_is_one_undo_step()
    {
        var vm = Builder();
        var orders = vm.AddTable(Table("sales", "Orders"), null)!;
        vm.ToggleColumn(orders, "OrderDate");
        var column = vm.Columns.Single();

        foreach (var text in new[] { "w", "wh", "whe", "when" }) column.OutputAlias = text;
        Assert.Contains("AS [when]", vm.Sql);

        vm.Undo();
        Assert.Equal("", vm.Columns.Single().OutputAlias);
        Assert.DoesNotContain(" AS ", vm.Sql.Replace(" AS o", ""));
    }

    [Fact]
    public void An_alias_typed_after_choosing_an_aggregate_undoes_on_its_own()
    {
        var vm = Builder();
        var orders = vm.AddTable(Table("sales", "Orders"), null)!;
        vm.ToggleColumn(orders, "OrderId");
        var column = vm.Columns.Single();

        column.Aggregate = ColumnAggregate.Count;
        Assert.Equal("count_OrderId", column.OutputAlias);
        column.OutputAlias = "orders";

        vm.Undo();
        Assert.Equal(ColumnAggregate.Count, vm.Columns.Single().Aggregate);
        Assert.Equal("count_OrderId", vm.Columns.Single().OutputAlias);
    }

    [Fact]
    public void Clear_can_be_undone()
    {
        var vm = Builder();
        var orders = vm.AddTable(Table("sales", "Orders"), 50, 60)!;
        vm.ToggleColumn(orders, "OrderId");
        var before = vm.Sql;

        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Tables);

        vm.Undo();
        Assert.Equal(before, vm.Sql);
        Assert.Equal((50.0, 60.0), (vm.Tables[0].X, vm.Tables[0].Y));
    }

    [Fact]
    public void Moving_a_table_is_one_undo_step()
    {
        var vm = Builder();
        var orders = vm.AddTable(Table("sales", "Orders"), 10, 10)!;
        for (var x = 20; x <= 200; x += 20) vm.MoveTable(orders, x, x);
        vm.EndMove();

        vm.Undo();
        Assert.Equal((10.0, 10.0), (vm.Tables[0].X, vm.Tables[0].Y));
    }

    [Fact]
    public void Select_every_column_of_a_table_then_clear_them()
    {
        var vm = Builder();
        var orders = vm.AddTable(Table("sales", "Orders"), null)!;
        vm.ToggleColumn(orders, "OrderDate");
        vm.Columns.Single().Filter = "> '2020-01-01'";

        vm.SetAllColumns(orders, true);
        Assert.Equal(["OrderDate", "OrderId", "CustomerId"], vm.Columns.Select(c => c.Column));
        Assert.All(vm.Columns, c => Assert.True(c.Output));

        vm.SetAllColumns(orders, false);
        // The filtered column stays to filter by; the plain ones go.
        var kept = Assert.Single(vm.Columns);
        Assert.Equal("OrderDate", kept.Column);
        Assert.False(kept.Output);
    }

    [Fact]
    public void Arrange_lines_the_tables_up_along_their_joins()
    {
        var vm = Builder();
        var lines = vm.AddTable(Table("sales", "OrderLines"), 600, 600)!;
        var orders = vm.AddTable(Table("sales", "Orders"), 10, 400)!;

        vm.ArrangeCommand.Execute(null);

        Assert.Equal((QueryLayout.Margin, QueryLayout.Margin), (lines.X, lines.Y));
        Assert.Equal(QueryLayout.Margin + QueryTableItem.Width + QueryLayout.ColumnGap, orders.X);
    }

    [Fact]
    public void Run_and_open_hand_the_sql_over()
    {
        var vm = Builder();
        (string Sql, string? Database)? ran = null;
        vm.RunSqlRequested += (sql, db) => ran = (sql, db);
        Assert.False(vm.RunCommand.CanExecute(null));

        vm.AddTable(Table("sales", "Orders"), null);
        vm.RunCommand.Execute(null);

        Assert.Equal(vm.Sql, ran?.Sql);
    }

    [Fact]
    public void Join_kind_reads_as_words()
    {
        var vm = Builder();
        vm.AddTable(Table("dbo", "Customers"), null);
        vm.AddTable(Table("sales", "Orders"), null);
        var join = Assert.Single(vm.Joins);

        vm.SetJoinKind(join, JoinKind.Left);

        Assert.Equal("every c row, with o where it matches", join.Description);
        Assert.Contains("LEFT JOIN", vm.Sql);
    }

    [Fact]
    public void Sort_button_cycles_none_ascending_descending()
    {
        var vm = Builder();
        var orders = vm.AddTable(Table("sales", "Orders"), null)!;
        vm.ToggleColumn(orders, "OrderDate");
        var column = vm.Columns.Single();

        column.CycleSortCommand.Execute(null);
        Assert.EndsWith("ORDER BY o.OrderDate;", vm.Sql);
        column.CycleSortCommand.Execute(null);
        Assert.EndsWith("ORDER BY o.OrderDate DESC;", vm.Sql);
        column.CycleSortCommand.Execute(null);
        Assert.DoesNotContain("ORDER BY", vm.Sql);
    }

    [Fact]
    public void Builder_keys_are_their_own_area()
    {
        Assert.Equal(ShortcutAreas.QueryBuilder, ShortcutCommands.Get(ShortcutCommands.BuilderRun)!.Area);
        Assert.Contains(ShortcutCatalog.Build(new KeymapService()), s => s.Area == ShortcutAreas.QueryBuilder && s.Id == ShortcutCommands.BuilderRun);
    }

    private static DbObject Table(string schema, string name) => TestSnapshots.Shop().Objects.Single(o => o.Schema == schema && o.Name == name);

    /// <summary>A builder on a SQL Server session over <see cref="TestSnapshots.Shop"/>, tables loaded.</summary>
    private static QueryBuilderViewModel Builder()
    {
        var vm = new QueryBuilderViewModel(new SessionService(null!, null!, null!));
        var provider = DesktopTabTests.ScriptedProvider.Create(new(), SqlDialect.SqlServerKey);
        vm.Attach(new DatabaseSession(new ConnectionProfile { ProviderKey = SqlDialect.SqlServerKey, Name = "fake" }, null!, provider, "1", TestSnapshots.Shop()));
        Assert.NotEmpty(vm.AvailableTables);
        return vm;
    }
}
