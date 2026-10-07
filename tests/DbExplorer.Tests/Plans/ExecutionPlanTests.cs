using DbExplorer.Application.Query.Plans;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests.Plans;

internal static class PlanSamples
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plans", "Samples", name));
}

public class PostgresPlanParserTests
{
    [Fact]
    public void Analyze_plan_builds_the_operator_tree_with_measured_rows()
    {
        var plan = Assert.Single(PostgresPlanParser.Parse(PlanSamples.Read("postgres-analyze.json"), "SELECT …"));

        Assert.True(plan.IsActual);
        Assert.Equal(["Limit", "Sort", "HashAggregate", "Hash Join", "Seq Scan", "Hash", "Seq Scan"], plan.Nodes.Select(n => n.Operator));
        Assert.Equal("public.orders o", plan.Nodes[4].Object);
        Assert.Equal("(o.status = 'open'::text)", plan.Nodes[4].Detail);
        Assert.Equal("(o.customer_id = c.id)", plan.Nodes[3].Detail);
        Assert.Equal(5352, plan.Nodes[3].EstimatedRows);
        Assert.Equal(69333, plan.Nodes[3].ActualRows);
        Assert.Equal(0.652, plan.PlanningTimeMs);
        Assert.Equal(47.854, plan.ExecutionTimeMs);
        Assert.Equal(plan.Nodes[3], plan.Nodes[4].Parent);
        Assert.Contains(plan.Nodes[4].Properties, p => p is { Key: "Rows Removed by Filter", Value: "130667" });
    }

    [Fact]
    public void Shares_add_up_and_the_busiest_operator_is_a_hotspot()
    {
        var plan = PostgresPlanParser.Parse(PlanSamples.Read("postgres-analyze.json"))[0];

        Assert.Equal(1, plan.Nodes.Sum(n => n.Share), 3);
        var busiest = plan.Nodes.MaxBy(n => n.Share)!;
        Assert.Equal("Seq Scan", busiest.Operator);
        Assert.True(busiest.IsHotspot);
        Assert.InRange(plan.Nodes.Count(n => n.IsHotspot), 1, 3);
        Assert.All(plan.Nodes.Where(n => n.IsHotspot), n => Assert.True(n.Share >= 0.15));
    }

    [Fact]
    public void Analyze_plan_warns_about_the_full_scan_and_the_estimate_gap()
    {
        var plan = PostgresPlanParser.Parse(PlanSamples.Read("postgres-analyze.json"))[0];
        var orders = plan.Nodes[4];

        var scan = Assert.Single(orders.Warnings, w => w.Kind == PlanWarningKind.FullScan);
        Assert.Contains("200k rows", scan.Message);
        Assert.Contains("index", scan.Message);
        var gap = Assert.Single(orders.Warnings, w => w.Kind == PlanWarningKind.EstimateGap);
        Assert.Contains("expected ≈5,352 rows but got 69.3k", gap.Message);
        Assert.Contains("13× more", gap.Message);
        // The join only passes the scan's wrong estimate on, and the Sort under the LIMIT stops early by design.
        Assert.DoesNotContain(plan.Nodes[3].Warnings, w => w.Kind == PlanWarningKind.EstimateGap);
        Assert.DoesNotContain(plan.Nodes[1].Warnings, w => w.Kind == PlanWarningKind.EstimateGap);
        // customers is read whole too, but it is small: no warning.
        Assert.DoesNotContain(plan.Nodes[6].Warnings, w => w.Kind == PlanWarningKind.FullScan);
    }

    [Fact]
    public void Estimated_plan_has_no_actuals_and_shares_follow_cost()
    {
        var plan = PostgresPlanParser.Parse(PlanSamples.Read("postgres-estimated.json"))[0];

        Assert.False(plan.IsActual);
        Assert.All(plan.Nodes, n => Assert.Null(n.ActualRows));
        Assert.Equal(plan.Root.SubtreeCost, plan.TotalCost);
        var scan = plan.Nodes.First(n => n.Object == "public.orders o");
        Assert.True(scan.IsHotspot);
        Assert.Contains(scan.Warnings, w => w.Kind == PlanWarningKind.FullScan && w.Message.Contains("cost"));
        Assert.DoesNotContain(plan.Warnings, w => w.Kind == PlanWarningKind.EstimateGap);
        Assert.StartsWith("Estimated plan (not run)", plan.Summary);
    }

