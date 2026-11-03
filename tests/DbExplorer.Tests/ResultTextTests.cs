using System.Globalization;
using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public sealed class ResultTextTests
{
    [Fact]
    public void Rows_are_aligned_with_numbers_on_the_right_and_a_count()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var text = ResultText.Format(["id", "name", "city"],
            [
                [1, "Acme", "Oslo"],
                [12, "Globex Corporation", null]
            ]);
            Assert.Equal(
                "id | name               | city\n" +
                "---+--------------------+-----\n" +
                " 1 | Acme               | Oslo\n" +
                "12 | Globex Corporation | NULL\n" +
                "(2 rows)\n", text.ReplaceLineEndings("\n"));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void Long_and_multi_line_values_stay_on_one_line()
    {
        var text = ResultText.Format(["note"], [[new string('x', 100)], ["two\nlines"]]).ReplaceLineEndings("\n");
        var lines = text.Split('\n');
        Assert.Equal(ResultText.MaxColumnWidth, lines[2].Length);
        Assert.EndsWith("…", lines[2]);
        Assert.StartsWith("two↵lines", lines[3]);
    }

    [Fact]
    public void A_cut_off_result_says_how_much_is_shown()
    {
        IReadOnlyList<object?>[] rows = [.. Enumerable.Range(1, 5).Select(i => (IReadOnlyList<object?>)[i])];
        Assert.EndsWith("(3 rows shown of 5)\n", ResultText.Format(["n"], rows, maxRows: 3).ReplaceLineEndings("\n"));
        Assert.EndsWith("(5 rows shown of 1,000)\n", ResultText.Format(["n"], rows, totalRows: 1000).ReplaceLineEndings("\n").Replace("1000", "1,000"));
        Assert.EndsWith("(1 row)\n", ResultText.Format(["n"], [[1]]).ReplaceLineEndings("\n"));
    }
}
