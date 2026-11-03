using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Controls.Documents;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using LogicalDirection = AvaloniaEdit.Document.LogicalDirection;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Editor;

/// <summary>
/// Draws a Query tab's <see cref="InlineResult"/> cards in the editor, under the line where their statement ends.
/// AvaloniaEdit gives every text line the same height, so the space is made of empty text lines: an element at the end
/// of the line ends its text there and adds as many empty lines as the cards need, all inside the one document line
/// (the document, line numbers, folding and scrolling stay as they are). The cards are drawn over those lines, pinned to
/// the left edge of the text whatever the horizontal scroll. Clicks on a card stay on the card, and the caret never
/// stops on the empty lines: Down from the statement goes on to the next line, Up comes back to the statement.
/// </summary>
public sealed class InlineResultCards : VisualLineElementGenerator
{
    private const double Gap = 4;
    private const double MinCardWidth = 240;

    private readonly TextArea _area;
    private readonly TextView _view;
    private QueryViewModel? _vm;
    private readonly Dictionary<int, LineHost> _hosts = new();
    private readonly Dictionary<InlineResult, Control> _cards = new();
    private readonly Dictionary<InlineResult, Button> _showButtons = new();
    private Dictionary<int, List<Control>> _layout = new();
    private double _lastWidth;
    private TextViewPosition _lastCaret;
    private bool _fixingCaret;
    private bool _caretCheckQueued;
    private bool _redrawQueued;

