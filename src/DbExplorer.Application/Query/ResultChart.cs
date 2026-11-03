using System.Globalization;

namespace DbExplorer.Application.Query;

/// <summary>How a result is drawn as a chart.</summary>
public enum ChartKind
{
    Bar,
    Line,
    Area,
    Pie,
    Scatter
}

/// <summary>What the X axis measures: evenly spaced labels, or a scale of numbers or dates.</summary>
public enum ChartAxisKind
{
    Category,
    Number,
    Time
}

/// <summary>What to chart: the X column, the value columns (none counts rows), how values that share an X are
/// summarised, and an optional column whose values split one value column into several series.</summary>
public sealed record ChartSpec(ChartKind Kind, int? XField, IReadOnlyList<int> YFields, PivotAggregate Aggregate, int? SeriesField = null)
{
    public static string Describe(ChartKind kind) => kind switch
    {
        ChartKind.Bar => "Bar",
        ChartKind.Line => "Line",
        ChartKind.Area => "Area",
        ChartKind.Pie => "Pie",
        ChartKind.Scatter => "Scatter",
        _ => kind.ToString()
    };
}

/// <summary>One series: a value (or <c>null</c> for a gap) per point.</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<double?> Values);

/// <summary>A chart ready to draw. Points share <see cref="Labels"/> (what the tooltip shows for each X) and, on a
/// number or time axis, <see cref="XValues"/>; each series has one value per point. Time values are
/// <see cref="DateTime.Ticks"/>.</summary>
public sealed record ChartData(
    ChartKind Kind,
    ChartAxisKind XAxis,
    string XTitle,
    string YTitle,
    IReadOnlyList<string> Labels,
    IReadOnlyList<double> XValues,
    IReadOnlyList<ChartSeries> Series,
    int SourceRows,
    int SkippedRows,
    string? Note)
{
    public int PointCount => Labels.Count;
    public bool IsEmpty => Labels.Count == 0 || Series.Count == 0 || Series.All(s => s.Values.All(v => v is null));
}

/// <summary>A tick on an axis: where it sits and the text under it.</summary>
public readonly record struct ChartTick(double Value, string Label);

/// <summary>
/// Turns fetched result rows into chart data, client side, so a result can be seen as a chart without writing a GROUP BY.
/// Rows that share an X value are summarised (sum by default); NULL X values are their own point. Labels keep the
/// order the rows are in (so ORDER BY and the grid's sort decide the order of bars), pies put the biggest slice first,
/// and lines and areas over numbers or dates are drawn to scale. Scatter plots draw every row.
/// </summary>
public static class ResultChart
{
    /// <summary>The most series a chart draws: one per colour in the palette.</summary>
    public const int MaxSeries = 8;

    /// <summary>The most bars (X values) a bar chart draws.</summary>
    public const int MaxBarPoints = 200;

    /// <summary>The most points a line, area or scatter chart draws.</summary>
    public const int MaxLinePoints = 5_000;

    /// <summary>A pie shows this many slices, then folds the rest into "Other".</summary>
    public const int MaxPieSlices = 8;

    public const string OtherLabel = "Other";
    public const string NullLabel = "NULL";

    /// <summary>A first chart that shows something: a line over the first date column, otherwise bars over the first
    /// text column, of the first number column (or a row count when there is none).</summary>
    public static ChartSpec DefaultSpec(IReadOnlyList<string> columnNames, IReadOnlyList<bool> numericColumns, IReadOnlyList<bool> dateColumns)
    {
        int? First(Func<int, bool> test) => Enumerable.Range(0, columnNames.Count).Cast<int?>().FirstOrDefault(i => test(i!.Value));
        var date = First(i => dateColumns[i]);
        var text = First(i => !numericColumns[i] && !dateColumns[i]);
        var x = date ?? text ?? (columnNames.Count > 1 ? 0 : null);
        var y = First(i => numericColumns[i] && i != x);
        var kind = date is not null && x == date ? ChartKind.Line : ChartKind.Bar;
        return new ChartSpec(kind, x, y is { } v ? [v] : [], y is null ? PivotAggregate.Count : PivotAggregate.Sum);
    }

