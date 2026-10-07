using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.Editor;

/// <summary>
/// Extra carets in the query editor (Ctrl+Alt+Click, Alt+J, Ctrl+Alt+Shift+J). Each caret is a range of the text kept
/// by two anchors; whatever is typed or deleted at the real caret is repeated at every other one, in the same undo step.
/// Moving the caret away from the ranges, clicking, Escape or Undo go back to one caret.
/// </summary>
public sealed class MultiCaretSession : IBackgroundRenderer
{
    private static readonly IBrush RangeFill = new SolidColorBrush(Color.FromArgb(0x50, 0x33, 0x88, 0xFF));
    private static readonly IBrush CaretBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x88, 0xFF));

    private readonly TextArea _area;
    private readonly List<(TextAnchor Start, TextAnchor End)> _ranges = [];
    private readonly List<(int Relative, int RemovalLength, string Inserted)> _pending = [];
    private TextDocument? _document;
    private int _primary = -1;
    private bool _applying;

    public MultiCaretSession(TextArea area)
    {
        _area = area;
        _area.TextView.BackgroundRenderers.Add(this);
        _area.Caret.PositionChanged += (_, _) => { if (IsActive && !_applying && !ContainsCaret()) Clear(); };
    }

    /// <summary>More than one caret is active.</summary>
    public bool IsActive => _ranges.Count > 1;

    public int Count => _ranges.Count;

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>The current ranges, in document order.</summary>
    public IReadOnlyList<(int Start, int End)> Ranges =>
        _ranges.Select(r => (r.Start.Offset, Math.Max(r.Start.Offset, r.End.Offset))).OrderBy(r => r.Item1).ToList();

    /// <summary>Adds a caret covering <paramref name="start"/>..+<paramref name="length"/> (0 for a plain caret).</summary>
    public bool Add(int start, int length)
    {
        Attach();
        if (_document is null) return false;
        start = Math.Clamp(start, 0, _document.TextLength);
        length = Math.Clamp(length, 0, _document.TextLength - start);
        if (_ranges.Any(r => r.Start.Offset == start)) return false;
        var startAnchor = _document.CreateAnchor(start);
        startAnchor.MovementType = AnchorMovementType.BeforeInsertion;
        startAnchor.SurviveDeletion = true;
        var endAnchor = _document.CreateAnchor(start + length);
        endAnchor.MovementType = AnchorMovementType.AfterInsertion;
        endAnchor.SurviveDeletion = true;
        _ranges.Add((startAnchor, endAnchor));
        _area.TextView.InvalidateLayer(Layer);
        return true;
    }

    /// <summary>Back to one caret.</summary>
    public void Clear()
    {
        if (_ranges.Count == 0) return;
        _ranges.Clear();
        _pending.Clear();
        Detach();
        _area.TextView.InvalidateLayer(Layer);
    }

    private bool ContainsCaret()
    {
        var caret = _area.Caret.Offset;
        return Ranges.Any(r => caret >= r.Start && caret <= r.End);
    }

    private void Attach()
    {
        if (ReferenceEquals(_document, _area.Document)) return;
        Detach();
        _ranges.Clear();
        _document = _area.Document;
        if (_document is null) return;
        _document.Changing += OnChanging;
        _document.UpdateFinished += OnUpdateFinished;
    }

    private void Detach()
    {
        if (_document is null) return;
        _document.Changing -= OnChanging;
        _document.UpdateFinished -= OnUpdateFinished;
        _document = null;
    }

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_applying || !IsActive) return;
        // The range under the real caret is the one being edited; any other edit (a paste far away, a format) ends it.
        var ranges = _ranges.Select(r => (r.Start.Offset, Math.Max(r.Start.Offset, r.End.Offset))).ToList();
        var caret = _area.Caret.Offset;
        var byCaret = ranges.FindIndex(r => caret >= r.Item1 && caret <= r.Item2);
        var located = byCaret >= 0 && MultiCaret.Locate([ranges[byCaret]], e.Offset, e.RemovalLength) is { } hit
            ? (byCaret, hit.Relative)
            : MultiCaret.Locate(ranges, e.Offset, e.RemovalLength);
        if (located is not { } at)
        {
            _ranges.Clear();
            _pending.Clear();
            _area.TextView.InvalidateLayer(Layer);
            return;
        }
        if (_pending.Count == 0) _primary = at.Item1;
        else if (_primary != at.Item1) return; // a second range in the same update: leave it alone
        _pending.Add((at.Item2, e.RemovalLength, e.InsertedText?.Text ?? ""));
    }

    private void OnUpdateFinished(object? sender, EventArgs e)
    {
        if (_applying || _pending.Count == 0 || _document is not { } document) return;
        var edits = _pending.ToList();
        _pending.Clear();
        var primary = _ranges[_primary];
        _applying = true;
        try
        {
            document.UndoStack.StartContinuedUndoGroup(null);
            document.BeginUpdate();
            // Starts are read again before each edit: earlier edits move the anchors after them.
            foreach (var (start, _) in _ranges.Where(r => r != primary).ToList())
                foreach (var (relative, removal, inserted) in edits)
                {
                    var (offset, length) = MultiCaret.Mirror(start.Offset, relative, removal, document.TextLength);
                    document.Replace(offset, length, inserted);
                }
            document.EndUpdate();
            document.UndoStack.EndUndoGroup();
        }
        finally
        {
            _applying = false;
        }
        _area.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!IsActive || textView.Document is not { } document || !textView.VisualLinesValid) return;
        foreach (var (start, end) in Ranges)
        {
            if (end > start)
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start, EndOffset = end }))
                    drawingContext.FillRectangle(RangeFill, rect, 2);
            // Each extra caret sits where typing lands: the end of its range.
            if (end == _area.Caret.Offset || end > document.TextLength) continue;
            var location = document.GetLocation(end);
            var bounds = textView.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineBottom) - textView.ScrollOffset;
            drawingContext.FillRectangle(CaretBrush, new Rect(bounds.X, bounds.Y, 1.5, Math.Max(1, bottom.Y - bounds.Y)));
        }
    }
}
