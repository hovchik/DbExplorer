using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DbExplorer.Application.Diagram;

namespace DbExplorer.Desktop.Controls;

/// <summary>Draws an <see cref="ErDiagram"/>: tables as boxes, foreign keys as curves from the
/// referencing column to the referenced column. Click selects a table, double-click refocuses.</summary>
public sealed class ErDiagramControl : Control
{
    public static readonly StyledProperty<ErDiagram?> DiagramProperty =
        AvaloniaProperty.Register<ErDiagramControl, ErDiagram?>(nameof(Diagram));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ErDiagramControl, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<ErTable?> SelectedTableProperty =
        AvaloniaProperty.Register<ErDiagramControl, ErTable?>(nameof(SelectedTable), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    static ErDiagramControl()
    {
        AffectsMeasure<ErDiagramControl>(DiagramProperty, ZoomProperty);
        AffectsRender<ErDiagramControl>(DiagramProperty, ZoomProperty, SelectedTableProperty);
    }

    public ErDiagramControl()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        Focusable = true;
    }

    public ErDiagram? Diagram { get => GetValue(DiagramProperty); set => SetValue(DiagramProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public ErTable? SelectedTable { get => GetValue(SelectedTableProperty); set => SetValue(SelectedTableProperty, value); }

    /// <summary>Raised on double-click of a table.</summary>
    public event Action<ErTable>? TableActivated;

    protected override Size MeasureOverride(Size availableSize) =>
        Diagram is { } d ? new Size(d.Width * Zoom, d.Height * Zoom) : default;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Diagram is null) return;
        var p = e.GetPosition(this) / Zoom;
        var hit = Diagram.Tables.LastOrDefault(t => t.Contains(p.X, p.Y));
        SelectedTable = hit;
        if (hit is not null && e.ClickCount == 2) TableActivated?.Invoke(hit);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) { base.OnPointerWheelChanged(e); return; }
        Zoom = Math.Clamp(Math.Round(Zoom + (e.Delta.Y > 0 ? 0.1 : -0.1), 1), 0.3, 2.5);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        if (Diagram is not { } d) return;