    public static ChartData Build<T>(
        IReadOnlyList<T> rows,
        Func<T, IReadOnlyList<object?>> values,
        IReadOnlyList<string> columnNames,
        ChartSpec spec)
    {
        int? Valid(int? field) => field is { } f && f >= 0 && f < columnNames.Count ? f : null;
        var xField = Valid(spec.XField);
        var yFields = spec.YFields.Where(f => f >= 0 && f < columnNames.Count).Distinct().ToList();
        var seriesField = spec.Kind is ChartKind.Pie or ChartKind.Scatter ? null : Valid(spec.SeriesField);
        if (seriesField is not null && yFields.Count > 1) yFields = [yFields[0]];
        if (spec.Kind == ChartKind.Pie && yFields.Count > 1) yFields = [yFields[0]];
        var aggregate = yFields.Count == 0 ? PivotAggregate.Count : spec.Aggregate;

        static object? CellOf(IReadOnlyList<object?> cells, int column) => column < cells.Count ? cells[column] : null;

        var axis = ChartAxisKind.Category;
        if (xField is { } xf && spec.Kind is ChartKind.Line or ChartKind.Area or ChartKind.Scatter)
            axis = AxisFor(rows.Select(r => CellOf(values(r), xf)));
        if (spec.Kind == ChartKind.Scatter && axis == ChartAxisKind.Category) axis = ChartAxisKind.Number; // scatter X is the row number then

        var xTitle = xField is { } xn ? columnNames[xn] : "Row";
        var yTitle = yFields.Count == 0 ? "Rows"
            : spec.Kind == ChartKind.Scatter ? (yFields.Count == 1 ? columnNames[yFields[0]] : "Value")
            : yFields.Count == 1 ? $"{PivotSpec.Describe(aggregate)} of {columnNames[yFields[0]]}"
            : PivotSpec.Describe(aggregate);

        return spec.Kind == ChartKind.Scatter
            ? BuildScatter(rows, values, columnNames, xField, yFields, axis, xTitle, yTitle)
            : BuildGrouped(rows, values, columnNames, spec.Kind, xField, yFields, seriesField, aggregate, axis, xTitle, yTitle);
    }

    private static ChartData BuildScatter<T>(IReadOnlyList<T> rows, Func<T, IReadOnlyList<object?>> values, IReadOnlyList<string> columnNames,
        int? xField, List<int> yFields, ChartAxisKind axis, string xTitle, string yTitle)
    {
        if (yFields.Count > MaxSeries) yFields = yFields.Take(MaxSeries).ToList();
        var labels = new List<string>();
        var xs = new List<double>();
        var series = yFields.Select(_ => new List<double?>()).ToList();
        var skipped = 0;
        var index = 0;
        foreach (var row in rows)
        {
            index++;
            var cells = values(row);
            double? x = xField is { } xf ? ToAxisValue(CellOf(cells, xf), axis) : index;
            if (x is null) { skipped++; continue; }
            var ys = yFields.Select(f => ToNumber(CellOf(cells, f))).ToList();
            if (ys.All(y => y is null)) { skipped++; continue; }
            if (labels.Count >= MaxLinePoints) continue;
            labels.Add(xField is { } lf ? Label(CellOf(cells, lf)) : index.ToString("N0", CultureInfo.CurrentCulture));
            xs.Add(x.Value);
            for (var i = 0; i < ys.Count; i++) series[i].Add(ys[i]);
        }
        var drawn = labels.Count + skipped;
        var note = rows.Count > drawn ? $"only the first {MaxLinePoints:N0} points are drawn" : null;
        var list = yFields.Select((f, i) => new ChartSeries(columnNames[f], series[i])).ToList();
        if (list.Count == 0) list.Add(new ChartSeries("Rows", labels.Select(_ => (double?)null).ToList()));
        return new ChartData(ChartKind.Scatter, axis, xTitle, yTitle, labels, xs, list, rows.Count, skipped, note);

        static object? CellOf(IReadOnlyList<object?> cells, int column) => column < cells.Count ? cells[column] : null;
    }

