using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DbExplorer.Application.Query.Plans;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// Draws an execution plan as a tree of operator cards, data flowing right to left into the statement's last operator.
/// Arrows are as thick as the rows they carry; each card shows its share of the work as a bar, and the busiest
/// operators and those with warnings stand out. Click (or the arrow keys) selects an operator; Ctrl+wheel zooms.
/// </summary>
public sealed class PlanDiagramControl : Control
{
    public static readonly StyledProperty<PlanLayout?> LayoutProperty =
        AvaloniaProperty.Register<PlanDiagramControl, PlanLayout?>(nameof(Layout));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PlanDiagramControl, double>(nameof(Zoom), 1.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<PlanNode?> SelectedNodeProperty =
        AvaloniaProperty.Register<PlanDiagramControl, PlanNode?>(nameof(SelectedNode), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private PlanNode? _hovered;

    static PlanDiagramControl()
    {
        AffectsMeasure<PlanDiagramControl>(LayoutProperty, ZoomProperty);
        AffectsRender<PlanDiagramControl>(LayoutProperty, ZoomProperty, SelectedNodeProperty);
    }

    public PlanDiagramControl()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        Focusable = true;
        ClipToBounds = true;
    }

    public PlanLayout? Layout { get => GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public PlanNode? SelectedNode { get => GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        Layout is { } l ? new Size(l.Width * Zoom, l.Height * Zoom) : default;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Layout is null) return;
        var p = e.GetPosition(this) / Zoom;
        if (Layout.HitTest(p.X, p.Y) is { } hit) SelectedNode = hit;
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Layout is null) return;
        var p = e.GetPosition(this) / Zoom;
        var hit = Layout.HitTest(p.X, p.Y);
        if (hit == _hovered) return;
        _hovered = hit;
        Cursor = hit is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered is null) return;
        _hovered = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) { base.OnPointerWheelChanged(e); return; }
        Zoom = Math.Clamp(Math.Round(Zoom + (e.Delta.Y > 0 ? 0.1 : -0.1), 1), 0.3, 2.5);
        e.Handled = true;
    }

    /// <summary>← the operator this one feeds, → its first input, ↑/↓ the neighbouring input of the same parent.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (SelectedNode is not { } node) { base.OnKeyDown(e); return; }
        var siblings = node.Parent?.Children;
        var index = siblings?.IndexOf(node) ?? 0;
        PlanNode? next = e.Key switch
        {
            Key.Left => node.Parent,
            Key.Right => node.Children.FirstOrDefault(),
            Key.Up when siblings is not null && index > 0 => siblings[index - 1],
            Key.Down when siblings is not null && index < siblings.Count - 1 => siblings[index + 1],
            _ => null
        };
        if (next is null) { base.OnKeyDown(e); return; }
        SelectedNode = next;
        ScrollIntoView(next);
        e.Handled = true;
    }

    /// <summary>Scrolls the enclosing scroll viewer so <paramref name="node"/> is visible.</summary>
    public void ScrollIntoView(PlanNode node)
    {
        if (Layout is null) return;
        var box = Layout[node];
        this.BringIntoView(new Rect(box.X * Zoom - 20, box.Y * Zoom - 20, (PlanLayout.NodeWidth + 40) * Zoom, (PlanLayout.NodeHeight + 40) * Zoom));
    }

    public override void Render(DrawingContext context)
    {
        if (Layout is not { } layout) return;

        var fg = Brush("AppForegroundBrush", Brushes.Black);
        var hint = Brush("AppHintForegroundBrush", Brushes.Gray);
        var border = Brush("AppBorderBrush", Brushes.Gray);
        var surface = Brush("AppPopupBackgroundBrush", Brushes.White);
        var subtle = Brush("AppSubtleBackgroundBrush", Brushes.LightGray);
        var hot = Brush("AppPlanHotBrush", Brushes.Firebrick);
        var warm = Brush("AppPlanWarmBrush", Brushes.DarkOrange);
        var cool = Brush("AppPlanCoolBrush", Brushes.SteelBlue);
        var warning = Brush("AppPlanWarningBrush", Brushes.Goldenrod);
        var accentColor = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var c) && c is Color col ? col : Colors.SteelBlue;
        var accent = new SolidColorBrush(accentColor);
        var edgeBrush = new SolidColorBrush(((ISolidColorBrush)hint).Color, 0.45);

        using var _ = context.PushTransform(Matrix.CreateScale(Zoom, Zoom));
        context.FillRectangle(Brush("AppCodeBackgroundBrush", Brushes.WhiteSmoke), new Rect(0, 0, layout.Width, layout.Height));

        // Arrows first, under the cards: from each input (right) into the operator it feeds (left).
        var maxRows = Math.Max(1, layout.Boxes.Max(b => b.Node.RowsOut));
        foreach (var box in layout.Boxes)
        {
            var node = box.Node;
            for (var i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                var from = layout[child];
                var spread = node.Children.Count == 1 ? 0 : (i - (node.Children.Count - 1) / 2.0) * Math.Min(14, PlanLayout.NodeHeight / node.Children.Count);
                var start = new Point(from.X, from.Y + PlanLayout.NodeHeight / 2);
                var end = new Point(box.X + PlanLayout.NodeWidth + 6, box.Y + PlanLayout.NodeHeight / 2 + spread);
                var thickness = 1.2 + 9 * Math.Log10(child.RowsOut + 1) / Math.Log10(maxRows + 1);
                var selected = child == SelectedNode || node == SelectedNode;
                var brush = selected ? accent : edgeBrush;

                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var mid = (start.X + end.X) / 2;
                    g.BeginFigure(start, false);
                    g.CubicBezierTo(new Point(mid, start.Y), new Point(mid, end.Y), end);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Flat), geometry);

                // Arrowhead into the parent.
                var head = new StreamGeometry();
                using (var g = head.Open())
                {
                    var size = 4 + thickness / 2;
                    g.BeginFigure(new Point(end.X - 6, end.Y), true);
                    g.LineTo(new Point(end.X + size - 2, end.Y - size));
                    g.LineTo(new Point(end.X + size - 2, end.Y + size));
                    g.EndFigure(true);
                }
                context.DrawGeometry(brush, null, head);

                // How many rows flow along the arrow, next to the input.
                var label = PlanFormat.Count(child.RowsOut);
                DrawText(context, label, new Point(start.X - 8 - Measure(label, 10.5), start.Y - 16), selected ? accent : hint, 10.5, FontWeight.Normal, 80);
            }
        }

        foreach (var box in layout.Boxes)
        {
            var n = box.Node;
            var rect = new Rect(box.X, box.Y, PlanLayout.NodeWidth, PlanLayout.NodeHeight);
            var heat = n.Share >= 0.30 ? hot : n.Share >= 0.10 ? warm : cool;
            var isSelected = n == SelectedNode;
            var pen = isSelected ? new Pen(accent, 2.2)
                : n.IsHotspot ? new Pen(heat, 1.6)
                : new Pen(n == _hovered ? hint : border, 1);
            context.DrawRectangle(n == _hovered && !isSelected ? subtle : surface, null, rect, 6, 6);
            context.DrawRectangle(null, pen, rect, 6, 6);

            // Heat stripe on the left edge.
            using (context.PushClip(new RoundedRect(rect, 6)))
                context.FillRectangle(heat, new Rect(box.X, box.Y, 4, PlanLayout.NodeHeight));

            var left = box.X + 12;
            var width = PlanLayout.NodeWidth - 20;
            var warnWidth = 0.0;
            if (n.Warnings.Count > 0)
            {
                var badge = n.Warnings.Count == 1 ? "⚠" : $"⚠ {n.Warnings.Count}";
                warnWidth = Measure(badge, 11.5, FontWeight.Bold) + 4;
                DrawText(context, badge, new Point(box.X + PlanLayout.NodeWidth - 8 - warnWidth + 4, box.Y + 6), warning, 11.5, FontWeight.Bold, warnWidth);
            }
            DrawText(context, n.Operator, new Point(left, box.Y + 6), fg, 12.5, FontWeight.SemiBold, width - warnWidth);

            var second = n.Object ?? n.Detail;
            if (second is not null) DrawText(context, second, new Point(left, box.Y + 24), hint, 11, FontWeight.Normal, width);

            var gap = n.Warnings.Any(w => w.Kind == PlanWarningKind.EstimateGap);
            DrawText(context, n.RowsText, new Point(left, box.Y + 40), gap ? warning : fg, 11, gap ? FontWeight.SemiBold : FontWeight.Normal, width);

            // Share of the work: a bar and its percentage (plus time when measured).
            var barY = box.Y + PlanLayout.NodeHeight - 13;
            var pct = PlanFormat.Percent(n.Share);
            var right = n.ActualTimeMs is { } ms ? $"{pct} · {PlanFormat.Ms(ms)}" : pct;
            var textWidth = Measure(right, 10.5, n.IsHotspot ? FontWeight.Bold : FontWeight.Normal);
            var barWidth = Math.Max(20, width - textWidth - 8);
            context.DrawRectangle(subtle, null, new Rect(left, barY, barWidth, 5), 2.5, 2.5);
            if (n.Share > 0)
                context.DrawRectangle(heat, null, new Rect(left, barY, Math.Max(3, barWidth * n.Share), 5), 2.5, 2.5);
            DrawText(context, right, new Point(left + barWidth + 6, barY - 5), n.IsHotspot ? heat : hint, 10.5, n.IsHotspot ? FontWeight.Bold : FontWeight.Normal, textWidth + 2);
        }

        IBrush Brush(string key, IBrush fallback) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush b ? b : fallback;
    }

    private static double Measure(string text, double size, FontWeight weight = FontWeight.Normal) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, Brushes.Black).Width;

    private static void DrawText(DrawingContext context, string text, Point origin, IBrush brush, double size, FontWeight weight, double maxWidth)
    {
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
