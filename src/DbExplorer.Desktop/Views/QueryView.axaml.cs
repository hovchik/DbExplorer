using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using DbExplorer.Application;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.Editor;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>
/// The SQL editor of one query tab. Besides highlighting and context-aware completion it offers what paid SQL IDEs
/// do: signature hints, go to definition (F12 / Ctrl+Click), matching brackets, highlighted occurrences, folding,
/// auto-closed brackets and quotes, line editing shortcuts, error markers, live SQL warnings with quick fixes
/// (Alt+Enter), execution plans and running the selection, the statement at the caret or the whole script.
/// </summary>
public partial class QueryView : UserControl
{
    private const int MaxLengthForLiveAnalysis = 400_000;

    private static readonly Regex KeywordBeforeSpace = new(@"\b(FROM|JOIN|ON|INTO|UPDATE|EXEC|EXECUTE|CALL|APPLY|TABLE|WHERE|AND|OR|BY|SELECT|SET)\s$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Right after "col = ", "col <> '", "col LIKE ", "col IN (" or a comma inside such a list: values of the column fit here.</summary>
    private static readonly Regex ValuePosition = new(@"[\w\]""]\s*(=|<>|!=|\bLIKE|\bILIKE|\bIN\s*\((?:[^()]*,)?)\s*'?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private int? _pendingValueCaret;

    /// <summary>Shows the column's values when they are known; otherwise remembers the spot and retries when they arrive.</summary>
    private void ShowValueCompletion()
    {
        if (_vm is null) return;
        var caret = Editor.CaretOffset;
        var result = _vm.GetCompletions(Editor.Document.Text, caret, explicitRequest: false);
        if (result.Items.Count > 0 && result.Items[0].Kind == CompletionKind.Value)
        {
            _pendingValueCaret = null;
            ShowCompletion(explicitRequest: false);
        }
        else
        {
            _pendingValueCaret = caret;
        }
    }

    private void OnCompletionValuesArrived()
    {
        if (_pendingValueCaret is int caret && caret == Editor.CaretOffset && _completion is null && Editor.TextArea.IsKeyboardFocusWithin)
            ShowValueCompletion();
    }

    private static readonly Regex InsertColumnsOpen = new(@"\bINTO\s+[\w\.\[\]""]+\s*\($", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<char, char> Pairs = new Dictionary<char, char>
        { ['('] = ')', ['['] = ']', ['\''] = '\'', ['"'] = '"' };

    private readonly SearchPanel _search;
    private readonly EditorDecorations _decorations = new();
    private readonly MultiCaretSession _carets;
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
        _carets = new MultiCaretSession(Editor.TextArea);

        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.Caret.PositionChanged += (_, _) => { UpdateCaretInfo(); _analysisTimer.Stop(); _analysisTimer.Start(); RestartCostLens(); OnCaretMovedForInspections(); };
        Editor.TextArea.SelectionChanged += (_, _) => UpdateCaretInfo();
        Editor.PointerHover += OnPointerHover;
        Editor.PointerHoverStopped += (_, _) => HideHover();
        Editor.TextArea.AddHandler(PointerPressedEvent, OnEditorPointerPressed, RoutingStrategies.Tunnel);
        Editor.AddHandler(PointerWheelChangedEvent, OnEditorWheel, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ResultsSplitter.DoubleTapped += (_, _) => SetSplit(DefaultEditorShare, DefaultResultsShare);
        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
        DataContextChanged += (_, _) => BindDocument();

        _analysisTimer.Tick += (_, _) => { _analysisTimer.Stop(); UpdateCaretMarks(); UpdateSignatureHelp(); };
        _foldingTimer.Tick += (_, _) => { _foldingTimer.Stop(); UpdateFoldings(); };
        _costLensTimer.Tick += OnCostLensTick;
        _inspectionTimer.Tick += OnInspectionTick;
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
            _vm.CompletionValuesArrived -= OnCompletionValuesArrived;
            _vm.InspectorChanged -= RestartInspections;
            Editor.Document.Changed -= OnDocumentChanged;
        }

        _vm = DataContext as QueryViewModel;
        if (_vm is not null)
        {
            if (!ReferenceEquals(Editor.Document, _vm.Document)) Editor.Document = _vm.Document;
            _vm.ErrorLocated += OnErrorLocated;
            _vm.ErrorCleared += OnErrorCleared;
            _vm.CompletionValuesArrived += OnCompletionValuesArrived;
            _vm.InspectorChanged += RestartInspections;
            Editor.Document.Changed += OnDocumentChanged;
        }
        HookSettings();

        _carets.Clear();
        _decorations.Error = null;
        _decorations.Executed = null;
        if (_folding is not null) FoldingManager.Uninstall(_folding);
        _folding = FoldingManager.Install(Editor.TextArea);
        UpdateFoldings();
        UpdateCaretMarks();
        UpdateCaretInfo();
        RestartCostLens();
        ShowInspections([]);
        RestartInspections();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        OnDocumentChangedForInspections(e);
        // Marks of a previous run no longer match the text once it is edited.
        if (_decorations.Error is not null || _decorations.Executed is not null)
        {
            _decorations.Error = null;
            _decorations.Executed = null;
            Redraw();
        }
        _foldingTimer.Stop();
        _foldingTimer.Start();
        RestartCostLens();
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
        if (ch is '(' or '\'') OnTextEntered(this, new TextInputEventArgs { Text = ch.ToString() });
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
        else if (ch is ' ' or '(' or '\'' or ',')
        {
            var line = Editor.Document.GetLineByOffset(caret);
            var before = Editor.Document.GetText(line.Offset, caret - line.Offset);
            if ((ch == ' ' && KeywordBeforeSpace.IsMatch(before)) || (ch == '(' && InsertColumnsOpen.IsMatch(before)))
                ShowCompletion(explicitRequest: true);
            else if (ValuePosition.IsMatch(before))
                ShowValueCompletion();
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

        if (_carets.IsActive && (e.Key == Key.Escape || (ctrl && e.Key is Key.Z or Key.Y)))
        {
            // Undo and redo replay single edits, which extra carets would repeat: go back to one caret first.
            _carets.Clear();
            if (e.Key == Key.Escape) { e.Handled = true; return; }
        }

        if (e.Key == Key.Escape && SignaturePopup.IsOpen) { SignaturePopup.IsOpen = false; e.Handled = true; }
        else if (alt && !ctrl && e.Key == Key.Enter) { ShowFixes(); e.Handled = true; }
        else if (e.Key == Key.F8) { GoToProblem(shift ? -1 : 1); e.Handled = true; }
        else if (ctrl && alt && shift && e.Key == Key.J) { SelectAllOccurrences(); e.Handled = true; }
        else if (alt && !ctrl && e.Key == Key.J) { AddNextOccurrence(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Space) { ShowCompletion(explicitRequest: true); e.Handled = true; }
        else if (e.Key == Key.F5 || (ctrl && e.Key == Key.E)) { Run(currentStatement: false); e.Handled = true; }
        else if (ctrl && e.Key == Key.Enter) { Run(currentStatement: true); e.Handled = true; }
        else if (ctrl && e.Key == Key.L) { Explain(analyze: shift); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.F) { Format(); e.Handled = true; }
        else if (ctrl && e.Key is Key.Oem2 or Key.Divide) { ToggleComment(); e.Handled = true; }
        else if (ctrl && e.Key == Key.H) { _search.IsReplaceMode = true; _search.Open(); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemPlus or Key.Add) { Zoom(+1); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { Zoom(-1); e.Handled = true; }
        else if (ctrl && e.Key is Key.D0 or Key.NumPad0) { ResetZoom(); e.Handled = true; }
        else if (alt && e.Key == Key.Z) { ToggleWordWrap(); e.Handled = true; }
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
        // Alt+Shift+arrows stay with the editor's box selection.
        else if (alt && !shift && e.Key is Key.Up or Key.Down) { MoveLines(e.Key == Key.Up ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.F12) { e.Handled = true; await GoToDefinitionAsync(Editor.CaretOffset); }
        else if (e.Key == Key.Back && !ctrl && DeleteEmptyPair()) e.Handled = true;
    }

    // ----- Several carets -----

    /// <summary>The selection, or a plain caret, becomes the first of several carets.</summary>
    private void StartCarets()
    {
        if (_carets.IsActive) return;
        _carets.Clear();
        _carets.Add(Editor.SelectionStart, Editor.SelectionLength);
    }

    /// <summary>Ctrl+Alt+Click: one more caret where the mouse is; the real caret stays where it was.</summary>
    private void AddCaret(int offset)
    {
        StartCarets();
        _carets.Add(offset, 0);
        UpdateCaretCount();
    }

    /// <summary>
    /// Alt+J: the first press selects the word at the caret; each next press adds the next occurrence of it (or of the
    /// selection) and moves the selection there, so typing replaces them all.
    /// </summary>
    private void AddNextOccurrence()
    {
        var text = Editor.Document.Text;
        if (!_carets.IsActive && Editor.SelectionLength == 0)
        {
            if (MultiCaret.WordAt(text, Editor.CaretOffset) is { } word) Editor.Select(word.Start, word.Length);
            return;
        }
        var needle = Editor.SelectedText;
        if (needle.Length == 0 || needle.Contains('\n')) return;
        StartCarets();
        var wholeWord = MultiCaret.WordAt(text, Editor.SelectionStart) is { } w && w.Start == Editor.SelectionStart && w.Length == needle.Length;
        var taken = _carets.Ranges.Select(r => r.Start).ToList();
        if (MultiCaret.FindNext(text, needle, wholeWord, Editor.SelectionStart + needle.Length, taken) is not { } next)
        {
            if (_vm is not null) _vm.Status = "No more occurrences.";
            return;
        }
        _carets.Add(next, needle.Length);
        Editor.Select(next, needle.Length);
        Editor.TextArea.Caret.BringCaretToView();
        UpdateCaretCount();
    }

    /// <summary>Ctrl+Alt+Shift+J: a caret on every occurrence of the selection or the word at the caret.</summary>
    private void SelectAllOccurrences()
    {
        var text = Editor.Document.Text;
        if (Editor.SelectionLength == 0 && MultiCaret.WordAt(text, Editor.CaretOffset) is { } word) Editor.Select(word.Start, word.Length);
        var needle = Editor.SelectedText;
        if (needle.Length == 0 || needle.Contains('\n')) return;
        var wholeWord = MultiCaret.WordAt(text, Editor.SelectionStart) is { } w && w.Start == Editor.SelectionStart && w.Length == needle.Length;
        _carets.Clear();
        StartCarets();
        foreach (var at in MultiCaret.FindAll(text, needle, wholeWord)) _carets.Add(at, needle.Length);
        UpdateCaretCount();
    }

    private void UpdateCaretCount()
    {
        if (_carets.IsActive && _vm is not null) _vm.Status = $"{_carets.Count} carets: type to edit them all. Esc goes back to one.";
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
    private void OnWhyNot(object? sender, RoutedEventArgs e) => RunLab(_vm?.WhyNotCommand, currentStatement: true);
    private void OnDryRun(object? sender, RoutedEventArgs e) => RunLab(_vm?.DryRunCommand, currentStatement: false);
    private void OnLockImpact(object? sender, RoutedEventArgs e) => RunLab(_vm?.LockImpactCommand, currentStatement: false);

    /// <summary>The why-not debugger works on the statement at the caret; dry run and lock impact on the selection or whole script.</summary>
    private void RunLab(System.Windows.Input.ICommand? command, bool currentStatement)
    {
        if (command is null) return;
        var run = Target(currentStatement);
        if (command.CanExecute(run)) command.Execute(run);
    }

    private void OnExplain(object? sender, RoutedEventArgs e) => Explain(analyze: false);
    private void OnExplainAnalyze(object? sender, RoutedEventArgs e) => Explain(analyze: true);
    private void OnFormat(object? sender, RoutedEventArgs e) => Format();
    private void OnToggleComment(object? sender, RoutedEventArgs e) => ToggleComment();
    private void OnUpperCase(object? sender, RoutedEventArgs e) => ChangeCase(upper: true);
    private void OnLowerCase(object? sender, RoutedEventArgs e) => ChangeCase(upper: false);
    private void OnDuplicate(object? sender, RoutedEventArgs e) => Duplicate();
    private void OnAddNextOccurrence(object? sender, RoutedEventArgs e) => AddNextOccurrence();
    private void OnSelectAllOccurrences(object? sender, RoutedEventArgs e) => SelectAllOccurrences();
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
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (!e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed) return;
        if (ctrl && alt)
        {
            if (Editor.GetPositionFromPoint(e.GetPosition(Editor)) is { } clicked)
                AddCaret(Editor.Document.GetOffset(clicked.Location));
            e.Handled = true;
            return;
        }
        _carets.Clear();
        if (!ctrl) return;
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
        var problems = _inspections.Where(i => offset >= i.Start && offset < Math.Max(i.End, i.Start + 1))
            .OrderByDescending(i => i.Severity).ToList();
        var described = _vm.Describe(Editor.Document.Text, offset);
        if (described is null && problems.Count == 0) return;

        var parts = problems.Select(DescribeProblem).ToList();
        if (described is not null) parts.Add(described + "\n\nF12 / Ctrl+Click: open definition");
        ToolTip.SetTip(Editor, new TextBlock
        {
            Text = string.Join("\n\n", parts),
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

    // ----- Zoom and word wrap (shared by every query tab, remembered between runs) -----

    private AppSettingsService? _settings;

    /// <summary>Follows the app-wide editor settings while this view is on screen; the settings outlive the view.</summary>
    private void HookSettings()
    {
        var wanted = this.IsAttachedToVisualTree() ? _vm?.Settings : null;
        if (ReferenceEquals(wanted, _settings)) { ApplyEditorSettings(); return; }
        if (_settings is not null)
        {
            _settings.EditorChanged -= ApplyEditorSettings;
            _settings.ExperimentChanged -= OnExperimentChanged;
        }
        _settings = wanted;
        if (_settings is not null)
        {
            _settings.EditorChanged += ApplyEditorSettings;
            _settings.ExperimentChanged += OnExperimentChanged;
        }
        ApplyEditorSettings();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookSettings();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        HookSettings();
    }

    private void ApplyEditorSettings()
    {
        if (_vm?.Settings is not { } settings) return;
        Editor.FontSize = settings.EditorFontSize;
        Editor.WordWrap = settings.EditorWordWrap;
        WordWrapItem.IsChecked = settings.EditorWordWrap;
        SqlWarningsItem.IsChecked = settings.SqlInspections;
        RestartInspections();
        ResultDiffItem.IsChecked = settings.IsEnabled(ExperimentalFeature.ResultDiff);
        ExpectationsItem.IsChecked = settings.IsEnabled(ExperimentalFeature.Expectations);
        CostLensItem.IsChecked = settings.IsEnabled(ExperimentalFeature.CostLens);
        var percent = (int)Math.Round(settings.EditorFontSize / AppSettingsService.DefaultEditorFontSize * 100);
        ZoomInfo.Content = $"{percent}%";
        ZoomInfo.IsVisible = percent != 100;
    }

    private void Zoom(int step)
    {
        if (_vm?.Settings is { } settings) settings.SetEditorFontSize(settings.EditorFontSize + step);
    }

    private void ResetZoom() => _vm?.Settings.SetEditorFontSize(AppSettingsService.DefaultEditorFontSize);

    private void ToggleWordWrap()
    {
        if (_vm?.Settings is { } settings) settings.SetEditorWordWrap(!settings.EditorWordWrap);
    }

    private void OnEditorWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) || e.Delta.Y == 0) return;
        Zoom(e.Delta.Y > 0 ? +1 : -1);
        e.Handled = true;
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Zoom(+1);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Zoom(-1);
    private void OnZoomReset(object? sender, RoutedEventArgs e) => ResetZoom();
    private void OnToggleWordWrap(object? sender, RoutedEventArgs e) => ToggleWordWrap();

    // ----- Experimental features (Lab menu) -----

    private void OnToggleExperiment(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } && Enum.TryParse<ExperimentalFeature>(name, out var feature) && _vm?.Settings is { } settings)
            settings.SetEnabled(feature, !settings.IsEnabled(feature));
    }

    private void OnExperimentChanged(ExperimentalFeature feature)
    {
        ApplyEditorSettings();
        _vm?.OnExperimentToggled(feature);
        RestartCostLens();
    }

    private void OnInsertExpectation(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        Editor.CaretOffset = _vm.InsertExpectation(Editor.CaretOffset);
        Editor.Focus();
    }

    /// <summary>The cost lens asks for an estimate once typing or caret moves pause, not on every key.</summary>
    private readonly DispatcherTimer _costLensTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    private void RestartCostLens()
    {
        _costLensTimer.Stop();
        _costLensTimer.Start();
    }

    private async void OnCostLensTick(object? sender, EventArgs e)
    {
        _costLensTimer.Stop();
        if (_vm is not { } vm || !this.IsAttachedToVisualTree() || Editor.Document.TextLength > MaxLengthForLiveAnalysis) return;
        try
        {
            await vm.UpdateCostLensAsync(Editor.Document.Text, Editor.CaretOffset);
        }
        catch
        {
            // The lens is a hint; it never interrupts editing.
        }
    }

    // ----- My snippets -----

    /// <summary>Lists the saved snippets (click inserts at the caret), plus saving the selection and deleting one.</summary>
    private void OnSnippets(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var snippets = vm.Snippets.Items;

        var save = new MenuItem { Header = "Save selection as snippet…", IsEnabled = Editor.SelectionLength > 0 };
        save.Click += async (_, _) => await vm.SaveSnippetAsync(Editor.SelectedText);
        menu.Items.Add(save);
        menu.Items.Add(new Separator());

        if (snippets.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No snippets yet: select some SQL and save it", IsEnabled = false });
        foreach (var snippet in snippets)
        {
            var item = new MenuItem { Header = snippet.Name };
            ToolTip.SetTip(item, new TextBlock
            {
                Text = snippet.Sql.Length > 1500 ? snippet.Sql[..1500] + "…" : snippet.Sql,
                FontFamily = Editor.FontFamily,
                FontSize = 12
            });
            item.Click += (_, _) => InsertSnippet(snippet.Sql);
            menu.Items.Add(item);
        }

        if (snippets.Count > 0)
        {
            var delete = new MenuItem { Header = "Delete" };
            foreach (var snippet in snippets)
            {
                var item = new MenuItem { Header = snippet.Name };
                item.Click += async (_, _) => await vm.DeleteSnippetAsync(snippet);
                delete.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(delete);
        }

        menu.ShowAt(SnippetsButton);
    }

    /// <summary>Replaces the selection (or inserts at the caret) as one undoable edit.</summary>
    private void InsertSnippet(string sql)
    {
        var (start, length) = Editor.SelectionLength > 0 ? (Editor.SelectionStart, Editor.SelectionLength) : (Editor.CaretOffset, 0);
        Editor.Document.Replace(start, length, sql);
        Editor.Select(start + sql.Length, 0);
        Editor.CaretOffset = start + sql.Length;
        Editor.TextArea.Focus();
    }

    // ----- Live SQL warnings (unknown names, ambiguous columns, GROUP BY) with quick fixes -----

    /// <summary>Warnings are recomputed once typing or caret moves pause, on a background thread.</summary>
    private readonly DispatcherTimer _inspectionTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private IReadOnlyList<SqlInspection> _inspections = [];
    private int _inspectionRun;
    private int _documentEdits;

    /// <summary>Where the last edit left the caret: a name ending there is still being typed, so it is not underlined yet.</summary>
    private int _typingAt = -1;

    private void RestartInspections()
    {
        _inspectionTimer.Stop();
        _inspectionTimer.Start();
    }

    private void OnCaretMovedForInspections()
    {
        if (Editor.CaretOffset != _typingAt)
        {
            if (_typingAt >= 0) RestartInspections(); // the name just typed is finished: check it now
            _typingAt = -1;
        }
        UpdateProblemInfo();
    }

    /// <summary>Keeps the underlines in place while the text changes; the ones the edit touches go until the next check.</summary>
    private void OnDocumentChangedForInspections(DocumentChangeEventArgs e)
    {
        _documentEdits++;
        _typingAt = e.Offset + e.InsertionLength;
        if (_inspections.Count > 0)
        {
            var delta = e.InsertionLength - e.RemovalLength;
            var removedEnd = e.Offset + e.RemovalLength;
            var kept = new List<SqlInspection>(_inspections.Count);
            foreach (var i in _inspections)
            {
                if (i.End < e.Offset) kept.Add(i);
                else if (i.Start > removedEnd) kept.Add(i with { Start = i.Start + delta });
            }
            ShowInspections(kept);
        }
        RestartInspections();
    }

    private async void OnInspectionTick(object? sender, EventArgs e)
    {
        _inspectionTimer.Stop();
        var run = ++_inspectionRun;
        if (_vm is not { Inspector: { } inspector } vm || !vm.Settings.SqlInspections || !this.IsAttachedToVisualTree() ||
            Editor.Document.TextLength > MaxLengthForLiveAnalysis)
        {
            ShowInspections([]);
            return;
        }

        var text = Editor.Document.Text;
        var edits = _documentEdits;
        IReadOnlyList<SqlInspection> found;
        try
        {
            found = await Task.Run(() => inspector.Inspect(text));
        }
        catch
        {
            return; // a check that fails must never get in the way of typing
        }
        if (run != _inspectionRun || edits != _documentEdits || !ReferenceEquals(_vm, vm)) return;
        ShowInspections(found);
    }

    private void ShowInspections(IReadOnlyList<SqlInspection> inspections)
    {
        _inspections = inspections;
        _decorations.Problems = inspections
            .Where(i => !(i.End == _typingAt && i.Start < _typingAt))
            .Select(i => (i.Start, i.Length, i.Severity == InspectionSeverity.Error))
            .ToList();
        Redraw();
        UpdateProblemInfo();
    }

    private IEnumerable<SqlInspection> Visible => _inspections.Where(i => !(i.End == _typingAt && i.Start < _typingAt));

    /// <summary>The status bar shows a lightbulb with the warning at the caret, otherwise how many warnings there are.</summary>
    private void UpdateProblemInfo()
    {
        var visible = Visible.ToList();
        if (visible.Count == 0)
        {
            ProblemsInfo.IsVisible = false;
            return;
        }

        var here = SqlInspector.At(visible, Editor.CaretOffset);
        if (here is not null)
        {
            var message = here.Message.Length > 70 ? here.Message[..70] + "…" : here.Message;
            ProblemsInfo.Content = (here.Fixes.Count > 0 ? "💡 " : "⚠ ") + message + (here.Fixes.Count > 0 ? " · Alt+Enter" : "");
        }
        else
        {
            var errors = visible.Count(i => i.Severity == InspectionSeverity.Error);
            var warnings = visible.Count - errors;
            var counts = new List<string>();
            if (errors > 0) counts.Add(errors == 1 ? "1 error" : $"{errors} errors");
            if (warnings > 0) counts.Add(warnings == 1 ? "1 warning" : $"{warnings} warnings");
            ProblemsInfo.Content = "⚠ " + string.Join(", ", counts) + " · F8";
        }
        ProblemsInfo.Foreground = new SolidColorBrush(visible.Any(i => i.Severity == InspectionSeverity.Error)
            ? Color.FromRgb(0xE5, 0x39, 0x35)
            : Color.FromRgb(0xE0, 0xA0, 0x00));
        ProblemsInfo.IsVisible = true;
    }

    private static string DescribeProblem(SqlInspection problem) =>
        problem.Message + problem.Fixes.Count switch
        {
            0 => "",
            1 => $"\nAlt+Enter: {problem.Fixes[0].Title}",
            _ => $"\nAlt+Enter: {problem.Fixes[0].Title} (+{problem.Fixes.Count - 1} more)"
        };

    /// <summary>Alt+Enter: the fixes for the warning at the caret (or the first one on its line), in a menu at the caret.</summary>
    private void ShowFixes()
    {
        if (_vm is not { } vm) return;
        if (vm.Inspector is not { } inspector || !vm.Settings.SqlInspections)
        {
            vm.Status = vm.Settings.SqlInspections ? "SQL warnings need the connection's objects; they are still loading." : "SQL warnings are off (Tools ▾).";
            return;
        }

        // Checked again on the current text, so the fix's offsets are exact.
        _typingAt = -1;
        var caret = Editor.CaretOffset;
        var fresh = Editor.Document.TextLength > MaxLengthForLiveAnalysis ? [] : inspector.Inspect(Editor.Document.Text);
        ShowInspections(fresh);
        var line = Editor.Document.GetLineByOffset(caret);
        var problem = SqlInspector.At(fresh, caret) ??
                      fresh.FirstOrDefault(i => i.Start >= line.Offset && i.Start <= line.EndOffset);
        if (problem is null)
        {
            vm.Status = fresh.Count == 0 ? "No SQL warnings." : "No SQL warning at the caret. F8 goes to the next one.";
            return;
        }
        // Several checks can flag the same name (ambiguous and missing from GROUP BY): offer the fixes of each.
        var here = fresh.Where(i => i.Start == problem.Start && i.Length == problem.Length)
            .OrderByDescending(i => i.Severity).ToList();
        if (here.All(i => i.Fixes.Count == 0))
        {
            vm.Status = problem.Message;
            return;
        }

        var menu = new ContextMenu();
        foreach (var p in here)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = p.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }, IsEnabled = false });
            foreach (var fix in p.Fixes)
            {
                var item = new MenuItem { Header = fix.Title };
                item.Click += (_, _) => ApplyFix(fix);
                menu.Items.Add(item);
            }
        }

        var textView = Editor.TextArea.TextView;
        var location = Editor.Document.GetLocation(problem.Start);
        var point = textView.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineBottom) - textView.ScrollOffset;
        menu.Placement = PlacementMode.AnchorAndGravity;
        menu.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
        menu.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
        menu.PlacementRect = new Rect(Math.Max(0, point.X), Math.Max(0, point.Y), 1, 1);
        menu.Closed += (_, _) => Editor.Focus();
        menu.Open(textView);
    }

    private void ApplyFix(SqlQuickFix fix)
    {
        var document = Editor.Document;
        if (fix.Start < 0 || fix.Start + fix.Length > document.TextLength) return;
        document.Replace(fix.Start, fix.Length, fix.Replacement);
        Editor.CaretOffset = Math.Min(document.TextLength, fix.Start + fix.Replacement.Length);
        Editor.Focus();
        if (_vm is not null) _vm.Status = fix.Title + ".";
    }

    /// <summary>F8 / Shift+F8: the next or previous warning, wrapping around.</summary>
    private void GoToProblem(int direction)
    {
        var visible = Visible.OrderBy(i => i.Start).ToList();
        if (visible.Count == 0)
        {
            if (_vm is not null) _vm.Status = "No SQL warnings.";
            return;
        }
        var caret = Editor.CaretOffset;
        var target = direction > 0
            ? visible.FirstOrDefault(i => i.Start > caret) ?? visible[0]
            : visible.LastOrDefault(i => i.End < caret) ?? visible[^1];
        Editor.CaretOffset = target.Start;
        Editor.TextArea.Caret.BringCaretToView();
        Editor.Focus();
        if (_vm is not null) _vm.Status = target.Message;
    }

    private void OnProblemsInfo(object? sender, RoutedEventArgs e)
    {
        if (SqlInspector.At(Visible.ToList(), Editor.CaretOffset) is not null) ShowFixes();
        else GoToProblem(1);
    }

    private void OnShowFixes(object? sender, RoutedEventArgs e) => ShowFixes();
    private void OnNextProblem(object? sender, RoutedEventArgs e) => GoToProblem(1);

    private void OnToggleSqlWarnings(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Settings is { } settings) settings.SetSqlInspections(!settings.SqlInspections);
    }
}