    private static ChartData BuildGrouped<T>(IReadOnlyList<T> rows, Func<T, IReadOnlyList<object?>> values, IReadOnlyList<string> columnNames,
        ChartKind kind, int? xField, List<int> yFields, int? seriesField, PivotAggregate aggregate, ChartAxisKind axis, string xTitle, string yTitle)
    {
        static object? CellOf(IReadOnlyList<object?> cells, int column) => column < cells.Count ? cells[column] : null;
        var notes = new List<string>();
        var skipped = 0;

        // Series: one per value column, or one per value of the split column (in grid order, capped).
        List<string> seriesNames;
        Dictionary<string, int>? seriesIndex = null;
        var nullSeries = -1;
        if (seriesField is { } sf)
        {
            var distinct = new Dictionary<string, object?>();
            var hasNull = false;
            foreach (var row in rows)
            {
                var cell = CellOf(values(row), sf);
                if (ResultViewQuery.DisplayText(cell) is { } text) distinct.TryAdd(text, cell);
                else hasNull = true;
            }
            var ordered = distinct.OrderBy(kv => kv.Value, ResultViewQuery.ValueComparer).Select(kv => (string?)kv.Key).ToList();
            if (hasNull) ordered.Insert(0, null);
            if (ordered.Count > MaxSeries) notes.Add($"only the first {MaxSeries} {columnNames[sf]} values are drawn");
            ordered = ordered.Take(MaxSeries).ToList();
            seriesNames = ordered.Select(o => o ?? NullLabel).ToList();
            seriesIndex = [];
            for (var i = 0; i < ordered.Count; i++)
                if (ordered[i] is { } key) seriesIndex[key] = i;
                else nullSeries = i;
        }
        else if (yFields.Count == 0) seriesNames = ["Rows"];
        else
        {
            if (yFields.Count > MaxSeries)
            {
                notes.Add($"only the first {MaxSeries} value columns are drawn");
                yFields = yFields.Take(MaxSeries).ToList();
            }
            seriesNames = yFields.Select(f => columnNames[f]).ToList();
        }

        // Points: one per distinct X value.
        var groups = new Dictionary<string, (object? X, Summary[] Cells)>();
        foreach (var row in rows)
        {
            var cells = values(row);
            var x = xField is { } xf ? CellOf(cells, xf) : null;
            if (axis != ChartAxisKind.Category && ToAxisValue(x, axis) is null) { skipped++; continue; }
            var key = xField is null ? "" : ResultViewQuery.DisplayText(x) is { } t ? "v" + t : "\u0000";
            if (!groups.TryGetValue(key, out var group))
            {
                group = (x, NewSummaries(seriesNames.Count));
                groups[key] = group;
            }
            if (seriesIndex is not null)
            {
                var s = ResultViewQuery.DisplayText(CellOf(cells, seriesField!.Value)) is { } st ? seriesIndex.GetValueOrDefault(st, -1) : nullSeries;
                if (s < 0) continue;
                Add(group.Cells[s], yFields.Count == 0 ? null : CellOf(cells, yFields[0]), yFields.Count == 0);
            }
            else if (yFields.Count == 0) Add(group.Cells[0], null, true);
            else
                for (var i = 0; i < yFields.Count; i++) Add(group.Cells[i], CellOf(cells, yFields[i]), false);
        }

        // Labels keep the order the rows are in (the query's ORDER BY, or the grid's sort); a scale is in value order.
        var points = groups.Values.ToList();
        if (axis != ChartAxisKind.Category) points.Sort((a, b) => ResultViewQuery.CompareValues(a.X, b.X));
        var max = kind == ChartKind.Bar ? MaxBarPoints : MaxLinePoints;
        if (kind != ChartKind.Pie && points.Count > max)
        {
            notes.Add($"only the first {max:N0} of {points.Count:N0} {xTitle} values are drawn");
            points = points.Take(max).ToList();
        }

        var labels = points.Select(p => xField is null ? "All rows" : Label(p.X)).ToList();
        var xs = axis == ChartAxisKind.Category ? Enumerable.Range(0, points.Count).Select(i => (double)i).ToList()
            : points.Select(p => ToAxisValue(p.X, axis)!.Value).ToList();
        var series = seriesNames.Select((name, s) => new ChartSeries(name, points.Select(p => p.Cells[s].Result(aggregate)).ToList())).ToList();

        if (kind == ChartKind.Pie) return Pie(labels, series[0], xTitle, yTitle, rows.Count, skipped);
        return new ChartData(kind, axis, xTitle, yTitle, labels, xs, series, rows.Count, skipped, notes.Count == 0 ? null : string.Join(" · ", notes));
    }

