using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class SqlCompletionEngineTests
{
    private readonly SqlCompletionEngine _engine = new(TestSnapshots.Shop(), id => "[" + id + "]");

    private CompletionResult CompleteAtEnd(string sql, bool explicitRequest = false) =>
        _engine.Complete(sql, sql.Length, explicitRequest);

    private CompletionResult CompleteAtMarker(string sqlWithMarker)
    {
        var caret = sqlWithMarker.IndexOf('|');
        return _engine.Complete(sqlWithMarker.Remove(caret, 1), caret);
    }

    [Fact]
    public void Alias_dot_lists_only_that_tables_columns()
    {
        var result = CompleteAtEnd("SELECT * FROM sales.Orders o WHERE o.");

        Assert.Equal(["OrderId", "CustomerId", "OrderDate"], result.Items.Select(i => i.Label));
        Assert.All(result.Items, i => Assert.Equal(CompletionKind.Column, i.Kind));
    }

    [Fact]
    public void Alias_defined_after_the_caret_is_resolved()
    {
        var result = CompleteAtMarker("SELECT c.Na| FROM dbo.Customers AS c");

        var item = Assert.Single(result.Items);
        Assert.Equal("Name", item.Label);
        Assert.Equal("SELECT c.".Length, result.ReplaceStart);
    }

    [Fact]
    public void Join_aliases_are_all_recognised()
    {
        var sql = "SELECT * FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId JOIN sales.OrderLines ol ON ol.";
        var result = CompleteAtEnd(sql);
        Assert.Contains(result.Items, i => i.Label == "ProductId");
        Assert.DoesNotContain(result.Items, i => i.Label == "Email");
    }

    [Fact]
    public void Keywords_are_not_mistaken_for_aliases()
    {
        var refs = _engine.ParseReferences("SELECT * FROM dbo.Customers WHERE Name = 'x'");
        var r = Assert.Single(refs);
        Assert.Null(r.Alias);
    }

    [Fact]
    public void Schema_dot_lists_objects_in_that_schema()
    {
        var result = CompleteAtEnd("SELECT * FROM sales.");
        Assert.Equal(["OrderLines", "Orders", "vOrderTotals"], result.Items.Select(i => i.Label).OrderBy(x => x));
    }

    [Fact]
    public void Table_name_dot_lists_columns_without_alias()
    {
        var result = CompleteAtEnd("SELECT Customers.");
        Assert.Contains(result.Items, i => i.Label == "Email");
    }

    [Fact]
    public void Unqualified_prefix_ranks_columns_of_referenced_tables_first()
    {
        var result = CompleteAtMarker("SELECT Or| FROM sales.Orders");
        Assert.Equal("OrderId", result.Items[0].Label);
        Assert.Equal(CompletionKind.Column, result.Items[0].Kind);
        Assert.Contains(result.Items, i => i.Kind == CompletionKind.Keyword && i.Label == "ORDER BY");
        Assert.Contains(result.Items, i => i.Kind == CompletionKind.Table && i.InsertText == "sales.Orders");
    }

    [Fact]
    public void Identifiers_needing_quotes_are_quoted_on_insert()
    {
        var result = CompleteAtEnd("SELECT n.No", explicitRequest: false);
        Assert.Empty(result.Items);

        var withAlias = _engine.Complete("SELECT n. FROM dbo.[Order Notes] n", "SELECT n.".Length);
        var item = Assert.Single(withAlias.Items);
        Assert.Equal("[Note Text]", item.InsertText);
    }

    [Fact]
    public void Empty_prefix_returns_nothing_unless_explicitly_requested()
    {
        Assert.Empty(CompleteAtEnd("SELECT ").Items);
        Assert.NotEmpty(CompleteAtEnd("SELECT ", explicitRequest: true).Items);
    }

    [Fact]
    public void Aliases_do_not_leak_across_statements()
    {
        var result = CompleteAtEnd("SELECT * FROM sales.Orders o;\nSELECT o.");
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Bracketed_qualifiers_are_understood()
    {
        var result = CompleteAtEnd("SELECT * FROM [sales].[Orders] AS [o] WHERE [o].Ord");
        Assert.Equal(["OrderId", "OrderDate"], result.Items.Select(i => i.Label));
    }
}
