using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>The Table designer's redesign: common columns, change marks on an open table, dropped columns that can be
/// restored, columns added where the caret is, and its keyboard shortcuts.</summary>
public class TableDesignerRedesignTests
{
    private const string SqlServer = SqlDialect.SqlServerKey;

    // ----- Common columns -----

    [Fact]
    public void Common_columns_on_PostgreSQL_are_snake_case_and_typed_for_the_engine()
    {
        var design = new TableDesign { Name = "invoices", Columns = [new ColumnDesign { Name = "id", Type = "integer", IsPrimaryKey = true }] };

        var choices = CommonColumns.For(SqlDialect.PostgresKey, DesignContext.Empty(SqlDialect.PostgresKey), design);

        var created = Assert.Single(choices, c => c.Label == "Created at").Column;
        Assert.Equal("created_at", created.Name);
        Assert.Equal("timestamptz", created.Type);
        Assert.Equal("now()", created.Default);
        Assert.False(created.IsNullable);
        var active = Assert.Single(choices, c => c.Label == "Active flag").Column;
        Assert.Equal(("is_active", "boolean", "true"), (active.Name, active.Type, active.Default));
        Assert.Equal("gen_random_uuid()", Assert.Single(choices, c => c.Label == "Public id").Column.Default);
    }

    [Fact]
    public void Common_columns_on_SQL_Server_follow_the_tables_PascalCase_names()
    {
        var design = new TableDesign { Name = "Invoices", Columns = [new ColumnDesign { Name = "InvoiceId", Type = "int", IsPrimaryKey = true }] };

        var choices = CommonColumns.For(SqlServer, DesignContext.Empty(SqlServer), design);

        var created = Assert.Single(choices, c => c.Label == "Created at").Column;
        Assert.Equal(("CreatedAt", "datetime2", "SYSUTCDATETIME()"), (created.Name, created.Type, created.Default));
        Assert.Equal(("IsActive", "bit", "1"), Assert.Single(choices, c => c.Label == "Active flag").Column is var a ? (a.Name, a.Type, a.Default) : default);
        Assert.Equal("NEWID()", Assert.Single(choices, c => c.Label == "Public id").Column.Default);
    }

    [Fact]
    public void Common_columns_leave_out_the_ones_the_table_has_and_follow_snake_case_on_SQL_Server_too()
    {
        var design = new TableDesign
        {
            Name = "invoices",
            Columns = [new ColumnDesign { Name = "invoice_id", Type = "int" }, new ColumnDesign { Name = "created_at", Type = "datetime2" }]
        };

        var choices = CommonColumns.For(SqlServer, DesignContext.Empty(SqlServer), design);

        Assert.DoesNotContain(choices, c => c.Label == "Created at");
        Assert.Equal("updated_at", Assert.Single(choices, c => c.Label == "Updated at").Column.Name);
    }

    // ----- New table -----

    [Fact]
    public void A_new_column_goes_below_the_one_being_edited_and_asks_for_the_caret()
    {
        var vm = Designer();
        vm.TableName = "Invoices";
        vm.AddColumnCommand.Execute(null);
        vm.Columns[^1].Name = "Last";
        ColumnRow? focused = null;
        vm.FocusColumnRequested += row => focused = row;

        vm.SelectedColumn = vm.Columns[0];
        vm.AddColumnCommand.Execute(null);

        Assert.Equal(3, vm.Columns.Count);
        Assert.Same(vm.Columns[1], focused);
        Assert.Same(vm.Columns[1], vm.SelectedColumn);
        Assert.Equal("Last", vm.Columns[2].Name);
        Assert.Equal("Columns (3)", vm.ColumnsHeader);
    }