    [Fact]
    public void Sort_that_went_to_disk_is_a_spill()
    {
        var plan = PostgresPlanParser.Parse(PlanSamples.Read("postgres-spill.json"))[0];

        var spill = Assert.Single(plan.Warnings, w => w.Kind == PlanWarningKind.Spill);
        Assert.Equal("Sort", spill.Node!.Operator);
        Assert.Contains("external merge", spill.Message);
        Assert.Contains("work_mem", spill.Message);
    }

    [Fact]
    public void Text_that_is_not_a_plan_is_rejected()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => PostgresPlanParser.Parse("[{\"x\": 1}]"));
    }
}

public class SqlServerPlanParserTests
{
    [Fact]
    public void Actual_plan_reads_operators_objects_and_runtime_counters()
    {
        var plan = Assert.Single(SqlServerPlanParser.Parse(PlanSamples.Read("sqlserver-actual.xml")));

        Assert.True(plan.IsActual);
        Assert.Equal(["Sort", "Nested Loops (Inner Join)", "Clustered Index Scan", "Clustered Index Seek"], plan.Nodes.Select(n => n.Operator));
        Assert.Equal("dbo.Orders (PK_Orders) o", plan.Nodes[2].Object);
        Assert.Equal("Seek: Id = [Shop].[dbo].[Orders].[CustomerId] as [o].[CustomerId]", plan.Nodes[3].Detail);
        Assert.Equal(4000, plan.Nodes[3].ActualExecutions);
        Assert.Equal(120, plan.Nodes[3].EstimatedExecutions);
        Assert.Equal(3.84212, plan.TotalCost);
        Assert.Equal(74, plan.ExecutionTimeMs);
        Assert.Equal(4, plan.PlanningTimeMs);
        Assert.StartsWith("SELECT o.Id", plan.Statement);
    }

    [Fact]
    public void Operator_cost_share_matches_management_studio()
    {
        var plan = SqlServerPlanParser.Parse(PlanSamples.Read("sqlserver-actual.xml"))[0];
        var scan = plan.Nodes[2];

        // 3.12 of 3.84212 total.
        Assert.Equal(0.812, scan.Share, 3);
        Assert.True(scan.IsHotspot);
        Assert.False(plan.Nodes[1].IsHotspot);
    }

    [Fact]
    public void Actual_plan_warnings_cover_scan_gap_spill_engine_and_missing_index()
    {
        var plan = SqlServerPlanParser.Parse(PlanSamples.Read("sqlserver-actual.xml"))[0];
        var scan = plan.Nodes[2];

        Assert.Contains(scan.Warnings, w => w.Kind == PlanWarningKind.FullScan && w.Message.Contains("200k rows"));
        Assert.Contains(scan.Warnings, w => w.Kind == PlanWarningKind.EstimateGap && w.Message.Contains("33.3× more"));
        Assert.Contains(scan.Warnings, w => w.Kind == PlanWarningKind.Engine && w.Message.Contains("implicit conversion"));
        Assert.Contains(scan.Warnings, w => w.Kind == PlanWarningKind.Engine && w.Message.Contains("no statistics on Orders.Status"));
        Assert.Contains(plan.Nodes[0].Warnings, w => w.Kind == PlanWarningKind.Spill);
        // The Sort and the loop only pass the scan's wrong estimate on.
        Assert.All(plan.Nodes.Take(2), n => Assert.DoesNotContain(n.Warnings, w => w.Kind == PlanWarningKind.EstimateGap));
        // One row per seek, as expected: no gap on the inner side of the loop.
        Assert.DoesNotContain(plan.Nodes[3].Warnings, w => w.Kind == PlanWarningKind.EstimateGap);

        var missing = Assert.Single(plan.StatementWarnings);
        Assert.Equal(PlanWarningKind.MissingIndex, missing.Kind);
        Assert.Contains("CREATE INDEX IX_Orders_Status ON [dbo].[Orders] ([Status]) INCLUDE ([CustomerId], [Total])", missing.Message);
        Assert.Equal(missing, plan.Warnings[0]);
    }