    /// <summary>The biggest slices first; past <see cref="MaxPieSlices"/> the rest fold into "Other". A pie can't show
    /// negative or empty values, so those are left out and counted in the note.</summary>
    private static ChartData Pie(List<string> labels, ChartSeries series, string xTitle, string yTitle, int sourceRows, int skipped)
    {
        var slices = labels.Select((l, i) => (Label: l, Value: series.Values[i])).ToList();
        var dropped = slices.Count(s => s.Value is not > 0);
        var positive = slices.Where(s => s.Value is > 0).OrderByDescending(s => s.Value!.Value).ToList();
        var keep = positive.Count > MaxPieSlices ? positive.Take(MaxPieSlices - 1).ToList() : positive;
        if (positive.Count > MaxPieSlices)
            keep.Add((OtherLabel, positive.Skip(MaxPieSlices - 1).Sum(s => s.Value!.Value)));
        var note = dropped > 0 ? $"{dropped:N0} zero, negative or empty value(s) left out" : null;
        if (positive.Count > MaxPieSlices)
            note = (note is null ? "" : note + " · ") + $"{positive.Count - MaxPieSlices + 1:N0} smaller slices grouped as {OtherLabel}";
        return new ChartData(ChartKind.Pie, ChartAxisKind.Category, xTitle, yTitle,
            keep.Select(s => s.Label).ToList(), Enumerable.Range(0, keep.Count).Select(i => (double)i).ToList(),
            [new ChartSeries(series.Name, keep.Select(s => s.Value).ToList())], sourceRows, skipped, note);
    }

    /// <summary>Number when every non-NULL X is a number, time when every one is a date, otherwise labels.</summary>
    public static ChartAxisKind AxisFor(IEnumerable<object?> xs)
    {
        bool numbers = true, dates = true, any = false;
        foreach (var x in xs)
        {
            if (x is null or DBNull) continue;
            any = true;
            numbers &= ToNumber(x) is not null;
            dates &= ToDate(x) is not null;
            if (!numbers && !dates) break;
        }
        return !any ? ChartAxisKind.Category : numbers ? ChartAxisKind.Number : dates ? ChartAxisKind.Time : ChartAxisKind.Category;
    }

    public static bool IsDateColumn(IEnumerable<object?> sample)
    {
        var any = false;
        foreach (var value in sample)
        {
            if (value is null or DBNull) continue;
            if (ToDate(value) is null) return false;
            any = true;
        }
        return any;
    }

    private static double? ToAxisValue(object? value, ChartAxisKind axis) => axis switch
    {
        ChartAxisKind.Number => ToNumber(value),
        ChartAxisKind.Time => ToDate(value)?.Ticks,
        _ => null
    };

