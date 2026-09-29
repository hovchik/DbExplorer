using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class SqlEditorAnalysisTests
{
    [Fact]
    public void Update_and_delete_without_where_truncate_and_drop_are_flagged()
    {
        const string sql = "UPDATE dbo.Orders SET Total = 0;\nDELETE FROM sales.Lines WHERE Id = 1;\nDELETE FROM sales.Lines;\n" +
                           "TRUNCATE TABLE audit;\nDROP TABLE IF EXISTS tmp;\nSELECT 1;";
        var flagged = SqlEditorAnalysis.FindUnsafeStatements(sql);
        Assert.Equal(
            ["1: UPDATE dbo.Orders has no WHERE clause: it changes every row",
             "3: DELETE sales.Lines has no WHERE clause: it changes every row",
             "4: TRUNCATE audit removes every row",
             "5: DROP tmp"],
            flagged.Select(f => $"{f.Line}: {f.Description}"));
    }

    [Fact]
    public void Where_in_a_subquery_does_not_count_and_routine_bodies_are_ignored()
    {
        Assert.Single(SqlEditorAnalysis.FindUnsafeStatements("UPDATE t SET x = (SELECT y FROM s WHERE s.id = 1)"));
        Assert.Empty(SqlEditorAnalysis.FindUnsafeStatements("CREATE PROCEDURE p AS\nBEGIN\n  DELETE FROM t;\nEND"));
        Assert.Empty(SqlEditorAnalysis.FindUnsafeStatements("CREATE TABLE c (id int REFERENCES p(id) ON DELETE CASCADE)"));
    }

    [Fact]
    public void Parameters_are_undeclared_variables_and_colon_placeholders()
    {
        const string sql = "DECLARE @since date = '2024-01-01', @n int;\nSELECT * FROM t WHERE d > @since AND id = @id AND x::text = :name AND r = @@ROWCOUNT";
        Assert.Equal(["@id", ":name"], SqlEditorAnalysis.FindParameters(sql));
        Assert.Empty(SqlEditorAnalysis.FindParameters("CREATE PROCEDURE p @id int AS SELECT @id"));
    }

    [Fact]
    public void Parameter_values_become_literals_and_are_substituted_outside_strings()
    {
        Assert.Equal("42", SqlEditorAnalysis.ParameterLiteral("42", "SqlServer"));
        Assert.Equal("NULL", SqlEditorAnalysis.ParameterLiteral("null", "Postgres"));
        Assert.Equal("N'O''Neil'", SqlEditorAnalysis.ParameterLiteral("O'Neil", "SqlServer"));
        Assert.Equal("'x'", SqlEditorAnalysis.ParameterLiteral("x", "Postgres"));

        var sql = SqlEditorAnalysis.SubstituteParameters("SELECT '@id', @id -- @id", new Dictionary<string, string> { ["@id"] = "7" });
        Assert.Equal("SELECT '@id', 7 -- @id", sql);
    }

    [Fact]
    public void Matching_parentheses_skip_strings()
    {
        const string sql = "SELECT (a + ')' + (b))";
        Assert.Equal((7, 21), SqlEditorAnalysis.MatchingParentheses(sql, 7));
        Assert.Equal((18, 20), SqlEditorAnalysis.MatchingParentheses(sql, 20));
        Assert.Null(SqlEditorAnalysis.MatchingParentheses(sql, 3));
    }

    [Fact]
    public void Fold_regions_cover_blocks_multiline_parentheses_and_comments()
    {
        const string sql = "/* a\n b */\nBEGIN TRAN;\nCREATE PROC p AS\nBEGIN\n  SELECT CASE WHEN 1=1 THEN 1 END\n  FROM (\n    SELECT 1 x\n  ) s\nEND";
        var regions = SqlEditorAnalysis.FoldRegions(sql);
        Assert.Equal(["/* … */", "BEGIN … END", "(…)"], regions.Select(r => r.Title));
        Assert.Equal(sql.Length, regions[1].End);
    }

    [Fact]
    public void Identifier_at_caret_ignores_keywords()
    {
        Assert.Equal("Orders", SqlEditorAnalysis.IdentifierAt("SELECT * FROM Orders o", 16)?.Text);
        Assert.Null(SqlEditorAnalysis.IdentifierAt("SELECT * FROM Orders o", 2));
    }
}

