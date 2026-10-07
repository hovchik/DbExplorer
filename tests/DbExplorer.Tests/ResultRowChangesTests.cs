using DbExplorer.Application.Export;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

/// <summary>Adding and deleting result rows: which results allow it, and the INSERT / DELETE statements it builds.</summary>
public class ResultRowChangesTests
{
    private static string Bracket(string id) => "[" + id.Replace("]", "]]") + "]";
    private static string DoubleQuote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    private static ResultSource Resolve(string sql, MetadataSnapshot snapshot, string provider, params string[] columns) =>
        ResultSourceResolver.Resolve(sql, columns, snapshot, provider, null)
        ?? throw new Xunit.Sdk.XunitException("no source for: " + sql);

    private static ResultSource Resolve(string sql, params string[] columns) => Resolve(sql, TestSnapshots.Shop(), "SqlServer", columns);

    /// <summary>The shop schema with sales.Orders.OrderId as an identity column.</summary>
    private static MetadataSnapshot ShopWithIdentity()
    {
        var shop = TestSnapshots.Shop();
        return new MetadataSnapshot
        {
            Objects = shop.Objects,
            Columns = shop.Columns.Select(c => c is { Table: "Orders", Name: "OrderId" } ? c with { IsIdentity = true } : c).ToList(),
            Modules = shop.Modules,
            ForeignKeys = shop.ForeignKeys,
            Indexes = shop.Indexes,
            RefreshedAt = shop.RefreshedAt
        };
    }

    private static Dictionary<int, object?> Values(params (int Column, object? Value)[] values) => values.ToDictionary(v => v.Column, v => v.Value);

    [Fact]
    public void Rows_of_a_single_keyed_table_can_be_added_and_deleted()
    {
        var s = Resolve("SELECT * FROM sales.Orders WHERE OrderId > 3", "OrderId", "CustomerId", "OrderDate");

        Assert.Equal("Orders", s.RowTable?.Table.Name);
        Assert.Null(s.RowEditReason);
        Assert.True(s.CanSetInNewRow(0)); // a plain key column can be typed
        Assert.True(s.CanSetInNewRow(2));
    }

    [Fact]
    public void Joins_missing_keys_and_grouping_say_why_rows_cannot_be_added_or_deleted()
    {
        var join = Resolve("SELECT o.OrderId, c.CustomerId, c.Name FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId",
            "OrderId", "CustomerId", "Name");
        Assert.Null(join.RowTable);
        Assert.Contains("one table", join.RowEditReason);
        Assert.Contains("sales.Orders", join.RowEditReason);

        var noKey = Resolve("SELECT CustomerId, OrderDate FROM sales.Orders", "CustomerId", "OrderDate");
        Assert.Null(noKey.RowTable);
        Assert.Contains("primary key", noKey.RowEditReason);

        var grouped = Resolve("SELECT CustomerId FROM sales.Orders GROUP BY CustomerId", "CustomerId");
        Assert.Null(grouped.RowTable);
        Assert.Contains("GROUP BY", grouped.RowEditReason);

        Assert.Throws<InvalidOperationException>(() =>
            ResultEditSql.BuildDeletes(join, [new object?[] { 1, 2, "x" }], SqlDialect.SqlServer, Bracket));
    }

    [Fact]
    public void Only_the_columns_of_one_table_count_even_when_it_is_joined()
    {
        var s = Resolve("SELECT o.* FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId",
            "OrderId", "CustomerId", "OrderDate");
        Assert.Equal("Orders", s.RowTable?.Table.Name);
    }

    [Fact]
    public void Deletes_match_each_row_by_the_key_it_was_read_with()
    {
        var s = Resolve("SELECT OrderId, LineNo, ProductId FROM sales.OrderLines", "OrderId", "LineNo", "ProductId");

        var deletes = ResultEditSql.BuildDeletes(s, [new object?[] { 5, 2, 9 }, new object?[] { 6, 1, null }], SqlDialect.SqlServer, Bracket);

        Assert.Equal(
            ["DELETE FROM [sales].[OrderLines] WHERE [OrderId] = 5 AND [LineNo] = 2;", "DELETE FROM [sales].[OrderLines] WHERE [OrderId] = 6 AND [LineNo] = 1;"],
            deletes.Select(d => d.Sql));
        Assert.All(deletes, d => Assert.Equal(ResultChangeKind.Delete, d.Kind));
        Assert.Throws<InvalidOperationException>(() =>
            ResultEditSql.BuildDeletes(s, [new object?[] { 5, null, 9 }], SqlDialect.SqlServer, Bracket));
    }

