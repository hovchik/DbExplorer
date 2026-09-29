using DbExplorer.Application.Diagram;
using DbExplorer.Application.Search;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class DataMatchSqlTests
{
    private static readonly Application.Metadata.MetadataSnapshot Snapshot = TestSnapshots.Shop();
    private static DbObject Obj(string name) => Snapshot.Objects.Single(o => o.Name == name);
    private static string Brackets(string id) => "[" + id + "]";
    private static string Quotes(string id) => "\"" + id + "\"";

    private static DataMatch Match(string schema, string table, string column, string? value, params (string Key, string? Value)[] keys) => new()
    {
        Schema = schema,
        Table = table,
        Column = column,
        Value = value,
        RowKey = keys.Length == 0 ? null : string.Join(", ", keys.Select(k => $"{k.Key}={k.Value ?? "NULL"}")),
        KeyValues = keys.Select(k => new KeyValuePair<string, string?>(k.Key, k.Value)).ToList()
    };

    [Fact]
    public void Row_predicate_uses_the_primary_key_and_escapes_values()
    {
        var m = Match("sales", "OrderLines", "ProductId", "7", ("OrderId", "42"), ("LineNo", "O'1"));

        Assert.Equal("[OrderId] = N'42' AND [LineNo] = N'O''1'", DataMatchSql.RowPredicate(m, "SqlServer", Brackets));
        Assert.Equal("t1.\"OrderId\" = '42' AND t1.\"LineNo\" = 'O''1'", DataMatchSql.RowPredicate(m, "Postgres", Quotes, "t1"));
    }

    [Fact]
    public void Row_predicate_falls_back_to_the_matched_value_without_a_key()
    {
        var m = Match("dbo", "Order Notes", "Note Text", "call back");
        Assert.Equal("[Note Text] = N'call back'", DataMatchSql.RowPredicate(m, "SqlServer", Brackets));
        Assert.Equal("[Note Text] IS NULL", DataMatchSql.RowPredicate(m with { Value = null }, "SqlServer", Brackets));
    }

    [Fact]
    public void Select_rows_limits_per_dialect_and_dedupes_rows()
    {
        var a = Match("sales", "Orders", "OrderId", "42", ("OrderId", "42"));
        var b = a with { Column = "CustomerId" };

        Assert.Equal("SELECT TOP (10) * FROM [sales].[Orders] WHERE [OrderId] = N'42';",
            DataMatchSql.SelectRows(Obj("Orders"), [a, b], "SqlServer", Brackets, 10));
        Assert.Equal("SELECT * FROM \"sales\".\"Orders\" WHERE \"OrderId\" = '42'\n LIMIT 10;",
            DataMatchSql.SelectRows(Obj("Orders"), [a], "Postgres", Quotes, 10));
    }

    [Fact]
    public void Diagram_for_tables_adds_linking_tables_and_highlights_the_found_ones()
    {
        // Customers and Products are only linked through Orders → OrderLines.
        var d = ErDiagramBuilder.ForTables(Snapshot, [Obj("Customers"), Obj("Products"), Obj("Audit")], ErColumnMode.KeysOnly, includeNeighbours: false);

        Assert.Equal(["Audit", "Customers", "OrderLines", "Orders", "Products"], d.Tables.Select(t => t.Object.Name).Order());
        Assert.Equal(["Audit", "Customers", "Products"], d.Tables.Where(t => t.IsFocus).Select(t => t.Object.Name).Order());
        Assert.Equal(["OrderLines", "Orders"], d.Tables.Where(t => t.IsLink).Select(t => t.Object.Name).Order());
        Assert.Equal(3, d.Edges.Count);
        Assert.All(d.Edges, e => Assert.Contains(e.Child, d.Tables));
    }

    [Fact]
    public void Diagram_for_tables_includes_directly_related_tables()
    {
        var d = ErDiagramBuilder.ForTables(Snapshot, [Obj("Orders")], ErColumnMode.KeysOnly);

        Assert.Equal(["Customers", "OrderLines", "Orders"], d.Tables.Select(t => t.Object.Name).Order());
        Assert.Equal("Orders", Assert.Single(d.Tables, t => t.IsFocus).Object.Name);
        Assert.DoesNotContain(d.Tables, t => t.IsLink);
    }

    [Fact]
    public void Diagram_for_tables_spans_databases_without_mixing_same_named_tables()
    {
        var snapshot = TestSnapshots.TwoDatabases();
        var tables = snapshot.Objects.Where(o => o.Name == "Transactions").ToList();

        var d = ErDiagramBuilder.ForTables(snapshot, tables, ErColumnMode.KeysOnly);

        Assert.Equal(["Billing.dbo.Invoices", "Billing.dbo.Transactions", "Sales.dbo.Transactions"], d.Tables.Select(t => t.Title).Order());
        var edge = Assert.Single(d.Edges);
        Assert.Equal("Billing.dbo.Transactions", edge.Child.Title);
    }

    [Fact]
    public void Diagram_for_tables_respects_the_hop_limit()
    {
        var d = ErDiagramBuilder.ForTables(Snapshot, [Obj("Customers"), Obj("Products")], ErColumnMode.KeysOnly, includeNeighbours: false, maxHops: 2);
        Assert.Equal(["Customers", "Products"], d.Tables.Select(t => t.Object.Name).Order());
        Assert.Empty(d.Edges);
    }

    [Fact]
    public void Related_queries_follow_every_foreign_key_of_the_found_rows()
    {
        var matches = new[] { Match("sales", "Orders", "OrderId", "42", ("OrderId", "42")) };
        var d = ErDiagramBuilder.ForTables(Snapshot, [Obj("Orders")], ErColumnMode.KeysOnly);

        var queries = DataMatchSql.Related(d, matches, "SqlServer", Brackets, 100);

        Assert.Equal(2, queries.Count);
        var parent = Assert.Single(queries, q => q.Tables[0].Name == "Customers");
        Assert.Equal("Customers · referenced by Orders.CustomerId", parent.Title);
        Assert.Equal(
            "SELECT TOP (100) r.* FROM [dbo].[Customers] r\n WHERE EXISTS (SELECT 1 FROM [sales].[Orders] f\n" +
            "                WHERE f.[CustomerId] = r.[CustomerId]\n                  AND (f.[OrderId] = N'42'));",
            parent.Sql);
        var child = Assert.Single(queries, q => q.Tables[0].Name == "OrderLines");
        Assert.Equal("OrderLines · referencing Orders by OrderId", child.Title);
        Assert.Contains("WHERE r.[OrderId] = f.[OrderId]", child.Sql);
    }

    [Fact]
    public void Combined_query_joins_related_tables_from_the_top_parent()
    {
        var matches = new[]
        {
            Match("sales", "Orders", "OrderId", "42", ("OrderId", "42")),
            Match("sales", "OrderLines", "OrderId", "42", ("OrderId", "42"), ("LineNo", "1")),
            Match("dbo", "Audit", "Id", "42", ("Id", "42"))
        };
        var d = ErDiagramBuilder.ForTables(Snapshot, [Obj("Orders"), Obj("OrderLines"), Obj("Audit")], ErColumnMode.KeysOnly, includeNeighbours: false);

        var queries = DataMatchSql.Combined(Snapshot, d, matches, "SqlServer", Brackets, 500);

        var q = Assert.Single(queries); // Audit is not related, so it gets no combined query
        Assert.Equal("Combined: Orders + OrderLines", q.Title);
        Assert.Equal(["Orders", "OrderLines"], q.Tables.Select(t => t.Name));
        Assert.StartsWith("SELECT TOP (500) t0.[OrderId] AS [Orders.OrderId]", q.Sql);
        Assert.Contains("FROM [sales].[Orders] t0\n  LEFT JOIN [sales].[OrderLines] t1 ON t1.[OrderId] = t0.[OrderId]", q.Sql);
        Assert.Contains("WHERE t0.[OrderId] = N'42'\n    OR (t1.[OrderId] = N'42' AND t1.[LineNo] = N'1')", q.Sql);
    }

    [Fact]
    public void Combined_query_adds_referenced_lookup_tables_but_not_other_children()
    {
        var matches = new[] { Match("sales", "OrderLines", "ProductId", "7", ("OrderId", "1"), ("LineNo", "2")) };
        var d = ErDiagramBuilder.ForTables(Snapshot, [Obj("OrderLines")], ErColumnMode.KeysOnly);

        var q = Assert.Single(DataMatchSql.Combined(Snapshot, d, matches, "SqlServer", Brackets, 100));

        Assert.Equal(["OrderLines", "Orders", "Products"], q.Tables.Select(t => t.Name));
        Assert.DoesNotContain("Customers", q.Sql); // two hops away: not a direct neighbour
    }

    [Fact]
    public void Combined_query_goes_through_linking_tables()
    {
        var matches = new[]
        {
            Match("dbo", "Customers", "CustomerId", "7", ("CustomerId", "7")),
            Match("dbo", "Products", "ProductId", "7", ("ProductId", "7"))
        };
        var d = ErDiagramBuilder.ForTables(Snapshot, [Obj("Customers"), Obj("Products")], ErColumnMode.KeysOnly);

        var q = Assert.Single(DataMatchSql.Combined(Snapshot, d, matches, "Postgres", Quotes, 100));

        Assert.Equal(["Customers", "Orders", "OrderLines", "Products"], q.Tables.Select(t => t.Name));
        Assert.Contains("LEFT JOIN \"sales\".\"OrderLines\" t2 ON t2.\"OrderId\" = t1.\"OrderId\"", q.Sql);
        Assert.Contains("LEFT JOIN \"dbo\".\"Products\" t3 ON t2.\"ProductId\" = t3.\"ProductId\"", q.Sql);
        Assert.EndsWith("\n LIMIT 100;", q.Sql);
    }
}