public class QueryPlanToolsTests
{
    [Fact]
    public void Sql_server_plans_wrap_the_script_in_showplan_or_a_rolled_back_profile()
    {
        Assert.Equal("SET SHOWPLAN_ALL ON;\nGO\nSELECT 1\nGO\nSET SHOWPLAN_ALL OFF;", QueryPlanTools.BuildExplainScript("SELECT 1", "SqlServer", analyze: false));
        var analyze = QueryPlanTools.BuildExplainScript("DELETE FROM t", "SqlServer", analyze: true);
        Assert.Contains("BEGIN TRANSACTION", analyze);
        Assert.Contains("ROLLBACK TRANSACTION", analyze);
    }

    [Fact]
    public void Postgres_explains_every_statement_and_rolls_back_analyze()
    {
        Assert.Equal("EXPLAIN (FORMAT TEXT)\nSELECT 1;\nEXPLAIN (FORMAT TEXT)\nSELECT 2;",
            QueryPlanTools.BuildExplainScript("SELECT 1;\nSELECT 2;", "Postgres", analyze: false));
        Assert.Equal("BEGIN;\nEXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)\nDELETE FROM t;\nROLLBACK;",
            QueryPlanTools.BuildExplainScript("DELETE FROM t", "Postgres", analyze: true));
    }

    [Fact]
    public void Insights_point_at_big_scans_and_lookups()
    {
        IReadOnlyList<IReadOnlyList<object?>> pg =
        [
            ["Hash Join  (cost=10.00..500.00 rows=100 width=8)"],
            ["  ->  Seq Scan on orders  (cost=0.00..400.00 rows=50000 width=8)"],
            ["Execution Time: 12.3 ms"]
        ];
        var pgInsights = QueryPlanTools.Insights([(["QUERY PLAN"], pg)], "Postgres");
        Assert.Contains(pgInsights, i => i.StartsWith("Sequential scan on orders (≈50,000 rows)"));
        Assert.Contains("Execution Time: 12.3 ms", pgInsights);

        IReadOnlyList<IReadOnlyList<object?>> ss =
        [
            ["SELECT * FROM t", null, 2.5, null],
            ["|--Clustered Index Scan(OBJECT:([t].[PK]))", "Clustered Index Scan", 1.2, 20000.0],
            ["|--Key Lookup(OBJECT:([t].[PK]))", "Key Lookup", 0.1, 1.0]
        ];
        var ssInsights = QueryPlanTools.Insights([(["StmtText", "PhysicalOp", "TotalSubtreeCost", "EstimateRows"], ss)], "SqlServer");
        Assert.Contains("Estimated cost 2.5", ssInsights);
        Assert.Contains(ssInsights, i => i.StartsWith("Clustered Index Scan over ≈20,000 rows"));
        Assert.Contains(ssInsights, i => i.StartsWith("Key Lookup"));
    }

    [Fact]
    public void Error_locations_are_one_based_lines_and_columns()
    {
        Assert.Equal((1, 1), SqlExecutionException.LocationOf("SELECT", 0));
        Assert.Equal((2, 3), SqlExecutionException.LocationOf("a\nbcd", 4));
    }
}

public class ValueFormatterTests
{
    [Fact]
    public void Json_and_xml_are_indented_and_text_is_kept()
    {
        Assert.Equal(("{\n  \"a\": [\n    1,\n    2\n  ]\n}", "JSON"), ValueFormatter.Pretty("{\"a\":[1,2]}") is var j ? (j.Text.Replace("\r\n", "\n"), j.Kind) : default);
        Assert.Equal("XML", ValueFormatter.Pretty("<a><b>1</b></a>").Kind);
        Assert.Equal(("{not json", "Text"), ValueFormatter.Pretty("{not json"));
    }
}

public class SelectionStatisticsTests
{
    [Fact]
    public void Numbers_get_sum_average_min_max_and_text_gets_distinct_count()
    {
        var numbers = SelectionStatistics.Summarize([1, 2L, 3.5m, null]);
        Assert.StartsWith("Count 4 · NULL 1 · Sum 6", numbers.Replace(",", ".").Replace(" ", " "));
        Assert.Contains("Max 3", numbers);
        Assert.Equal("Count 3 · Distinct 2", SelectionStatistics.Summarize(["a", "b", "a"]));
    }
}
