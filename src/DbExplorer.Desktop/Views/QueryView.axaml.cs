using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.Editor;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>
/// The SQL editor of one query tab. Besides highlighting and context-aware completion it offers what paid SQL IDEs
/// do: signature hints, go to definition (F12 / Ctrl+Click), matching brackets, highlighted occurrences, folding,
/// auto-closed brackets and quotes, line editing shortcuts, error markers, execution plans and running the selection,
/// the statement at the caret or the whole script.
/// </summary>
public partial class QueryView : UserControl
{
    private const int MaxLengthForLiveAnalysis = 400_000;

    private static readonly Regex KeywordBeforeSpace = new(@"\b(FROM|JOIN|ON|INTO|UPDATE|EXEC|EXECUTE|CALL|APPLY|TABLE|WHERE|AND|OR|BY|SELECT|SET)\s$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InsertColumnsOpen = new(@"\bINTO\s+[\w\.\[\]""]+\s*\($", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<char, char> Pairs = new Dictionary<char, char>
        { ['('] = ')', ['['] = ']', ['\''] = '\'', ['"'] = '"' };

    private readonly SearchPanel _search;
    private readonly EditorDecorations _decorations = new();
    private readonly DispatcherTimer _analysisTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _foldingTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private FoldingManager? _folding;
    private CompletionWindow? _completion;
    private QueryViewModel? _vm;

    public QueryView()
    {
        InitializeComponent();
        _search = SearchPanel.Install(Editor);

        var options = Editor.Options;
        options.ConvertTabsToSpaces = true;
        options.IndentationSize = 4;
        options.HighlightCurrentLine = true;
        options.EnableHyperlinks = false;
        options.EnableEmailHyperlinks = false;
        options.AllowScrollBelowDocument = true;

        // A translucent selection keeps the syntax colors readable.
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x33, 0x88, 0xFF));
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.TextView.BackgroundRenderers.Add(_decorations);

        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.Caret.PositionChanged += (_, _) => { UpdateCaretInfo(); _analysisTimer.Stop(); _analysisTimer.Start(); };
        Editor.TextArea.SelectionChanged += (_, _) => UpdateCaretInfo();
        Editor.PointerHover += OnPointerHover;
        Editor.PointerHoverStopped += (_, _) => HideHover();
        Editor.TextArea.AddHandler(PointerPressedEvent, OnEditorPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ResultsSplitter.DoubleTapped += (_, _) => SetSplit(DefaultEditorShare, DefaultResultsShare);
        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
        DataContextChanged += (_, _) => BindDocument();

        _analysisTimer.Tick += (_, _) => { _analysisTimer.Stop(); UpdateCaretMarks(); UpdateSignatureHelp(); };
        _foldingTimer.Tick += (_, _) => { _foldingTimer.Stop(); UpdateFoldings(); };
        ApplyHighlighting();
    }

    /// <summary>Each tab owns its document (text + undo history); the view is reused when tabs switch.</summary>
    private void BindDocument()
    {
        _completion?.Close();
        SignaturePopup.IsOpen = false;
        if (_vm is not null)
        {
            _vm.ErrorLocated -= OnErrorLocated;
            _vm.ErrorCleared -= OnErrorCleared;
            Editor.Document.Changed -= OnDocumentChanged;
        }

        _vm = DataContext as QueryViewModel;
        if (_vm is not null)
        {
            if (!ReferenceEquals(Editor.Document, _vm.Document)) Editor.Document = _vm.Document;
            _vm.ErrorLocated += OnErrorLocated;
            _vm.ErrorCleared += OnErrorCleared;
            Editor.Document.Changed += OnDocumentChanged;
        }

        _decorations.Error = null;
        _decorations.Executed = null;
        if (_folding is not null) FoldingManager.Uninstall(_folding);
        _folding = FoldingManager.Install(Editor.TextArea);
        UpdateFoldings();
        UpdateCaretMarks();
        UpdateCaretInfo();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        // Marks of a previous run no longer match the text once it is edited.
        if (_decorations.Error is not null || _decorations.Executed is not null)
        {
            _decorations.Error = null;
            _decorations.Executed = null;
            Redraw();
        }
        _foldingTimer.Stop();
        _foldingTimer.Start();
    }

    private void ApplyHighlighting() =>
        Editor.SyntaxHighlighting = SqlHighlighting.Get(dark: ActualThemeVariant == ThemeVariant.Dark);

    private void Redraw() => Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

    // ----- Typing: completion, auto-closing pairs, signature help -----

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        var ch = e.Text[0];

        // A character that cannot be part of a name ends the word: close the list without inserting anything.
        if (_completion is not null && !IsWordChar(ch)) _completion.Close();

        if (e.Text.Length == 1 && TryAutoPair(ch)) e.Handled = true;
    }

