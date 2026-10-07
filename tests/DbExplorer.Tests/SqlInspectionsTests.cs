using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class SqlInspectionsTests
{
    private static readonly SqlInspector Inspector = new(TestSnapshots.Shop(), id => "[" + id + "]");
    private static readonly SqlInspector SqlServer = new(TestSnapshots.Shop(), id => "[" + id + "]", "SqlServer");
    private static readonly SqlInspector Postgres = new(TestSnapshots.Shop(), id => "\"" + id + "\"", "Postgres");

    private static SqlInspection Single(string sql, SqlInspector? inspector = null) => Assert.Single((inspector ?? Inspector).Inspect(sql));

    private static void Clean(string sql, SqlInspector? inspector = null) => Assert.Empty((inspector ?? Inspector).Inspect(sql));

    private static string Underlined(string sql, SqlInspection i) => sql.Substring(i.Start, i.Length);

    // ----- Tables -----

    [Fact]
    public void Unknown_table_offers_the_closest_names()
    {
        const string sql = "SELECT * FROM Custmers";
        var problem = Single(sql);

        Assert.Equal(InspectionSeverity.Error, problem.Severity);
        Assert.Equal("Custmers", Underlined(sql, problem));
        Assert.Contains("Did you mean Customers", problem.Message);
        var fix = problem.Fixes[0];
        Assert.Equal("Change to Customers", fix.Title);
        Assert.Equal("SELECT * FROM Customers", fix.ApplyTo(sql));
    }

    [Fact]
    public void Unknown_table_in_a_schema_keeps_the_schema()
    {
        const string sql = "SELECT * FROM sales.Order o";
        var problem = Single(sql);

        Assert.Equal("sales.Order", Underlined(sql, problem));
        Assert.Equal("SELECT * FROM sales.Orders o", problem.Fixes[0].ApplyTo(sql));
    }

    [Fact]
    public void Table_outside_the_default_schema_is_suggested_with_its_schema()
    {
        const string sql = "SELECT * FROM OrderLine";
        Assert.Equal("SELECT * FROM sales.OrderLines", Single(sql).Fixes[0].ApplyTo(sql));
    }

    [Fact]
    public void Join_and_update_targets_are_checked()
    {
        Assert.Contains("Produtcs", Single("SELECT * FROM sales.OrderLines l JOIN dbo.Produtcs p ON p.ProductId = l.ProductId").Message);
        Assert.Contains("Custmer", Single("UPDATE Custmer SET Name = 'x' WHERE CustomerId = 1").Message);
        Assert.Contains("Ordrs", Single("INSERT INTO sales.Ordrs (OrderId) VALUES (1)").Message);
    }

    [Theory]
    [InlineData("SELECT * FROM #work")]
    [InlineData("SELECT * FROM @rows r WHERE r.Id = 1")]
    [InlineData("WITH recent AS (SELECT * FROM sales.Orders) SELECT * FROM recent")]
    [InlineData("SELECT * FROM (SELECT CustomerId FROM sales.Orders) d WHERE d.CustomerId = 1")]
    [InlineData("SELECT * FROM sys.objects")]
    [InlineData("SELECT * FROM information_schema.tables")]
    [InlineData("SELECT * FROM pg_catalog.pg_class")]
    [InlineData("SELECT * FROM pg_stat_activity")]
    [InlineData("SELECT * FROM generate_series(1, 10) g")]
    [InlineData("SELECT * FROM OtherDb.dbo.Things")]
    [InlineData("CREATE TABLE dbo.Staging (Id int)\nGO\nINSERT INTO dbo.Staging (Id) SELECT CustomerId FROM dbo.Customers")]
    [InlineData("SELECT CustomerId INTO #ids FROM dbo.Customers;\nSELECT * FROM #ids")]
    [InlineData("SELECT * FROM sales.")]
    [InlineData("SELECT CustomerId FROM dbo.Customers WHERE Name IS DISTINCT FROM Email")]
    [InlineData("GRANT SELECT ON dbo.Customers TO reporting")]
    [InlineData("COPY dbo.Customers FROM STDIN")]
    public void Names_the_catalog_cannot_know_are_not_reported(string sql)
    {
        Assert.DoesNotContain(Inspector.Inspect(sql), i => i.Message.StartsWith("Table", StringComparison.Ordinal));
    }

    [Fact]
    public void Procedure_bodies_are_not_checked()
    {
        Clean("CREATE PROCEDURE dbo.p AS\nBEGIN\n  SELECT Bogus FROM Nothing\nEND");
    }

    [Fact]
    public void Update_alias_from_sql_server_is_not_a_table()
    {
        Clean("UPDATE o SET OrderDate = GETDATE() FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId WHERE c.Name = 'x'");
    }

    // ----- alias.column -----

    [Fact]
    public void Unknown_qualified_column_suggests_the_tables_columns()
    {
        const string sql = "SELECT o.OrderDat FROM sales.Orders o";
        var problem = Single(sql);

        Assert.Equal("OrderDat", Underlined(sql, problem));
        Assert.Equal("Column OrderDat not found in sales.Orders. Did you mean OrderDate?", problem.Message);
        Assert.Equal("SELECT o.OrderDate FROM sales.Orders o", problem.Fixes[0].ApplyTo(sql));
    }

    [Fact]
    public void Unknown_alias_suggests_the_alias_whose_table_has_the_column()
    {
        const string sql = "SELECT x.Email FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId";
        var problem = Single(sql);

        Assert.Equal("x", Underlined(sql, problem));
        Assert.Equal("x is not a table or alias of this query", problem.Message);
        var fix = Assert.Single(problem.Fixes);
        Assert.Equal("Change to c", fix.Title);
    }

    [Fact]
    public void Correlated_subquery_sees_the_outer_alias()
    {
        Clean("SELECT c.Name FROM dbo.Customers c WHERE EXISTS (SELECT 1 FROM sales.Orders o WHERE o.CustomerId = c.CustomerId)");
    }

    [Fact]
    public void Table_name_works_as_qualifier_and_star_is_fine()
    {
        Clean("SELECT Customers.Name, o.* FROM dbo.Customers JOIN sales.Orders o ON o.CustomerId = Customers.CustomerId");
    }

    [Fact]
    public void Quoted_names_are_matched()
    {
        Clean("SELECT n.[Note Text] FROM dbo.[Order Notes] n");
    }

    // ----- Unqualified columns -----

    [Fact]
    public void Unknown_column_is_reported_with_did_you_mean()
    {
        const string sql = "SELECT Nmae FROM dbo.Customers WHERE Email LIKE '%@x.com'";
        var problem = Single(sql);

        Assert.Equal("Nmae", Underlined(sql, problem));
        Assert.Equal("SELECT Name FROM dbo.Customers WHERE Email LIKE '%@x.com'", problem.Fixes[0].ApplyTo(sql));
    }

    [Fact]
    public void Ambiguous_column_offers_each_qualifier()
    {
        const string sql = "SELECT CustomerId FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId";
        var problem = Single(sql);

        Assert.Equal(InspectionSeverity.Warning, problem.Severity);
        Assert.Equal("CustomerId", Underlined(sql, problem));
        Assert.Contains("o (sales.Orders)", problem.Message);
        Assert.Equal(["Change to o.CustomerId", "Change to c.CustomerId"], problem.Fixes.Select(f => f.Title));
        Assert.StartsWith("SELECT c.CustomerId FROM", problem.Fixes[1].ApplyTo(sql));
    }

    [Fact]
    public void Inner_query_resolves_its_own_columns_first()
    {
        Clean("SELECT o.OrderId FROM sales.Orders o WHERE o.CustomerId IN (SELECT CustomerId FROM dbo.Customers WHERE Name = 'x')");
    }

    [Fact]
    public void Union_branches_are_separate_queries()
    {
        Clean("SELECT CustomerId FROM dbo.Customers UNION ALL SELECT CustomerId FROM sales.Orders");
    }

    [Fact]
    public void Using_joins_merge_the_column()
    {
        Clean("SELECT CustomerId FROM sales.Orders JOIN dbo.Customers USING (CustomerId)", Postgres);
    }

    [Fact]
    public void Statements_of_a_batch_without_semicolons_are_separate()
    {
        Clean("BEGIN\n  SELECT CustomerId FROM dbo.Customers\n  SELECT CustomerId FROM sales.Orders\nEND");
    }

    [Theory]
    [InlineData("SELECT Name AS CustomerName FROM dbo.Customers ORDER BY CustomerName")]
    [InlineData("SELECT Name n, Email AS e FROM dbo.Customers")]
    [InlineData("SELECT Total = COUNT(*) FROM dbo.Customers")]
    [InlineData("SELECT TOP 5 Name FROM dbo.Customers")]
    [InlineData("SELECT CAST(Name AS varchar(10)), CONVERT(int, Name), DATEADD(day, 1, OrderDate) FROM dbo.Customers JOIN sales.Orders o ON o.CustomerId = Customers.CustomerId")]
    [InlineData("SELECT Name::text, CURRENT_TIMESTAMP, NULL, TRUE FROM dbo.Customers")]
    [InlineData("SELECT EXTRACT(YEAR FROM OrderDate) FROM sales.Orders")]
    [InlineData("SELECT CASE WHEN Name IS NULL THEN 'none' ELSE Name END label FROM dbo.Customers")]
    [InlineData("SELECT Name FROM dbo.Customers WHERE Name COLLATE Latin1_General_CI_AS = 'x'")]
    [InlineData("SELECT ROW_NUMBER() OVER (PARTITION BY CustomerId ORDER BY OrderDate DESC) FROM sales.Orders")]
    [InlineData("SELECT @x = Name FROM dbo.Customers")]
    [InlineData("SELECT Name FROM dbo.Customers c WITH (NOLOCK) WHERE c.CustomerId = 1")]
    [InlineData("SELECT DISTINCT ON (CustomerId) CustomerId, OrderDate FROM sales.Orders ORDER BY CustomerId, OrderDate DESC")]
    public void Ordinary_sql_has_no_problems(string sql)
    {
        Clean(sql);
    }

    [Fact]
    public void Columns_of_unknown_sources_are_not_guessed()
    {
        // The CTE's and derived table's columns are not in the catalog, so nothing is reported for them.
        Clean("WITH t AS (SELECT CustomerId AS Who FROM sales.Orders) SELECT Who, Name FROM t JOIN dbo.Customers c ON c.CustomerId = t.Who");
        Clean("SELECT Anything FROM (SELECT 1 AS Anything) d");
    }

    [Fact]
    public void Insert_column_list_and_update_set_are_checked_against_the_target()
    {
        const string insert = "INSERT INTO dbo.Customers (CustomerId, Nme) VALUES (1, 'x')";
        Assert.Equal("Nme", Underlined(insert, Single(insert)));

        const string update = "UPDATE dbo.Customers SET Emial = 'x' WHERE CustomerId = 1";
        var problem = Single(update);
        Assert.Equal("UPDATE dbo.Customers SET Email = 'x' WHERE CustomerId = 1", problem.Fixes[0].ApplyTo(update));
    }

    [Fact]
    public void Insert_select_does_not_make_target_columns_ambiguous()
    {
        Clean("INSERT INTO sales.Orders (OrderId, CustomerId) SELECT CustomerId, CustomerId FROM dbo.Customers");
    }

    // ----- GROUP BY -----

    [Fact]
    public void Column_missing_from_group_by_can_be_added()
    {
        const string sql = "SELECT o.CustomerId, o.OrderDate, COUNT(*) FROM sales.Orders o GROUP BY o.CustomerId";
        var problem = Single(sql, SqlServer);

        Assert.Equal("o.OrderDate", Underlined(sql, problem));
        Assert.Equal("Add o.OrderDate to GROUP BY", problem.Fixes[0].Title);
        Assert.EndsWith("GROUP BY o.CustomerId, o.OrderDate", problem.Fixes[0].ApplyTo(sql));
    }

    [Fact]
    public void Several_missing_columns_can_be_added_at_once()
    {
        const string sql = "SELECT o.OrderId, o.OrderDate, COUNT(*) FROM sales.Orders o GROUP BY o.CustomerId ORDER BY 1";
        var problems = SqlServer.Inspect(sql);

        Assert.Equal(2, problems.Count);
        var all = problems[0].Fixes.Single(f => f.Title == "Add all 2 missing columns to GROUP BY");
        Assert.Equal("SELECT o.OrderId, o.OrderDate, COUNT(*) FROM sales.Orders o GROUP BY o.CustomerId, o.OrderId, o.OrderDate ORDER BY 1", all.ApplyTo(sql));
    }

    [Fact]
    public void Aggregate_without_group_by_offers_one()
    {
        const string sql = "SELECT CustomerId, COUNT(*) FROM sales.Orders WHERE OrderDate > '2026-01-01' ORDER BY 2";
        var problem = Single(sql);

        Assert.Equal("CustomerId needs a GROUP BY: the query also uses an aggregate", problem.Message);
        Assert.Equal("SELECT CustomerId, COUNT(*) FROM sales.Orders WHERE OrderDate > '2026-01-01' GROUP BY CustomerId ORDER BY 2",
            problem.Fixes[0].ApplyTo(sql));
    }

    [Fact]
    public void Group_by_on_its_own_line_when_the_query_spans_lines()
    {
        const string sql = "  SELECT CustomerId, MAX(OrderDate)\n  FROM sales.Orders";
        Assert.Equal("  SELECT CustomerId, MAX(OrderDate)\n  FROM sales.Orders\n  GROUP BY CustomerId", Single(sql).Fixes[0].ApplyTo(sql));
    }

    [Theory]
    [InlineData("SELECT YEAR(OrderDate) AS y, COUNT(*) FROM sales.Orders GROUP BY YEAR(OrderDate)")]
    [InlineData("SELECT CustomerId, COUNT(*) FROM sales.Orders GROUP BY 1")]
    [InlineData("SELECT o.CustomerId, SUM(l.LineNo) FILTER (WHERE l.ProductId > 0) FROM sales.Orders o JOIN sales.OrderLines l ON l.OrderId = o.OrderId GROUP BY CustomerId")]
    [InlineData("SELECT CustomerId, COUNT(*), (SELECT MAX(Name) FROM dbo.Customers) FROM sales.Orders GROUP BY CustomerId")]
    [InlineData("SELECT CustomerId, OrderDate, ROW_NUMBER() OVER (ORDER BY OrderDate) FROM sales.Orders")]
    [InlineData("SELECT COUNT(*), MAX(OrderDate) FROM sales.Orders")]
    [InlineData("SELECT 'all' AS bucket, COUNT(*) FROM sales.Orders")]
    public void Valid_grouping_is_not_reported(string sql)
    {
        Clean(sql, SqlServer);
    }

    [Fact]
    public void Postgres_allows_columns_of_a_table_grouped_by_its_primary_key()
    {
        const string sql = "SELECT c.CustomerId, c.Name, COUNT(*) FROM dbo.Customers c JOIN sales.Orders o ON o.CustomerId = c.CustomerId GROUP BY c.CustomerId";
        Clean(sql, Postgres);
        Assert.Equal("c.Name", Underlined(sql, Single(sql, SqlServer)));
    }

    // ----- Script handling -----

    [Fact]
    public void Offsets_are_relative_to_the_whole_script()
    {
        const string sql = "SELECT 1;\n\nSELECT * FROM Custmers;";
        var problem = Single(sql);
        Assert.Equal("Custmers", Underlined(sql, problem));
    }

    [Fact]
    public void At_picks_the_problem_under_the_caret()
    {
        const string sql = "SELECT Nmae FROM dbo.Customers";
        var problems = Inspector.Inspect(sql);
        Assert.NotNull(SqlInspector.At(problems, sql.IndexOf("Nmae", StringComparison.Ordinal) + 4));
        Assert.Null(SqlInspector.At(problems, 0));
    }

    [Fact]
    public void An_empty_catalog_reports_nothing()
    {
        var empty = new SqlInspector(new DbExplorer.Application.Metadata.MetadataSnapshot
        {
            Objects = [], Columns = [], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        }, id => id);
        Assert.Empty(empty.Inspect("SELECT * FROM Anything"));
    }
}