    [Fact]
    public void Shortcut_commands_act_on_the_column_being_edited()
    {
        var vm = Designer();
        vm.TableName = "Invoices";
        vm.AddCommonColumnCommand.Execute(vm.CommonColumnChoices.First(c => c.Label == "Name"));
        vm.AddCommonColumnCommand.Execute(vm.CommonColumnChoices.First(c => c.Label == "Email"));
        Assert.Equal(["Id", "Name", "Email"], vm.Columns.Select(c => c.Name));

        vm.SelectedColumn = vm.Columns[2];
        vm.MoveColumnUpCommand.Execute(null);
        Assert.Equal(["Id", "Email", "Name"], vm.Columns.Select(c => c.Name));

        vm.RemoveColumnCommand.Execute(null);
        Assert.Equal(["Id", "Name"], vm.Columns.Select(c => c.Name));
        Assert.Equal("Name", vm.SelectedColumn?.Name);
    }

    [Fact]
    public void Index_this_column_adds_one_index_and_the_index_menu_appends_columns()
    {
        var vm = Designer();
        vm.TableName = "Invoices";
        vm.AddCommonColumnCommand.Execute(vm.CommonColumnChoices.First(c => c.Label == "Status"));
        var status = vm.Columns.Single(c => c.Name == "Status");

        vm.IndexColumnCommand.Execute(status);
        vm.IndexColumnCommand.Execute(status);
        var index = Assert.Single(vm.Indexes);
        vm.AppendIndexColumn(index, "Id");
        vm.AppendIndexColumn(index, "status");

        Assert.Equal("Status, Id", index.Columns);
        Assert.Contains("CREATE INDEX [IX_Invoices_Status_Id]", vm.Script);
    }

    [Fact]
    public void A_new_table_has_no_change_marks_and_says_CREATE_TABLE()
    {
        var vm = Designer();
        vm.TableName = "Invoices";
        vm.AddColumnCommand.Execute(null);

        Assert.All(vm.Columns, c => Assert.Equal(ColumnChange.None, c.Change));
        Assert.Equal("NEW", vm.ModeBadge);
        Assert.Equal("CREATE TABLE", vm.ScriptSummary);
        Assert.False(vm.HasChanges);
        Assert.Equal("dbo.Invoices", vm.TitleText);
    }

    [Fact]
    public void Errors_are_counted_for_the_header()
    {
        var vm = Designer();
        Assert.True(vm.HasErrorCount); // no table name yet
        vm.TableName = "Invoices";
        Assert.False(vm.HasErrorCount);
    }

    // ----- Open table -----

    [Fact]
    public Task An_open_table_marks_new_renamed_and_changed_columns() => UiThread.RunAsync(async () =>
    {
        var vm = await OpenOrders();
        Assert.Equal("ALTER", vm.ModeBadge);
        Assert.Equal("No changes yet", vm.ScriptSummary);

        vm.Columns.Single(c => c.Name == "Total").Type = "bigint";
        vm.Columns.Single(c => c.Name == "CustomerId").Name = "ClientId";
        vm.AddColumnCommand.Execute(null);
        vm.Columns[^1].Name = "Note";
        vm.Columns[^1].Type = "nvarchar";

        Assert.Equal(ColumnChange.None, vm.Columns.Single(c => c.Name == "OrderId").Change);
        var total = vm.Columns.Single(c => c.Name == "Total");
        Assert.Equal(ColumnChange.Changed, total.Change);
        Assert.Contains("type int → bigint", total.ChangeText);
        var client = vm.Columns.Single(c => c.Name == "ClientId");
        Assert.Equal(ColumnChange.Renamed, client.Change);
        Assert.True(client.IsChanged);
        Assert.True(vm.Columns.Single(c => c.Name == "Note").IsNew);
        Assert.Equal("1 new · 2 changed", vm.ChangeSummary);
        Assert.StartsWith("ALTER · ", vm.ScriptSummary);
    });

