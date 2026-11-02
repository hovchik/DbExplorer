using DbExplorer.Application.Compare;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

/// <summary>The whole-database migration script of the Comparer: what makes the right database's schema match the left's.</summary>
public class MigrationScriptTests
{
    private const string Ss = SqlDialect.SqlServerKey;
    private const string Pg = SqlDialect.PostgresKey;
    private const string My = SqlDialect.MySqlKey;

    private static MetadataSnapshot Shop() => TestSnapshots.Shop();

    private static MigrationScript Build(MetadataSnapshot source, MetadataSnapshot target, string provider = Ss, MigrationOptions? options = null,
        string? sourceDb = null, string? targetDb = null) =>
        MigrationScriptBuilder.Build(new MigrationSide(source, provider, sourceDb), new MigrationSide(target, provider, targetDb), options);

    private static MetadataSnapshot With(
        MetadataSnapshot s,
        Func<DbObject, bool>? keepObject = null, Func<DbColumn, bool>? keepColumn = null, Func<DbForeignKey, bool>? keepKey = null,
        IEnumerable<DbObject>? addObjects = null, IEnumerable<DbColumn>? addColumns = null, IEnumerable<DbForeignKey>? addKeys = null,
        IEnumerable<DbModule>? modules = null, Func<DbColumn, DbColumn>? changeColumn = null) => new()
    {
        Objects = s.Objects.Where(keepObject ?? (_ => true)).Concat(addObjects ?? []).ToList(),
        Columns = s.Columns.Where(keepColumn ?? (_ => true)).Select(changeColumn ?? (c => c)).Concat(addColumns ?? []).ToList(),
        ForeignKeys = s.ForeignKeys.Where(keepKey ?? (_ => true)).Concat(addKeys ?? []).ToList(),
        Modules = modules?.ToList() ?? s.Modules,
        Indexes = s.Indexes,
        RefreshedAt = s.RefreshedAt
    };

    private static DbColumn Col(string schema, string table, string name, int ordinal, string type, bool nullable = true) =>
        new() { Schema = schema, Table = table, Name = name, Ordinal = ordinal, DataType = type, BaseType = type, IsNullable = nullable };

    private static MigrationItem Item(MigrationScript script, string fullName) => Assert.Single(script.Items, i => i.FullName == fullName);

    [Fact]
    public void Same_schema_needs_no_script()
    {
        var script = Build(Shop(), Shop());

        Assert.False(script.HasChanges);
        Assert.Contains(MigrationScriptBuilder.NothingToDo, script.Script);
        Assert.All(script.Items, i => Assert.Equal(MigrationAction.Unchanged, i.Action));
    }

    [Fact]
    public void Missing_table_is_created_before_the_foreign_keys_that_reference_it()
    {
        var target = With(Shop(), keepObject: o => o.Name != "Products", keepColumn: c => c.Table != "Products",
            keepKey: f => f.Name != "FK_OrderLines_Products");

        var script = Build(Shop(), target);

        Assert.Equal(MigrationAction.Create, Item(script, "dbo.Products").Action);
        Assert.Equal(MigrationAction.Alter, Item(script, "sales.OrderLines").Action);
        var create = script.Script.IndexOf("CREATE TABLE [dbo].[Products]", StringComparison.Ordinal);
        var key = script.Script.IndexOf("ADD CONSTRAINT [FK_OrderLines_Products] FOREIGN KEY ([ProductId])", StringComparison.Ordinal);
        Assert.True(create > 0 && key > create, script.Script);
        Assert.Contains("GO", script.Script);
    }

    [Fact]
    public void Extra_column_on_the_target_is_kept_unless_drops_are_asked_for()
    {
        var target = With(Shop(), keepColumn: c => !(c.Table == "Customers" && c.Name == "Email"),
            addColumns: [Col("dbo", "Customers", "Phone", 4, "nvarchar(30)")]);

        var kept = Build(Shop(), target);
        Assert.True(kept.Script.Contains("ALTER TABLE [dbo].[Customers] ADD [Email] nvarchar(200) NOT NULL;"), kept.Script);
        Assert.DoesNotContain("DROP COLUMN", kept.Script);
        Assert.Equal("1 column(s) added", Item(kept, "dbo.Customers").Details);

        var dropped = Build(Shop(), target, options: new MigrationOptions { DropExtras = true });
        Assert.Contains("ALTER TABLE [dbo].[Customers] DROP COLUMN [Phone];", dropped.Script);
        Assert.Contains(dropped.Warnings, w => w.Contains("Phone", StringComparison.Ordinal));
    }

