using System.Globalization;
using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class ResultEditingTests
{
    private static string Bracket(string id) => "[" + id.Replace("]", "]]") + "]";
    private static string DoubleQuote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    private static ResultSource Resolve(string sql, params string[] columns) =>
        ResultSourceResolver.Resolve(sql, columns, TestSnapshots.Shop(), "SqlServer", null)
        ?? throw new Xunit.Sdk.XunitException("no source for: " + sql);

    [Fact]
    public void Star_from_one_table_maps_every_column_and_finds_the_key()
    {
        var s = Resolve("SELECT * FROM sales.Orders", "OrderId", "CustomerId", "OrderDate");

        var table = Assert.Single(s.Tables);
        Assert.Equal("Orders", table.Table.Name);
        Assert.Equal(["OrderId", "CustomerId", "OrderDate"], s.Columns.Select(c => c!.Column.Name));
        Assert.Equal(0, Assert.Single(table.Key).ResultColumn);
        Assert.True(s.CanEdit(1));
        Assert.True(s.CanEdit(2));
        Assert.Null(s.ReadOnlyReason);
    }

    [Fact]
    public void Foreign_key_columns_link_to_the_referenced_table()
    {
        var s = Resolve("SELECT TOP (50) * FROM sales.Orders WHERE OrderId > 3 ORDER BY OrderDate;", "OrderId", "CustomerId", "OrderDate");

        var reference = s.ReferenceOf(1)!;
        Assert.Equal("dbo.Customers", reference.TargetName);
        Assert.Equal((1, "CustomerId"), Assert.Single(reference.Columns));
        Assert.NotNull(reference.ReferencedTable);
        Assert.Null(s.ReferenceOf(0));
        Assert.Null(s.ReferenceOf(2));

        var sql = ResultEditSql.ReferenceSelect(s, reference, [7, 42, null], SqlDialect.SqlServer, Bracket);
        Assert.Equal("SELECT * FROM [dbo].[Customers] WHERE [CustomerId] = 42;", sql);
        Assert.Null(ResultEditSql.ReferenceSelect(s, reference, [7, null, null], SqlDialect.SqlServer, Bracket));
    }

    [Fact]
    public void Joins_aliases_and_expressions_map_per_table()
    {
        var s = Resolve("""
            SELECT o.OrderId, o.CustomerId AS Buyer, c.Name, UPPER(c.Email) AS Email, c.CustomerId
            FROM sales.Orders o
            JOIN dbo.Customers c ON c.CustomerId = o.CustomerId
            """, "OrderId", "Buyer", "Name", "Email", "CustomerId");

        Assert.Equal(2, s.Tables.Count);
        Assert.Equal("CustomerId", s.Columns[1]!.Column.Name);
        Assert.Null(s.Columns[3]); // expression
        Assert.True(s.CanEdit(1));
        Assert.True(s.CanEdit(2)); // Customers' key (CustomerId) is in the result
        Assert.False(s.CanEdit(3));
        Assert.Equal("dbo.Customers", s.ReferenceOf(1)!.TargetName);
    }

    [Fact]
    public void A_table_whose_key_is_missing_is_read_only_but_still_links()
    {
        var s = Resolve("SELECT CustomerId, OrderDate FROM sales.Orders", "CustomerId", "OrderDate");

        Assert.False(s.CanEdit(0));
        Assert.False(s.CanEdit(1));
        Assert.Contains("primary key of sales.Orders", s.ReadOnlyReason);
        Assert.NotNull(s.ReferenceOf(0));
    }

    [Fact]
    public void Composite_keys_and_unqualified_columns_of_joins()
    {
        var s = Resolve("""
            SELECT l.OrderId, LineNo, l.ProductId, Title
            FROM sales.OrderLines l LEFT JOIN dbo.Products p ON p.ProductId = l.ProductId
            """, "OrderId", "LineNo", "ProductId", "Title");

        Assert.Equal(["OrderId", "LineNo"], s.Tables[0].Key.Select(k => k.Column.Name));
        Assert.True(s.CanEdit(2));
        Assert.False(s.CanEdit(3)); // Products' key is not in the result
        Assert.Equal("sales.Orders", s.ReferenceOf(0)!.TargetName);
        Assert.Equal("dbo.Products", s.ReferenceOf(2)!.TargetName);
    }

    [Fact]
    public void Grouping_and_distinct_are_read_only()
    {
        var grouped = Resolve("SELECT CustomerId FROM sales.Orders GROUP BY CustomerId", "CustomerId");
        Assert.False(grouped.CanEdit(0));
        Assert.Contains("GROUP BY", grouped.ReadOnlyReason);
        Assert.NotNull(grouped.ReferenceOf(0)); // the values still identify customers

        Assert.False(Resolve("SELECT DISTINCT OrderId, CustomerId FROM sales.Orders", "OrderId", "CustomerId").CanEdit(1));
    }

    [Fact]
    public void Unknown_or_mismatching_results_get_no_source()
    {
        var snapshot = TestSnapshots.Shop();
        // Column count does not match what * expands to (catalog out of date).
        Assert.Null(ResultSourceResolver.Resolve("SELECT * FROM sales.Orders", ["OrderId", "CustomerId"], snapshot, "SqlServer", null));
        // A name the server reports differently than the statement says.
        Assert.Null(ResultSourceResolver.Resolve("SELECT OrderId AS X FROM sales.Orders", ["Y"], snapshot, "SqlServer", null));
        // Derived tables and set operations.
        Assert.Null(ResultSourceResolver.Resolve("SELECT * FROM (SELECT 1 AS a) d", ["a"], snapshot, "SqlServer", null));
        Assert.Null(ResultSourceResolver.Resolve("SELECT OrderId FROM sales.Orders UNION SELECT 1", ["OrderId"], snapshot, "SqlServer", null));
        // A CTE named like a real table is not that table.
        var cte = ResultSourceResolver.Resolve("WITH Orders AS (SELECT 1 AS OrderId) SELECT OrderId FROM Orders", ["OrderId"], snapshot, "SqlServer", null);
        Assert.True(cte is null || cte.Tables.Count == 0);
    }

    [Fact]
    public void Scripts_match_result_sets_to_their_selects_in_order()
    {
        var snapshot = TestSnapshots.Shop();
        var sources = ResultSourceResolver.ResolveScript(
            "DECLARE @x int = 1;\nSELECT * FROM dbo.Products;\nUPDATE dbo.Audit SET Id = Id WHERE 1 = 0;\nSELECT OrderId, CustomerId FROM sales.Orders;",
            [["ProductId", "Title"], ["OrderId", "CustomerId"]], snapshot, "SqlServer", null);
        Assert.Equal("Products", sources[0]!.Tables[0].Table.Name);
        Assert.Equal("Orders", sources[1]!.Tables[0].Table.Name);

        // A procedure call may add result sets of its own: no guessing.
        var withExec = ResultSourceResolver.ResolveScript(
            "EXEC dbo.usp_GetCustomer 1;\nSELECT * FROM dbo.Products;", [["ProductId", "Title"]], snapshot, "SqlServer", null);
        Assert.Null(withExec[0]);
    }

    [Fact]
    public void Updates_set_changed_columns_and_match_the_original_key()
    {
        var s = Resolve("SELECT o.OrderId, o.OrderDate, c.CustomerId, c.Name FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId",
            "OrderId", "OrderDate", "CustomerId", "Name");
        var original = new object?[] { 5, new DateTime(2024, 1, 2, 3, 4, 5, 6), 9, "O'Brien" };
        var edits = new Dictionary<int, object?> { [1] = new DateTime(2025, 6, 7), [3] = "New 'name'", [0] = 6 };

        var updates = ResultEditSql.BuildUpdates(s, [new ResultRowEdit(original, edits)], SqlDialect.SqlServer, Bracket);

        Assert.Equal(2, updates.Count);
        Assert.Contains(updates, u => u.Sql ==
            "UPDATE [sales].[Orders] SET [OrderDate] = CAST(N'2025-06-07' AS datetime2(7)), [OrderId] = 6 WHERE [OrderId] = 5;");
        Assert.Contains(updates, u => u.Sql == "UPDATE [dbo].[Customers] SET [Name] = N'New ''name''' WHERE [CustomerId] = 9;");
    }

    [Fact]
    public void Updates_refuse_rows_without_a_key_value()
    {
        var s = Resolve("SELECT OrderId, OrderDate FROM sales.Orders", "OrderId", "OrderDate");
        Assert.Throws<InvalidOperationException>(() => ResultEditSql.BuildUpdates(s,
            [new ResultRowEdit([null, null], new Dictionary<int, object?> { [1] = DateTime.Today })], SqlDialect.SqlServer, Bracket));
    }

    [Fact]
    public void Postgres_literals_keep_types_and_utc()
    {
        var snapshot = TestSnapshots.Shop();
        var s = ResultSourceResolver.Resolve("SELECT OrderId, OrderDate FROM sales.Orders", ["orderid", "orderdate"], snapshot, "Postgres", null)!;
        var utc = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var update = Assert.Single(ResultEditSql.BuildUpdates(s,
            [new ResultRowEdit([1, null], new Dictionary<int, object?> { [1] = utc })], SqlDialect.Postgres, DoubleQuote));
        Assert.Equal("UPDATE \"sales\".\"Orders\" SET \"OrderDate\" = '2024-05-06 07:08:09+00' WHERE \"OrderId\" = 1;", update.Sql);

        Assert.Equal("'01:02:03.0000000'", ResultEditSql.Literal(new TimeSpan(1, 2, 3), SqlDialect.Postgres));
        Assert.Equal("TRUE", ResultEditSql.Literal(true, SqlDialect.Postgres));
        Assert.Equal("NULL", ResultEditSql.Literal(null, SqlDialect.Postgres));
    }

    [Fact]
    public void Literals_match_the_stored_value()
    {
        // MySQL reads a backslash as an escape.
        Assert.Equal(@"'\\'", ResultEditSql.Literal('\\', SqlDialect.MySql));
        Assert.Equal(@"'a\\b''c'", ResultEditSql.Literal(@"a\b'c", SqlDialect.MySql));
        Assert.Equal(@"N'\'", ResultEditSql.Literal('\\', SqlDialect.SqlServer));

        // A SQL Server key is cast to its column's own type, so a datetime's .997 equals the stored value.
        var at = new DateTime(2025, 3, 4, 10, 11, 12, 997);
        Assert.Equal("CAST(N'2025-03-04T10:11:12.997' AS datetime)", ResultEditSql.Literal(at, SqlDialect.SqlServer, "datetime"));
        Assert.Equal("CAST(N'2025-03-04T10:11:00.000' AS smalldatetime)",
            ResultEditSql.Literal(new DateTime(2025, 3, 4, 10, 11, 0), SqlDialect.SqlServer, "SmallDateTime"));
        Assert.Equal("CAST(N'2025-03-04' AS date)", ResultEditSql.Literal(new DateTime(2025, 3, 4), SqlDialect.SqlServer, "date"));
        Assert.Equal("CAST(N'2025-03-04 10:11:12.997' AS datetime2(7))", ResultEditSql.Literal(at, SqlDialect.SqlServer, "datetime2"));
        Assert.Equal("CAST(N'2025-03-04 10:11:12.997' AS datetime2(7))", ResultEditSql.Literal(at, SqlDialect.SqlServer));
    }

    [Fact]
    public void Typed_text_parses_back_to_the_cells_type()
    {
        Assert.Equal(42, ResultEditSql.ParseValue(" 42 ", 1, null));
        Assert.Equal(12.5m, ResultEditSql.ParseValue("12.5", 1m, null));
        Assert.Equal(true, ResultEditSql.ParseValue("yes", false, null));
        Assert.Equal(new DateTime(2024, 2, 3, 4, 5, 6), ResultEditSql.ParseValue("2024-02-03 04:05:06", DateTime.Now, null));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)ResultEditSql.ParseValue("2024-02-03", DateTime.UtcNow, null)!).Kind);
        Assert.Equal(new byte[] { 0xAB, 0x01 }, ResultEditSql.ParseValue("0xAB01", new byte[] { 1 }, null));
        Assert.Equal("anything", ResultEditSql.ParseValue("anything", "old", null));
        Assert.Throws<FormatException>(() => ResultEditSql.ParseValue("abc", 1, null));
        Assert.Throws<FormatException>(() => ResultEditSql.ParseValue("2147483648", 1, null));

        // A NULL cell uses the column's type: numbers are checked, text and dates are left to the server.
        var intColumn = new DbColumn { Name = "n", BaseType = "int" };
        Assert.Equal(7L, ResultEditSql.ParseValue("7", null, intColumn));
        Assert.Throws<FormatException>(() => ResultEditSql.ParseValue("x", null, intColumn));
        Assert.Equal("2024-01-01", ResultEditSql.ParseValue("2024-01-01", null, new DbColumn { BaseType = "date" }));
        Assert.Equal("2024-01-01", ResultEditSql.EditText(new DateTime(2024, 1, 1)));
        Assert.Equal("", ResultEditSql.EditText(null));
    }

    private static void InCulture(string name, Action action)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { action(); }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void A_decimal_comma_is_read_in_the_users_culture() => InCulture("de-DE", () =>
    {
        Assert.Equal(1.5m, ResultEditSql.ParseValue("1,5", 1m, null));
        Assert.Equal(1.5, ResultEditSql.ParseValue("1,5", 1.0, null));
        Assert.Equal(1.5f, ResultEditSql.ParseValue("1,5", 1f, null));
        Assert.Equal(1234.5m, ResultEditSql.ParseValue("1.234,5", 1m, null));
        Assert.Equal(1.5m, ResultEditSql.ParseValue("1,5", null, new DbColumn { BaseType = "decimal" }));
        // The editor's own (invariant) text still reads back the same.
        Assert.Equal(12.5m, ResultEditSql.ParseValue(ResultEditSql.EditText(12.5m), 1m, null));
        Assert.Equal(0.1, ResultEditSql.ParseValue(ResultEditSql.EditText(0.1), 1.0, null));
    });

    [Fact]
    public void Group_separators_are_only_read_in_the_users_culture() => InCulture("en-US", () =>
    {
        Assert.Equal(1234.5m, ResultEditSql.ParseValue("1,234.5", 1m, null));
        Assert.Equal(1.5m, ResultEditSql.ParseValue("1.5", 1m, null));
    });

    [Fact]
    public void Dates_are_iso_or_in_the_users_culture() => InCulture("en-GB", () =>
    {
        Assert.Equal(new DateTime(2025, 4, 3), ResultEditSql.ParseValue("03/04/2025", DateTime.Now, null));
        Assert.Equal(new DateOnly(2025, 4, 3), ResultEditSql.ParseValue("03/04/2025", new DateOnly(2000, 1, 1), null));
        Assert.Equal(new DateTime(2025, 3, 4), ResultEditSql.ParseValue("2025-03-04", DateTime.Now, null));
        Assert.Equal(new DateTime(2025, 3, 4, 10, 11, 12, 123), ResultEditSql.ParseValue("2025-03-04T10:11:12.123", DateTime.Now, null));
        Assert.Equal(new DateTime(2025, 3, 4, 10, 11, 12), ResultEditSql.ParseValue("2025-03-04 10:11:12", DateTime.Now, null));
        Assert.Equal(new DateTimeOffset(2025, 3, 4, 10, 0, 0, TimeSpan.FromHours(2)),
            ResultEditSql.ParseValue("2025-03-04 10:00:00+02:00", DateTimeOffset.Now, null));
        var value = new DateTime(2025, 3, 4, 5, 6, 7, 890);
        Assert.Equal(value, ResultEditSql.ParseValue(ResultEditSql.EditText(value), DateTime.Now, null));
    });
}
