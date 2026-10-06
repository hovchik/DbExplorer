using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class ResultPivotTests
{
    private static readonly string[] Columns = ["region", "product", "qty", "price", "sold"];

    private static readonly List<object?[]> Rows =
    [
        ["East", "Apple", 3, 1.5m, new DateTime(2024, 1, 5)],
        ["East", "Pear", 1, 2m, new DateTime(2024, 2, 1)],
        ["West", "Apple", 5, 1.25m, new DateTime(2024, 3, 9)],
        ["East", "Apple", 2, null, new DateTime(2023, 12, 31)],
        [null, "Pear", 4, 2.5m, null],
        ["West", "Banana", 7, 0.5m, new DateTime(2024, 6, 1)]
    ];

    private static PivotResult Run(int[] rowFields, int? columnField, int? valueField, PivotAggregate aggregate) =>
        ResultPivot.Build(Rows, r => r, Columns, new PivotSpec(rowFields, columnField, valueField, aggregate));

    [Fact]
    public void Sums_a_value_by_row_and_column_with_totals()
    {
        var pivot = Run([0], 1, 2, PivotAggregate.Sum);

        Assert.Equal(["region", "Apple", "Banana", "Pear", "Total"], pivot.Headers);
        // NULL region sorts first, like the grid.
        Assert.Equal([null, null, null, 4m, 4m], pivot.Rows[0]);
        Assert.Equal(["East", 5m, null, 1m, 6m], pivot.Rows[1]);
        Assert.Equal(["West", 5m, 7m, null, 12m], pivot.Rows[2]);
        Assert.Equal([ResultPivot.TotalLabel, 10m, 7m, 5m, 22m], pivot.Rows[pivot.TotalRowIndex]);
    }

    [Fact]
    public void Without_a_value_field_cells_count_rows()
    {
        var pivot = Run([1], null, null, PivotAggregate.Sum);

        Assert.Equal(["product", "Count"], pivot.Headers);
        Assert.Equal(["Apple", 3], pivot.Rows[0]);
        Assert.Equal(["Banana", 1], pivot.Rows[1]);
        Assert.Equal(["Pear", 2], pivot.Rows[2]);
        Assert.Equal([ResultPivot.TotalLabel, 6], pivot.Rows[3]);
    }

    [Fact]
    public void Count_of_a_value_field_skips_nulls()
    {
        var pivot = Run([0], null, 3, PivotAggregate.Count);

        Assert.Equal("Count of price", pivot.Headers[1]);
        Assert.Equal(["East", 2], pivot.Rows[1]);
    }

    [Fact]
    public void Average_is_over_the_underlying_values_not_the_cells()
    {
        var pivot = Run([0], null, 2, PivotAggregate.Average);

        Assert.Equal(["East", 2m], pivot.Rows[1]);
        Assert.Equal(22m / 6, pivot.Rows[pivot.TotalRowIndex][1]);
    }

    [Fact]
    public void Min_and_max_compare_by_type()
    {
        var min = Run([0], null, 4, PivotAggregate.Min);
        var max = Run([0], null, 4, PivotAggregate.Max);

        Assert.Equal(new DateTime(2023, 12, 31), min.Rows[1][1]);
        Assert.Equal(new DateTime(2024, 6, 1), max.Rows[2][1]);
        Assert.Equal(new DateTime(2024, 6, 1), max.Rows[max.TotalRowIndex][1]);
        Assert.Null(max.Rows[0][1]); // the NULL region has no dates
    }

    [Fact]
    public void Several_row_fields_group_by_their_combination()
    {
        var pivot = Run([0, 1], null, 2, PivotAggregate.Sum);

        Assert.Equal(["region", "product", "Sum of qty"], pivot.Headers);
        Assert.Equal(6, pivot.Rows.Count); // five groups and the total
        Assert.Equal(["East", "Apple", 5m], pivot.Rows[1]);
        Assert.Equal([ResultPivot.TotalLabel, null, 22m], pivot.Rows[5]);
    }

    [Fact]
    public void Sum_skips_text_and_falls_back_to_double_for_floats()
    {
        List<object?[]> rows = [["a", 1.5], ["a", "n/a"], ["a", 2]];
        var pivot = ResultPivot.Build(rows, r => r, ["k", "v"], new PivotSpec([0], null, 1, PivotAggregate.Sum));

        Assert.Equal(3.5, pivot.Rows[0][1]);
    }

    [Fact]
    public void Column_values_beyond_the_cap_are_left_out_and_flagged()
    {
        var rows = Enumerable.Range(0, ResultPivot.MaxColumnValues + 10).Select(i => new object?[] { "x", i }).ToList();
        var pivot = ResultPivot.Build(rows, r => r, ["k", "n"], new PivotSpec([0], 1, null, PivotAggregate.Count));

        Assert.True(pivot.ColumnsTruncated);
        Assert.Equal(ResultPivot.MaxColumnValues, pivot.ColumnValueCount);
        Assert.Equal(ResultPivot.MaxColumnValues, pivot.Rows[0][^1]);
    }

    [Fact]
    public void No_row_fields_gives_a_single_total_row()
    {
        var pivot = Run([], 0, 2, PivotAggregate.Sum);

        Assert.Equal(["NULL", "East", "West", "Total"], pivot.Headers);
        Assert.Equal([4m, 6m, 12m, 22m], pivot.Rows.Single());
    }
}