    [Fact]
    public void Postgres_inserts_set_typed_values_and_return_the_stored_row()
    {
        var s = Resolve("SELECT OrderId, OrderDate, CustomerId FROM sales.Orders", TestSnapshots.Shop(), "Postgres", "OrderId", "OrderDate", "CustomerId");

        var inserts = ResultEditSql.BuildInserts(s, [Values((0, 7), (2, 42)), Values((1, null))], SqlDialect.Postgres, DoubleQuote);

        Assert.Equal(
            """INSERT INTO "sales"."Orders" ("OrderId", "CustomerId") VALUES (7, 42) RETURNING "OrderId", "OrderDate", "CustomerId";""",
            inserts[0].Sql);
        // Nothing typed: every column takes its default.
        Assert.Equal("""INSERT INTO "sales"."Orders" DEFAULT VALUES RETURNING "OrderId", "OrderDate", "CustomerId";""", inserts[1].Sql);
        Assert.Equal([[0], [1], [2]], inserts[0].ReturnedColumns.Select(c => c.ToArray()));
        Assert.All(inserts, i => Assert.Equal(ResultChangeKind.Insert, i.Kind));
    }

    [Fact]
    public void Sql_server_inserts_read_the_row_back_by_typed_key_or_identity()
    {
        var typed = Resolve("SELECT * FROM dbo.Customers", "CustomerId", "Name", "Email");
        var insert = Assert.Single(ResultEditSql.BuildInserts(typed, [Values((0, 9), (1, "O'Brien"))], SqlDialect.SqlServer, Bracket));
        Assert.Equal(
            "INSERT INTO [dbo].[Customers] ([CustomerId], [Name]) VALUES (9, N'O''Brien');\n" +
            "SELECT [CustomerId], [Name], [Email] FROM [dbo].[Customers] WHERE [CustomerId] = 9;",
            insert.Sql);
        Assert.Equal(3, insert.ReturnedColumns.Count);

        var identity = Resolve("SELECT * FROM sales.Orders", ShopWithIdentity(), "SqlServer", "OrderId", "CustomerId", "OrderDate");
        Assert.False(identity.CanSetInNewRow(0));
        var generated = Assert.Single(ResultEditSql.BuildInserts(identity, [Values((1, 42))], SqlDialect.SqlServer, Bracket));
        Assert.Equal(
            "INSERT INTO [sales].[Orders] ([CustomerId]) VALUES (42);\n" +
            "SELECT [OrderId], [CustomerId], [OrderDate] FROM [sales].[Orders] WHERE [OrderId] = SCOPE_IDENTITY();",
            generated.Sql);

        // An identity value cannot be typed into a new row.
        Assert.Throws<InvalidOperationException>(() =>
            ResultEditSql.BuildInserts(identity, [Values((0, 5))], SqlDialect.SqlServer, Bracket));
    }

    [Fact]
    public void Sql_server_inserts_without_the_key_are_not_read_back()
    {
        var s = Resolve("SELECT * FROM dbo.Customers", "CustomerId", "Name", "Email");
        var insert = Assert.Single(ResultEditSql.BuildInserts(s, [Values((1, "Acme"))], SqlDialect.SqlServer, Bracket));
        Assert.Equal("INSERT INTO [dbo].[Customers] ([Name]) VALUES (N'Acme');", insert.Sql);
        Assert.Empty(insert.ReturnedColumns);
    }

    [Fact]
    public void Changes_run_deletes_then_updates_then_inserts()
    {
        var s = Resolve("SELECT * FROM dbo.Customers", "CustomerId", "Name", "Email");

        var changes = ResultEditSql.BuildChanges(s,
            deleted: [new object?[] { 1, "Old", null }],
            edited: [new ResultRowEdit([2, "Two", null], Values((2, "two@example.com")))],
            added: [Values((0, 1), (1, "Reused key"))],
            SqlDialect.SqlServer, Bracket);

        Assert.Equal([ResultChangeKind.Delete, ResultChangeKind.Update, ResultChangeKind.Insert], changes.Select(c => c.Kind));
        Assert.Equal("DELETE FROM [dbo].[Customers] WHERE [CustomerId] = 1;", changes[0].Sql);
        Assert.Equal("UPDATE [dbo].[Customers] SET [Email] = N'two@example.com' WHERE [CustomerId] = 2;", changes[1].Sql);
        Assert.StartsWith("INSERT INTO [dbo].[Customers] ([CustomerId], [Name]) VALUES (1, N'Reused key');", changes[2].Sql);

        var script = ResultEditSql.Script(changes);
        Assert.Contains("DELETE FROM", script);
        Assert.Contains("SELECT [CustomerId], [Name], [Email]", script);
    }

    [Fact]
    public void Edits_alone_still_work_on_joined_results()
    {
        var s = Resolve("SELECT o.OrderId, c.CustomerId, c.Name FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId",
            "OrderId", "CustomerId", "Name");
        var changes = ResultEditSql.BuildChanges(s, [], [new ResultRowEdit([1, 2, "x"], Values((2, "y")))], [], SqlDialect.SqlServer, Bracket);
        Assert.Equal("UPDATE [dbo].[Customers] SET [Name] = N'y' WHERE [CustomerId] = 2;", Assert.Single(changes).Sql);
    }
}