    [Fact]
    public void Estimated_plan_names_key_lookups_and_warns_about_them()
    {
        var plan = SqlServerPlanParser.Parse(PlanSamples.Read("sqlserver-estimated.xml"))[0];

        Assert.False(plan.IsActual);
        Assert.Equal(["Nested Loops (Inner Join)", "Index Seek", "Key Lookup"], plan.Nodes.Select(n => n.Operator));
        Assert.Equal("Seek: Country = 'AM'", plan.Nodes[1].Detail);
        var lookup = Assert.Single(plan.Warnings);
        Assert.Equal(PlanWarningKind.Lookup, lookup.Kind);
        Assert.Contains("≈50 times", lookup.Message);
        Assert.Contains("INCLUDE", lookup.Message);
    }

    [Fact]
    public void Xml_that_is_not_a_showplan_is_rejected()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => SqlServerPlanParser.Parse("<root />"));
    }
}

public class PlanReaderTests
{
    [Fact]
    public void Postgres_script_explains_each_statement_as_json()
    {
        Assert.Equal("EXPLAIN (VERBOSE, FORMAT JSON)\nSELECT 1;\nEXPLAIN (VERBOSE, FORMAT JSON)\nSELECT 2;",
            PlanReader.BuildScript("SELECT 1; SELECT 2;", "PostgreSQL", analyze: false));
        Assert.Equal("BEGIN;\nEXPLAIN (ANALYZE, BUFFERS, VERBOSE, FORMAT JSON)\nDELETE FROM t;\nROLLBACK;",
            PlanReader.BuildScript("DELETE FROM t", "PostgreSQL", analyze: true));
    }

    [Fact]
    public void Sql_server_script_asks_for_showplan_xml_and_rolls_back_measured_runs()
    {
        Assert.Equal("SET SHOWPLAN_XML ON;\nGO\nSELECT 1\nGO\nSET SHOWPLAN_XML OFF;", PlanReader.BuildScript("SELECT 1", "SqlServer", analyze: false));
        var analyze = PlanReader.BuildScript("DELETE FROM t", "SqlServer", analyze: true);
        Assert.StartsWith("SET STATISTICS XML ON;", analyze);
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", analyze);
    }

    [Fact]
    public void Read_keeps_the_plans_and_skips_the_statements_own_results()
    {
        var xml = PlanSamples.Read("sqlserver-actual.xml");
        QueryResultSet[] sets =
        [
            new() { Columns = ["Id", "Total", "Name"], Rows = [[1, 10m, "a"]] },
            new() { Columns = ["Microsoft SQL Server 2005 XML Showplan"], Rows = [[xml]] }
        ];

        var plan = Assert.Single(PlanReader.Read(sets, "SqlServer", "SELECT …"));
        Assert.Equal(4, plan.Nodes.Count);
    }

    [Fact]
    public void Read_pairs_each_postgres_plan_with_its_statement()
    {
        var json = PlanSamples.Read("postgres-estimated.json");
        QueryResultSet[] sets = [new() { Columns = ["QUERY PLAN"], Rows = [[json]] }, new() { Columns = ["QUERY PLAN"], Rows = [[json]] }];

        var plans = PlanReader.Read(sets, "PostgreSQL", "SELECT 1;\nSELECT 2;");
        Assert.Equal(["SELECT 1", "SELECT 2"], plans.Select(p => p.Statement));
    }
}

public class PlanLayoutTests
{
    [Fact]
    public void Root_is_on_the_left_and_parents_sit_between_their_children()
    {
        var plan = PostgresPlanParser.Parse(PlanSamples.Read("postgres-analyze.json"))[0];
        var layout = PlanLayout.Arrange(plan);

        var join = plan.Nodes[3];
        Assert.Equal(PlanLayout.Margin, layout[plan.Root].X);
        Assert.True(layout[join.Children[0]].X > layout[join].X);
        Assert.Equal((layout[join.Children[0]].Y + layout[join.Children[1]].Y) / 2, layout[join].Y);
        Assert.Equal(join, layout.HitTest(layout[join].X + 5, layout[join].Y + 5));
        Assert.Null(layout.HitTest(-10, -10));
        Assert.True(layout.Width >= layout.Boxes.Max(b => b.X) + PlanLayout.NodeWidth);
    }
}
