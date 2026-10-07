using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.QueryBuilder;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class QueryBuilderTests
{
    private static readonly QueryTable Customers = new("c", "dbo", "Customers");
    private static readonly QueryTable Orders = new("o", "sales", "Orders");
    private static readonly QueryTable Lines = new("ol", "sales", "OrderLines");
    private static readonly QueryTable Products = new("p", "dbo", "Products");

    private static QueryColumn Col(string alias, string column) => new() { TableAlias = alias, Column = column };

    [Fact]
    public void Single_table_with_no_columns_selects_star()
    {
        var sql = QuerySqlBuilder.Build(new QueryDesign { Tables = [Customers] }, SqlDialect.SqlServer).Sql;
        Assert.Equal("SELECT *\nFROM dbo.Customers AS c;", sql);
    }

    [Fact]
    public void Empty_design_builds_nothing() =>
        Assert.Equal("", QuerySqlBuilder.Build(new QueryDesign(), SqlDialect.Postgres).Sql);

    [Fact]
    public void Joins_filters_sort_and_limit_on_sql_server()
    {
        var design = new QueryDesign
        {
            Tables = [Customers, Orders],
            Joins = [new JoinCondition("c", "CustomerId", "o", "CustomerId", JoinKind.Left)],
            Columns =
            [
                Col("c", "Name") with { OutputAlias = "Customer" },
                Col("o", "OrderDate") with { Sort = SortDirection.Descending },
                Col("c", "Email") with { Output = false, Filter = "LIKE '%@example.com'" }
            ],
            Limit = 50
        };

        var built = QuerySqlBuilder.Build(design, SqlDialect.SqlServer);

        Assert.Equal(
            "SELECT TOP (50)\n    c.Name AS Customer,\n    o.OrderDate\n" +
            "FROM dbo.Customers AS c\n    LEFT JOIN sales.Orders AS o ON c.CustomerId = o.CustomerId\n" +
            "WHERE c.Email LIKE '%@example.com'\n" +
            "ORDER BY o.OrderDate DESC;", built.Sql);
        Assert.Empty(built.Warnings);
    }

    [Fact]
    public void Postgres_quotes_capitals_and_uses_limit()
    {
        var design = new QueryDesign
        {
            Tables = [new QueryTable("c", "public", "Customers")],
            Columns = [Col("c", "name"), Col("c", "Email")],
            Distinct = true,
            Limit = 10
        };

        var sql = QuerySqlBuilder.Build(design, SqlDialect.Postgres).Sql;

        Assert.Equal("SELECT DISTINCT\n    c.name,\n    c.\"Email\"\nFROM public.\"Customers\" AS c\nLIMIT 10;", sql);
    }

    [Fact]
    public void Odd_names_are_quoted_on_sql_server()
    {
        var design = new QueryDesign { Tables = [new QueryTable("o", "dbo", "Order Notes")], Columns = [Col("o", "Note Text"), Col("o", "Key")] };
        var sql = QuerySqlBuilder.Build(design, SqlDialect.SqlServer).Sql;
        Assert.Contains("o.[Note Text]", sql);
        Assert.Contains("o.[Key]", sql);
        Assert.Contains("FROM dbo.[Order Notes] AS o", sql);
    }

    [Fact]
    public void Aggregates_group_by_the_other_output_columns_and_filter_in_having()
    {
        var design = new QueryDesign
        {
            Tables = [Customers, Orders],
            Joins = [new JoinCondition("c", "CustomerId", "o", "CustomerId")],
            Columns =
            [
                Col("c", "Name"),
                Col("o", "OrderId") with { Aggregate = ColumnAggregate.Count, OutputAlias = "orders", Filter = "> 5", Sort = SortDirection.Descending },
                Col("c", "Email") with { Output = false, Filter = "IS NOT NULL" }
            ]
        };

        var sql = QuerySqlBuilder.Build(design, SqlDialect.SqlServer).Sql;

        Assert.Equal(
            "SELECT\n    c.Name,\n    COUNT(o.OrderId) AS orders\n" +
            "FROM dbo.Customers AS c\n    INNER JOIN sales.Orders AS o ON c.CustomerId = o.CustomerId\n" +
            "WHERE c.Email IS NOT NULL\n" +
            "GROUP BY c.Name\n" +
            "HAVING COUNT(o.OrderId) > 5\n" +
            "ORDER BY orders DESC;", sql);
    }

    [Fact]
    public void Count_star_and_table_star()
    {
        var design = new QueryDesign
        {
            Tables = [Orders],
            Columns = [Col("o", "*") with { Aggregate = ColumnAggregate.Count, OutputAlias = "total" }]
        };
        Assert.Equal("SELECT\n    COUNT(*) AS total\nFROM sales.Orders AS o;", QuerySqlBuilder.Build(design, SqlDialect.SqlServer).Sql);

        var star = design with { Columns = [Col("o", "*")] };
        Assert.Contains("    o.*", QuerySqlBuilder.Build(star, SqlDialect.SqlServer).Sql);
    }

    [Fact]
    public void Tables_join_in_placing_order_and_multi_column_conditions_share_one_on()
    {
        var design = new QueryDesign
        {
            // Products is placed before OrderLines but only joins through it: OrderLines comes first in the FROM.
            Tables = [Orders, Products, Lines],
            Joins =
            [
                new JoinCondition("o", "OrderId", "ol", "OrderId"),
                new JoinCondition("ol", "ProductId", "p", "ProductId"),
                new JoinCondition("ol", "LineNo", "p", "ProductId")
            ]
        };

        var built = QuerySqlBuilder.Build(design, SqlDialect.SqlServer);

        Assert.Equal(
            "SELECT *\nFROM sales.Orders AS o\n" +
            "    INNER JOIN sales.OrderLines AS ol ON o.OrderId = ol.OrderId\n" +
            "    INNER JOIN dbo.Products AS p ON ol.ProductId = p.ProductId AND ol.LineNo = p.ProductId;", built.Sql);
        Assert.Empty(built.Warnings);
    }

    [Fact]
    public void Left_join_flips_when_the_new_table_is_on_the_left_of_its_condition()
    {
        // "Keep every order": Orders is on the left of the condition, but Customers is placed first.
        var design = new QueryDesign
        {
            Tables = [Customers, Orders],
            Joins = [new JoinCondition("o", "CustomerId", "c", "CustomerId", JoinKind.Left)]
        };
        Assert.Contains("RIGHT JOIN sales.Orders AS o ON o.CustomerId = c.CustomerId",
            QuerySqlBuilder.Build(design, SqlDialect.SqlServer).Sql);
    }

    [Fact]
    public void Unjoined_table_is_cross_joined_with_a_warning()
    {
        var built = QuerySqlBuilder.Build(new QueryDesign { Tables = [Customers, Products] }, SqlDialect.Postgres);
        Assert.Contains("CROSS JOIN dbo.\"Products\" AS p", built.Sql);
        Assert.Single(built.Warnings);
    }

    [Fact]
    public void Sorting_by_an_ungrouped_column_warns()
    {
        var design = new QueryDesign
        {
            Tables = [Orders],
            Columns =
            [
                Col("o", "CustomerId"),
                Col("o", "OrderId") with { Aggregate = ColumnAggregate.Count },
                Col("o", "OrderDate") with { Output = false, Sort = SortDirection.Ascending }
            ]
        };
        Assert.Contains(QuerySqlBuilder.Build(design, SqlDialect.SqlServer).Warnings, w => w.Contains("OrderDate"));
    }

    [Theory]
    [InlineData("> 100", "x.a > 100")]
    [InlineData(">=10", "x.a >= 10")]
    [InlineData("<> Smith", "x.a <> 'Smith'")]
    [InlineData("= 'it''s'", "x.a = 'it''s'")]
    [InlineData("Smith", "x.a = 'Smith'")]
    [InlineData("O'Hara", "x.a = 'O''Hara'")]
    [InlineData("42", "x.a = 42")]
    [InlineData("-3.5", "x.a = -3.5")]
    [InlineData("2024-01-31", "x.a = '2024-01-31'")]
    [InlineData("ann@example.com", "x.a = 'ann@example.com'")]
    [InlineData("[other]", "x.a = [other]")]
    [InlineData("A%", "x.a LIKE 'A%'")]
    [InlineData("like A%", "x.a LIKE 'A%'")]
    [InlineData("not  like '%x'", "x.a NOT LIKE '%x'")]
    [InlineData("is null", "x.a IS null")]
    [InlineData("IS NOT NULL", "x.a IS NOT NULL")]
    [InlineData("in (1, 2, 3)", "x.a IN (1, 2, 3)")]
    [InlineData("BETWEEN 1 AND 5", "x.a BETWEEN 1 AND 5")]
    [InlineData("= y.b", "x.a = y.b")]
    [InlineData("< GETDATE()", "x.a < GETDATE()")]
    [InlineData("= @id", "x.a = @id")]
    [InlineData("= NULL", "x.a = NULL")]
    public void Filter_text_becomes_a_condition(string filter, string expected) =>
        Assert.Equal(expected, QuerySqlBuilder.Condition("x.a", filter));

    [Fact]
    public void Blank_filter_is_no_condition() => Assert.Null(QuerySqlBuilder.Condition("x.a", "  "));

    // ----- Join suggestions -----

    [Fact]
    public void Suggests_the_foreign_key_join_from_either_side()
    {
        var shop = TestSnapshots.Shop();

        var fromChild = Assert.Single(JoinSuggester.Suggest(shop, "", [Customers, Orders], Orders));
        Assert.True(fromChild.FromForeignKey);
        Assert.Equal(new JoinCondition("c", "CustomerId", "o", "CustomerId"), Assert.Single(fromChild.Conditions));

        var fromParent = Assert.Single(JoinSuggester.Suggest(shop, "", [Orders, Customers], Customers));
        Assert.Equal(new JoinCondition("o", "CustomerId", "c", "CustomerId"), Assert.Single(fromParent.Conditions));
    }

    [Fact]
    public void A_table_between_two_placed_tables_joins_both()
    {
        var joins = JoinSuggester.Suggest(TestSnapshots.Shop(), "", [Orders, Products, Lines], Lines);
        Assert.Equal(2, joins.Count);
        Assert.Contains(joins, j => j.Conditions[0] == new JoinCondition("o", "OrderId", "ol", "OrderId"));
        Assert.Contains(joins, j => j.Conditions[0] == new JoinCondition("p", "ProductId", "ol", "ProductId"));
    }

    [Fact]
    public void First_table_and_unrelated_tables_get_no_join()
    {
        Assert.Empty(JoinSuggester.Suggest(TestSnapshots.Shop(), "", [Customers], Customers));
        Assert.Empty(JoinSuggester.Suggest(TestSnapshots.Shop(), "", [Customers, new QueryTable("a", "dbo", "Audit")], new QueryTable("a", "dbo", "Audit")));
    }

    [Fact]
    public void Multi_column_key_gives_one_condition_per_column()
    {
        var snapshot = Snapshot(
            [Column("dbo", "Head", "A", true), Column("dbo", "Head", "B", true), Column("dbo", "Detail", "HeadA"), Column("dbo", "Detail", "HeadB")],
            [new DbForeignKey { Name = "FK_Detail_Head", Schema = "dbo", Table = "Detail", Columns = "HeadA, HeadB", ReferencedSchema = "dbo", ReferencedTable = "Head", ReferencedColumns = "A, B" }]);
        var head = new QueryTable("h", "dbo", "Head");
        var detail = new QueryTable("d", "dbo", "Detail");

        var join = Assert.Single(JoinSuggester.Suggest(snapshot, "", [head, detail], detail));

        Assert.Equal([new JoinCondition("h", "A", "d", "HeadA"), new JoinCondition("h", "B", "d", "HeadB")], join.Conditions);
    }

    [Fact]
    public void Self_reference_joins_the_second_copy_once()
    {
        var snapshot = Snapshot(
            [Column("hr", "Employees", "Id", true), Column("hr", "Employees", "ManagerId")],
            [new DbForeignKey { Name = "FK_Manager", Schema = "hr", Table = "Employees", Columns = "ManagerId", ReferencedSchema = "hr", ReferencedTable = "Employees", ReferencedColumns = "Id" }]);
        var e = new QueryTable("e", "hr", "Employees");
        var e2 = new QueryTable("e2", "hr", "Employees");

        var join = Assert.Single(JoinSuggester.Suggest(snapshot, "", [e, e2], e2));

        Assert.Single(join.Conditions);
    }

    [Fact]
    public void Without_keys_a_column_named_after_the_other_table_is_guessed()
    {
        var snapshot = Snapshot(
            [Column("public", "customers", "id", true), Column("public", "orders", "id", true), Column("public", "orders", "customer_id")], []);
        var c = new QueryTable("c", "public", "customers");
        var o = new QueryTable("o", "public", "orders");

        var join = Assert.Single(JoinSuggester.Suggest(snapshot, "", [c, o], o));
        Assert.False(join.FromForeignKey);
        Assert.Equal(new JoinCondition("c", "id", "o", "customer_id"), Assert.Single(join.Conditions));

        // And from the other side, keeping the condition's sides as placed.
        var back = Assert.Single(JoinSuggester.Suggest(snapshot, "", [o, c], c));
        Assert.Equal(new JoinCondition("o", "customer_id", "c", "id"), Assert.Single(back.Conditions));
    }

    [Theory]
    [InlineData("Customers", "c")]
    [InlineData("OrderLines", "ol")]
    [InlineData("order_items", "oi")]
    [InlineData("Order Notes", "on2")]
    [InlineData("2020_sales", "t2s")]
    public void Aliases_are_word_initials(string table, string expected) =>
        Assert.Equal(expected, JoinSuggester.AliasFor(table, []));

    [Fact]
    public void Taken_aliases_are_numbered() =>
        Assert.Equal("c3", JoinSuggester.AliasFor("Customers", ["c", "c2"]));

    private static DbColumn Column(string schema, string table, string name, bool pk = false) =>
        new() { Schema = schema, Table = table, Name = name, DataType = "int", BaseType = "int", IsPrimaryKey = pk };

    private static MetadataSnapshot Snapshot(IReadOnlyList<DbColumn> columns, IReadOnlyList<DbForeignKey> keys) => new()
    {
        Objects = columns.Select(c => new DbObject { Schema = c.Schema, Name = c.Table, Type = DbObjectType.Table }).Distinct().ToList(),
        Columns = columns,
        Modules = [],
        ForeignKeys = keys,
        Indexes = [],
        RefreshedAt = DateTimeOffset.Now
    };
}