    [Fact]
    public void Changed_column_type_is_altered()
    {
        var target = With(Shop(), changeColumn: c => c.Table == "Customers" && c.Name == "Name" ? c with { DataType = "nvarchar(50)", BaseType = "nvarchar(50)" } : c);

        var script = Build(Shop(), target);

        Assert.True(script.Script.Contains("ALTER TABLE [dbo].[Customers] ALTER COLUMN [Name] nvarchar(100) NOT NULL;"), script.Script);
        Assert.Equal("1 changed", Item(script, "dbo.Customers").Details);
    }

    [Fact]
    public void Foreign_key_with_another_name_but_the_same_columns_is_not_a_change()
    {
        var target = With(Shop(), keepKey: f => f.Name != "FK_Orders_Customers",
            addKeys: [new DbForeignKey { Name = "FK_Orders_Cust", Schema = "sales", Table = "Orders", Columns = "CustomerId", ReferencedSchema = "dbo", ReferencedTable = "Customers", ReferencedColumns = "CustomerId" }]);

        var script = Build(Shop(), target);

        Assert.False(script.HasChanges, script.Script);
    }

    [Fact]
    public void Table_only_on_the_target_is_listed_and_dropped_only_when_asked()
    {
        var target = With(Shop(), addObjects: [new DbObject { Schema = "dbo", Name = "Legacy", Type = DbObjectType.Table, RowCount = 12 }],
            addColumns: [Col("dbo", "Legacy", "Id", 1, "int", nullable: false)]);

        var kept = Build(Shop(), target);
        Assert.False(kept.HasChanges);
        Assert.Contains("-- DROP TABLE [dbo].[Legacy];", kept.Script);

        var dropped = Build(Shop(), target, options: new MigrationOptions { DropExtras = true });
        Assert.Equal(MigrationAction.Drop, Item(dropped, "dbo.Legacy").Action);
        Assert.Contains("\nDROP TABLE [dbo].[Legacy];", dropped.Script);
        Assert.Contains(dropped.Warnings, w => w.Contains("12 row(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Views_are_created_or_replaced_and_ordered_by_what_they_read()
    {
        var source = With(Shop(),
            addObjects: [new DbObject { Schema = "sales", Name = "vBigOrders", Type = DbObjectType.View }],
            modules:
            [
                new DbModule { Schema = "sales", Name = "vBigOrders", Type = DbObjectType.View, Definition = "CREATE VIEW sales.vBigOrders AS SELECT * FROM sales.vOrderTotals WHERE Total > 100" },
                new DbModule { Schema = "sales", Name = "vOrderTotals", Type = DbObjectType.View, Definition = "CREATE VIEW sales.vOrderTotals AS SELECT OrderId, 2 AS Total FROM sales.Orders" },
                new DbModule { Schema = "dbo", Name = "usp_GetCustomer", Type = DbObjectType.Procedure, Definition = "CREATE PROCEDURE dbo.usp_GetCustomer AS SELECT 1" }
            ]);
        var target = With(Shop(), modules:
        [
            new DbModule { Schema = "sales", Name = "vOrderTotals", Type = DbObjectType.View, Definition = "CREATE VIEW sales.vOrderTotals AS SELECT OrderId, 1 AS Total FROM sales.Orders" },
            new DbModule { Schema = "dbo", Name = "usp_GetCustomer", Type = DbObjectType.Procedure, Definition = "CREATE  PROCEDURE dbo.usp_GetCustomer\nAS SELECT 1" }
        ]);

        var script = Build(source, target);

        Assert.Equal(MigrationAction.Replace, Item(script, "sales.vOrderTotals").Action);
        Assert.Equal(MigrationAction.Create, Item(script, "sales.vBigOrders").Action);
        Assert.Equal(MigrationAction.Unchanged, Item(script, "dbo.usp_GetCustomer").Action);
        var replaced = script.Script.IndexOf("CREATE OR ALTER VIEW sales.vOrderTotals", StringComparison.Ordinal);
        var created = script.Script.IndexOf("CREATE VIEW sales.vBigOrders", StringComparison.Ordinal);
        Assert.True(replaced > 0 && created > replaced, script.Script);
    }

    [Fact]
    public void Module_only_on_the_target_is_dropped_when_asked_except_where_the_name_is_not_enough()
    {
        var target = With(Shop(), modules:
        [
            new DbModule { Schema = "dbo", Name = "fn_Old", Type = DbObjectType.Function, Definition = "CREATE FUNCTION dbo.fn_Old(a int) RETURNS int AS $$ SELECT a $$ LANGUAGE sql" }
        ], addObjects: [new DbObject { Schema = "dbo", Name = "fn_Old", Type = DbObjectType.Function }]);

        var ss = Build(Shop(), target, options: new MigrationOptions { DropExtras = true });
        Assert.Contains("DROP FUNCTION [dbo].[fn_Old];", ss.Script);

        var pg = Build(Shop(), target, Pg, new MigrationOptions { DropExtras = true });
        Assert.Equal(MigrationAction.Manual, Item(pg, "dbo.fn_Old").Action);
        Assert.DoesNotContain("\nDROP FUNCTION", pg.Script);
    }

    [Fact]
    public void Different_engines_are_refused()
    {
        var script = MigrationScriptBuilder.Build(new MigrationSide(Shop(), Ss, null), new MigrationSide(Shop(), Pg, null));

        Assert.Empty(script.Items);
        Assert.Contains("Copy & sync", script.Warnings.Single());
        Assert.DoesNotContain("CREATE", script.Script);
    }

    [Fact]
    public void Only_the_selected_objects_are_scripted()
    {
        var target = With(Shop(), keepObject: o => o.Name is not ("Products" or "Audit"), keepColumn: c => c.Table is not ("Products" or "Audit"),
            keepKey: f => f.Name != "FK_OrderLines_Products");

        var script = Build(Shop(), target, options: new MigrationOptions { Only = [(DbObjectType.Table, "dbo", "Audit")] });

        Assert.Equal(MigrationAction.Create, Item(script, "dbo.Audit").Action);
        Assert.DoesNotContain(script.Items, i => i.Name == "Products" || i.Name == "OrderLines");
        Assert.DoesNotContain("Products", script.Script);
    }

    [Fact]
    public void MySql_tables_land_in_the_target_database()
    {
        static MetadataSnapshot Db(string name, bool withAudit) => new()
        {
            Objects = withAudit
                ? [new DbObject { Database = name, Schema = name, Name = "audit", Type = DbObjectType.Table }, new DbObject { Database = name, Schema = name, Name = "users", Type = DbObjectType.Table }]
                : [new DbObject { Database = name, Schema = name, Name = "users", Type = DbObjectType.Table }],
            Columns =
            [
                new DbColumn { Database = name, Schema = name, Table = "users", Name = "id", Ordinal = 1, DataType = "int", BaseType = "int", IsPrimaryKey = true },
                .. withAudit
                    ? [new DbColumn { Database = name, Schema = name, Table = "audit", Name = "user_id", Ordinal = 1, DataType = "int", BaseType = "int" }]
                    : Array.Empty<DbColumn>()
            ],
            ForeignKeys = withAudit
                ? [new DbForeignKey { Database = name, Name = "fk_audit_user", Schema = name, Table = "audit", Columns = "user_id", ReferencedSchema = name, ReferencedTable = "users", ReferencedColumns = "id" }]
                : [],
            Modules = [],
            Indexes = [],
            RefreshedAt = DateTimeOffset.Now
        };

        var script = Build(Db("shop_dev", true), Db("shop_test", false), My, sourceDb: "shop_dev", targetDb: "shop_test");

        Assert.Equal(MigrationAction.Create, Item(script, "shop_test.audit").Action);
        Assert.Equal(MigrationAction.Unchanged, Item(script, "shop_test.users").Action);
        Assert.Contains("CREATE TABLE `shop_test`.`audit`", script.Script);
        Assert.Contains("REFERENCES `shop_test`.`users`", script.Script);
        Assert.DoesNotContain("shop_dev", script.Script);
    }

    [Fact]
    public void Serial_column_gets_its_sequence_created_first_and_owned_after()
    {
        static MetadataSnapshot Db(bool withNotes) => new()
        {
            Objects = withNotes
                ? [new DbObject { Schema = "public", Name = "notes", Type = DbObjectType.Table }, new DbObject { Schema = "public", Name = "notes_id_seq", Type = DbObjectType.Sequence }]
                : [],
            Columns = withNotes ? [new DbColumn { Schema = "public", Table = "notes", Name = "id", Ordinal = 1, DataType = "integer", BaseType = "integer", IsPrimaryKey = true }] : [],
            ForeignKeys = [],
            Modules = [],
            Indexes = [],
            RefreshedAt = DateTimeOffset.Now
        };
        var source = Db(true);
        var constraints = new Dictionary<DbObject, DbTableConstraints>
        {
            [source.Objects[0]] = new() { Defaults = new Dictionary<string, string> { ["id"] = "nextval('notes_id_seq'::regclass)" } }
        };

        var script = MigrationScriptBuilder.Build(new MigrationSide(source, Pg, null, constraints), new MigrationSide(Db(false), Pg, null));

        var sequence = script.Script.IndexOf("CREATE SEQUENCE IF NOT EXISTS \"public\".\"notes_id_seq\";", StringComparison.Ordinal);
        var table = script.Script.IndexOf("CREATE TABLE \"public\".\"notes\"", StringComparison.Ordinal);
        var owned = script.Script.IndexOf("ALTER SEQUENCE \"public\".\"notes_id_seq\" OWNED BY \"public\".\"notes\".\"id\";", StringComparison.Ordinal);
        Assert.True(sequence > 0 && table > sequence && owned > table, script.Script);
        Assert.DoesNotContain(script.Items, i => i.Action == MigrationAction.Manual);
    }
}
