using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;

namespace DbExplorer.Tests;

public class ResultChartTests
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

    private static ChartData Run(ChartSpec spec) => ResultChart.Build(Rows, r => r, Columns, spec);

    [Fact]
    public void Bars_sum_values_that_share_an_x_in_the_order_the_rows_are_in()
    {
        var chart = Run(new ChartSpec(ChartKind.Bar, 0, [2], PivotAggregate.Sum));

        Assert.Equal(ChartAxisKind.Category, chart.XAxis);
        Assert.Equal(["East", "West", "NULL"], chart.Labels);
        var series = Assert.Single(chart.Series);
        Assert.Equal("qty", series.Name);
        Assert.Equal([6, 12, 4], series.Values);
        Assert.Equal("Sum of qty", chart.YTitle);
        Assert.Equal(6, chart.SourceRows);
    }

    [Theory]
    [InlineData(PivotAggregate.Count, new double[] { 2, 2, 1 })]
    [InlineData(PivotAggregate.Average, new double[] { 1.75, 0.875, 2.5 })]
    [InlineData(PivotAggregate.Min, new double[] { 1.5, 0.5, 2.5 })]
    [InlineData(PivotAggregate.Max, new double[] { 2, 1.25, 2.5 })]
    public void Other_summaries_skip_null_values(PivotAggregate aggregate, double[] expected)
    {
        var chart = Run(new ChartSpec(ChartKind.Bar, 0, [3], aggregate));
        Assert.Equal(expected.Select(v => (double?)v), chart.Series[0].Values);
    }

    [Fact]
    public void Without_value_columns_the_chart_counts_rows()
    {
        var chart = Run(new ChartSpec(ChartKind.Bar, 1, [], PivotAggregate.Sum));

        Assert.Equal(["Apple", "Pear", "Banana"], chart.Labels);
        Assert.Equal("Rows", chart.YTitle);
        Assert.Equal([3, 2, 1], chart.Series[0].Values);
    }

    [Fact]
    public void Several_value_columns_become_several_series()
    {
        var chart = Run(new ChartSpec(ChartKind.Bar, 1, [2, 3], PivotAggregate.Sum));

        Assert.Equal(["qty", "price"], chart.Series.Select(s => s.Name));
        Assert.Equal([10, 5, 7], chart.Series[0].Values);
        Assert.Equal([2.75, 4.5, 0.5], chart.Series[1].Values);
    }

    [Fact]
    public void Split_by_makes_a_series_per_value_with_gaps_where_a_group_has_none()
    {
        var chart = Run(new ChartSpec(ChartKind.Bar, 0, [2], PivotAggregate.Sum, SeriesField: 1));

        Assert.Equal(["Apple", "Banana", "Pear"], chart.Series.Select(s => s.Name));
        // Points East, West, NULL; series in value order. A group without the value leaves a gap.
        Assert.Equal([5, 5, null], chart.Series[0].Values);
        Assert.Equal([null, 7, null], chart.Series[1].Values);
        Assert.Equal([1, null, 4], chart.Series[2].Values);
    }

    [Fact]
    public void Split_by_works_with_one_value_column()
    {
        var withTwo = Run(new ChartSpec(ChartKind.Bar, 0, [2, 3], PivotAggregate.Sum, SeriesField: 1));
        // Only the first value column is split.
        Assert.Equal(["Apple", "Banana", "Pear"], withTwo.Series.Select(s => s.Name));
        Assert.Equal("Sum of qty", withTwo.YTitle);
    }

    [Fact]
    public void Lines_over_dates_are_drawn_to_scale_and_skip_rows_without_a_date()
    {
        var chart = Run(new ChartSpec(ChartKind.Line, 4, [2], PivotAggregate.Sum));

        Assert.Equal(ChartAxisKind.Time, chart.XAxis);
        Assert.Equal(1, chart.SkippedRows);
        Assert.Equal(5, chart.PointCount);
        Assert.Equal(new DateTime(2023, 12, 31).Ticks, chart.XValues[0]);
        Assert.Equal(new DateTime(2024, 6, 1).Ticks, chart.XValues[^1]);
        Assert.Equal([2, 3, 1, 5, 7], chart.Series[0].Values);
    }

    [Fact]
    public void Bars_over_dates_stay_labels()
    {
        var chart = Run(new ChartSpec(ChartKind.Bar, 4, [2], PivotAggregate.Sum));
        Assert.Equal(ChartAxisKind.Category, chart.XAxis);
        Assert.Equal("NULL", chart.Labels[4]); // in row order, not sorted
        Assert.Equal(0, chart.SkippedRows);
    }

    [Fact]
    public void Scatter_draws_every_row_on_number_axes()
    {
        var chart = Run(new ChartSpec(ChartKind.Scatter, 2, [3], PivotAggregate.Sum));

        Assert.Equal(ChartKind.Scatter, chart.Kind);
        Assert.Equal(ChartAxisKind.Number, chart.XAxis);
        Assert.Equal(5, chart.PointCount); // the row without a price is skipped
        Assert.Equal(1, chart.SkippedRows);
        Assert.Equal([3, 1, 5, 4, 7], chart.XValues);
        Assert.Equal("price", chart.YTitle);
    }

    [Fact]
    public void Scatter_without_x_uses_the_row_number()
    {
        var chart = Run(new ChartSpec(ChartKind.Scatter, null, [2], PivotAggregate.Sum));
        Assert.Equal([1, 2, 3, 4, 5, 6], chart.XValues);
        Assert.Equal("Row", chart.XTitle);
    }

    [Fact]
    public void Pies_sort_slices_biggest_first_and_leave_out_empty_ones()
    {
        var chart = Run(new ChartSpec(ChartKind.Pie, 1, [3], PivotAggregate.Sum));

        Assert.Equal(["Pear", "Apple", "Banana"], chart.Labels);
        Assert.Equal([4.5, 2.75, 0.5], chart.Series[0].Values);
        Assert.Null(chart.Note);
    }

    [Fact]
    public void Pies_fold_small_slices_into_other()
    {
        var rows = Enumerable.Range(1, 12).Select(i => new object?[] { "k" + i.ToString("00"), i }).ToList();
        rows.Add(["neg", -5]);
        var chart = ResultChart.Build(rows, r => r, ["k", "v"], new ChartSpec(ChartKind.Pie, 0, [1], PivotAggregate.Sum));

        Assert.Equal(ResultChart.MaxPieSlices, chart.PointCount);
        Assert.Equal("k12", chart.Labels[0]);
        Assert.Equal(ResultChart.OtherLabel, chart.Labels[^1]);
        Assert.Equal(1 + 2 + 3 + 4 + 5, chart.Series[0].Values[^1]);
        Assert.Contains("1 zero, negative or empty", chart.Note);
        Assert.Contains("5 smaller slices", chart.Note);
    }

    [Fact]
    public void Bar_charts_are_capped_and_say_so()
    {
        var rows = Enumerable.Range(0, ResultChart.MaxBarPoints + 50).Select(i => new object?[] { i, 1 }).ToList();
        var chart = ResultChart.Build(rows, r => r, ["n", "v"], new ChartSpec(ChartKind.Bar, 0, [1], PivotAggregate.Sum));

        Assert.Equal(ResultChart.MaxBarPoints, chart.PointCount);
        Assert.Contains("only the first 200 of 250", chart.Note);
    }

    [Fact]
    public void Series_are_capped_at_the_palette_size()
    {
        var rows = Enumerable.Range(0, 20).Select(i => new object?[] { "x", "s" + i.ToString("00"), i }).ToList();
        var chart = ResultChart.Build(rows, r => r, ["x", "s", "v"], new ChartSpec(ChartKind.Line, 0, [2], PivotAggregate.Sum, 1));

        Assert.Equal(ResultChart.MaxSeries, chart.Series.Count);
        Assert.Contains("only the first 8 s values", chart.Note);
    }

    [Fact]
    public void Default_is_a_line_over_the_first_date_or_bars_over_the_first_text_column()
    {
        var withDate = ResultChart.DefaultSpec(Columns, [false, false, true, true, false], [false, false, false, false, true]);
        Assert.Equal((ChartKind.Line, 4, PivotAggregate.Sum), (withDate.Kind, withDate.XField, withDate.Aggregate));
        Assert.Equal([2], withDate.YFields);

        var noDate = ResultChart.DefaultSpec(Columns, [false, false, true, true, false], [false, false, false, false, false]);
        Assert.Equal(ChartKind.Bar, noDate.Kind);
        Assert.Equal(0, noDate.XField);

        var noNumbers = ResultChart.DefaultSpec(["a", "b"], [false, false], [false, false]);
        Assert.Empty(noNumbers.YFields);
        Assert.Equal(PivotAggregate.Count, noNumbers.Aggregate);
    }

    [Fact]
    public void Empty_or_non_numeric_values_make_an_empty_chart()
    {
        var chart = Run(new ChartSpec(ChartKind.Bar, 0, [1], PivotAggregate.Sum)); // product is text
        Assert.True(chart.IsEmpty);
        Assert.True(ResultChart.Build(new List<object?[]>(), r => r, Columns, new ChartSpec(ChartKind.Bar, 0, [2], PivotAggregate.Sum)).IsEmpty);
    }

    [Theory]
    [InlineData(0, 97, new double[] { 0, 20, 40, 60, 80, 100 })]
    [InlineData(-3, 7, new double[] { -4, -2, 0, 2, 4, 6, 8 })]
    [InlineData(0.1, 0.43, new double[] { 0.1, 0.2, 0.3, 0.4, 0.5 })]
    [InlineData(5, 5, new double[] { 4.4, 4.6, 4.8, 5, 5.2, 5.4, 5.6 })]
    public void Nice_ticks_are_round_and_cover_the_range(double min, double max, double[] expected)
    {
        var ticks = ResultChart.NiceTicks(min, max, 5);
        Assert.Equal(expected.Length, ticks.Count);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], ticks[i], 9);
    }

    [Fact]
    public void Axis_numbers_are_short()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("1.5M", ResultChart.FormatNumber(1_500_000));
        Assert.Equal("25K", ResultChart.FormatNumber(25_000));
        Assert.Equal("2,500", ResultChart.FormatNumber(2_500));
        Assert.Equal("0.25", ResultChart.FormatNumber(0.25, 0.05));
        Assert.Equal("1,234.5", ResultChart.FormatValue(1234.5));
    }

    [Fact]
    public void Time_ticks_pick_a_round_unit()
    {
        using var _ = new CultureScope("en-US");
        var months = ResultChart.TimeTicks(new DateTime(2024, 1, 15).Ticks, new DateTime(2024, 7, 2).Ticks, 6);
        Assert.Equal(["Feb 2024", "Mar 2024", "Apr 2024", "May 2024", "Jun 2024", "Jul 2024"], months.Select(t => t.Label));

        var years = ResultChart.TimeTicks(new DateTime(2001, 3, 1).Ticks, new DateTime(2024, 3, 1).Ticks, 6);
        Assert.All(years, t => Assert.Equal(1, new DateTime((long)t.Value).Month));
        Assert.InRange(years.Count, 3, 6);

        var hours = ResultChart.TimeTicks(new DateTime(2024, 1, 1, 9, 10, 0).Ticks, new DateTime(2024, 1, 1, 14, 0, 0).Ticks, 6);
        Assert.StartsWith("10:00", hours[0].Label);
        Assert.Equal(5, hours.Count);
    }

    [Fact]
    public void Chart_data_copies_as_tab_separated_text()
    {
        var chart = Run(new ChartSpec(ChartKind.Bar, 0, [2, 3], PivotAggregate.Sum));
        var lines = ResultGridView.ChartDataText(chart).TrimEnd().Split(Environment.NewLine);

        Assert.Equal("region\tqty\tprice", lines[0]);
        Assert.Equal("East\t6\t3.5", lines[1]);
        Assert.Equal("NULL\t4\t2.5", lines[3]);
    }

    [AvaloniaFact]
    public void The_chart_toggle_swaps_the_grid_for_a_chart_and_turns_the_pivot_off()
    {
        var rs = new ResultSetView("t", Columns, Rows.Select(r => new ResultRow(r)).ToList());
        var view = new ResultGridView { ResultSet = rs };
        var window = new Window { Width = 1200, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        view.FindControl<ToggleButton>("PivotToggle")!.IsChecked = true;
        view.ShowChart(true);
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.FindControl<ToggleButton>("PivotToggle")!.IsChecked);
        Assert.True(view.FindControl<Control>("ChartPanel")!.IsVisible);
        Assert.False(view.FindControl<Control>("BodyGrid")!.IsVisible);
        Assert.False(view.FindControl<Control>("PivotGrid")!.IsVisible);
        var chart = view.CurrentChart!;
        Assert.Equal(ChartKind.Line, chart.Kind); // "sold" is a date column
        Assert.Equal("sold", chart.XTitle);

        // The search box narrows what is charted.
        var filter = view.FindControl<TextBox>("FilterBox")!;
        filter.Text = "West";
        Dispatcher.UIThread.RunJobs(); // TextChanged is raised from the dispatcher
        filter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, view.CurrentChart!.SourceRows);

        view.SetChart(new ChartSpec(ChartKind.Pie, 1, [2], PivotAggregate.Sum));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ChartKind.Pie, view.CurrentChart!.Kind);

        view.FindControl<ToggleButton>("ChartToggle")!.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.FindControl<Control>("BodyGrid")!.IsVisible);
        Assert.Null(view.CurrentChart);
        window.Close();
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _old = CultureInfo.CurrentCulture;
        public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);
        public void Dispose() => CultureInfo.CurrentCulture = _old;
    }
}
