using DbExplorer.Application;
using DbExplorer.Application.Lab;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

/// <summary>The experimental Query tab features: what changed since the last run, -- expect: checks and the cost lens.</summary>
public class QueryExperimentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-experiments-" + Guid.NewGuid().ToString("N"));

    public QueryExperimentsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static IReadOnlyList<IReadOnlyList<object?>> Rows(params object?[][] rows) => rows;

    // ----- What changed since the last run -----

    [Fact]
    public void Diff_matches_rows_on_an_id_column_and_lists_new_gone_and_changed()
    {
        string[] cols = ["Name", "OrderId", "Status"];
        var before = Rows(["a", 1, "open"], ["b", 2, "open"], ["c", 3, "open"]);
        var after = Rows(["a", 1, "open"], ["b", 2, "paid"], ["d", 4, "open"]);

        var diff = ResultDiff.Compare(cols, before, cols, after)!;

        Assert.Equal(["OrderId"], diff.KeyColumns);
        Assert.Equal((1, 1, 1, 1), (diff.Added, diff.Removed, diff.Changed, diff.Unchanged));
        Assert.Equal([ResultRowChange.Added, ResultRowChange.Changed, ResultRowChange.Removed], diff.Rows.Select(r => r.Change));
        var changed = diff.Rows[1];
        Assert.Equal("OrderId=2", changed.Key);
        var cell = Assert.Single(changed.Cells);
        Assert.Equal(("Status", "open", "paid"), (cell.Column, cell.Before, cell.After));
        Assert.Equal("+1 new · −1 gone · ~1 changed", diff.Summary);
    }

    [Fact]
    public void Diff_uses_the_given_key_when_it_is_unique()
    {
        string[] cols = ["Code", "Qty"];
        var diff = ResultDiff.Compare(cols, Rows(["x", 1], ["y", 2]), cols, Rows(["y", 5], ["x", 1]), keyColumns: [0])!;
        Assert.Equal(["Code"], diff.KeyColumns);
        Assert.Equal((0, 0, 1), (diff.Added, diff.Removed, diff.Changed));
    }

    [Fact]
    public void Diff_without_a_unique_column_compares_whole_rows_and_counts_duplicates()
    {
        string[] cols = ["Status", "Total"];
        var before = Rows(["open", 1], ["open", 1], ["paid", 2]);
        var after = Rows(["open", 1], ["paid", 2], ["paid", 3]);

        var diff = ResultDiff.Compare(cols, before, cols, after)!;

        Assert.Empty(diff.KeyColumns);
        Assert.Equal((1, 1, 0, 2), (diff.Added, diff.Removed, diff.Changed, diff.Unchanged));
        Assert.Equal(["paid", 3], diff.Rows[0].Values);
        Assert.Equal(["open", 1], diff.Rows[1].Values);
    }

    [Fact]
    public void Diff_sees_null_and_the_text_null_as_different_and_same_rows_as_no_change()
    {
        string[] cols = ["Id", "Note"];
        var diff = ResultDiff.Compare(cols, Rows([1, null], [2, "x"]), cols, Rows([1, "NULL"], [2, "x"]))!;
        Assert.Equal(1, diff.Changed);

        var same = ResultDiff.Compare(cols, Rows([1, 2.5m]), cols, Rows([1, 2.5m]))!;
        Assert.False(same.HasChanges);
        Assert.Equal("no changes", same.Summary);
    }

    [Fact]
    public void Diff_is_not_possible_when_the_columns_changed()
    {
        Assert.Null(ResultDiff.Compare(["Id"], Rows([1]), ["Id", "Name"], Rows([1, "a"])));
    }

    // ----- -- expect: comments -----

    [Fact]
    public void Expectations_attach_to_the_result_of_the_statement_below_them()
    {
        const string sql = """
            UPDATE t SET x = 1;
            -- expect: rows = 0
            SELECT * FROM orders WHERE total < 0;

            SELECT 1;
            -- expect: not empty and id unique
            SELECT id FROM customers;
            """;

        var expectations = QueryExpectations.Parse(sql);

        Assert.Equal(3, expectations.Count);
        Assert.Equal((2, "rows = 0", 0), (expectations[0].Line, expectations[0].Text, expectations[0].ResultIndex));
        Assert.Equal(("not empty", 2), (expectations[1].Text, expectations[1].ResultIndex));
        Assert.Equal(("id unique", 2), (expectations[2].Text, expectations[2].ResultIndex));
    }

    [Fact]
    public void Expectations_on_statements_without_results_or_ordinary_comments_are_ignored()
    {
        Assert.Empty(QueryExpectations.Parse("-- expect: rows = 0\nDELETE FROM t WHERE 1 = 0;"));
        Assert.Empty(QueryExpectations.Parse("-- we expect this to be quick\nSELECT 1;"));
        Assert.Empty(QueryExpectations.Parse("-- expect: rows = 0\nSELECT * INTO copy FROM t;"));
    }

    private static QueryResultSet Set(string[] columns, params object?[][] rows) => new()
    {
        Columns = columns,
        Rows = rows,
        TotalRowCount = rows.Length
    };

    private static ExpectationOutcome CheckOne(string expectation, QueryResultSet set) =>
        Assert.Single(QueryExpectations.Check([new QueryExpectation(1, expectation, 0)], [set]));

    [Theory]
    [InlineData("rows = 2", true)]
    [InlineData("rows > 2", false)]
    [InlineData("rows <= 2", true)]
    [InlineData("rows != 2", false)]
    [InlineData("not empty", true)]
    [InlineData("empty", false)]
    [InlineData("id unique", true)]
    [InlineData("email not null", false)]
    [InlineData("value = 1", true)]
    [InlineData("value > 5", false)]
    [InlineData("nonsense here", false)]
    public void Expectations_are_checked_against_the_rows(string expectation, bool passes)
    {
        var set = Set(["id", "email"], [1, "a@x"], [2, null]);
        Assert.Equal(passes, CheckOne(expectation, set).Passed);
    }

    [Fact]
    public void Failed_expectations_say_what_was_found()
    {
        var set = Set(["code"], ["A"], ["B"], ["A"]);
        Assert.Equal("1 duplicated value(s), e.g. A ×2", CheckOne("code unique", set).Detail);
        Assert.Equal("got 3 row(s)", CheckOne("rows = 0", set).Detail);
        Assert.Contains("no column \"missing\"", CheckOne("missing not null", set).Detail);
        Assert.Equal("✗ line 1: expect rows = 0 — got 3 row(s)", CheckOne("rows = 0", set).ToString());
    }

    [Fact]
    public void A_truncated_result_counts_every_row_the_server_returned()
    {
        var set = new QueryResultSet { Columns = ["id"], Rows = [[1]], IsTruncated = true, TotalRowCount = 5000 };
        Assert.True(CheckOne("rows > 1000", set).Passed);
    }

    [Fact]
    public void An_expectation_without_its_result_set_fails()
    {
        var outcome = Assert.Single(QueryExpectations.Check([new QueryExpectation(3, "empty", 1)], [Set(["x"])]));
        Assert.False(outcome.Passed);
    }

    // ----- Cost lens -----

    [Theory]
    [InlineData("SELECT * FROM orders WHERE id = 1;", true)]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT a FROM x", true)]
    [InlineData("UPDATE orders SET total = 0", false)]
    [InlineData("SELECT 1; SELECT 2;", false)]
    [InlineData("SELECT * FROM orders WHERE id = :id", false)]
    [InlineData("SELECT * INTO copy FROM orders", false)]
    [InlineData("EXEC dbo.DoThings", false)]
    public void Only_a_single_read_only_query_is_estimated(string sql, bool estimable)
    {
        Assert.Equal(estimable, CostLens.Estimable(sql) is not null);
    }

    [Fact]
    public void Postgres_plan_gives_rows_cost_and_large_sequential_scans()
    {
        const string json = """
            [{"Plan": {"Node Type": "Hash Join", "Total Cost": 152000.5, "Plan Rows": 42000,
              "Plans": [
                {"Node Type": "Seq Scan", "Relation Name": "orders", "Total Cost": 150000, "Plan Rows": 1200000},
                {"Node Type": "Hash", "Total Cost": 12, "Plan Rows": 10, "Plans": [
                  {"Node Type": "Seq Scan", "Relation Name": "statuses", "Total Cost": 12, "Plan Rows": 10}]}
              ]}}]
            """;

        var estimate = CostLens.FromPostgresPlan(json)!;

        Assert.Equal(42000, estimate.Rows);
        Assert.Equal(CostLevel.Expensive, estimate.Level);
        Assert.Equal("orders", Assert.Single(estimate.Scans).Table);
        Assert.Equal("≈ 42k rows · cost 152k · full scan of orders", estimate.Summary);
    }

    [Fact]
    public void Cheap_postgres_index_lookup_is_green()
    {
        var estimate = CostLens.FromPostgresPlan("""[{"Plan": {"Node Type": "Index Scan", "Relation Name": "orders", "Total Cost": 8.3, "Plan Rows": 1}}]""")!;
        Assert.Equal(CostLevel.Cheap, estimate.Level);
        Assert.Empty(estimate.Scans);
        Assert.Equal("≈ 1 row · cost 8.3", estimate.Summary);
    }

    [Fact]
    public void Sql_server_showplan_gives_statement_estimate_and_scanned_tables()
    {
        string[] cols = ["StmtText", "PhysicalOp", "Argument", "EstimateRows", "TotalSubtreeCost"];
        IReadOnlyList<IReadOnlyList<object?>> rows =
        [
            ["SELECT * FROM dbo.Orders o WHERE o.Total > 5", null, null, 25000.0, 61.2],
            ["|--Clustered Index Scan", "Clustered Index Scan", "OBJECT:([Shop].[dbo].[Orders].[PK_Orders] AS [o]), WHERE:([o].[Total]>(5))", 25000.0, 61.2],
            ["|--Table Scan", "Table Scan", "OBJECT:([Shop].[dbo].[Tiny] AS [t])", 3.0, 0.003]
        ];

        var estimate = CostLens.FromShowPlan(cols, rows)!;

        Assert.Equal(25000, estimate.Rows);
        Assert.Equal(CostLevel.Expensive, estimate.Level);
        Assert.Equal("dbo.Orders", Assert.Single(estimate.Scans).Table);
    }

    // ----- Switches -----

    [Fact]
    public void Experiments_start_on_and_a_switched_off_one_stays_off_after_a_restart()
    {
        var settings = new AppSettingsService(new AppPaths(_root));
        Assert.All(Enum.GetValues<ExperimentalFeature>(), f => Assert.True(settings.IsEnabled(f)));

        ExperimentalFeature? changed = null;
        settings.ExperimentChanged += f => changed = f;
        settings.SetEnabled(ExperimentalFeature.CostLens, false);

        Assert.Equal(ExperimentalFeature.CostLens, changed);
        var reloaded = new AppSettingsService(new AppPaths(_root));
        Assert.False(reloaded.IsEnabled(ExperimentalFeature.CostLens));
        Assert.True(reloaded.IsEnabled(ExperimentalFeature.ResultDiff));
    }
}
