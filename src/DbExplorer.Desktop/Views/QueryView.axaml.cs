using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Search;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.Editor;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>
/// The SQL editor of one query tab: syntax highlighting, context-aware completion (as you type, after '.', after
/// FROM/JOIN/ON/INTO/EXEC and on Ctrl+Space), hover details, find/replace, formatting, comment toggling and running
/// the selection, the statement at the caret or the whole script.
/// </summary>
public partial class QueryView : UserControl
{
    private static readonly Regex KeywordBeforeSpace = new(@"\b(FROM|JOIN|ON|INTO|UPDATE|EXEC|EXECUTE|CALL|APPLY|TABLE|WHERE|AND|OR|BY|SELECT|SET)\s$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InsertColumnsOpen = new(@"\bINTO\s+[\w\.\[\]""]+\s*\($", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SearchPanel _search;
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

        // A translucent selection keeps the syntax colors readable (e.g. the statement selected by Ctrl+Enter).
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x33, 0x88, 0xFF));
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;

        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretInfo();
        Editor.TextArea.SelectionChanged += (_, _) => UpdateCaretInfo();
        Editor.PointerHover += OnPointerHover;
        Editor.PointerHoverStopped += (_, _) => ToolTip.SetIsOpen(Editor, false);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
        DataContextChanged += (_, _) => BindDocument();
        ApplyHighlighting();
    }

    /// <summary>Each tab owns its document (text + undo history); the view is reused when tabs switch.</summary>
    private void BindDocument()
    {
        _completion?.Close();
        _vm = DataContext as QueryViewModel;
        if (_vm is not null && !ReferenceEquals(Editor.Document, _vm.Document)) Editor.Document = _vm.Document;
        UpdateCaretInfo();
    }

    private void ApplyHighlighting() =>
        Editor.SyntaxHighlighting = SqlHighlighting.Get(dark: ActualThemeVariant == ThemeVariant.Dark);

    // ----- Completion -----

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        // A character that cannot be part of a name ends the word: close the list without inserting anything.
        if (_completion is not null && !string.IsNullOrEmpty(e.Text) && !IsWordChar(e.Text[0]))
            _completion.Close();
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_vm is null || string.IsNullOrEmpty(e.Text) || _completion is not null) return;
        var ch = e.Text[0];
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

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    // ----- Keys -----

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // While the list is open, Enter/Tab/arrows/Escape belong to it.
        if (_completion is not null && e.Key is Key.Enter or Key.Tab or Key.Up or Key.Down or Key.Escape or Key.PageUp or Key.PageDown) return;

        if (ctrl && e.Key == Key.Space) { ShowCompletion(explicitRequest: true); e.Handled = true; }
        else if (e.Key == Key.F5 || (ctrl && e.Key == Key.E)) { Run(currentStatement: false); e.Handled = true; }
        else if (ctrl && e.Key == Key.Enter) { Run(currentStatement: true); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.F) { Format(); e.Handled = true; }
        else if (ctrl && e.Key is Key.Oem2 or Key.Divide) { ToggleComment(); e.Handled = true; }
        else if (ctrl && e.Key == Key.H) { _search.IsReplaceMode = true; _search.Open(); e.Handled = true; }
    }

    // ----- Commands -----

    private void OnRun(object? sender, RoutedEventArgs e) => Run(currentStatement: false);
    private void OnRunStatement(object? sender, RoutedEventArgs e) => Run(currentStatement: true);
    private void OnFormat(object? sender, RoutedEventArgs e) => Format();
    private void OnToggleComment(object? sender, RoutedEventArgs e) => ToggleComment();

    private void OnFind(object? sender, RoutedEventArgs e)
    {
        _search.IsReplaceMode = false;
        _search.Open();
    }

    /// <summary>Runs the selection when there is one; otherwise the statement at the caret or the whole script.</summary>
    private void Run(bool currentStatement)
    {
        if (_vm is null) return;
        string? part = null;
        if (Editor.SelectionLength > 0)
        {
            part = Editor.SelectedText;
        }
        else if (currentStatement && SqlScriptTools.StatementAt(Editor.Text, Editor.CaretOffset) is { } range)
        {
            part = range.Of(Editor.Text);
            // Show what runs: select the statement (Run again then repeats exactly it).
            Editor.Select(range.Start, range.Length);
        }

        if (_vm.ExecuteCommand.CanExecute(part)) _vm.ExecuteCommand.Execute(part);
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

    private void ToggleComment()
    {
        var document = Editor.Document;
        var selectionEnd = Editor.SelectionStart + Editor.SelectionLength;
        var first = document.GetLineByOffset(Editor.SelectionStart);
        var last = document.GetLineByOffset(selectionEnd);
        // A selection ending at the very start of a line does not include that line.
        if (last.LineNumber > first.LineNumber && selectionEnd == last.Offset) last = last.PreviousLine;

        var start = first.Offset;
        var length = last.EndOffset - start;
        var toggled = SqlScriptTools.ToggleLineComments(document.GetText(start, length));
        document.Replace(start, length, toggled);
        Editor.Select(start, toggled.Length);
    }

    // ----- Hover and status -----

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
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12
        });
        ToolTip.SetIsOpen(Editor, true);
    }

    private void UpdateCaretInfo()
    {
        var caret = Editor.TextArea.Caret;
        var selected = Editor.SelectionLength;
        var dialect = _vm?.ProviderKey switch { "SqlServer" => " · SQL Server", "Postgres" => " · PostgreSQL", _ => "" };
        CaretInfo.Text = $"Ln {caret.Line}, Col {caret.Column}" + (selected > 0 ? $" · {selected:N0} selected" : "") + dialect;
    }
}