    public static double? ToNumber(object? value) => value switch
    {
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture) is var d && double.IsFinite(d) ? d : null,
        _ => null
    };

    private static DateTime? ToDate(object? value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.DateTime, // the clock time the grid shows
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => null
    };

    private static string Label(object? value) => value switch
    {
        DateTime { TimeOfDay.Ticks: 0 } dt => dt.ToString("d", CultureInfo.CurrentCulture),
        DateOnly d => d.ToString("d", CultureInfo.CurrentCulture),
        _ => ResultViewQuery.DisplayText(value) ?? NullLabel
    };

    // ------------------------------------------------------------------ summaries

    private sealed class Summary
    {
        public int Rows;
        public int Numbers;
        public double Sum;
        public double Min = double.PositiveInfinity;
        public double Max = double.NegativeInfinity;

        public double? Result(PivotAggregate aggregate) => aggregate switch
        {
            PivotAggregate.Count => Rows == 0 ? null : Rows,
            _ when Numbers == 0 => null,
            PivotAggregate.Sum => Sum,
            PivotAggregate.Average => Sum / Numbers,
            PivotAggregate.Min => Min,
            PivotAggregate.Max => Max,
            _ => null
        };
    }

    private static Summary[] NewSummaries(int count)
    {
        var list = new Summary[count];
        for (var i = 0; i < count; i++) list[i] = new Summary();
        return list;
    }

    /// <summary>Counts the row (or the non-NULL value) and adds the value when it is a number.</summary>
    private static void Add(Summary summary, object? value, bool countsRow)
    {
        if (countsRow) { summary.Rows++; return; }
        if (value is null or DBNull) return;
        summary.Rows++;
        if (ToNumber(value) is not { } d) return;
        summary.Numbers++;
        summary.Sum += d;
        summary.Min = Math.Min(summary.Min, d);
        summary.Max = Math.Max(summary.Max, d);
    }

    // ------------------------------------------------------------------ axes

    /// <summary>Evenly spaced round numbers (1, 2, 2.5 or 5 × a power of ten) covering <paramref name="min"/>..<paramref name="max"/>,
    /// about <paramref name="target"/> of them. The first and last tick are the ends of the axis.</summary>
    public static IReadOnlyList<double> NiceTicks(double min, double max, int target = 5)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return [0, 1];
        if (min > max) (min, max) = (max, min);
        if (min == max)
        {
            if (min == 0) return [0, 1];
            var pad = Math.Abs(min) * 0.1;
            (min, max) = (min - pad, max + pad);
        }
        var step = NiceStep((max - min) / Math.Max(1, target));
        var first = Math.Floor(min / step) * step;
        var last = Math.Ceiling(max / step) * step;
        var ticks = new List<double>();
        for (var v = first; v <= last + step / 2 && ticks.Count < 100; v += step)
            ticks.Add(Math.Abs(v) < step * 1e-9 ? 0 : Math.Round(v / step) * step);
        return ticks;
    }

    private static double NiceStep(double raw)
    {
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var fraction = raw / power;
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;
        return nice * power;
    }

    /// <summary>A short label for an axis: 1.2K, 3.4M, 5B; small numbers keep the decimals the step needs.</summary>
    public static string FormatNumber(double value, double step = 0)
    {
        var abs = Math.Abs(value);
        var culture = CultureInfo.CurrentCulture;
        if (abs >= 1e12) return (value / 1e12).ToString("0.##", culture) + "T";
        if (abs >= 1e9) return (value / 1e9).ToString("0.##", culture) + "B";
        if (abs >= 1e6) return (value / 1e6).ToString("0.##", culture) + "M";
        if (abs >= 1e4) return (value / 1e3).ToString("0.##", culture) + "K";
        var decimals = step is > 0 and < 1 ? Math.Min(6, (int)Math.Ceiling(-Math.Log10(step))) : 0;
        if (step == 0 && abs is > 0 and < 1) decimals = 4;
        return value.ToString((decimals == 0 ? "#,0" : "#,0." + new string('#', decimals)), culture);
    }

    /// <summary>A tooltip value: the full number with grouping, rounded to 6 decimals.</summary>
    public static string FormatValue(double value) =>
        Math.Round(value, 6).ToString("#,0.######", CultureInfo.CurrentCulture);

    /// <summary>Ticks on a time axis at a round unit (seconds up to years) chosen so about <paramref name="target"/> fit,
    /// labelled at that unit. Values are <see cref="DateTime.Ticks"/>.</summary>
    public static IReadOnlyList<ChartTick> TimeTicks(double min, double max, int target = 6)
    {
        var culture = CultureInfo.CurrentCulture;
        var minTicks = (long)Math.Clamp(min, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        var maxTicks = (long)Math.Clamp(max, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        var start = new DateTime(minTicks);
        var end = new DateTime(maxTicks);
        if (end <= start) return [new ChartTick(minTicks, start.TimeOfDay.Ticks == 0 ? start.ToString("d", culture) : start.ToString("g", culture))];
        var span = end - start;

        (TimeSpan Size, int Months, string Format)[] units =
        [
            (TimeSpan.FromSeconds(1), 0, "T"), (TimeSpan.FromSeconds(5), 0, "T"), (TimeSpan.FromSeconds(15), 0, "T"), (TimeSpan.FromSeconds(30), 0, "T"),
            (TimeSpan.FromMinutes(1), 0, "t"), (TimeSpan.FromMinutes(5), 0, "t"), (TimeSpan.FromMinutes(15), 0, "t"), (TimeSpan.FromMinutes(30), 0, "t"),
            (TimeSpan.FromHours(1), 0, "t"), (TimeSpan.FromHours(3), 0, "t"), (TimeSpan.FromHours(6), 0, "g"), (TimeSpan.FromHours(12), 0, "g"),
            (TimeSpan.FromDays(1), 0, "d"), (TimeSpan.FromDays(2), 0, "d"), (TimeSpan.FromDays(7), 0, "d"), (TimeSpan.FromDays(14), 0, "d"),
            (default, 1, "MMM yyyy"), (default, 3, "MMM yyyy"), (default, 6, "MMM yyyy"), (default, 12, "yyyy"), (default, 24, "yyyy"), (default, 60, "yyyy"),
            (default, 120, "yyyy"), (default, 600, "yyyy"), (default, 1200, "yyyy")
        ];
        var chosen = units[^1];
        foreach (var unit in units)
        {
            var approx = unit.Months > 0 ? TimeSpan.FromDays(30.44 * unit.Months) : unit.Size;
            if (span.Ticks / approx.Ticks <= target) { chosen = unit; break; }
        }

        var ticks = new List<ChartTick>();
        DateTime t;
        if (chosen.Months > 0)
        {
            var monthIndex = start.Year * 12 + start.Month - 1;
            monthIndex = (int)Math.Ceiling((double)monthIndex / chosen.Months) * chosen.Months;
            if (new DateTime(monthIndex / 12, monthIndex % 12 + 1, 1) < start) monthIndex += chosen.Months;
            for (; ticks.Count < 100; monthIndex += chosen.Months)
            {
                if (monthIndex / 12 > 9999) break;
                t = new DateTime(monthIndex / 12, monthIndex % 12 + 1, 1);
                if (t > end) break;
                ticks.Add(new ChartTick(t.Ticks, t.ToString(chosen.Format, culture)));
            }
        }
        else
        {
            var size = chosen.Size.Ticks;
            var first = (start.Ticks + size - 1) / size * size;
            for (var v = first; v <= end.Ticks && ticks.Count < 100; v += size)
            {
                t = new DateTime(v);
                ticks.Add(new ChartTick(v, t.ToString(chosen.Format, culture)));
            }
        }
        return ticks;
    }
}
