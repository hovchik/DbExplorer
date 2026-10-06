using DbExplorer.Application.Copy;
using DbExplorer.Application.Diagram;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class SchemaSuggesterTests
{
    /// <summary>The shop schema without its FK_Orders_Customers constraint, plus an Invoices table whose CustomerId is a bigint.</summary>
    private static MetadataSnapshot MissingKeys()
    {
        var shop = TestSnapshots.Shop();
        return new MetadataSnapshot
        {
            Objects = [.. shop.Objects, new DbObject { Schema = "dbo", Name = "Invoices", Type = DbObjectType.Table }],
            Columns =
            [
                .. shop.Columns,
                new DbColumn { Schema = "dbo", Table = "Invoices", Name = "InvoiceId", Ordinal = 1, DataType = "int", BaseType = "int", IsPrimaryKey = true },
                new DbColumn { Schema = "dbo", Table = "Invoices", Name = "CustomerId", Ordinal = 2, DataType = "bigint", BaseType = "bigint" }
            ],
            Modules = [],
            ForeignKeys = shop.ForeignKeys.Where(f => f.Name != "FK_Orders_Customers").ToList(),
            Indexes = [],
            RefreshedAt = shop.RefreshedAt
        };
    }

    [Fact]
    public void Missing_foreign_keys_are_suggested_and_drawn_as_suggested_edges()
    {
        var s = SchemaSuggester.Suggest(MissingKeys(), SqlDialect.SqlServerKey, null, null, ErColumnMode.KeysOnly);

        Assert.Equal(["dbo.Invoices(CustomerId)", "sales.Orders(CustomerId)"], s.ForeignKeys.Select(f => f.Child).Order());
        Assert.All(s.ForeignKeys, f => Assert.Equal("dbo.Customers(CustomerId)", f.Parent));

        var suggested = s.Diagram.Edges.Where(e => e.IsSuggested).ToList();
        Assert.Equal(2, suggested.Count);
        Assert.All(suggested, e => Assert.Equal("Customers", e.Parent.Object.Name));
        // Declared constraints stay ordinary edges.
        Assert.Equal(2, s.Diagram.Edges.Count(e => !e.IsSuggested));
        // The child column now shows as a key column, so the edge attaches to its row.
        Assert.Contains(s.Diagram.Tables.Single(t => t.Object.Name == "Orders").Columns, c => c.Name == "CustomerId" && c.IsForeignKey);
    }

    [Fact]
    public void Script_adds_each_constraint_and_flags_type_mismatches()
    {
        var s = SchemaSuggester.Suggest(MissingKeys(), SqlDialect.SqlServerKey, null, null, ErColumnMode.KeysOnly);

        Assert.Contains("ALTER TABLE [sales].[Orders] ADD CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customers] ([CustomerId]);", s.Script);
        Assert.Contains("ALTER TABLE [dbo].[Invoices] ADD CONSTRAINT [FK_Invoices_Customers] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customers] ([CustomerId]);", s.Script);
        Assert.Contains("-- Types differ (CustomerId bigint, CustomerId int): align them first.", s.Script);
        Assert.Null(s.ForeignKeys.Single(f => f.ForeignKey.Table == "Orders").TypeWarning);
        Assert.Contains("Nothing here has been run", s.Script);
    }

    [Fact]
    public void Postgres_script_uses_double_quotes()
    {
        var s = SchemaSuggester.Suggest(MissingKeys(), SqlDialect.PostgresKey, null, "sales", ErColumnMode.KeysOnly);
        // Only sales is in scope, and Customers lives in dbo: nothing to suggest there.
        Assert.Empty(s.ForeignKeys);
        Assert.Contains("No missing foreign keys found", s.Script);

        var all = SchemaSuggester.Suggest(MissingKeys(), SqlDialect.PostgresKey, null, null, ErColumnMode.KeysOnly);
        Assert.Contains("ALTER TABLE \"sales\".\"Orders\" ADD CONSTRAINT \"FK_Orders_Customers\" FOREIGN KEY (\"CustomerId\") REFERENCES \"dbo\".\"Customers\" (\"CustomerId\");", all.Script);
    }

    [Fact]
    public void Complete_schema_has_no_suggestions()
    {
        var s = SchemaSuggester.Suggest(TestSnapshots.Shop(), SqlDialect.SqlServerKey, null, null, ErColumnMode.KeysOnly);
        Assert.Empty(s.ForeignKeys);
        Assert.DoesNotContain(s.Diagram.Edges, e => e.IsSuggested);
        Assert.Equal(3, s.Diagram.Edges.Count);
    }

    [Fact]
    public void Accepted_lab_relationships_are_suggested_as_constraints_with_unique_names()
    {
        var accepted = new DbForeignKey
        {
            Name = "inferred_Orders_CustomerId", Schema = "sales", Table = "Orders", Columns = "CustomerId",
            ReferencedSchema = "dbo", ReferencedTable = "Customers", ReferencedColumns = "CustomerId", IsVirtual = true
        };
        var snapshot = MissingKeys().WithVirtualForeignKeys([accepted]);
        var s = SchemaSuggester.Suggest(snapshot, SqlDialect.SqlServerKey, null, null, ErColumnMode.KeysOnly);

        var orders = s.ForeignKeys.Single(f => f.ForeignKey.Table == "Orders");
        Assert.True(orders.Accepted);
        Assert.Equal("FK_Orders_Customers", orders.ForeignKey.Name);
        Assert.Contains("relationship you accepted in the Lab", s.Script);
        Assert.Equal(2, s.ForeignKeys.Count);
        Assert.Equal(2, s.Diagram.Edges.Count(e => e.IsSuggested));
    }

    [Fact]
    public void Taken_constraint_names_fall_back_to_the_column()
    {
        var snapshot = MissingKeys();
        snapshot = new MetadataSnapshot
        {
            Objects = snapshot.Objects, Columns = snapshot.Columns, Modules = [], Indexes = [], RefreshedAt = snapshot.RefreshedAt,
            ForeignKeys = [.. snapshot.ForeignKeys, snapshot.ForeignKeys[0] with { Name = "FK_Orders_Customers" }]
        };
        var s = SchemaSuggester.Suggest(snapshot, SqlDialect.SqlServerKey, null, null, ErColumnMode.KeysOnly);
        Assert.Equal("FK_Orders_CustomerId", s.ForeignKeys.Single(f => f.ForeignKey.Table == "Orders").ForeignKey.Name);
    }
}