    /// <summary>Wraps a selection in the pair, types over an existing closing character, or inserts both halves.</summary>
    private bool TryAutoPair(char ch)
    {
        var document = Editor.Document;
        var caret = Editor.CaretOffset;
        var next = caret < document.TextLength ? document.GetCharAt(caret) : '\0';
        var previous = caret > 0 ? document.GetCharAt(caret - 1) : '\0';

        if (Pairs.TryGetValue(ch, out var close) && Editor.SelectionLength > 0)
        {
            var (start, length) = (Editor.SelectionStart, Editor.SelectionLength);
            var selected = document.GetText(start, length);
            document.Replace(start, length, ch + selected + close);
            Editor.Select(start + 1, length);
            return true;
        }

        var isCloser = ch is ')' or ']' || (ch is '\'' or '"' && next == ch);
        if (isCloser && next == ch)
        {
            Editor.CaretOffset = caret + 1;
            return true;
        }

        if (!Pairs.TryGetValue(ch, out close)) return false;
        var nextAllowsPair = next is '\0' or ' ' or '\t' or '\r' or '\n' or ')' or ']' or ',' or ';';
        if (!nextAllowsPair) return false;
        if (ch is '\'' or '"' && (IsWordChar(previous) || InsideStringOrComment(caret))) return false;

        document.Insert(caret, $"{ch}{close}");
        Editor.CaretOffset = caret + 1;
        if (ch == '(') OnTextEntered(this, new TextInputEventArgs { Text = "(" });
        return true;
    }

