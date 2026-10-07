using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DbExplorer.Application.QueryBuilder;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// The query builder's canvas: placed tables as boxes with a tick per column, joins as curves between columns.
/// Click a column to select it, drag a header to move the table, drag a column onto another table's column to join
/// them, click a join's label to select it, Delete removes the selected join or table. Tables dropped from the
/// table list land where they are dropped. Drawn in the schema diagram's style.
/// </summary>
public sealed class QueryCanvasControl : Control
{
    /// <summary>Drag-and-drop format carrying a <see cref="DbObject"/> from the table list.</summary>
    public const string TableFormat = "dbexplorer/query-builder-table";

    public static readonly StyledProperty<QueryBuilderViewModel?> BuilderProperty =
        AvaloniaProperty.Register<QueryCanvasControl, QueryBuilderViewModel?>(nameof(Builder));

    private const double CloseSize = 16;
    private QueryTableItem? _moving;
    private Point _moveOffset;
    private (QueryTableItem Table, string Column)? _pressedColumn;
    private Point _pressPoint;
    private Point? _dragPoint;

    static QueryCanvasControl()
    {
        AffectsMeasure<QueryCanvasControl>(BuilderProperty);
        AffectsRender<QueryCanvasControl>(BuilderProperty);
    }

    public QueryCanvasControl()
    {
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public QueryBuilderViewModel? Builder { get => GetValue(BuilderProperty); set => SetValue(BuilderProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != BuilderProperty) return;
        if (change.OldValue is QueryBuilderViewModel old) old.CanvasChanged -= OnCanvasChanged;
        if (change.NewValue is QueryBuilderViewModel vm) vm.CanvasChanged += OnCanvasChanged;
    }

    private void OnCanvasChanged()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var tables = Builder?.Tables ?? [];
        var width = tables.Count == 0 ? 0 : tables.Max(t => t.X + QueryTableItem.Width) + 80;
        var height = tables.Count == 0 ? 0 : tables.Max(t => t.Y + t.Height) + 80;
        // Fill the viewport so the whole area takes drops, and grow past it so the scroll bars reach every table.
        return new Size(
            Math.Max(width, double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width),
            Math.Max(height, double.IsInfinity(availableSize.Height) ? 500 : availableSize.Height));
    }

