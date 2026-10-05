using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class QueryTabNamerTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Customers", "Customers")]
    [InlineData("select c.Name from [sales].[Order Lines] c join dbo.Products p on p.Id = c.ProductId", "Order Lines")]
    [InlineData("SELECT \"id\" FROM public.\"Invoices\" WHERE id = 1;", "Invoices")]
    [InlineData("WITH recent AS (SELECT * FROM Orders) SELECT * FROM recent r JOIN Customers c ON c.Id = r.CustomerId", "recent")]
    [InlineData("SELECT * FROM (SELECT * FROM Orders) o", "SELECT")]
    [InlineData("SELECT (SELECT COUNT(*) FROM Orders) AS n", "SELECT")]
    [InlineData("SELECT 1", "SELECT")]
    public void Selects_are_named_after_their_main_table(string sql, string expected) =>
        Assert.Equal(expected, QueryTabNamer.Suggest(sql));

    [Theory]
    [InlineData("INSERT INTO dbo.Orders (Id) VALUES (1)", "INSERT Orders")]
    [InlineData("UPDATE TOP (10) Orders SET Status = 1 WHERE Id = 2", "UPDATE Orders")]
    [InlineData("DELETE FROM Orders WHERE Id = 2", "DELETE Orders")]
    [InlineData("MERGE INTO dbo.Stock AS t USING src s ON 1 = 0 WHEN NOT MATCHED THEN INSERT (Id) VALUES (s.Id);", "MERGE Stock")]
    [InlineData("TRUNCATE TABLE Logs", "TRUNCATE Logs")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.usp_Report AS SELECT 1", "CREATE usp_Report")]
    [InlineData("DROP TABLE IF EXISTS tmp.Scratch", "DROP Scratch")]
    [InlineData("EXEC @rc = dbo.usp_Report @Year = 2024", "EXEC usp_Report")]
    public void Changes_name_the_verb_and_the_table(string sql, string expected) =>
        Assert.Equal(expected, QueryTabNamer.Suggest(sql));

    [Fact]
    public void Setup_statements_are_skipped_for_the_one_that_does_the_work()
    {
        Assert.Equal("Customers", QueryTabNamer.Suggest("USE Sales;\nDECLARE @id int = 1;\nSELECT * FROM Customers WHERE Id = @id;"));
        Assert.Equal("DECLARE", QueryTabNamer.Suggest("DECLARE @x int = 1;\nSET @x = 2;"));
    }

    [Fact]
    public void Leading_comments_are_ignored_and_empty_scripts_have_no_name()
    {
        Assert.Equal("Orders", QueryTabNamer.Suggest("-- last week's orders\n/* draft */ SELECT * FROM Orders"));
        Assert.Null(QueryTabNamer.Suggest("  \n-- nothing yet\n"));
    }

    [Fact]
    public void Long_names_are_shortened()
    {
        var name = QueryTabNamer.Suggest("SELECT * FROM " + new string('x', 80))!;
        Assert.Equal(QueryTabNamer.MaxLength, name.Length);
        Assert.EndsWith("…", name);
    }
}
