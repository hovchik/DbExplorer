using DbExplorer.Application.Diagram;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class ErDiagramBuilderTests
{
    private static readonly Application.Metadata.MetadataSnapshot Snapshot = TestSnapshots.Shop();
    private static DbObject Obj(string name) => Snapshot.Objects.Single(o => o.Name == name);

    [Fact]
    public void Depth_one_includes_direct_parents_and_children_only()
    {
        var d = ErDiagramBuilder.AroundTable(Snapshot, Obj("Orders"), depth: 1, ErColumnMode.KeysOnly);

        Assert.Equal(["Customers", "OrderLines", "Orders"], d.Tables.Select(t => t.Object.Name).Order());
        Assert.Equal(2, d.Edges.Count);
        Assert.True(d.Tables.Single(t => t.Object.Name == "Orders").IsFocus);
    }

    [Fact]
    public void Parents_are_laid_out_left_of_children()
    {
        var d = ErDiagramBuilder.AroundTable(Snapshot, Obj("Orders"), depth: 2, ErColumnMode.KeysOnly);
        double X(string n) => d.Tables.Single(t => t.Object.Name == n).X;

        Assert.Contains(d.Tables, t => t.Object.Name == "Products");
        Assert.True(X("Customers") < X("Orders"));
        Assert.True(X("Orders") < X("OrderLines"));
        Assert.True(X("Products") < X("OrderLines"));
        Assert.DoesNotContain(d.Tables, t => t.Object.Name == "Audit");
    }

    [Fact]
    public void Tables_in_one_column_do_not_overlap()
    {
        var d = ErDiagramBuilder.WholeSchema(Snapshot, database: null, schema: null, ErColumnMode.AllColumns);
        foreach (var column in d.Tables.GroupBy(t => t.X))
        {
            var ordered = column.OrderBy(t => t.Y).ToList();
            for (var i = 1; i < ordered.Count; i++)
                Assert.True(ordered[i].Y >= ordered[i - 1].Y + ordered[i - 1].Height);
        }
        Assert.True(d.Width >= d.Tables.Max(t => t.X + t.Width));
        Assert.True(d.Height >= d.Tables.Max(t => t.Y + t.Height));
    }

    [Fact]
    public void Column_modes_filter_columns()
    {
        var keys = ErDiagramBuilder.AroundTable(Snapshot, Obj("Customers"), 0, ErColumnMode.KeysOnly).Tables.Single();
        Assert.Equal(["CustomerId"], keys.Columns.Select(c => c.Name));
        Assert.Equal(2, keys.HiddenColumnCount);

        var none = ErDiagramBuilder.AroundTable(Snapshot, Obj("Customers"), 0, ErColumnMode.NoColumns).Tables.Single();
        Assert.Empty(none.Columns);
        Assert.Equal(ErDiagramBuilder.HeaderHeight, none.Height);
    }

    [Fact]
    public void Max_tables_cap_is_reported()
    {
        var d = ErDiagramBuilder.AroundTable(Snapshot, Obj("OrderLines"), depth: 3, ErColumnMode.KeysOnly, maxTables: 2);
        Assert.Equal(2, d.Tables.Count);
        Assert.True(d.OmittedTables > 0);
    }

    [Fact]
    public void Whole_schema_filters_by_schema()
    {
        var d = ErDiagramBuilder.WholeSchema(Snapshot, null, "sales", ErColumnMode.KeysOnly);
        Assert.Equal(["OrderLines", "Orders"], d.Tables.Select(t => t.Object.Name).Order());
        Assert.Single(d.Edges);
    }

    [Fact]
    public void Views_have_no_diagram() =>
        Assert.Same(ErDiagram.Empty, ErDiagramBuilder.AroundTable(Snapshot, Obj("vOrderTotals"), 2, ErColumnMode.KeysOnly));

    [Fact]
    public void Mermaid_output_is_well_formed()
    {
        var d = ErDiagramBuilder.WholeSchema(Snapshot, null, null, ErColumnMode.AllColumns);
        var mermaid = ErDiagramBuilder.ToMermaid(d);

        Assert.StartsWith("erDiagram\n", mermaid);
        Assert.Contains("dbo_Customers[\"dbo.Customers\"] {", mermaid);
        Assert.Contains("int CustomerId PK", mermaid);
        Assert.Contains("nvarchar(100) Name", mermaid);
        Assert.Contains("dbo_Order_Notes[\"dbo.Order Notes\"]", mermaid);
        Assert.Contains("nvarchar(max) Note_Text", mermaid);
        Assert.Contains("dbo_Customers ||--o{ sales_Orders : \"FK_Orders_Customers\"", mermaid);
    }
}
