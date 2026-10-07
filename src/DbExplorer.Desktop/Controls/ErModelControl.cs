using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DbExplorer.Application.Diagram;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// The ER model canvas: draws like <see cref="ErDiagramControl"/>, and edits. Drag a table's title to move it; drag a
/// column onto another table's column to make it a foreign key to that column, or onto the table's title to reference
/// its primary key; Shift-drag a title onto another table to add a key column for it. Double-click empty space to add a
/// table there; Delete removes the selected table from the model.
/// </summary>
public sealed class ErModelControl : ErDiagramControl
{
    private enum DragKind { None, Move, Link }

    private DragKind _drag;
    private ErTable? _dragTable;
    private string? _dragColumn;
    private Point _dragStart;
    private Point _grabOffset;
    private Point _pointer;
    private bool _moved;
    private int _dragIndex = -1;

    /// <summary>A table dragged to a new place; <c>final</c> on release.</summary>
    public event Action<ErTable, double, double, bool>? TableMoved;

    /// <summary>Child table and column (null: the drag started at its title), parent table and column (null: dropped on
    /// its title, meaning its primary key).</summary>
    public event Action<ErTable, string?, ErTable, string?>? LinkRequested;

    /// <summary>Double-click on empty space, in diagram coordinates.</summary>
    public event Action<double, double>? EmptyDoubleClicked;

    public event Action<ErTable>? DeleteRequested;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        if (Diagram is null) return;
        var p = e.GetPosition(this) / Zoom;
        var hit = Diagram.Tables.LastOrDefault(t => t.Contains(p.X, p.Y));
        SelectedTable = hit;
        e.Handled = true;

        if (hit is null)
        {
            if (e.ClickCount == 2) EmptyDoubleClicked?.Invoke(p.X, p.Y);
            return;
        }
        if (e.ClickCount == 2)
        {
            RaiseTableActivated(hit);
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var column = ColumnAt(hit, p.Y);
        _drag = column is null && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DragKind.Move : DragKind.Link;
        _dragTable = hit;
        _dragIndex = Diagram.Tables.ToList().IndexOf(hit);
        _dragColumn = column;
        _dragStart = p;
        _pointer = p;
        _grabOffset = new Point(p.X - hit.X, p.Y - hit.Y);
        _moved = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this) / Zoom;
        if (_drag == DragKind.None || _dragTable is null)
        {
            Cursor = Diagram?.Tables.LastOrDefault(t => t.Contains(p.X, p.Y)) is { } over
                ? new Cursor(ColumnAt(over, p.Y) is null ? StandardCursorType.SizeAll : StandardCursorType.Cross)
                : Cursor.Default;
            return;
        }
        _pointer = p;
        if (!_moved && Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) < 4) return;
        _moved = true;
        if (_drag == DragKind.Move) TableMoved?.Invoke(Current(_dragTable), p.X - _grabOffset.X, p.Y - _grabOffset.Y, false);
        else InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var drag = _drag;
        var table = _dragTable;
        _drag = DragKind.None;
        _dragTable = null;
        e.Pointer.Capture(null);
        if (table is null || !_moved) return;

        var p = e.GetPosition(this) / Zoom;
        if (drag == DragKind.Move)
        {
            TableMoved?.Invoke(Current(table), p.X - _grabOffset.X, p.Y - _grabOffset.Y, true);
            return;
        }
        InvalidateVisual();
        if (Diagram?.Tables.LastOrDefault(t => t.Contains(p.X, p.Y)) is { } target)
            LinkRequested?.Invoke(table, _dragColumn, target, ColumnAt(target, p.Y));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag == DragKind.None) return;
        _drag = DragKind.None;
        _dragTable = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete && SelectedTable is { } table)
        {
            DeleteRequested?.Invoke(table);
            e.Handled = true;
        }
    }

    /// <summary>Each move redraws the model into new boxes; the dragged one is found again by its place in the list.</summary>
    private ErTable Current(ErTable dragged)
    {
        if (Diagram is not { } d) return dragged;
        if (d.Tables.Contains(dragged)) return dragged;
        return _dragIndex >= 0 && _dragIndex < d.Tables.Count ? d.Tables[_dragIndex] : dragged;
    }

    /// <summary>The column whose row is at <paramref name="y"/>; null on the title.</summary>
    private static string? ColumnAt(ErTable table, double y)
    {
        var index = (int)Math.Floor((y - table.Y - ErDiagramBuilder.HeaderHeight - ErDiagramBuilder.Padding) / ErDiagramBuilder.RowHeight);
        return y < table.Y + ErDiagramBuilder.HeaderHeight || index < 0 || index >= table.Columns.Count ? null : table.Columns[index].Name;
    }

    private static double RowCenter(ErTable table, string? column)
    {
        var index = column is null ? -1 : table.Columns.ToList().FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
        return index < 0
            ? table.Y + ErDiagramBuilder.HeaderHeight / 2
            : table.Y + ErDiagramBuilder.HeaderHeight + ErDiagramBuilder.Padding + (index + 0.5) * ErDiagramBuilder.RowHeight;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_drag != DragKind.Link || !_moved || _dragTable is not { } from || Diagram is null) return;

        var accentColor = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var c) && c is Color col ? col : Colors.SteelBlue;
        var accent = new SolidColorBrush(accentColor);
        using var _ = context.PushTransform(Matrix.CreateScale(Zoom, Zoom));

        // The row the drag started from, the row or title it would link to, and a dashed line between them.
        var startY = RowCenter(from, _dragColumn);
        var start = new Point(_pointer.X < from.X + from.Width / 2 ? from.X : from.X + from.Width, startY);
        context.DrawRectangle(new SolidColorBrush(accentColor, 0.15), null,
            _dragColumn is null ? new Rect(from.X, from.Y, from.Width, ErDiagramBuilder.HeaderHeight)
                : new Rect(from.X, startY - ErDiagramBuilder.RowHeight / 2, from.Width, ErDiagramBuilder.RowHeight));
        if (Diagram.Tables.LastOrDefault(t => t.Contains(_pointer.X, _pointer.Y)) is { } target)
        {
            var column = ColumnAt(target, _pointer.Y);
            var y = RowCenter(target, column);
            context.DrawRectangle(new SolidColorBrush(accentColor, 0.25), new Pen(accent, 1.5),
                column is null ? new Rect(target.X, target.Y, target.Width, ErDiagramBuilder.HeaderHeight)
                    : new Rect(target.X, y - ErDiagramBuilder.RowHeight / 2, target.Width, ErDiagramBuilder.RowHeight), 3, 3);
        }
        context.DrawLine(new Pen(accent, 2) { DashStyle = DashStyle.Dash }, start, _pointer);
        context.DrawEllipse(accent, null, _pointer, 4, 4);
    }
}