    private bool InsideStringOrComment(int offset) =>
        SqlLexer.Tokenize(Editor.Document.GetText(0, offset)).LastOrDefault() is { Kind: SqlTokenKind.String or SqlTokenKind.Comment } last &&
        (last.End == offset);

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_vm is null || string.IsNullOrEmpty(e.Text)) return;
        var ch = e.Text[0];
        if (ch is '(' or ',' or ')') UpdateSignatureHelp();
        if (_completion is not null) return;
        var caret = Editor.CaretOffset;

        if (ch == '.')
        {
            ShowCompletion(explicitRequest: false);
        }
        else if (IsWordChar(ch) && !char.IsDigit(ch))
        {
            // Open once, at the start of a word; the list then filters itself as typing continues.
            var wordStart = caret - 1;
            if (wordStart == 0 || !IsWordChar(Editor.Document.GetCharAt(wordStart - 1)) || Editor.Document.GetCharAt(wordStart - 1) == '.')
                ShowCompletion(explicitRequest: false);
        }
        else if (ch == ' ' || ch == '(')
        {
            var line = Editor.Document.GetLineByOffset(caret);
            var before = Editor.Document.GetText(line.Offset, caret - line.Offset);
            if ((ch == ' ' && KeywordBeforeSpace.IsMatch(before)) || (ch == '(' && InsertColumnsOpen.IsMatch(before)))
                ShowCompletion(explicitRequest: true);
        }
    }

    private void ShowCompletion(bool explicitRequest)
    {
        if (_vm is null) return;
        _completion?.Close();

        var caret = Editor.CaretOffset;
        var result = _vm.GetCompletions(Editor.Document.Text, caret, explicitRequest);
        if (result.Items.Count == 0) return;

        var window = new CompletionWindow(Editor.TextArea)
        {
            StartOffset = Math.Clamp(result.ReplaceStart, 0, caret),
            EndOffset = caret,
            CloseWhenCaretAtBeginning = !explicitRequest,
            MinWidth = 420,
            MaxWidth = 760
        };
        // "Expand *" replaces text that does not look like its label, so it must not be filtered away.
        window.CompletionList.IsFiltering = !(result.Items.Count == 1 && result.Items[0].Kind == CompletionKind.Snippet && result.ReplaceStart < caret);
        foreach (var item in result.Items) window.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        window.Closed += (_, _) => { if (ReferenceEquals(_completion, window)) _completion = null; };
        _completion = window;
        window.Show();
        window.CompletionList.SelectedItem = window.CompletionList.CompletionData[0];
    }

    /// <summary>Shows "DATEADD(part, **number**, date)" above the caret while inside a known function's arguments.</summary>
    private void UpdateSignatureHelp()
    {
        if (_vm is null || Editor.Document.TextLength > MaxLengthForLiveAnalysis || !Editor.TextArea.IsKeyboardFocusWithin)
        {
            SignaturePopup.IsOpen = false;
            return;
        }

        var signature = _vm.SignatureAt(Editor.Document.Text, Editor.CaretOffset);
        if (signature is not { } s)
        {
            SignaturePopup.IsOpen = false;
            return;
        }

        SignatureText.Inlines = BuildSignatureInlines(s.Signature, s.Argument);
        var textView = Editor.TextArea.TextView;
        var location = Editor.Document.GetLocation(Editor.CaretOffset);
        var point = textView.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineTop) - textView.ScrollOffset;
        SignaturePopup.PlacementTarget = textView;
        SignaturePopup.HorizontalOffset = Math.Max(0, point.X - 20);
        SignaturePopup.VerticalOffset = point.Y - 2;
        SignaturePopup.IsOpen = true;
    }

    private static InlineCollection BuildSignatureInlines(string signature, int argument)
    {
        var inlines = new InlineCollection();
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            inlines.Add(new Run(signature));
            return inlines;
        }

        inlines.Add(new Run(signature[..(open + 1)]));
        var args = signature[(open + 1)..close].Split(", ");
        for (var i = 0; i < args.Length; i++)
        {
            if (i > 0) inlines.Add(new Run(", "));
            var current = i == argument || (i == args.Length - 1 && argument >= args.Length && args[i].Contains('…'));
            inlines.Add(new Run(args[i]) { FontWeight = current ? FontWeight.Bold : FontWeight.Normal, TextDecorations = current ? TextDecorations.Underline : null });
        }
        inlines.Add(new Run(signature[close..]));
        return inlines;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    // ----- Editor / results split -----

    private const double DefaultEditorShare = 2;
    private const double DefaultResultsShare = 3;

    /// <summary>Shares the height between editor and results (proportions; each keeps its minimum height).</summary>
    private void SetSplit(double editor, double results)
    {
        LayoutGrid.RowDefinitions[1].Height = new GridLength(editor, GridUnitType.Star);
        LayoutGrid.RowDefinitions[3].Height = new GridLength(results, GridUnitType.Star);
    }

    private void OnMaximizeResults(object? sender, RoutedEventArgs e) => SetSplit(1, 12);
    private void OnMaximizeEditor(object? sender, RoutedEventArgs e) => SetSplit(12, 1);
    private void OnResetLayout(object? sender, RoutedEventArgs e) => SetSplit(DefaultEditorShare, DefaultResultsShare);

    // ----- Keys -----

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // While the list is open, Enter/Tab/arrows/Escape belong to it.
        if (_completion is not null && e.Key is Key.Enter or Key.Tab or Key.Up or Key.Down or Key.Escape or Key.PageUp or Key.PageDown) return;

        if (e.Key == Key.Escape && SignaturePopup.IsOpen) { SignaturePopup.IsOpen = false; e.Handled = true; }
        else if (ctrl && e.Key == Key.Space) { ShowCompletion(explicitRequest: true); e.Handled = true; }
        else if (e.Key == Key.F5 || (ctrl && e.Key == Key.E)) { Run(currentStatement: false); e.Handled = true; }
        else if (ctrl && e.Key == Key.Enter) { Run(currentStatement: true); e.Handled = true; }
        else if (ctrl && e.Key == Key.L) { Explain(analyze: shift); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.F) { Format(); e.Handled = true; }
        else if (ctrl && e.Key is Key.Oem2 or Key.Divide) { ToggleComment(); e.Handled = true; }
        else if (ctrl && e.Key == Key.H) { _search.IsReplaceMode = true; _search.Open(); e.Handled = true; }
        else if (ctrl && e.Key == Key.D) { Duplicate(); e.Handled = true; }
        else if (ctrl && e.Key == Key.G) { OpenGoToLine(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.U) { ChangeCase(upper: true); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.U) { ChangeCase(upper: false); e.Handled = true; }
        else if (ctrl && shift && e.Key is Key.Up or Key.Down)
        {
            if (e.Key == Key.Up) SetSplit(1, 12);
            else SetSplit(12, 1);
            e.Handled = true;
        }
        else if (alt && e.Key is Key.Up or Key.Down) { MoveLines(e.Key == Key.Up ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.F12) { e.Handled = true; await GoToDefinitionAsync(Editor.CaretOffset); }
        else if (e.Key == Key.Back && !ctrl && DeleteEmptyPair()) e.Handled = true;
    }

    /// <summary>Backspace between an auto-inserted "()" / "''" pair removes both halves.</summary>
    private bool DeleteEmptyPair()
    {
        var caret = Editor.CaretOffset;
        var document = Editor.Document;
        if (Editor.SelectionLength > 0 || caret == 0 || caret >= document.TextLength) return false;
        var before = document.GetCharAt(caret - 1);
        if (!Pairs.TryGetValue(before, out var close) || document.GetCharAt(caret) != close) return false;
        document.Remove(caret - 1, 2);
        return true;
    }

    // ----- Commands -----

    private void OnRun(object? sender, RoutedEventArgs e) => Run(currentStatement: false);
    private void OnRunStatement(object? sender, RoutedEventArgs e) => Run(currentStatement: true);
    private void OnExplain(object? sender, RoutedEventArgs e) => Explain(analyze: false);
    private void OnExplainAnalyze(object? sender, RoutedEventArgs e) => Explain(analyze: true);
    private void OnFormat(object? sender, RoutedEventArgs e) => Format();
    private void OnToggleComment(object? sender, RoutedEventArgs e) => ToggleComment();
    private void OnUpperCase(object? sender, RoutedEventArgs e) => ChangeCase(upper: true);
    private void OnLowerCase(object? sender, RoutedEventArgs e) => ChangeCase(upper: false);
    private void OnDuplicate(object? sender, RoutedEventArgs e) => Duplicate();
    private void OnGoToLine(object? sender, RoutedEventArgs e) => OpenGoToLine();
    private async void OnGoToDefinition(object? sender, RoutedEventArgs e) => await GoToDefinitionAsync(Editor.CaretOffset);

    private void OnFind(object? sender, RoutedEventArgs e)
    {
        _search.IsReplaceMode = false;
        _search.Open();
    }

    private void OnReplace(object? sender, RoutedEventArgs e)
    {
        _search.IsReplaceMode = true;
        _search.Open();
    }

    /// <summary>
    /// The text to run: the selection, else (for "statement") the statement at the caret, else the whole script.
    /// The statement is highlighted rather than selected, so typing afterwards cannot overwrite it.
    /// </summary>
    private QueryRun? Target(bool currentStatement)
    {
        if (Editor.SelectionLength > 0) return new QueryRun(Editor.SelectedText, Editor.SelectionStart);
        if (currentStatement && SqlScriptTools.StatementAt(Editor.Text, Editor.CaretOffset) is { } range)
        {
            _decorations.Executed = (range.Start, range.Length);
            Redraw();
            return new QueryRun(range.Of(Editor.Text), range.Start);
        }
        return null;
    }

    private void Run(bool currentStatement)
    {
        if (_vm is null) return;
        var run = Target(currentStatement);
        if (_vm.ExecuteCommand.CanExecute(run)) _vm.ExecuteCommand.Execute(run);
    }

    private void Explain(bool analyze)
    {
        if (_vm is null) return;
        var run = Target(currentStatement: true);
        var command = analyze ? _vm.ExplainAnalyzeCommand : _vm.ExplainCommand;
        if (command.CanExecute(run)) command.Execute(run);
    }

    private void Format()
    {
        var document = Editor.Document;
        var (start, length) = Editor.SelectionLength > 0 ? (Editor.SelectionStart, Editor.SelectionLength) : (0, document.TextLength);
        if (length == 0) return;
        var formatted = SqlScriptTools.Format(document.GetText(start, length));
        document.Replace(start, length, formatted);
        if (start > 0 || Editor.SelectionLength > 0) Editor.Select(start, formatted.Length);
    }

    /// <summary>The whole lines touched by the selection (or the caret line).</summary>
    private (int Start, int Length) SelectedLines()
    {
        var document = Editor.Document;
        var selectionEnd = Editor.SelectionStart + Editor.SelectionLength;
        var first = document.GetLineByOffset(Editor.SelectionStart);
        var last = document.GetLineByOffset(selectionEnd);
        // A selection ending at the very start of a line does not include that line.
        if (last.LineNumber > first.LineNumber && selectionEnd == last.Offset) last = last.PreviousLine;
        return (first.Offset, last.EndOffset - first.Offset);
    }

    private void ToggleComment()
    {
        var (start, length) = SelectedLines();
        var toggled = SqlScriptTools.ToggleLineComments(Editor.Document.GetText(start, length));
        Editor.Document.Replace(start, length, toggled);
        Editor.Select(start, toggled.Length);
    }

    private void ChangeCase(bool upper)
    {
        if (Editor.SelectionLength == 0) return;
        var (start, length) = (Editor.SelectionStart, Editor.SelectionLength);
        var text = Editor.Document.GetText(start, length);
        Editor.Document.Replace(start, length, upper ? text.ToUpperInvariant() : text.ToLowerInvariant());
        Editor.Select(start, length);
    }

    private void Duplicate()
    {
        var document = Editor.Document;
        if (Editor.SelectionLength > 0)
        {
            var (start, length) = (Editor.SelectionStart, Editor.SelectionLength);
            document.Insert(start + length, document.GetText(start, length));
            Editor.Select(start + length, length);
            return;
        }
        var line = document.GetLineByOffset(Editor.CaretOffset);
        var column = Editor.CaretOffset - line.Offset;
        var text = document.GetText(line.Offset, line.Length);
        document.Insert(line.EndOffset, "\n" + text);
        Editor.CaretOffset = line.NextLine!.Offset + column;
    }

    /// <summary>Alt+Up / Alt+Down: moves the selected lines past the neighbouring line.</summary>
    private void MoveLines(int direction)
    {
        var document = Editor.Document;
        var (start, length) = SelectedLines();
        var firstLine = document.GetLineByOffset(start);
        var lastLine = document.GetLineByOffset(start + length);
        var neighbour = direction < 0 ? firstLine.PreviousLine : lastLine.NextLine;
        if (neighbour is null) return;

        var block = document.GetText(start, length);
        var other = document.GetText(neighbour.Offset, neighbour.Length);
        var caretColumn = Editor.CaretOffset - start;
        using (document.RunUpdate())
        {
            if (direction < 0)
            {
                document.Replace(neighbour.Offset, lastLine.EndOffset - neighbour.Offset, block + "\n" + other);
                Editor.Select(neighbour.Offset, block.Length);
                Editor.CaretOffset = neighbour.Offset + caretColumn;
            }
            else
            {
                document.Replace(start, neighbour.EndOffset - start, other + "\n" + block);
                var newStart = start + other.Length + 1;
                Editor.Select(newStart, block.Length);
                Editor.CaretOffset = newStart + caretColumn;
            }
        }
    }

    private void OpenGoToLine()
    {
        GoToLineBox.Text = Editor.TextArea.Caret.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        GoToLinePopup.PlacementTarget = Editor;
        GoToLinePopup.IsOpen = true;
        GoToLineBox.Focus();
        GoToLineBox.SelectAll();
    }

    private void OnGoToLineKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { GoToLinePopup.IsOpen = false; Editor.TextArea.Focus(); return; }
        if (e.Key != Key.Enter) return;
        if (int.TryParse(GoToLineBox.Text, out var line))
        {
            line = Math.Clamp(line, 1, Editor.Document.LineCount);
            Editor.CaretOffset = Editor.Document.GetLineByNumber(line).Offset;
            Editor.ScrollToLine(line);
        }
        GoToLinePopup.IsOpen = false;
        Editor.TextArea.Focus();
        e.Handled = true;
    }

    private async Task GoToDefinitionAsync(int offset)
    {
        if (_vm is null) return;
        if (!await _vm.GoToDefinitionAsync(Editor.Document.Text, offset) && string.IsNullOrEmpty(_vm.Status))
            _vm.Status = "No table, view or routine at the caret.";
    }

    private async void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl || !e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed) return;
        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position is null) return;
        e.Handled = true;
        await GoToDefinitionAsync(Editor.Document.GetOffset(position.Value.Location));
    }

    // ----- Error markers -----

    private void OnErrorLocated(int runStart, int line, int? column)
    {
        var document = Editor.Document;
        if (runStart > document.TextLength) return;
        var baseLine = document.GetLineByOffset(runStart);
        var number = baseLine.LineNumber + line - 1;
        if (number < 1 || number > document.LineCount) return;

        var target = document.GetLineByNumber(number);
        var lineText = document.GetText(target.Offset, target.Length);
        int start, length;
        if (column is { } col)
        {
            // Column is 1-based within the run's text; on its first line the run may start mid-line.
            var offsetInLine = (line == 1 ? runStart - baseLine.Offset : 0) + col - 1;
            offsetInLine = Math.Clamp(offsetInLine, 0, lineText.Length);
            var end = offsetInLine;
            while (end < lineText.Length && (IsWordChar(lineText[end]) || lineText[end] is '.' or '[' or ']' or '"')) end++;
            (start, length) = (target.Offset + offsetInLine, Math.Max(1, end - offsetInLine));
        }
        else
        {
            var indent = lineText.Length - lineText.TrimStart().Length;
            (start, length) = (target.Offset + indent, Math.Max(1, lineText.Trim().Length));
        }

        _decorations.Error = (start, Math.Min(length, document.TextLength - start));
        _decorations.Executed = null;
        Editor.CaretOffset = Math.Min(start, document.TextLength);
        Editor.ScrollToLine(number);
        Redraw();
    }

    private void OnErrorCleared()
    {
        _decorations.Error = null;
        Redraw();
    }

    // ----- Caret marks, folding, hover, status -----

    private void UpdateCaretMarks()
    {
        var document = Editor.Document;
        if (document.TextLength > MaxLengthForLiveAnalysis)
        {
            _decorations.Brackets = [];
            _decorations.Occurrences = [];
            return;
        }

        var text = document.Text;
        var caret = Editor.CaretOffset;
        _decorations.Brackets = SqlEditorAnalysis.MatchingParentheses(text, caret) is { } pair ? [(pair.Open, 1), (pair.Close, 1)] : [];

        var occurrences = new List<(int Start, int Length)>();
        if (Editor.SelectionLength == 0 && SqlEditorAnalysis.IdentifierAt(text, caret) is { } id)
        {
            foreach (var t in SqlLexer.Tokenize(text))
                if (t.IsIdentifier && string.Equals(t.Identifier, id.Identifier, StringComparison.OrdinalIgnoreCase))
                    occurrences.Add((t.Start, t.Length));
        }
        _decorations.Occurrences = occurrences.Count > 1 ? occurrences : [];
        Redraw();
    }

    private void UpdateFoldings()
    {
        if (_folding is null) return;
        var text = Editor.Document.TextLength > MaxLengthForLiveAnalysis ? "" : Editor.Document.Text;
        var foldings = SqlEditorAnalysis.FoldRegions(text).Select(r => new NewFolding(r.Start, r.End) { Name = r.Title });
        _folding.UpdateFoldings(foldings, -1);
    }

    private void OnPointerHover(object? sender, PointerEventArgs e)
    {
        if (_vm is null) return;
        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position is null) return;
        var offset = Editor.Document.GetOffset(position.Value.Location);
        var text = _vm.Describe(Editor.Document.Text, offset);
        if (text is null) return;

        ToolTip.SetTip(Editor, new TextBlock
        {
            Text = text + "\n\nF12 / Ctrl+Click: open definition",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12
        });
        ToolTip.SetIsOpen(Editor, true);
    }

    /// <summary>Clears the tip as well as closing it: a tip left on the editor would pop up again anywhere.</summary>
    private void HideHover()
    {
        ToolTip.SetIsOpen(Editor, false);
        ToolTip.SetTip(Editor, null);
    }

    private void UpdateCaretInfo()
    {
        var caret = Editor.TextArea.Caret;
        var selected = Editor.SelectionLength;
        var dialect = _vm?.ProviderKey switch { "SqlServer" => " · SQL Server", "Postgres" => " · PostgreSQL", _ => "" };
        CaretInfo.Text = $"Ln {caret.Line}, Col {caret.Column}" + (selected > 0 ? $" · {selected:N0} selected" : "") + dialect;
    }
}
