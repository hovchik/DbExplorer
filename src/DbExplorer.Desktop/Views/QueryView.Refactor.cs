using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.Views;

/// <summary>Rename (Shift+F6 / F2) and Find usages (Alt+F7) in the query editor; see <see cref="SqlSymbolFinder"/>.</summary>
public partial class QueryView
{
    private SqlSymbol? _renaming;
    private string? _renamingText;

    private void OnRename(object? sender, RoutedEventArgs e) => StartRename();
    private async void OnFindUsages(object? sender, RoutedEventArgs e) => await FindUsagesAsync();

    /// <summary>Shift+F6 / F2: the name at the caret, its places highlighted, and a box for the new name.</summary>
    private void StartRename()
    {
        if (_vm is not { } vm) return;
        if (Editor.Document.TextLength > MaxLengthForLiveAnalysis)
        {
            vm.Status = "The script is too long to rename in.";
            return;
        }
        var text = Editor.Document.Text;
        var lookup = vm.SymbolFinder.SymbolAt(text, Editor.CaretOffset);
        if (lookup.Symbol is not { } symbol)
        {
            vm.Status = lookup.Reason ?? "Nothing to rename at the caret.";
            return;
        }
        if (!symbol.CanRename)
        {
            vm.Status = $"{Capitalize(symbol.Title)} belongs to the database, so it can't be renamed only in the script. Alt+F7 shows where it is used.";
            ShowOccurrences(symbol);
            return;
        }

        _carets.Clear();
        _renaming = symbol;
        _renamingText = text;
        ShowOccurrences(symbol);
        RenameTitle.Text = $"Rename {symbol.KindLabel} {symbol.Name} · {Places(symbol.Occurrences.Count)}";
        RenameBox.Text = symbol.Name;
        ShowRenameMessage(null, null);

        // Under the name at the caret, like the fixes menu.
        var textView = Editor.TextArea.TextView;
        var anchor = symbol.Occurrences.FirstOrDefault(o => Editor.CaretOffset >= o.Start && Editor.CaretOffset <= o.End);
        var at = anchor.Length > 0 ? anchor.Start : Editor.CaretOffset;
        var point = textView.GetVisualPosition(new TextViewPosition(Editor.Document.GetLocation(at)), VisualYPosition.LineBottom) - textView.ScrollOffset;
        var origin = textView.TranslatePoint(new Point(Math.Max(0, point.X), Math.Max(0, point.Y)), Editor) ?? new Point(0, 0);
        RenamePopup.PlacementTarget = Editor;
        RenamePopup.PlacementAnchor = PopupAnchor.TopLeft;
        RenamePopup.PlacementGravity = PopupGravity.BottomRight;
        RenamePopup.PlacementRect = new Rect(origin.X, origin.Y + 2, 1, 1);
        RenamePopup.Closed -= OnRenamePopupClosed;
        RenamePopup.Closed += OnRenamePopupClosed;
        RenamePopup.IsOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnRenameTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_renaming is null || _vm is null) return;
        var name = RenameBox.Text ?? "";
        if (string.Equals(name.Trim(), _renaming.Name, StringComparison.Ordinal))
        {
            ShowRenameMessage(null, null);
            return;
        }
        var plan = _vm.SymbolFinder.Rename(_renaming, name);
        ShowRenameMessage(plan.Error, plan.Warning);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRename();
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyRename();
    }

    private void ApplyRename()
    {
        if (_renaming is not { } symbol || _vm is not { } vm) return;
        if (!string.Equals(Editor.Document.Text, _renamingText, StringComparison.Ordinal))
        {
            vm.Status = "The script changed while renaming; press Shift+F6 again.";
            CloseRename();
            return;
        }
        var plan = vm.SymbolFinder.Rename(symbol, RenameBox.Text ?? "");
        if (plan.Error is not null)
        {
            if (string.Equals((RenameBox.Text ?? "").Trim(), symbol.Name, StringComparison.Ordinal)) CloseRename();
            else ShowRenameMessage(plan.Error, null);
            return;
        }

        // One undo step; the edits run from the end so earlier offsets stay valid.
        var caret = NewCaret(Editor.CaretOffset, plan.Edits);
        var document = Editor.Document;
        using (document.RunUpdate())
        {
            foreach (var edit in plan.Edits.OrderByDescending(x => x.Start))
                document.Replace(edit.Start, edit.Length, edit.Replacement);
        }
        Editor.CaretOffset = Math.Clamp(caret, 0, document.TextLength);
        vm.Status = $"Renamed {symbol.KindLabel} {symbol.Name} to {plan.NewText} in {Places(plan.Edits.Count)}." + (plan.Warning is null ? "" : " " + plan.Warning) +
                    " Ctrl+Z undoes it.";
        CloseRename();
    }

    /// <summary>Where the caret ends up after the edits: it keeps its place in the renamed name or the text around it.</summary>
    private static int NewCaret(int caret, IReadOnlyList<SqlQuickFix> edits)
    {
        var shift = 0;
        foreach (var edit in edits.OrderBy(x => x.Start))
        {
            var end = edit.Start + edit.Length;
            if (caret < edit.Start) break;
            if (caret == end) return edit.Start + shift + edit.Replacement.Length;
            if (caret < end) return edit.Start + shift + Math.Min(caret - edit.Start, edit.Replacement.Length);
            shift += edit.Replacement.Length - edit.Length;
        }
        return caret + shift;
    }

    private void CloseRename()
    {
        RenamePopup.IsOpen = false;
        Editor.TextArea.Focus();
    }

    private void OnRenamePopupClosed(object? sender, EventArgs e)
    {
        _renaming = null;
        _renamingText = null;
        _decorations.Occurrences = [];
        Redraw();
    }

    private void ShowRenameMessage(string? error, string? warning)
    {
        if (error is not null)
        {
            RenameMessage.Text = error;
            RenameMessage.Foreground = Brush("AppPlanHotBrush");
        }
        else if (warning is not null)
        {
            RenameMessage.Text = warning + " Enter renames anyway.";
            RenameMessage.Foreground = Brush("AppPlanWarningBrush");
        }
        else
        {
            RenameMessage.Text = _renaming is { Kind: SqlSymbolKind.Variable or SqlSymbolKind.TempTable }
                ? "Enter renames · Esc cancels"
                : "Enter renames · Esc cancels · names that need it are quoted";
            RenameMessage.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush brush ? brush : null;

    /// <summary>Alt+F7: every place the name at the caret is used, in a window beside the editor.</summary>
    private async Task FindUsagesAsync()
    {
        if (_vm is not { } vm) return;
        if (Editor.Document.TextLength > MaxLengthForLiveAnalysis)
        {
            vm.Status = "The script is too long to search in.";
            return;
        }
        var text = Editor.Document.Text;
        var lookup = vm.SymbolFinder.SymbolAt(text, Editor.CaretOffset);
        if (lookup.Symbol is not { } symbol)
        {
            vm.Status = lookup.Reason ?? "Nothing to look up at the caret.";
            return;
        }
        ShowOccurrences(symbol);
        var document = Editor.Document;
        var usages = await vm.BuildUsagesAsync(symbol, text, o => GoToOccurrence(document, text, o));
        vm.ShowUsages(usages);
        vm.Status = $"{Capitalize(symbol.Title)}: {usages.Subtitle}.";
    }

    /// <summary>Selects a place listed by Find usages, if this tab still shows the same text.</summary>
    private void GoToOccurrence(TextDocument document, string text, SqlOccurrence occurrence)
    {
        if (!ReferenceEquals(Editor.Document, document) || !string.Equals(document.Text, text, StringComparison.Ordinal))
        {
            if (_vm is not null) _vm.Status = "The script changed since Find usages; press Alt+F7 again.";
            return;
        }
        Editor.Select(occurrence.Start, occurrence.Length);
        Editor.ScrollTo(document.GetLineByOffset(occurrence.Start).LineNumber, 1);
        if (TopLevel.GetTopLevel(this) is Window window) window.Activate();
        Editor.TextArea.Focus();
    }

    private void ShowOccurrences(SqlSymbol symbol)
    {
        _decorations.Occurrences = symbol.Occurrences.Select(o => (o.Start, o.Length)).ToList();
        Redraw();
    }

    /// <summary>A tab opened at a usage (a view's source) puts its caret there once it is shown.</summary>
    private void ApplyPendingCaret()
    {
        if (_vm is not { PendingCaretOffset: { } offset } vm) return;
        vm.PendingCaretOffset = null;
        var document = Editor.Document;
        offset = Math.Clamp(offset, 0, document.TextLength);
        Editor.CaretOffset = offset;
        Editor.ScrollTo(document.GetLineByOffset(offset).LineNumber, 1);
        if (SqlEditorAnalysis.IdentifierAt(document.Text, offset) is { } name && name.Start == offset) Editor.Select(name.Start, name.Length);
    }

    private static string Places(int n) => n == 1 ? "1 place" : $"{n} places";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
