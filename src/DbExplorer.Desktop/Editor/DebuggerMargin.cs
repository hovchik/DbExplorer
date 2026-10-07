using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace DbExplorer.Desktop.Editor;

/// <summary>
/// The debugger's gutter: a red dot on each breakpoint line and an arrow on the line the routine is paused on.
/// Clicking the gutter toggles a breakpoint.
/// </summary>
public sealed class DebuggerMargin : AbstractMargin
{
    private static readonly IBrush BreakpointBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xB7, 0x05));
    private static readonly IBrush CallerBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B));

    public Func<int, bool> HasBreakpoint { get; set; } = _ => false;
    public int CurrentLine { get; set; }

    /// <summary>The shown frame is a caller (green arrow: where it will continue) rather than the paused line.</summary>
    public bool IsCaller { get; set; }

    public event Action<int>? LineClicked;

    protected override Size MeasureOverride(Size availableSize) => new(18, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // hit-testable everywhere
        var view = TextView;
        if (view is null || !view.VisualLinesValid) return;
        foreach (var line in view.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
            var height = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineBottom) - view.VerticalOffset - top;
            var center = new Point(Bounds.Width / 2, top + height / 2);
            var radius = Math.Min(6, height / 2 - 1);
            if (HasBreakpoint(number)) context.DrawEllipse(BreakpointBrush, null, center, radius, radius);
            if (number == CurrentLine) context.DrawGeometry(IsCaller ? CallerBrush : CurrentBrush, null, Arrow(center, radius));
        }
    }

    private static Geometry Arrow(Point c, double r)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(new Point(c.X - r, c.Y - r * 0.55), true);
        ctx.LineTo(new Point(c.X, c.Y - r * 0.55));
        ctx.LineTo(new Point(c.X, c.Y - r));
        ctx.LineTo(new Point(c.X + r, c.Y));
        ctx.LineTo(new Point(c.X, c.Y + r));
        ctx.LineTo(new Point(c.X, c.Y + r * 0.55));
        ctx.LineTo(new Point(c.X - r, c.Y + r * 0.55));
        ctx.EndFigure(true);
        return g;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TextView is not { } view || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var y = e.GetPosition(this).Y + view.VerticalOffset;
        if (view.GetVisualLineFromVisualTop(y) is { } line)
        {
            LineClicked?.Invoke(line.FirstDocumentLine.LineNumber);
            e.Handled = true;
        }
    }
}

/// <summary>Highlights the line the routine is paused on (yellow) or, in a caller's frame, the calling line (green).</summary>
public sealed class DebuggerLineHighlight : IBackgroundRenderer
{
    private static readonly IBrush CurrentFill = new SolidColorBrush(Color.FromArgb(0x40, 0xF2, 0xB7, 0x05));
    private static readonly IBrush CallerFill = new SolidColorBrush(Color.FromArgb(0x30, 0x2E, 0x9E, 0x5B));

    public int Line { get; set; }
    public bool IsCaller { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Line < 1 || textView.Document is not { } document || Line > document.LineCount || !textView.VisualLinesValid) return;
        var line = document.GetLineByNumber(Line);
        var segment = new TextSegment { StartOffset = line.Offset, EndOffset = line.EndOffset };
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, extendToFullWidthAtLineEnd: true))
            drawingContext.FillRectangle(IsCaller ? CallerFill : CurrentFill, new Rect(0, rect.Top, Math.Max(rect.Right, textView.Bounds.Width), rect.Height));
    }
}
