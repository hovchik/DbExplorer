using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class ResultViewQueryTests
{
    private static readonly List<object?[]> Rows =
    [
        [1, "Apple", 9.5m, new DateTime(2024, 1, 5), null],
        [2, "banana", 10m, new DateTime(2023, 12, 31), "x"],
        [3, null, 100m, new DateTime(2024, 6, 1), ""],
        [4, "cherry", 2m, null, "y"],
        [5, "apple", 9.5m, new DateTime(2024, 1, 5), "x"]
    ];

    private static List<object?[]> Run(string? quick = null, IReadOnlyCollection<ColumnFilter>? filters = null, IReadOnlyList<ColumnSort>? sorts = null) =>
        ResultViewQuery.Apply(Rows, r => r, quick, filters ?? [], sorts ?? []);

    private static int[] Ids(IEnumerable<object?[]> rows) => rows.Select(r => (int)r[0]!).ToArray();

    [Fact]
    public void Quick_filter_matches_any_cell_case_insensitively()
    {
        Assert.Equal([1, 5], Ids(Run("APPLE")));
    }

    [Fact]
    public void Numeric_comparison_parses_the_filter_value_as_a_number()
    {
        var filter = new ColumnFilter(2) { Operator = ColumnFilterOperator.GreaterOrEqual, Value = "10" };
        Assert.Equal([2, 3], Ids(Run(filters: [filter])));
    }

    [Fact]
    public void Date_comparison_parses_the_filter_value_as_a_date()
    {
        var filter = new ColumnFilter(3) { Operator = ColumnFilterOperator.LessThan, Value = "2024-01-05" };
        Assert.Equal([2], Ids(Run(filters: [filter])));
    }

    [Fact]
    public void Null_and_empty_operators()
    {
        Assert.Equal([3], Ids(Run(filters: [new ColumnFilter(1) { Operator = ColumnFilterOperator.IsNull }])));
        Assert.Equal([1, 3], Ids(Run(filters: [new ColumnFilter(4) { Operator = ColumnFilterOperator.IsEmpty }])));
        Assert.Equal([1, 2, 4, 5], Ids(Run(filters: [new ColumnFilter(1) { Operator = ColumnFilterOperator.IsNotNull }])));
    }

    [Fact]
    public void Negative_operators_keep_nulls()
    {
        Assert.Equal([1, 3, 4], Ids(Run(filters: [new ColumnFilter(4) { Operator = ColumnFilterOperator.NotEquals, Value = "x" }])));
        Assert.Equal([1, 3, 4, 5], Ids(Run(filters: [new ColumnFilter(1) { Operator = ColumnFilterOperator.NotContains, Value = "an" }])));
    }

    [Fact]
    public void Allowed_values_include_null_and_combine_with_a_condition()
    {
        var filter = new ColumnFilter(4) { AllowedValues = new HashSet<string?> { "x", null } };
        Assert.Equal([1, 2, 5], Ids(Run(filters: [filter])));

        var withCondition = filter with { Operator = ColumnFilterOperator.IsNotNull };
        Assert.Equal([2, 5], Ids(Run(filters: [withCondition])));
    }

    [Fact]
    public void Excluded_values_drop_exact_matches_only()
    {
        var filter = new ColumnFilter(4) { ExcludedValues = new HashSet<string?> { "x", null } };
        Assert.Equal([3, 4], Ids(Run(filters: [filter])));
        Assert.Equal("c not in (2 values)", filter.Summary("c"));
    }

    [Fact]
    public void Sort_is_typed_nulls_first_and_multi_column()
    {
        Assert.Equal([3, 1, 5, 2, 4], Ids(Run(sorts: [new ColumnSort(1, false)])));
        Assert.Equal([3, 2, 1, 5, 4], Ids(Run(sorts: [new ColumnSort(2, true)])));
        Assert.Equal([3, 5, 1, 2, 4], Ids(Run(sorts: [new ColumnSort(3, true), new ColumnSort(0, true)])));
    }

    [Fact]
    public void Stable_sort_keeps_fetched_order_for_ties()
    {
        Assert.Equal([4, 1, 5, 2, 3], Ids(Run(sorts: [new ColumnSort(2, false)])));
    }

    [Fact]
    public void Compare_values_handles_mixed_numeric_types()
    {
        Assert.True(ResultViewQuery.CompareValues(2, 10L) < 0);
        Assert.True(ResultViewQuery.CompareValues(2.5, 2m) > 0);
        Assert.True(ResultViewQuery.CompareValues(null, 0) < 0);
        Assert.Equal(0, ResultViewQuery.CompareValues(DBNull.Value, null));
    }

    [Fact]
    public void Distinct_values_are_counted_most_frequent_first_with_null()
    {
        var values = ResultViewQuery.DistinctValues(Rows, r => r, 4, 10, out var truncated);
        Assert.False(truncated);
        Assert.Equal((null, 1), values[0]);
        Assert.Equal(("x", 2), values[1]);
        Assert.Equal(4, values.Count);

        var limited = ResultViewQuery.DistinctValues(Rows, r => r, 0, 2, out truncated);
        Assert.True(truncated);
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public void Numeric_column_detection_ignores_nulls()
    {
        Assert.True(ResultViewQuery.IsNumericColumn([null, 1, 2.5m]));
        Assert.False(ResultViewQuery.IsNumericColumn([1, "2"]));
        Assert.False(ResultViewQuery.IsNumericColumn([null, DBNull.Value]));
    }

    [Fact]
    public void Summary_describes_the_filter()
    {
        Assert.Equal("price ≥ '10'", new ColumnFilter(0) { Operator = ColumnFilterOperator.GreaterOrEqual, Value = "10" }.Summary("price"));
        Assert.Equal("name is NULL", new ColumnFilter(0) { Operator = ColumnFilterOperator.IsNull }.Summary("name"));
        Assert.Equal("s in (2 values)", new ColumnFilter(0) { AllowedValues = new HashSet<string?> { "a", "b" } }.Summary("s"));
    }
}
