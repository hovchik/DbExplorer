using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// A result shown in the editor, right under the statement that produced it. The anchor sits at the end of the
/// statement and moves with the text; the card goes away when the statement is deleted, run again, or closed.
/// </summary>
public sealed class InlineResult
{
    /// <summary>Rows a card shows; the rest are in the grid below.</summary>
    public const int MaxRows = 10;

    public required TextAnchor Anchor { get; init; }

    /// <summary>The result as the grid got it (same instance), so "Show in grid" can find its tab.</summary>
    public required ResultSetView Result { get; init; }

    /// <summary>"Result 2", the name of its tab in the grid when it ran.</summary>
    public required string Title { get; init; }

    public required DateTime At { get; init; }

    /// <summary>"3 rows", "No rows", "First 10 of 1,204 rows".</summary>
    public string Summary
    {
        get
        {
            var shown = Math.Min(Result.Rows.Count, MaxRows);
            var total = Result.IsTruncated ? Result.TotalRowCount : Result.Rows.Count;
            var totalText = Result.IsTruncated && !Result.TotalRowCountIsExact ? $"{total:N0}+" : $"{total:N0}";
            if (total == 0) return "No rows";
            if (shown == total && !Result.IsTruncated) return total == 1 ? "1 row" : $"{total:N0} rows";
            return $"First {shown:N0} of {totalText} rows";
        }
    }
}

/// <summary>Results shown in the editor under their statements (Preferences › SQL editor, on by default).</summary>
public partial class QueryViewModel
{
    /// <summary>A run whose results can be placed under its statements: the editor text it ran (before parameters were
    /// filled in), and anchors around it so edits made meanwhile move it along.</summary>
    private sealed record InlineRun(string Sql, string EditorText, TextAnchor Start, TextAnchor End);

    private InlineRun? _inlineRun;
    private List<InlineResult> _inlineResults = [];

    /// <summary>The cards on screen, in document order.</summary>
    public IReadOnlyList<InlineResult> InlineResults => _inlineResults;

    /// <summary>The cards changed; the editor redraws the lines under which they sit.</summary>
    public event Action? InlineResultsChanged;

    /// <summary>The grid tab of a card was asked for.</summary>
    public bool CanShowInGrid(InlineResult card) => ShownResults.Any(r => ReferenceEquals(r, card.Result));

    /// <summary>Remembers where <paramref name="editorText"/> (the selection or statement that runs, as typed) sits, for
    /// the results of the run of <paramref name="sql"/> (the same text with its parameters filled in).</summary>
    private void PrepareInline(string sql, string editorText, int startOffset)
    {
        if (startOffset < 0 || startOffset + editorText.Length > Document.TextLength ||
            Document.GetText(startOffset, editorText.Length) != editorText)
        {
            _inlineRun = null;
            return;
        }
        var start = Document.CreateAnchor(startOffset);
        start.MovementType = AnchorMovementType.BeforeInsertion;
        var end = Document.CreateAnchor(startOffset + editorText.Length);
        end.MovementType = AnchorMovementType.AfterInsertion;
        _inlineRun = new InlineRun(sql, editorText, start, end);
    }

    /// <summary>
    /// Puts the run's results under their statements, replacing the cards of earlier runs between the same lines. Cards of
    /// other statements stay, so a script can be checked one statement at a time.
    /// </summary>
    private void PlaceInline(string sql, IReadOnlyList<ResultSetView> views)
    {
        if (_inlineRun is not { } run || run.Sql != sql || run.Start.IsDeleted || run.End.IsDeleted) return;
        var from = run.Start.Offset;
        var to = run.End.Offset;
        var changed = _inlineResults.RemoveAll(c => c.Anchor.IsDeleted || (c.Anchor.Offset >= from && c.Anchor.Offset <= to)) > 0;

        // Text edited while the run went on (or since, for auto refresh): the statements no longer line up.
        var unchanged = to - from == run.EditorText.Length && Document.GetText(from, to - from) == run.EditorText;
        if (Settings.InlineResults && unchanged && views.Count > 0 &&
            InlineResultPlacement.Place(run.EditorText, views.Count) is { } owners)
        {
            var at = DateTime.Now;
            for (var i = 0; i < views.Count; i++)
            {
                // On the statement's last character: deleting the whole statement deletes the anchor too.
                var anchor = Document.CreateAnchor(from + Math.Max(owners[i].Start, owners[i].End - 1));
                anchor.MovementType = AnchorMovementType.BeforeInsertion;
                _inlineResults.Add(new InlineResult { Anchor = anchor, Result = views[i], Title = views[i].Title, At = at });
            }
            _inlineResults = _inlineResults.OrderBy(c => c.Anchor.Offset).ToList(); // stable: a statement's own results keep their order
            changed = true;
        }
        if (changed) InlineResultsChanged?.Invoke();
    }

    /// <summary>The run failed or was cancelled: the cards of its statements would show results it replaced.</summary>
    private void DropInline(string sql)
    {
        if (_inlineRun is not { } run || run.Sql != sql || run.Start.IsDeleted || run.End.IsDeleted) return;
        var from = run.Start.Offset;
        var to = run.End.Offset;
        if (_inlineResults.RemoveAll(c => c.Anchor.IsDeleted || (c.Anchor.Offset >= from && c.Anchor.Offset <= to)) > 0)
            InlineResultsChanged?.Invoke();
    }

    /// <summary>Cards whose statement was deleted; called by the editor after an edit.</summary>
    public void PruneInlineResults()
    {
        if (_inlineResults.RemoveAll(c => c.Anchor.IsDeleted) > 0) InlineResultsChanged?.Invoke();
    }

    [RelayCommand]
    private void CloseInlineResult(InlineResult? card)
    {
        if (card is null || !_inlineResults.Remove(card)) return;
        InlineResultsChanged?.Invoke();
    }

    [RelayCommand]
    public void ClearInlineResults()
    {
        if (_inlineResults.Count == 0) return;
        _inlineResults = [];
        InlineResultsChanged?.Invoke();
    }

    /// <summary>Selects the card's result in the grid below; false when a later run replaced it there.</summary>
    public bool ShowInGrid(InlineResult card)
    {
        if (ShownResults.FirstOrDefault(r => ReferenceEquals(r, card.Result)) is not { } view) return false;
        SelectedResult = view;
        return true;
    }

    /// <summary>A cell as the card shows it: NULL, text cut to one line, binary as hex.</summary>
    public static string InlineCellText(object? value)
    {
        if (ResultViewQuery.DisplayText(value) is not { } text) return "NULL";
        var line = text.ReplaceLineEndings(" ");
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