    public InlineResultCards(TextArea area)
    {
        _area = area;
        _view = area.TextView;
        _view.ElementGenerators.Add(this);
        _view.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.BoundsProperty || _hosts.Count == 0) return;
            var width = _view.Bounds.Width;
            if (Math.Abs(width - _lastWidth) < 1) return;
            _lastWidth = width;
            QueueRedraw();
        };
        // After the move is complete (a document swap moves the caret before the editor is ready to lay lines out).
        area.Caret.PositionChanged += (_, _) =>
        {
            if (_fixingCaret || _caretCheckQueued) return;
            _caretCheckQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _caretCheckQueued = false;
                KeepCaretOffCards();
            }, DispatcherPriority.Input);
        };
        area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Follows the cards of <paramref name="vm"/>, the tab now shown in the editor.</summary>
    public void Bind(QueryViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) { Refresh(); return; }
        if (_vm is not null) _vm.InlineResultsChanged -= Refresh;
        _vm = vm;
        _cards.Clear();
        _showButtons.Clear();
        if (_vm is not null) _vm.InlineResultsChanged += Refresh;
        Refresh();
    }

    /// <summary>The point (in TextView coordinates) is over a card: no hover help.</summary>
    public bool IsOverCard(Point point) =>
        _view.InputHitTest(point) is Visual hit && hit.FindAncestorOfType<LineHost>(includeSelf: true) is not null;

    /// <summary>Rebuilds which lines have cards and redraws them.</summary>
    public void Refresh()
    {
        var cards = _vm?.InlineResults ?? [];
        foreach (var gone in _cards.Keys.Except(cards).ToList())
        {
            _cards.Remove(gone);
            _showButtons.Remove(gone);
        }

        var document = _view.Document;
        var byLine = new Dictionary<int, List<Control>>();
        if (document is not null && _vm is not null)
        {
            foreach (var card in cards)
            {
                if (card.Anchor.IsDeleted || card.Anchor.Offset > document.TextLength) continue;
                var line = document.GetLineByOffset(card.Anchor.Offset).LineNumber;
                if (!_cards.TryGetValue(card, out var control))
                {
                    _cards[card] = control = BuildCard(_vm, card, out var show);
                    _showButtons[card] = show;
                }
                UpdateShowButton(_vm, card, _showButtons[card]);
                (byLine.TryGetValue(line, out var list) ? list : byLine[line] = []).Add(control);
            }
        }

        // Typing moves cards to other line numbers only now and then: redraw only when the layout changed.
        var same = byLine.Count == _layout.Count &&
                   byLine.All(kv => _layout.TryGetValue(kv.Key, out var old) && old.SequenceEqual(kv.Value));
        _layout = byLine;
        if (same) return;

        foreach (var (line, host) in _hosts.ToList())
        {
            if (byLine.ContainsKey(line)) continue;
            host.SetCards([]);
            _hosts.Remove(line);
        }
        foreach (var (line, list) in byLine)
        {
            if (!_hosts.TryGetValue(line, out var host)) _hosts[line] = host = new LineHost(this);
            host.SetCards(list);
        }
        _lastWidth = _view.Bounds.Width;
        _view.Redraw();
    }

    /// <summary>A card's height changed (styles applied once it is on screen, a new width): the empty lines are counted again.</summary>
    private void QueueRedraw()
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;
            _view.Redraw();
        }, DispatcherPriority.Render);
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var line = CurrentContext.VisualLine.LastDocumentLine;
        return _hosts.ContainsKey(line.LineNumber) && startOffset <= line.EndOffset ? line.EndOffset : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.VisualLine.LastDocumentLine;
        if (offset != line.EndOffset || !_hosts.TryGetValue(line.LineNumber, out var host)) return null;
        host.InvalidateMeasure();
        host.Measure(Size.Infinity);
        host.UsedHeight = host.CardsHeight;
        var lineHeight = Math.Max(1, CurrentContext.GlobalTextRunProperties.FontRenderingEmSize * 1.35);
        var emptyLines = Math.Max(1, (int)Math.Ceiling((host.CardsHeight + 2 * Gap) / lineHeight));
        return new CardsElement(host, emptyLines);
    }

    /// <summary>
    /// Zero characters of the document, shown as: end of the text line, a line holding the cards, and more empty lines
    /// until the cards fit. It takes no caret stop of its own.
    /// </summary>
    private sealed class CardsElement(LineHost host, int emptyLines) : VisualLineElement(emptyLines + 1, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            startVisualColumn - VisualColumn == 1
                ? new InlineObjectRun(1, TextRunProperties, host)
                : new TextEndOfLine(1);

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) => -1;
    }

    /// <summary>The visual column where the line's text ends and its cards begin; null for a line without cards.</summary>
    private int? CardsColumn(int lineNumber)
    {
        if (!_hosts.ContainsKey(lineNumber) || _view.GetVisualLine(lineNumber) is not { } visual) return null;
        return visual.Elements.OfType<CardsElement>().FirstOrDefault()?.VisualColumn;
    }

    /// <summary>The caret landed on the empty lines under a statement (Up from the next line, End, a click beside a
    /// card): it goes back to the statement's line, at the column it had or at the end of the text.</summary>
    private void KeepCaretOffCards()
    {
        if (_fixingCaret || _view.Document is null || !ReferenceEquals(_view.Document, _area.Document)) return;
        var caret = _area.Caret;
        if (_hosts.Count == 0)
        {
            _lastCaret = new TextViewPosition(caret.Line, caret.Column);
            return;
        }
        var position = caret.Position;
        if (CardsColumn(position.Line) is { } column && position.VisualColumn > column)
        {
            var length = _view.Document.GetLineByNumber(position.Line).Length;
            var cameFromBelow = _lastCaret.Line > position.Line;
            var target = cameFromBelow ? Math.Min(_lastCaret.Column, length + 1) : length + 1;
            _fixingCaret = true;
            try
            {
                caret.Position = target == length + 1
                    ? new TextViewPosition(position.Line, target, column)
                    : new TextViewPosition(position.Line, target);
            }
            finally
            {
                _fixingCaret = false;
            }
        }
        _lastCaret = caret.Position;
    }

    /// <summary>Down (or Shift+Down) on a statement with cards goes straight to the next line, past the cards.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || (e.KeyModifiers & ~KeyModifiers.Shift) != 0 || _view.Document is not { } document) return;
        var caret = _area.Caret;
        if (CardsColumn(caret.Line) is null || caret.Line >= document.LineCount) return;
        var next = document.GetLineByNumber(caret.Line + 1);
        var column = Math.Min(caret.Column, next.Length + 1);
        var from = caret.Position;
        var to = new TextViewPosition(caret.Line + 1, column);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            _area.Selection = _area.Selection.StartSelectionOrSetEndpoint(from, to);
        else
            _area.ClearSelection();
        caret.Position = to;
        caret.BringCaretToView();
        e.Handled = true;
    }

    private double CardWidth => Math.Max(MinCardWidth, _view.Bounds.Width - 2 * Gap - 16);

    /// <summary>
    /// The cards under one line, on the first empty line the element adds. It takes no room in the line itself; the
    /// cards are placed back at the left edge of the text area, wherever the line's text ends.
    /// </summary>
    internal sealed class LineHost : Control
    {
        private readonly InlineResultCards _owner;
        private readonly StackPanel _stack = new() { Spacing = Gap };
        private double _x;

        public LineHost(InlineResultCards owner)
        {
            _owner = owner;
            _stack.SetValue(TextElement.FontFamilyProperty, FontFamily.Default);
            VisualChildren.Add(_stack);
            LogicalChildren.Add(_stack);
            Cursor = new Cursor(StandardCursorType.Arrow);
            // A click on a card is the card's: the editor would move the caret or start a selection.
            AddHandler(PointerPressedEvent, (_, e) => e.Handled = true);
            AddHandler(PointerReleasedEvent, (_, e) => e.Handled = true);
        }

        /// <summary>The cards' height when the empty lines under them were counted.</summary>
        public double UsedHeight { get; set; }

        public double CardsHeight => _stack.DesiredSize.Height;

        public void SetCards(IReadOnlyList<Control> cards)
        {
            if (_stack.Children.SequenceEqual(cards)) return;
            _stack.Children.Clear();
            foreach (var card in cards)
            {
                (card.Parent as Panel)?.Children.Remove(card);
                _stack.Children.Add(card);
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _stack.Measure(new Size(_owner.CardWidth, double.PositiveInfinity));
            if (Math.Abs(_stack.DesiredSize.Height - UsedHeight) > 0.5) _owner.QueueRedraw();
            return default;
        }

        protected override void ArrangeCore(Rect finalRect)
        {
            // Where the line's text ends, in the text view: the cards go back by that much.
            _x = finalRect.X;
            base.ArrangeCore(finalRect);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _stack.Arrange(new Rect(-_x + Gap, Gap, _owner.CardWidth, _stack.DesiredSize.Height));
            return finalSize;
        }
    }

    // ----- One card -----

    private static void UpdateShowButton(QueryViewModel vm, InlineResult card, Button show)
    {
        // Gone from the grid once a later run replaced it there.
        show.IsVisible = vm.CanShowInGrid(card);
    }

    private static Control BuildCard(QueryViewModel vm, InlineResult card, out Button show)
    {
        var result = card.Result;
        var title = card.Title.Split(" (first", 2)[0];

        var header = new DockPanel { Margin = new Thickness(8, 3, 4, 3), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        DockPanel.SetDock(buttons, Dock.Right);

        var showButton = SmallButton("Show in grid", "Select this result's tab in the grid below");
        showButton.Click += (_, _) => vm.ShowInGrid(card);
        show = showButton;
        var copy = SmallButton("Copy", "Copy the rows shown here, with the column names, as tab-separated text");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(AsText(result));
            vm.Status = "Copied the rows shown under the statement.";
        };
        var close = SmallButton("✕", "Close (the result stays in the grid)");
        close.Click += (_, _) => vm.CloseInlineResultCommand.Execute(card);
        buttons.Children.Add(showButton);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        header.Children.Add(buttons);

        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var info = new TextBlock { Text = $"{card.Summary} · {card.At:HH:mm:ss}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        info.Bind(TextBlock.ForegroundProperty, info.GetResourceObservable("AppHintForegroundBrush"));
        caption.Children.Add(info);
        header.Children.Add(caption);

        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        if (result.Columns.Count > 0)
        {
            panel.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = BuildTable(result)
            });
        }

        var border = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            ClipToBounds = true
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("AppPopupBackgroundBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("AppBorderBrush"));
        return border;
    }

    private static Button SmallButton(string text, string tip)
    {
        var button = new Button
        {
            Content = text, FontSize = 11, Padding = new Thickness(6, 1), MinHeight = 20,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static Grid BuildTable(ResultSetView result)
    {
        var rows = result.Rows.Take(InlineResult.MaxRows).ToList();
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var _ in result.Columns) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // the stripes reach the card's edge
        for (var r = 0; r <= Math.Max(rows.Count, 1); r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var headerBack = new Border();
        headerBack.Bind(Border.BackgroundProperty, headerBack.GetResourceObservable("AppGridHeaderBrush"));
        Grid.SetColumnSpan(headerBack, result.Columns.Count + 1);
        grid.Children.Add(headerBack);
        for (var c = 0; c < result.Columns.Count; c++)
        {
            var name = new TextBlock
            {
                Text = string.IsNullOrEmpty(result.Columns[c]) ? "(no name)" : result.Columns[c],
                FontWeight = FontWeight.SemiBold, FontSize = 12, Padding = new Thickness(8, 3), MaxWidth = 320,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, c);
            grid.Children.Add(name);
        }

        if (rows.Count == 0)
        {
            var none = new TextBlock { Text = "No rows", FontSize = 12, FontStyle = FontStyle.Italic, Padding = new Thickness(8, 3) };
            none.Bind(TextBlock.ForegroundProperty, none.GetResourceObservable("AppHintForegroundBrush"));
            Grid.SetRow(none, 1);
            Grid.SetColumnSpan(none, result.Columns.Count + 1);
            grid.Children.Add(none);
            return grid;
        }

        for (var r = 0; r < rows.Count; r++)
        {
            if (r % 2 == 1)
            {
                var stripe = new Border();
                stripe.Bind(Border.BackgroundProperty, stripe.GetResourceObservable("AppGridAltRowBrush"));
                Grid.SetRow(stripe, r + 1);
                Grid.SetColumnSpan(stripe, result.Columns.Count + 1);
                grid.Children.Add(stripe);
            }
            var values = rows[r].Values;
            for (var c = 0; c < result.Columns.Count; c++)
            {
                var value = c < values.Count ? values[c] : null;
                var cell = new TextBlock
                {
                    Text = QueryViewModel.InlineCellText(value), FontSize = 12, Padding = new Thickness(8, 2), MaxWidth = 320,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = IsNumber(value) ? TextAlignment.Right : TextAlignment.Left,
                    HorizontalAlignment = IsNumber(value) ? HorizontalAlignment.Right : HorizontalAlignment.Left
                };
                if (value is null or DBNull) cell.FontStyle = FontStyle.Italic;
                cell.Bind(TextBlock.ForegroundProperty, cell.GetResourceObservable(BrushFor(value)));
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    private static bool IsNumber(object? value) => value is byte or sbyte or short or ushort or int or uint or long or ulong
        or float or double or decimal;

    private static string BrushFor(object? value) => value switch
    {
        null or DBNull => "AppCellNullBrush",
        bool => "AppCellBooleanBrush",
        DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => "AppCellDateBrush",
        Guid => "AppCellGuidBrush",
        byte[] => "AppCellBinaryBrush",
        _ when IsNumber(value) => "AppCellNumberBrush",
        _ => "AppCellTextBrush"
    };

    /// <summary>The shown rows as tab-separated text with a header line.</summary>
    public static string AsText(ResultSetView result)
    {
        static string Clean(string s) => s.Replace('\t', ' ').ReplaceLineEndings(" ");
        var lines = new List<string> { string.Join('\t', result.Columns.Select(Clean)) };
        foreach (var row in result.Rows.Take(InlineResult.MaxRows))
            lines.Add(string.Join('\t', row.Values.Select(v => Clean(ResultViewQuery.DisplayText(v) ?? "NULL"))));
        return string.Join(Environment.NewLine, lines);
    }
}
