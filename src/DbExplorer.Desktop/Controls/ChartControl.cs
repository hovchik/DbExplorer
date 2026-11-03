using DbExplorer.Desktop.Services;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// Draws <see cref="ChartData"/> as a bar, line, area, scatter or pie chart that fills the control. Hovering shows the
/// values under the pointer (a band on bars, a crosshair on lines and areas, the nearest dot on scatter plots, the
/// slice on pies). Series take the palette's colours in order, so a series keeps its colour when others come and go.
/// </summary>
public sealed class ChartControl : Control
{
    public static readonly StyledProperty<ChartData?> DataProperty =
        AvaloniaProperty.Register<ChartControl, ChartData?>(nameof(Data));

    /// <summary>Categorical colours, validated for colour-blind separation on the light and dark surfaces.</summary>
    private static readonly Color[] LightPalette = Parse("#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948");
    private static readonly Color[] DarkPalette = Parse("#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767");

    private const double FontSize = 11.5;
    private const double Pad = 14;

    private Point? _pointer;
    private Rect _plot;
    private Layout? _layout;

    static ChartControl()
    {
        AffectsRender<ChartControl>(DataProperty, CaptionProperty);
    }

    public ChartControl()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ClipToBounds = true;
    }

    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<ChartControl, string?>(nameof(Caption));

    public ChartData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>A line about what is charted (points, rows, what was left out), drawn small in the bottom-right corner.</summary>
    public string? Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    /// <summary>The colour of series <paramref name="index"/> in the current theme.</summary>
    public Color SeriesColor(int index) => (IsDark ? DarkPalette : LightPalette)[index % LightPalette.Length];

    private bool IsDark => AppThemes.IsDark(ActualThemeVariant);

    /// <summary>Writes the chart as it looks now (without the hover tooltip) as a PNG at twice the screen resolution.</summary>
    public void SavePng(Stream stream)
    {
        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1) throw new InvalidOperationException("The chart isn't showing.");
        var pointer = _pointer;
        _pointer = null;
        try
        {
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2)), new Vector(192, 192));
            bitmap.Render(this);
            bitmap.Save(stream);
        }
        finally
        {
            _pointer = pointer;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    private sealed record Layout(double YMin, double YMax, double XMin, double XMax, IReadOnlyList<double> YTicks);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var surface = Brush("AppPopupBackgroundBrush", Brushes.White);
        context.FillRectangle(surface, bounds);
        var fg = Brush("AppForegroundBrush", Brushes.Black);
        var hint = Brush("AppHintForegroundBrush", Brushes.Gray);

        if (Data is not { } data || data.IsEmpty)
        {
            var message = Data is null ? "" : "Nothing to chart: pick an X column and a value column that holds numbers.";
            DrawText(context, message, new Point(Pad, Pad), hint, 12.5, FontWeight.Normal, bounds.Width - 2 * Pad);
            return;
        }
        if (bounds.Width < 120 || bounds.Height < 90) return;

        if (data.Kind == ChartKind.Pie) { RenderPie(context, data, bounds, fg, hint, surface); return; }

        var grid = new ImmutableSolidColorBrush(((ISolidColorBrush)hint).Color, IsDark ? 0.16 : 0.13);
        var axisBrush = new ImmutableSolidColorBrush(((ISolidColorBrush)hint).Color, 0.5);

        // Legend across the top when there is more than one series; a single series is named by the Y title.
        // The Y title on the left; the legend on the same line to the right when it fits, otherwise below.
        var titleWidth = Math.Min(bounds.Width / 2, Measure(data.YTitle, FontSize, FontWeight.SemiBold));
        DrawText(context, data.YTitle, new Point(Pad, Pad), hint, FontSize, FontWeight.SemiBold, bounds.Width / 2);
        var top = Pad + 18;
        if (data.Series.Count > 1)
        {
            var legendWidth = LegendWidth(data);
            top = legendWidth <= bounds.Width - 2 * Pad - titleWidth - 24
                ? DrawLegend(context, data, new Rect(bounds.Width - Pad - legendWidth, Pad, legendWidth + 20, 60), fg)
                : DrawLegend(context, data, new Rect(Pad, Pad + 20, bounds.Width - 2 * Pad, 60), fg);
        }
        top += 12; // room for the top tick label

        // Y range: bars and areas start at zero; lines and dots fit the data.
        var values = data.Series.SelectMany(s => s.Values).OfType<double>().ToList();
        double yMin = values.Min(), yMax = values.Max();
        if (data.Kind is ChartKind.Bar or ChartKind.Area) { yMin = Math.Min(0, yMin); yMax = Math.Max(0, yMax); }
        var plotHeightGuess = bounds.Height - top - 44;
        var yTicks = ResultChart.NiceTicks(yMin, yMax, Math.Clamp((int)(plotHeightGuess / 48), 2, 8));
        var yStep = yTicks.Count > 1 ? yTicks[1] - yTicks[0] : 0;
        var yLabels = yTicks.Select(t => ResultChart.FormatNumber(t, yStep)).ToList();
        var left = Pad + yLabels.Max(l => Measure(l, FontSize)) + 8;

        var xTitleHeight = 18;
        var bottom = bounds.Height - Pad - xTitleHeight - 16;
        _plot = new Rect(left, top, Math.Max(10, bounds.Width - left - Pad - 6), Math.Max(10, bottom - top));

        double xMin = 0, xMax = Math.Max(1, data.PointCount);
        if (data.XAxis != ChartAxisKind.Category)
        {
            xMin = data.XValues.Min();
            xMax = data.XValues.Max();
            if (xMin == xMax) { xMin -= 1; xMax += 1; }
            if (data.XAxis == ChartAxisKind.Number)
            {
                var nice = ResultChart.NiceTicks(xMin, xMax, Math.Clamp((int)(_plot.Width / 90), 2, 10));
                if (data.Kind == ChartKind.Scatter) { xMin = nice[0]; xMax = nice[^1]; }
            }
            else if (data.Kind == ChartKind.Scatter)
            {
                var pad = (xMax - xMin) * 0.03;
                xMin -= pad; xMax += pad;
            }
        }
        _layout = new Layout(yTicks[0], yTicks[^1], xMin, xMax, yTicks);

        // Grid lines and Y labels.
        var spacing = _plot.Height / Math.Max(1, yTicks.Count - 1);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(16 / Math.Max(1, spacing))); // a short chart labels fewer ticks
        for (var i = 0; i < yTicks.Count; i++)
        {
            var y = Y(yTicks[i]);
            context.DrawLine(RenderPens.Create(yTicks[i] == 0 ? axisBrush : grid, 1), new Point(_plot.Left, Snap(y)), new Point(_plot.Right, Snap(y)));
            if (i % labelEvery != 0 && i != yTicks.Count - 1) continue;
            var w = Measure(yLabels[i], FontSize);
            DrawText(context, yLabels[i], new Point(left - 8 - w, y - 8), hint, FontSize, FontWeight.Normal, w + 2);
        }
        context.DrawLine(RenderPens.Create(axisBrush, 1), new Point(_plot.Left, Snap(_plot.Bottom)), new Point(_plot.Right, Snap(_plot.Bottom)));

        DrawXAxis(context, data, hint, axisBrush);
        var xTitleWidth = Measure(data.XTitle, FontSize, FontWeight.SemiBold);
        var xTitleLeft = _plot.Left + (_plot.Width - xTitleWidth) / 2;
        var footerY = bounds.Height - Pad - xTitleHeight + 2;
        DrawText(context, data.XTitle, new Point(xTitleLeft, footerY), hint, FontSize, FontWeight.SemiBold, _plot.Width);
        DrawCaption(context, xTitleLeft + xTitleWidth + 16, footerY, bounds, hint);

        var hover = HoverIndex(data);
        using (context.PushClip(_plot.Inflate(new Thickness(6, 6, 6, 1))))
        {
            switch (data.Kind)
            {
                case ChartKind.Bar: RenderBars(context, data, hover, grid); break;
                case ChartKind.Line or ChartKind.Area: RenderLines(context, data, surface, hover, axisBrush); break;
                case ChartKind.Scatter: RenderScatter(context, data, surface, hover); break;
            }
        }
        if (hover is { } h) DrawTooltip(context, data, h, fg, hint, bounds);
    }

    // ------------------------------------------------------------------ marks

    private void RenderBars(DrawingContext context, ChartData data, int? hover, IBrush band)
    {
        var count = data.PointCount;
        var bandWidth = _plot.Width / count;
        var series = data.Series.Count;
        var groupWidth = bandWidth * (count == 1 ? 0.5 : 0.78);
        const double gap = 2;
        var barWidth = Math.Max(1, (groupWidth - gap * (series - 1)) / series);
        if (hover is { } h) context.FillRectangle(band, new Rect(_plot.Left + h * bandWidth, _plot.Top, bandWidth, _plot.Height));
        var zero = Y(Math.Clamp(0, _layout!.YMin, _layout.YMax));
        for (var s = 0; s < series; s++)
        {
            var brush = new ImmutableSolidColorBrush(SeriesColor(s));
            var values = data.Series[s].Values;
            for (var i = 0; i < count; i++)
            {
                if (values[i] is not { } v) continue;
                var x = _plot.Left + i * bandWidth + (bandWidth - groupWidth) / 2 + s * (barWidth + gap);
                var y = Y(v);
                var height = Math.Abs(zero - y);
                if (height < 0.5) continue;
                context.DrawGeometry(brush, null, BarGeometry(new Rect(x, Math.Min(y, zero), barWidth, height), Math.Min(4, barWidth / 2), v < 0));
            }
        }
    }

    /// <summary>A bar rounded at its data end (the top, or the bottom of a negative bar), square at the baseline.</summary>
    private static StreamGeometry BarGeometry(Rect r, double radius, bool down)
    {
        radius = Math.Min(radius, r.Height);
        var g = new StreamGeometry();
        using var c = g.Open();
        var size = new Size(radius, radius);
        if (!down)
        {
            c.BeginFigure(new Point(r.Left, r.Bottom), true);
            c.LineTo(new Point(r.Left, r.Top + radius));
            c.ArcTo(new Point(r.Left + radius, r.Top), size, 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(r.Right - radius, r.Top));
            c.ArcTo(new Point(r.Right, r.Top + radius), size, 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(r.Right, r.Bottom));
        }
        else
        {
            c.BeginFigure(new Point(r.Left, r.Top), true);
            c.LineTo(new Point(r.Right, r.Top));
            c.LineTo(new Point(r.Right, r.Bottom - radius));
            c.ArcTo(new Point(r.Right - radius, r.Bottom), size, 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(r.Left + radius, r.Bottom));
            c.ArcTo(new Point(r.Left, r.Bottom - radius), size, 0, false, SweepDirection.Clockwise);
        }
        c.EndFigure(true);
        return g;
    }

    private void RenderLines(DrawingContext context, ChartData data, IBrush surface, int? hover, IBrush axisBrush)
    {
        if (hover is { } h)
        {
            var hx = X(data, h);
            context.DrawLine(RenderPens.Create(axisBrush, 1), new Point(Snap(hx), _plot.Top), new Point(Snap(hx), _plot.Bottom));
        }
        var markers = data.PointCount <= Math.Max(2, _plot.Width / 14);
        var zero = Y(Math.Clamp(0, _layout!.YMin, _layout.YMax));
        for (var s = 0; s < data.Series.Count; s++)
        {
            var color = SeriesColor(s);
            var brush = new ImmutableSolidColorBrush(color);
            var values = data.Series[s].Values;

            // Runs of consecutive values; a NULL leaves a gap.
            var runs = new List<List<Point>>();
            List<Point>? run = null;
            for (var i = 0; i < data.PointCount; i++)
            {
                if (values[i] is not { } v) { run = null; continue; }
                if (run is null) runs.Add(run = []);
                run.Add(new Point(X(data, i), Y(v)));
            }

            foreach (var points in runs)
            {
                if (data.Kind == ChartKind.Area && points.Count > 1)
                {
                    var fill = new StreamGeometry();
                    using (var c = fill.Open())
                    {
                        c.BeginFigure(new Point(points[0].X, zero), true);
                        foreach (var p in points) c.LineTo(p);
                        c.LineTo(new Point(points[^1].X, zero));
                        c.EndFigure(true);
                    }
                    context.DrawGeometry(new ImmutableSolidColorBrush(color, data.Series.Count > 1 ? 0.14 : 0.22), null, fill);
                }
                if (points.Count > 1)
                {
                    var line = new StreamGeometry();
                    using (var c = line.Open())
                    {
                        c.BeginFigure(points[0], false);
                        foreach (var p in points.Skip(1)) c.LineTo(p);
                        c.EndFigure(false);
                    }
                    context.DrawGeometry(null, RenderPens.Create(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
                }
                if (markers || points.Count == 1)
                    foreach (var p in points)
                        context.DrawEllipse(brush, RenderPens.Create(surface, 1.5), p, 3.5, 3.5);
            }
            if (hover is { } hi && values[hi] is { } hv)
                context.DrawEllipse(brush, RenderPens.Create(surface, 2), new Point(X(data, hi), Y(hv)), 5, 5);
        }
    }

    private void RenderScatter(DrawingContext context, ChartData data, IBrush surface, int? hover)
    {
        var ring = RenderPens.Create(surface, 1);
        for (var s = 0; s < data.Series.Count; s++)
        {
            var brush = new ImmutableSolidColorBrush(SeriesColor(s), 0.8);
            var values = data.Series[s].Values;
            for (var i = 0; i < data.PointCount; i++)
                if (values[i] is { } v) context.DrawEllipse(brush, ring, new Point(X(data, i), Y(v)), 4, 4);
        }
        if (hover is { } h)
            for (var s = 0; s < data.Series.Count; s++)
                if (data.Series[s].Values[h] is { } v)
                    context.DrawEllipse(new ImmutableSolidColorBrush(SeriesColor(s)), RenderPens.Create(surface, 2), new Point(X(data, h), Y(v)), 6, 6);
    }

    private void RenderPie(DrawingContext context, ChartData data, Rect bounds, IBrush fg, IBrush hint, IBrush surface)
    {
        var values = data.Series[0].Values.Select(v => v ?? 0).ToList();
        var total = values.Sum();
        DrawText(context, data.YTitle + " by " + data.XTitle, new Point(Pad, Pad), hint, FontSize, FontWeight.SemiBold, bounds.Width - 2 * Pad);

        // Legend on the right (below on narrow charts), the donut in the rest.
        var labels = data.Labels.Select((l, i) => $"{l}  {values[i] / total:P1}").ToList();
        var legendWidth = Math.Min(260, labels.Max(l => Measure(l, FontSize)) + 26);
        var side = bounds.Width - legendWidth > bounds.Height * 0.9;
        var area = side
            ? new Rect(Pad, Pad + 22, bounds.Width - legendWidth - 3 * Pad, bounds.Height - 2 * Pad - 22)
            : new Rect(Pad, Pad + 22, bounds.Width - 2 * Pad, bounds.Height - 2 * Pad - 22 - Math.Min(140, labels.Count * 20 + 8));
        var radius = Math.Max(10, Math.Min(area.Width, area.Height) / 2 - 4);
        var center = area.Center;
        var inner = radius * 0.56;

        var hover = (int?)null;
        if (_pointer is { } p)
        {
            var d = p - center;
            var dist = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (dist >= inner && dist <= radius + 4)
            {
                var angle = (Math.Atan2(d.Y, d.X) + Math.PI / 2 + 2 * Math.PI) % (2 * Math.PI);
                var acc = 0.0;
                for (var i = 0; i < values.Count; i++)
                {
                    acc += values[i] / total * 2 * Math.PI;
                    if (angle <= acc) { hover = i; break; }
                }
            }
        }

        var start = -Math.PI / 2;
        for (var i = 0; i < values.Count; i++)
        {
            var sweep = values[i] / total * 2 * Math.PI;
            var r = hover == i ? radius + 4 : radius;
            var brush = new ImmutableSolidColorBrush(SeriesColor(i));
            if (sweep >= 2 * Math.PI - 1e-6)
            {
                context.DrawEllipse(brush, null, center, r, r);
                context.DrawEllipse(surface, null, center, inner, inner);
            }
            else context.DrawGeometry(brush, RenderPens.Create(surface, 2), Slice(center, inner, r, start, sweep));
            start += sweep;
        }
        var totalText = ResultChart.FormatNumber(total);
        var tw = Measure(totalText, 16, FontWeight.SemiBold);
        DrawText(context, totalText, new Point(center.X - tw / 2, center.Y - 14), fg, 16, FontWeight.SemiBold, inner * 2);
        var tl = Measure("Total", FontSize);
        DrawText(context, "Total", new Point(center.X - tl / 2, center.Y + 6), hint, FontSize, FontWeight.Normal, inner * 2);

        var lx = side ? bounds.Width - legendWidth - Pad : Pad;
        var ly = side ? Math.Max(Pad + 22, center.Y - labels.Count * 10) : area.Bottom + 8;
        for (var i = 0; i < labels.Count; i++)
        {
            context.DrawRectangle(new ImmutableSolidColorBrush(SeriesColor(i)), null, new Rect(lx, ly + i * 20 + 3, 10, 10), 2, 2);
            DrawText(context, labels[i], new Point(lx + 16, ly + i * 20), hover == i ? fg : hint, FontSize, hover == i ? FontWeight.SemiBold : FontWeight.Normal, legendWidth - 16);
        }

        DrawCaption(context, Pad, bounds.Height - Pad - 16, bounds, hint);
        if (hover is { } h)
        {
            _layout = null;
            DrawTooltipBox(context, data.Labels[h], [(SeriesColor(h), data.Series[0].Name, $"{ResultChart.FormatValue(values[h])}  ({values[h] / total:P1})")], fg, hint, bounds);
        }
    }

    private static StreamGeometry Slice(Point c, double inner, double outer, double start, double sweep)
    {
        Point At(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        var large = sweep > Math.PI;
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(At(outer, start), true);
        ctx.ArcTo(At(outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise);
        ctx.LineTo(At(inner, start + sweep));
        ctx.ArcTo(At(inner, start), new Size(inner, inner), 0, large, SweepDirection.CounterClockwise);
        ctx.EndFigure(true);
        return g;
    }

    // ------------------------------------------------------------------ axes, legend, tooltip

    private void DrawXAxis(DrawingContext context, ChartData data, IBrush hint, IBrush axisBrush)
    {
        var labelY = _plot.Bottom + 5;
        if (data.XAxis == ChartAxisKind.Category)
        {
            var bandWidth = _plot.Width / data.PointCount;
            var widest = data.Labels.Max(l => Math.Min(160, Measure(l, FontSize))) + 10;
            var every = Math.Max(1, (int)Math.Ceiling(widest / bandWidth));
            var maxWidth = Math.Max(bandWidth * every - 6, 20);
            for (var i = 0; i < data.PointCount; i += every)
            {
                var w = Math.Min(Measure(data.Labels[i], FontSize), maxWidth);
                var cx = _plot.Left + (i + 0.5) * bandWidth;
                DrawText(context, data.Labels[i], new Point(cx - w / 2, labelY), hint, FontSize, FontWeight.Normal, maxWidth);
            }
            return;
        }

        IReadOnlyList<ChartTick> ticks;
        if (data.XAxis == ChartAxisKind.Time)
            ticks = ResultChart.TimeTicks(_layout!.XMin, _layout.XMax, Math.Clamp((int)(_plot.Width / 110), 2, 12));
        else
        {
            var nice = ResultChart.NiceTicks(_layout!.XMin, _layout.XMax, Math.Clamp((int)(_plot.Width / 90), 2, 10));
            var step = nice.Count > 1 ? nice[1] - nice[0] : 0;
            ticks = nice.Where(v => v >= _layout.XMin - 1e-9 && v <= _layout.XMax + 1e-9).Select(v => new ChartTick(v, ResultChart.FormatNumber(v, step))).ToList();
        }
        var lastRight = double.NegativeInfinity;
        foreach (var tick in ticks)
        {
            var x = XValue(tick.Value);
            if (x < _plot.Left - 1 || x > _plot.Right + 1) continue;
            context.DrawLine(RenderPens.Create(axisBrush, 1), new Point(Snap(x), _plot.Bottom), new Point(Snap(x), _plot.Bottom + 4));
            var w = Measure(tick.Label, FontSize);
            var lx = Math.Clamp(x - w / 2, _plot.Left - 20, Bounds.Width - Pad - w);
            if (lx < lastRight + 8) continue;
            DrawText(context, tick.Label, new Point(lx, labelY + 2), hint, FontSize, FontWeight.Normal, w + 2);
            lastRight = lx + w;
        }
    }

    /// <summary>The caption right-aligned on a line, trimmed to start no further left than <paramref name="left"/>.</summary>
    private void DrawCaption(DrawingContext context, double left, double y, Rect bounds, IBrush hint)
    {
        if (string.IsNullOrEmpty(Caption)) return;
        var room = bounds.Width - Pad - left;
        if (room < 60) return;
        var width = Math.Min(room, Measure(Caption, 10.5));
        DrawText(context, Caption, new Point(bounds.Width - Pad - width, y + 1), hint, 10.5, FontWeight.Normal, width + 1);
    }

    private static double LegendWidth(ChartData data) =>
        data.Series.Sum(s => Math.Min(200, Measure(s.Name, FontSize)) + 30) - 14;

    /// <summary>Draws the series names as swatches in rows; returns the bottom of the legend.</summary>
    private double DrawLegend(DrawingContext context, ChartData data, Rect area, IBrush fg)
    {
        double x = area.Left, y = area.Top;
        for (var s = 0; s < data.Series.Count; s++)
        {
            var name = data.Series[s].Name;
            var w = Math.Min(200, Measure(name, FontSize)) + 30;
            if (x + w > area.Right && x > area.Left) { x = area.Left; y += 18; }
            context.DrawRectangle(new ImmutableSolidColorBrush(SeriesColor(s)), null, new Rect(x, y + 3, 10, 10), 2, 2);
            DrawText(context, name, new Point(x + 15, y), fg, FontSize, FontWeight.Normal, w - 30);
            x += w;
        }
        return y + 16;
    }

    /// <summary>The point under the pointer: the bar band, the nearest X on lines and areas, the nearest dot on scatter plots.</summary>
    private int? HoverIndex(ChartData data)
    {
        if (_pointer is not { } p || _layout is null || data.PointCount == 0) return null;
        if (!_plot.Inflate(6).Contains(p)) return null;
        if (data.Kind == ChartKind.Bar)
            return Math.Clamp((int)((p.X - _plot.Left) / (_plot.Width / data.PointCount)), 0, data.PointCount - 1);
        if (data.Kind == ChartKind.Scatter)
        {
            int? best = null;
            var bestDistance = 12.0 * 12.0;
            for (var i = 0; i < data.PointCount; i++)
                foreach (var s in data.Series)
                    if (s.Values[i] is { } v)
                    {
                        var dx = X(data, i) - p.X;
                        var dy = Y(v) - p.Y;
                        var d = dx * dx + dy * dy;
                        if (d < bestDistance) { bestDistance = d; best = i; }
                    }
            return best;
        }
        var nearest = 0;
        var nearestDistance = double.MaxValue;
        for (var i = 0; i < data.PointCount; i++)
        {
            var d = Math.Abs(X(data, i) - p.X);
            if (d < nearestDistance) { nearestDistance = d; nearest = i; }
        }
        return nearest;
    }

    private void DrawTooltip(DrawingContext context, ChartData data, int index, IBrush fg, IBrush hint, Rect bounds)
    {
        var rows = new List<(Color, string, string)>();
        for (var s = 0; s < data.Series.Count; s++)
        {
            var v = data.Series[s].Values[index];
            if (v is null && data.Series.Count > 6) continue;
            rows.Add((SeriesColor(s), data.Series.Count == 1 ? data.YTitle : data.Series[s].Name, v is { } d ? ResultChart.FormatValue(d) : "—"));
        }
        var title = data.Kind == ChartKind.Scatter ? $"{data.XTitle}: {data.Labels[index]}" : data.Labels[index];
        DrawTooltipBox(context, title, rows, fg, hint, bounds);
    }

    private void DrawTooltipBox(DrawingContext context, string title, IReadOnlyList<(Color Color, string Name, string Value)> rows, IBrush fg, IBrush hint, Rect bounds)
    {
        if (_pointer is not { } p) return;
        var nameWidth = rows.Count == 0 ? 0 : rows.Max(r => Math.Min(180, Measure(r.Name, FontSize)));
        var valueWidth = rows.Count == 0 ? 0 : rows.Max(r => Measure(r.Value, FontSize, FontWeight.SemiBold));
        var width = Math.Max(Math.Min(300, Measure(title, FontSize, FontWeight.SemiBold)), 16 + nameWidth + 14 + valueWidth) + 20;
        var height = 26 + rows.Count * 18 + 4;
        var x = p.X + 14;
        if (x + width > bounds.Right - 4) x = p.X - 14 - width;
        var y = Math.Clamp(p.Y - height / 2, 4, Math.Max(4, bounds.Height - height - 4));
        x = Math.Max(4, x);
        var box = new Rect(x, y, width, height);
        context.DrawRectangle(Brush("AppPopupBackgroundBrush", Brushes.White), RenderPens.Create(Brush("AppPopupBorderBrush", Brushes.Gray), 1), box, 6, 6);
        DrawText(context, title, new Point(x + 10, y + 6), fg, FontSize, FontWeight.SemiBold, width - 20);
        for (var i = 0; i < rows.Count; i++)
        {
            var ry = y + 26 + i * 18;
            context.DrawRectangle(new ImmutableSolidColorBrush(rows[i].Color), null, new Rect(x + 10, ry + 3, 10, 10), 2, 2);
            DrawText(context, rows[i].Name, new Point(x + 26, ry), hint, FontSize, FontWeight.Normal, nameWidth + 2);
            var vw = Measure(rows[i].Value, FontSize, FontWeight.SemiBold);
            DrawText(context, rows[i].Value, new Point(box.Right - 10 - vw, ry), fg, FontSize, FontWeight.SemiBold, vw + 2);
        }
    }

    // ------------------------------------------------------------------ scales and helpers

    private double Y(double value) =>
        _plot.Bottom - (value - _layout!.YMin) / Math.Max(1e-300, _layout.YMax - _layout.YMin) * _plot.Height;

    private double X(ChartData data, int index) =>
        data.XAxis == ChartAxisKind.Category
            ? _plot.Left + (index + 0.5) * (_plot.Width / data.PointCount)
            : XValue(data.XValues[index]);

    private double XValue(double value) =>
        _plot.Left + (value - _layout!.XMin) / Math.Max(1e-300, _layout.XMax - _layout.XMin) * _plot.Width;

    private static double Snap(double v) => Math.Round(v) + 0.5;

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static Color[] Parse(params string[] hex) => hex.Select(Color.Parse).ToArray();

    private static double Measure(string text, double size, FontWeight weight = FontWeight.Normal) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, Brushes.Black).Width;

    private static void DrawText(DrawingContext context, string text, Point origin, IBrush brush, double size, FontWeight weight, double maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        context.DrawText(formatted, origin);
    }
}