    // ----- Drag from the table list -----

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(TableFormat) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Builder is not { } vm || e.Data.Get(TableFormat) is not DbObject table) return;
        var p = e.GetPosition(this);
        vm.AddTable(table, p.X - 30, p.Y - 12);
        e.Handled = true;
    }

    // ----- Mouse -----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Builder is not { } vm) return;
        var p = e.GetPosition(this);
        _pressPoint = p;

        if (JoinAt(vm, p) is { } join)
        {
            vm.SelectedJoin = join;
            vm.SelectedTable = null;
            e.Handled = true;
            return;
        }

        var table = TableAt(vm, p);
        vm.SelectedJoin = null;
        vm.SelectedTable = table;
        if (table is null) return;

        if (CloseRect(table).Contains(p))
        {
            vm.RemoveTable(table);
        }
        else if (table.RowAt(p.Y) is -1)
        {
            _moving = table;
            _moveOffset = new Point(p.X - table.X, p.Y - table.Y);
        }
        else if (table.RowAt(p.Y) is { } row)
        {
            _pressedColumn = (table, table.ColumnAtRow(row));
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Builder is not { } vm) return;
        var p = e.GetPosition(this);
        if (_moving is not null)
        {
            vm.MoveTable(_moving, p.X - _moveOffset.X, p.Y - _moveOffset.Y);
            InvalidateMeasure();
        }
        else if (_pressedColumn is not null && (_dragPoint is not null || Distance(p, _pressPoint) > 4))
        {
            _dragPoint = p;
            InvalidateVisual();
        }
        Cursor = _moving is not null ? new Cursor(StandardCursorType.SizeAll)
            : _dragPoint is not null ? new Cursor(StandardCursorType.Cross)
            : TableAt(vm, p) is { } t && t.RowAt(p.Y) is -1 && !CloseRect(t).Contains(p) ? new Cursor(StandardCursorType.SizeAll)
            : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Builder is { } vm && _pressedColumn is { } pressed)
        {
            var p = e.GetPosition(this);
            if (_dragPoint is null)
            {
                vm.ToggleColumn(pressed.Table, pressed.Column);
            }
            else if (DrawOrder(vm).LastOrDefault(t => t != pressed.Table && t.Contains(p.X, p.Y)) is { } target &&
                     target.RowAt(p.Y) is { } row and >= 0)
            {
                vm.AddJoin(pressed.Table, pressed.Column, target, target.ColumnAtRow(row));
            }
        }
        _moving = null;
        _pressedColumn = null;
        _dragPoint = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Builder is not { } vm || e.Key is not (Key.Delete or Key.Back)) return;
        if (vm.SelectedJoin is { } join) vm.RemoveJoin(join);
        else if (vm.SelectedTable is { } table) vm.RemoveTable(table);
        e.Handled = true;
    }

    /// <summary>The selected table is drawn last (on top) and hit first; the list's own order is the FROM order.</summary>
    private static IEnumerable<QueryTableItem> DrawOrder(QueryBuilderViewModel vm) =>
        vm.Tables.Where(t => t != vm.SelectedTable).Concat(vm.SelectedTable is { } s && vm.Tables.Contains(s) ? [s] : []);

    private static QueryTableItem? TableAt(QueryBuilderViewModel vm, Point p) => DrawOrder(vm).LastOrDefault(t => t.Contains(p.X, p.Y));

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Rect CloseRect(QueryTableItem t) =>
        new(t.X + QueryTableItem.Width - CloseSize - 7, t.Y + (QueryTableItem.HeaderHeight - CloseSize) / 2, CloseSize, CloseSize);

    // ----- Joins geometry -----

    private readonly record struct JoinGeometry(Point Start, Point End, double C1, double C2, Rect Label);

    private static JoinGeometry? Geometry(QueryBuilderViewModel vm, JoinItem join)
    {
        var left = vm.Tables.FirstOrDefault(t => string.Equals(t.Alias, join.LeftAlias, StringComparison.OrdinalIgnoreCase));
        var right = vm.Tables.FirstOrDefault(t => string.Equals(t.Alias, join.RightAlias, StringComparison.OrdinalIgnoreCase));
        if (left is null || right is null) return null;
        var y1 = left.RowCenter(join.LeftColumn);
        var y2 = right.RowCenter(join.RightColumn);

        Point start, end;
        double c1, c2;
        if (left.X + QueryTableItem.Width < right.X)
        {
            start = new Point(left.X + QueryTableItem.Width, y1);
            end = new Point(right.X, y2);
            var dx = Math.Max(30, (end.X - start.X) / 2);
            (c1, c2) = (start.X + dx, end.X - dx);
        }
        else if (right.X + QueryTableItem.Width < left.X)
        {
            start = new Point(left.X, y1);
            end = new Point(right.X + QueryTableItem.Width, y2);
            var dx = Math.Max(30, (start.X - end.X) / 2);
            (c1, c2) = (start.X - dx, end.X + dx);
        }
        else
        {
            // Overlapping columns of boxes: loop out on the right.
            start = new Point(left.X + QueryTableItem.Width, y1);
            end = new Point(right.X + QueryTableItem.Width, y2);
            (c1, c2) = (Math.Max(start.X, end.X) + 50, Math.Max(start.X, end.X) + 50);
        }

        // The bezier's middle, where the kind label sits.
        var mid = new Point(0.125 * start.X + 0.375 * c1 + 0.375 * c2 + 0.125 * end.X, (start.Y + end.Y) / 2);
        var label = new Rect(mid.X - 22, mid.Y - 9, 44, 18);
        return new JoinGeometry(start, end, c1, c2, label);
    }

    private static JoinItem? JoinAt(QueryBuilderViewModel vm, Point p) =>
        vm.Joins.LastOrDefault(j => Geometry(vm, j) is { } g && g.Label.Inflate(2).Contains(p));

    private static string KindLabel(JoinKind kind) => kind switch
    {
        JoinKind.Left => "LEFT",
        JoinKind.Right => "RIGHT",
        JoinKind.Full => "FULL",
        _ => "INNER"
    };

    // ----- Drawing -----

    public override void Render(DrawingContext context)
    {
        var bg = Brush("AppCodeBackgroundBrush", Brushes.WhiteSmoke);
        context.FillRectangle(bg, new Rect(Bounds.Size));
        var hint = Brush("AppHintForegroundBrush", Brushes.Gray);
        if (Builder is not { } vm || vm.Tables.Count == 0)
        {
            DrawText(context, "Drag tables here from the list on the left (or double-click them).", new Point(24, 24), hint, 13, FontWeight.Normal, 600);
            return;
        }

        var fg = Brush("AppForegroundBrush", Brushes.Black);
        var border = Brush("AppBorderBrush", Brushes.Gray);
        var surface = Brush("AppPopupBackgroundBrush", Brushes.White);
        var subtle = Brush("AppSubtleBackgroundBrush", Brushes.LightGray);
        var accentColor = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var c) && c is Color col ? col : Colors.SteelBlue;
        var accent = new SolidColorBrush(accentColor);
        var accentSoft = new SolidColorBrush(accentColor, 0.16);
        var edge = new SolidColorBrush(((ISolidColorBrush)hint).Color, 0.75);

        // Joins under the tables, labels on top of everything (drawn after the boxes).
        var geometries = vm.Joins.Select(j => (Join: j, Geometry: Geometry(vm, j))).Where(x => x.Geometry is not null).ToList();
        foreach (var (join, g) in geometries)
        {
            var selected = vm.SelectedJoin == join;
            DrawCurve(context, g!.Value, new Pen(selected ? accent : edge, selected ? 2.4 : 1.4)
            {
                DashStyle = join.FromForeignKey ? null : DashStyle.Dash
            });
        }

        foreach (var t in DrawOrder(vm))
        {
            var selected = vm.SelectedTable == t;
            var rect = new Rect(t.X, t.Y, QueryTableItem.Width, t.Height);
            context.DrawRectangle(surface, new Pen(selected ? accent : border, selected ? 2 : 1), rect, 6, 6);

            var typeBrush = Brush(ObjectTypeColors.ResourceKey(t.Object.Type) ?? "AppObjectTableBrush", accent);
            using (context.PushClip(new RoundedRect(rect, 6)))
            {
                context.DrawRectangle(subtle, null, new Rect(t.X, t.Y, QueryTableItem.Width, QueryTableItem.HeaderHeight));
                context.DrawRectangle(typeBrush, null, new Rect(t.X, t.Y, 4, QueryTableItem.HeaderHeight));
            }
            DrawText(context, t.Object.FullName, new Point(t.X + 12, t.Y + 7), fg, 12.5, FontWeight.SemiBold, QueryTableItem.Width - 70);
            DrawText(context, t.Alias, new Point(t.X + QueryTableItem.Width - 58, t.Y + 8), accent, 11.5, FontWeight.Bold, 34);
            var close = CloseRect(t);
            DrawText(context, "✕", new Point(close.X + 3, close.Y), hint, 11, FontWeight.Normal, CloseSize);

            for (var row = 0; row < t.RowCount; row++)
            {
                var name = t.ColumnAtRow(row);
                var y = t.Y + QueryTableItem.HeaderHeight + QueryTableItem.Padding + row * QueryTableItem.RowHeight;
                var ticked = vm.IsOutput(t, name);
                var hovered = _pressedColumn is { } pc && pc.Table == t && pc.Column == name;
                if (ticked || hovered)
                    context.DrawRectangle(accentSoft, null, new Rect(t.X + 1, y, QueryTableItem.Width - 2, QueryTableItem.RowHeight));

                var box = new Rect(t.X + 10, y + 5, 12, 12);
                context.DrawRectangle(ticked ? accent : null, new Pen(ticked ? accent : hint, 1), box, 2, 2);
                if (ticked)
                {
                    var tick = new Pen(Brushes.White, 1.6);
                    context.DrawLine(tick, new Point(box.X + 2.5, box.Y + 6), new Point(box.X + 5, box.Y + 8.8));
                    context.DrawLine(tick, new Point(box.X + 5, box.Y + 8.8), new Point(box.X + 9.5, box.Y + 3.2));
                }

                if (row == 0)
                {
                    DrawText(context, "* (all columns)", new Point(t.X + 30, y + 3), hint, 12, FontWeight.Normal, 150);
                    continue;
                }
                var column = t.Columns[row - 1];
                DrawText(context, column.Name, new Point(t.X + 30, y + 3), fg, 12, column.IsPrimaryKey ? FontWeight.SemiBold : FontWeight.Normal, 118);
                var marker = column.IsPrimaryKey ? "PK" : column.IsForeignKey ? "FK" : "";
                if (marker.Length > 0)
                    DrawText(context, marker, new Point(t.X + 150, y + 4), column.IsPrimaryKey ? accent : hint, 9.5, FontWeight.Bold, 20);
                DrawText(context, column.DataType, new Point(t.X + 170, y + 4), hint, 10.5, FontWeight.Normal, QueryTableItem.Width - 176);
            }
        }

        foreach (var (join, g) in geometries)
        {
            var selected = vm.SelectedJoin == join;
            var label = g!.Value.Label;
            context.DrawRectangle(selected ? accent : surface, new Pen(selected ? accent : edge, 1), label, 9, 9);
            DrawText(context, KindLabel(join.Kind), new Point(label.X + 4, label.Y + 2.5), selected ? Brushes.White : fg, 10, FontWeight.Bold, label.Width - 6, TextAlignment.Center);
            context.DrawEllipse(selected ? accent : edge, null, g.Value.Start, 3, 3);
            context.DrawEllipse(selected ? accent : edge, null, g.Value.End, 3, 3);
        }

        // Rubber band while dragging a column towards another table.
        if (_pressedColumn is { } from && _dragPoint is { } to)
        {
            var start = new Point(to.X >= from.Table.X + QueryTableItem.Width / 2 ? from.Table.X + QueryTableItem.Width : from.Table.X, from.Table.RowCenter(from.Column));
            context.DrawLine(new Pen(accent, 2) { DashStyle = DashStyle.Dash }, start, to);
            context.DrawEllipse(accent, null, to, 4, 4);
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush b ? b : fallback;

    private static void DrawCurve(DrawingContext context, JoinGeometry g, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(g.Start, false);
            ctx.CubicBezierTo(new Point(g.C1, g.Start.Y), new Point(g.C2, g.End.Y), g.End);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawText(DrawingContext context, string text, Point origin, IBrush brush, double size, FontWeight weight,
        double maxWidth, TextAlignment alignment = TextAlignment.Left)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment
        };
        context.DrawText(formatted, origin);
    }
}