    [Fact]
    public Task A_dropped_column_is_listed_and_restore_puts_it_back_where_it_was() => UiThread.RunAsync(async () =>
    {
        var vm = await OpenOrders();

        vm.RemoveColumnCommand.Execute(vm.Columns.Single(c => c.Name == "CustomerId"));
        var dropped = Assert.Single(vm.RemovedColumns);
        Assert.Equal("CustomerId · int", dropped.Text);
        Assert.Equal("1 dropped", vm.ChangeSummary);
        Assert.Contains("DROP COLUMN [CustomerId]", vm.Script);

        vm.RestoreColumnCommand.Execute(dropped);

        Assert.Empty(vm.RemovedColumns);
        Assert.Equal(["OrderId", "CustomerId", "Total"], vm.Columns.Select(c => c.Name));
        Assert.False(vm.HasChanges);
        Assert.Equal("No changes yet", vm.ScriptSummary);
    });

    [Fact]
    public Task Revert_clears_the_selected_column_so_the_remove_key_cannot_drop_its_old_keys() => UiThread.RunAsync(async () =>
    {
        var vm = await OpenOrders();
        var stale = vm.Columns.Single(c => c.Name == "CustomerId");
        vm.SelectedColumn = stale;
        vm.RevertChangesCommand.Execute(null);
        Assert.Null(vm.SelectedColumn);
        vm.AddIndexCommand.Execute(null);
        vm.Indexes[^1].Columns = "CustomerId";

        vm.RemoveColumnCommand.Execute(null);
        vm.RemoveColumnCommand.Execute(stale);

        Assert.Equal(3, vm.Columns.Count);
        Assert.Single(vm.Indexes);
    });

    // ----- Shortcuts -----

    [Fact]
    public void Table_designer_shortcuts_are_listed_and_do_not_clash()
    {
        var keymap = new KeymapService();
        var ids = new[]
        {
            ShortcutCommands.DesignerExecute, ShortcutCommands.DesignerAddColumn, ShortcutCommands.DesignerRemoveColumn,
            ShortcutCommands.DesignerColumnUp, ShortcutCommands.DesignerColumnDown, ShortcutCommands.DesignerCopyScript
        };
        var sheet = ShortcutCatalog.Build(keymap);
        foreach (var id in ids)
        {
            var command = ShortcutCommands.Get(id);
            Assert.NotNull(command);
            Assert.Equal(ShortcutAreas.TableDesigner, command.Area);
            Assert.Contains(sheet, s => s.Id == id && s.Area == ShortcutAreas.TableDesigner);
            foreach (var chord in command.Defaults) Assert.Empty(keymap.Conflicts(id, chord));
        }
    }

    // ----- Helpers -----

    /// <summary>A designer without a connection, on a fresh SQL Server design (schema dbo, key column Id).</summary>
    private static TableDesignerViewModel Designer()
    {
        var vm = new TableDesignerViewModel(new SessionService(null!, null!, null!), DesktopTabTests.DialogFake.Create(out _));
        vm.NewTableCommand.Execute(null);
        return vm;
    }

    /// <summary>dbo.Orders (OrderId, CustomerId, Total) opened for changing on a SQL Server session.</summary>
    private static async Task<TableDesignerViewModel> OpenOrders()
    {
        var provider = DesktopTabTests.ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.GetTableConstraintsAsync)] = _ => Task.FromResult(DbTableConstraints.None)
        }, SqlServer);
        DbColumn Col(string name, int ordinal) => new()
        {
            Database = "main", Schema = "dbo", Table = "Orders", Name = name, DataType = "int", BaseType = "int", Ordinal = ordinal, IsNullable = ordinal > 1
        };
        var session = new DatabaseSession(new ConnectionProfile { ProviderKey = SqlServer, Name = "fake", Database = "main" }, null!, provider, "1", new MetadataSnapshot
        {
            Objects = [new DbObject { Database = "main", Schema = "dbo", Name = "Orders", Type = DbObjectType.Table }],
            Columns = [Col("OrderId", 1), Col("CustomerId", 2), Col("Total", 3)],
            Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        });
        var vm = Designer();
        vm.Attach(session);
        await vm.OpenTableAsync(new DbObject { Database = "main", Schema = "dbo", Name = "Orders", Type = DbObjectType.Table });
        Assert.True(vm.IsAltering);
        return vm;
    }
}
