using DbExplorer.Application.Compare;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class ComparisonReportBuilderTests
{
    private static readonly ReportSide Left = new("dev", "Shop", "dbo.usp_Get");
    private static readonly ReportSide Right = new("prod <main>", "Shop", "dbo.usp_Get");
    private static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static readonly IReadOnlyList<DiffLine> Diff = TextDiffer.Diff(
        "CREATE PROC p AS\nSELECT 1\nFROM t", "CREATE PROC p AS\nSELECT 2\nFROM t", SchemaCompareMode.LineByLine);

    private static readonly DataComparisonResult Data = new()
    {
        KeyColumns = ["Id"],
        Rows =
        [
            new DataComparisonRow { Key = "1", Status = DataRowStatus.Same, Cells = [new DataCellDiff { Column = "Name", LeftValue = "a", RightValue = "a" }] },
            new DataComparisonRow { Key = "2", Status = DataRowStatus.Different, Cells = [new DataCellDiff { Column = "Name", LeftValue = "<b>", RightValue = "c|d", IsDifferent = true }] },
            new DataComparisonRow { Key = "3", Status = DataRowStatus.OnlyRight, Cells = [new DataCellDiff { Column = "Name", RightValue = null, IsDifferent = true }] }
        ]
    };

    [Fact]
    public void Schema_markdown_is_a_unified_diff()
    {
        var md = ComparisonReportBuilder.SchemaMarkdown(Left, Right, Diff, At);
        Assert.Contains("**1 line(s) only on left · 1 line(s) only on right**", md);
        Assert.Contains("```diff", md);
        Assert.Contains("- SELECT 1\n", md);
        Assert.Contains("+ SELECT 2\n", md);
        Assert.Contains("  FROM t\n", md);
        Assert.Contains("| Right | prod <main> · Shop · dbo.usp_Get |", md);
    }

    [Fact]
    public void Identical_schema_says_so_without_a_diff_block()
    {
        var same = TextDiffer.Diff("x", "x", SchemaCompareMode.LineByLine);
        var md = ComparisonReportBuilder.SchemaMarkdown(Left, Right, same, At);
        Assert.Contains("Definitions are identical", md);
        Assert.DoesNotContain("```diff", md);
    }

    [Fact]
    public void Schema_html_escapes_labels_and_marks_changes()
    {
        var html = ComparisonReportBuilder.SchemaHtml(Left, Right, Diff, At);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("prod &lt;main&gt;", html);
        Assert.DoesNotContain("prod <main>", html);
        Assert.Contains("<tr class=\"del\">", html);
        Assert.Contains("<tr class=\"add\">", html);
    }

    [Fact]
    public void Data_html_highlights_changed_cells_and_escapes_values()
    {
        var html = ComparisonReportBuilder.DataHtml(Left, Right, Data, onlyDifferences: true, At);
        Assert.Contains("1 same", html);
        Assert.Contains("<span class=\"old\">&lt;b&gt;</span> → <span class=\"new\">c|d</span>", html);
        Assert.Contains("<span class=\"null\">NULL</span>", html);
        Assert.Equal(2, html.Split("<tr class=").Length - 1);
    }

    [Fact]
    public void Data_markdown_lists_differences_with_escaped_pipes()
    {
        var md = ComparisonReportBuilder.DataMarkdown(Left, Right, Data, onlyDifferences: false, At);
        Assert.Contains("Rows matched by Id.", md);
        Assert.Contains("| 2 | Different | Name: <b> → c\\|d |", md);
        Assert.Contains("| 3 | Only in right |", md);
        Assert.Contains("| 1 | Same |", md);
    }
}