        var fg = Brush("AppForegroundBrush", Brushes.Black);
        var hint = Brush("AppHintForegroundBrush", Brushes.Gray);
        var border = Brush("AppBorderBrush", Brushes.Gray);
        var surface = Brush("AppPopupBackgroundBrush", Brushes.White);
        var subtle = Brush("AppSubtleBackgroundBrush", Brushes.LightGray);
        var accentColor = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var c) && c is Color col ? col : Colors.SteelBlue;
        var accent = new SolidColorBrush(accentColor);
        var accentSoft = new SolidColorBrush(accentColor, 0.18);
        var edgeColor = new SolidColorBrush(((ISolidColorBrush)hint).Color, 0.6);

        using var _ = context.PushTransform(Matrix.CreateScale(Zoom, Zoom));
        context.FillRectangle(Brush("AppCodeBackgroundBrush", Brushes.WhiteSmoke), new Rect(0, 0, d.Width, d.Height));

        foreach (var edge in d.Edges)
        {
            var highlighted = SelectedTable is { } s && (edge.Child.Title == s.Title || edge.Parent.Title == s.Title);
            DrawEdge(context, edge, highlighted ? accent : edgeColor, highlighted ? 2 : 1.2);
        }

        foreach (var t in d.Tables)
        {
            var selected = SelectedTable?.Title == t.Title;
            var rect = new Rect(t.X, t.Y, t.Width, t.Height);
            context.DrawRectangle(surface, new Pen(selected ? accent : border, selected ? 2 : 1), rect, 6, 6);

            var header = new Rect(t.X, t.Y, t.Width, ErDiagramBuilder.HeaderHeight);
            using (context.PushClip(new RoundedRect(rect, 6)))
                context.DrawRectangle(t.IsFocus ? accent : subtle, null, header);

            var titleBrush = t.IsFocus ? Brushes.White : fg;
            DrawText(context, t.Title, new Point(t.X + 8, t.Y + 6), titleBrush, 12.5, FontWeight.SemiBold, t.Width - 16);

            var y = t.Y + ErDiagramBuilder.HeaderHeight + ErDiagramBuilder.Padding;
            foreach (var column in t.Columns)
            {
                var marker = column.IsPrimaryKey ? "PK" : column.IsForeignKey ? "FK" : "";
                if (marker.Length > 0)
                    DrawText(context, marker, new Point(t.X + 8, y + 1), column.IsPrimaryKey ? accent : hint, 10, FontWeight.Bold, 22);
                DrawText(context, column.Name, new Point(t.X + 32, y), fg, 12, column.IsPrimaryKey ? FontWeight.SemiBold : FontWeight.Normal, 110);
                DrawText(context, column.DataType, new Point(t.X + 146, y + 1), hint, 10.5, FontWeight.Normal, t.Width - 152);
                y += ErDiagramBuilder.RowHeight;
            }
            if (t.HiddenColumnCount > 0)
                DrawText(context, $"… {t.HiddenColumnCount} more column(s)", new Point(t.X + 32, y), hint, 10.5, FontWeight.Normal, t.Width - 40);
        }

        IBrush Brush(string key, IBrush fallback) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush b ? b : fallback;
    }

    private static void DrawEdge(DrawingContext context, ErEdge edge, IBrush brush, double thickness)
    {
        var child = edge.Child;
        var parent = edge.Parent;
        var fkColumns = ErDiagramBuilder.SplitColumns(edge.ForeignKey.Columns).FirstOrDefault();
        var refColumns = ErDiagramBuilder.SplitColumns(edge.ForeignKey.ReferencedColumns).FirstOrDefault();
        var y1 = RowY(child, fkColumns);
        var y2 = RowY(parent, refColumns);
        var pen = new Pen(brush, thickness);

        Point start, end;
        double c1, c2;
        if (ReferenceEquals(child, parent) || Math.Abs(child.X - parent.X) < 1)
        {
            // Same column (or self reference): loop out to the right.
            start = new Point(child.X + child.Width, y1);
            end = new Point(parent.X + parent.Width, y2);
            c1 = start.X + 50;
            c2 = end.X + 50;
        }
        else if (child.X > parent.X)
        {
            start = new Point(child.X, y1);
            end = new Point(parent.X + parent.Width, y2);
            var dx = Math.Max(30, (start.X - end.X) / 2);
            c1 = start.X - dx;
            c2 = end.X + dx;
        }
        else
        {
            start = new Point(child.X + child.Width, y1);
            end = new Point(parent.X, y2);
            var dx = Math.Max(30, (end.X - start.X) / 2);
            c1 = start.X + dx;
            c2 = end.X - dx;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.CubicBezierTo(new Point(c1, start.Y), new Point(c2, end.Y), end);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);

        // Many side: small circle at the child; one side: bar at the parent.
        context.DrawEllipse(brush, null, start, 3, 3);
        var barX = end.X + (c2 > end.X ? 6 : -6);
        context.DrawLine(pen, new Point(barX, end.Y - 6), new Point(barX, end.Y + 6));
    }

    private static double RowY(ErTable t, string? column)
    {
        var index = column is null ? -1 : t.Columns.ToList().FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
        return index < 0
            ? t.Y + ErDiagramBuilder.HeaderHeight / 2
            : t.Y + ErDiagramBuilder.HeaderHeight + ErDiagramBuilder.Padding + index * ErDiagramBuilder.RowHeight + ErDiagramBuilder.RowHeight / 2;
    }

    private static void DrawText(DrawingContext context, string text, Point origin, IBrush brush, double size, FontWeight weight, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        context.DrawText(formatted, origin);
    }
}
