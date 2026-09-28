using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace DbExplorer.Desktop.Editor;

/// <summary>
/// Background marks in the query editor: the matching parenthesis, other occurrences of the name under the caret,
/// the statement that was just run, and the error position (wavy underline).
/// </summary>
public sealed class EditorDecorations : IBackgroundRenderer
{
    private static readonly IBrush BracketFill = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    private static readonly IPen BracketPen = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x80, 0x80, 0x80)), 1);
    private static readonly IBrush OccurrenceFill = new SolidColorBrush(Color.FromArgb(0x38, 0xF2, 0xC0, 0x3C));
    private static readonly IBrush ExecutedFill = new SolidColorBrush(Color.FromArgb(0x22, 0x3C, 0xB3, 0x71));
    private static readonly IBrush ErrorFill = new SolidColorBrush(Color.FromArgb(0x30, 0xE5, 0x39, 0x35));
    private static readonly IPen ErrorPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), 1.2);

    public IReadOnlyList<(int Start, int Length)> Brackets { get; set; } = [];
    public IReadOnlyList<(int Start, int Length)> Occurrences { get; set; } = [];
    public (int Start, int Length)? Executed { get; set; }
    public (int Start, int Length)? Error { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is not { } document || !textView.VisualLinesValid) return;

        if (Executed is { } executed)
            foreach (var rect in Rects(textView, document, executed, wholeLines: true)) drawingContext.FillRectangle(ExecutedFill, rect);

        foreach (var occurrence in Occurrences)
            foreach (var rect in Rects(textView, document, occurrence)) drawingContext.FillRectangle(OccurrenceFill, rect, 2);

        foreach (var bracket in Brackets)
            foreach (var rect in Rects(textView, document, bracket))
            {
                drawingContext.FillRectangle(BracketFill, rect, 2);
                drawingContext.DrawRectangle(BracketPen, rect, 2);
            }

        if (Error is { } error)
            foreach (var rect in Rects(textView, document, error))
            {
                drawingContext.FillRectangle(ErrorFill, rect);
                drawingContext.DrawGeometry(null, ErrorPen, Wave(rect));
            }
    }

    private static IEnumerable<Rect> Rects(TextView textView, TextDocument document, (int Start, int Length) span, bool wholeLines = false)
    {
        var start = Math.Clamp(span.Start, 0, document.TextLength);
        var end = Math.Clamp(span.Start + span.Length, start, document.TextLength);
        if (end == start) yield break;
        var segment = new TextSegment { StartOffset = start, EndOffset = end };
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, extendToFullWidthAtLineEnd: wholeLines))
            yield return rect;
    }

    /// <summary>A zigzag under the rectangle, like a spell-check underline.</summary>
    private static Geometry Wave(Rect rect)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var y = rect.Bottom - 1;
        context.BeginFigure(new Point(rect.Left, y), false);
        var up = true;
        for (var x = rect.Left + 2; x <= rect.Right + 2; x += 2)
        {
            context.LineTo(new Point(x, up ? y - 2 : y));
            up = !up;
        }
        context.EndFigure(false);
        return geometry;
    }
}
