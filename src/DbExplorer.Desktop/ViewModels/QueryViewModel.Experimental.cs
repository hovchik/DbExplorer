using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// Experimental Query tab features, each of which can be turned off in the Lab menu: what changed since the last run
/// (or a pinned baseline), <c>-- expect:</c> checks after each run, and the cost lens on the statement at the caret.
/// </summary>
public partial class QueryViewModel
{
    /// <summary>Results above this many rows are not kept for the next comparison (they would double the memory used).</summary>
    private const int MaxRowsRemembered = 100_000;

    /// <summary>Rows listed in a "Δ" result tab.</summary>
    private const int MaxDiffRowsShown = 5_000;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The result sets of one run, kept to compare the next run of the same query with.</summary>
    private sealed record RememberedRun(string Query, IReadOnlyList<ResultSetView> Sets, DateTime At);

    private RememberedRun? _previousResults;
    private RememberedRun? _baseline;

    /// <summary>"Baseline pinned 12:01:05", next to the editor's status line while a baseline is pinned.</summary>
    [ObservableProperty] private string _baselineInfo = "";

    private bool Enabled(ExperimentalFeature feature) => Settings.IsEnabled(feature);

    /// <summary>The same query in the same database, whatever its spacing.</summary>
    private string QueryKey(string sql) => (TargetDatabase ?? "") + "\u001F" + Whitespace.Replace(sql.Trim().TrimEnd(';'), " ");

    private void ResetExperiments()
    {
        _previousResults = null;
        _baseline = null;
        BaselineInfo = "";
        ClearCostLens();
        _costCache.Clear();
    }

    /// <summary>After a successful run: checks its <c>-- expect:</c> comments and compares it with the previous run.</summary>
    private void AfterRun(DatabaseSession session, string sql)
    {
        try
        {
            var sets = ResultSets;
            var extra = new List<ResultSetView>();
            var notes = new List<string>();

            if (Enabled(ExperimentalFeature.Expectations) && QueryExpectations.Parse(sql) is { Count: > 0 } expectations)
            {
                var outcomes = QueryExpectations.Check(expectations, sets.Select(ToResultSet).ToList());
                extra.Add(Table(session, outcomes.All(o => o.Passed) ? "✓ Expectations" : "✗ Expectations", ["", "Line", "Expectation", "Result"],
                    outcomes.Select(o => (IReadOnlyList<object?>)[o.Icon, o.Expectation.Line, o.Expectation.Text, o.Detail]).ToList()));
                foreach (var o in outcomes) Messages.Add(o.ToString());
                var failed = outcomes.Count(o => !o.Passed);
                notes.Add(failed == 0 ? $"✓ {outcomes.Count} expectation(s) met" : $"✗ {failed} of {outcomes.Count} expectation(s) failed");
            }

            if (Enabled(ExperimentalFeature.ResultDiff))
            {
                var key = QueryKey(sql);
                var against = _baseline is { } pinned && pinned.Query == key ? pinned : _previousResults is { } previous && previous.Query == key ? previous : null;
                if (against is not null && Compare(session, against, sets, extra) is { } summary) notes.Add(summary);
                _previousResults = sets.Sum(s => s.Rows.Count) <= MaxRowsRemembered ? new RememberedRun(key, sets, DateTime.Now) : null;
                PinBaselineCommand.NotifyCanExecuteChanged();
            }

            if (extra.Count > 0) ResultSets = [.. sets, .. extra];
            if (notes.Count > 0) Status = string.Join(" · ", notes) + " · " + Status;
        }
        catch (Exception ex)
        {
            // An experiment must never fail a run that worked.
            Messages.Add("Experimental checks skipped: " + ex.Message);
        }
    }

    /// <summary>Adds a "Δ" tab per result set that changed; returns the status note, or null when nothing could be compared.</summary>
    private string? Compare(DatabaseSession session, RememberedRun against, IReadOnlyList<ResultSetView> sets, List<ResultSetView> extra)
    {
        var reports = new List<string>();
        for (var i = 0; i < sets.Count && i < against.Sets.Count; i++)
        {
            var before = against.Sets[i];
            var after = sets[i];
            var report = ResultDiff.Compare(before.Columns, before.Rows.Select(r => r.OriginalValues).ToList(),
                after.Columns, after.Rows.Select(r => r.OriginalValues).ToList(), KeyOf(after));
            if (report is null) continue;
            var name = sets.Count == 1 ? "" : $"result {i + 1}: ";
            reports.Add(name + report.Summary);
            if (!report.HasChanges) continue;

            var rows = report.Rows.Take(MaxDiffRowsShown).Select(d => (IReadOnlyList<object?>)new object?[]
            {
                d.Change switch { ResultRowChange.Added => "+ new", ResultRowChange.Removed => "− gone", _ => "~ changed" },
                d.Key,
                string.Join("; ", d.Cells.Select(c => $"{c.Column}: {ResultDiff.Display(c.Before)} → {ResultDiff.Display(c.After)}"))
            }.Concat(d.Values).ToArray()).ToList();
            var title = $"Δ {(sets.Count == 1 ? "Result" : $"Result {i + 1}")} ({report.Summary})";
            extra.Add(Table(session, title, ["Change", "Key", "What changed", .. after.Columns], rows));
        }
        if (reports.Count == 0) return null;
        var since = ReferenceEquals(against, _baseline) ? $"vs baseline {against.At:HH:mm:ss}" : $"since {against.At:HH:mm:ss}";
        return $"Δ {since}: {string.Join(", ", reports)}";
    }

    /// <summary>The primary key of the single table the result reads, when all of it is in the result.</summary>
    private static IReadOnlyList<int>? KeyOf(ResultSetView view) =>
        view.Source?.Tables is [{ Key.Count: > 0 } table] ? table.Key.Select(k => k.ResultColumn).ToList() : null;

    private static QueryResultSet ToResultSet(ResultSetView view) => new()
    {
        Columns = view.Columns,
        Rows = view.Rows.Select(r => r.OriginalValues).ToList(),
        IsTruncated = view.IsTruncated,
        TotalRowCount = view.IsTruncated ? view.TotalRowCount : view.Rows.Count,
        TotalRowCountIsExact = view.TotalRowCountIsExact
    };

    private bool CanPinBaseline => _previousResults is not null;

    /// <summary>Keeps the current results as the baseline: later runs of the same query are compared with them instead of the run before.</summary>
    [RelayCommand(CanExecute = nameof(CanPinBaseline))]
    private void PinBaseline()
    {
        if (_previousResults is not { } run) return;
        _baseline = run;
        BaselineInfo = $"📌 baseline {run.At:HH:mm:ss}";
        Status = $"Results of {run.At:HH:mm:ss} pinned: the next runs of this query are compared with them.";
        ClearBaselineCommand.NotifyCanExecuteChanged();
    }

    private bool HasBaseline => _baseline is not null;

    [RelayCommand(CanExecute = nameof(HasBaseline))]
    private void ClearBaseline()
    {
        _baseline = null;
        BaselineInfo = "";
        Status = "Baseline cleared: runs are compared with the run before again.";
        ClearBaselineCommand.NotifyCanExecuteChanged();
    }

    // ----- Cost lens -----

    private readonly Dictionary<string, CostEstimate?> _costCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _costCts;
    private string? _costStatement;

    /// <summary>"🟠 ≈ 12k rows · cost 4,310 · full scan of orders" for the statement at the caret; empty when there is none.</summary>
    [ObservableProperty] private string _costLensText = "";
    [ObservableProperty] private string _costLensDetail = "";
    [ObservableProperty] private IBrush _costLensBrush = Brushes.Gray;

    private void ClearCostLens()
    {
        _costCts?.Cancel();
        _costStatement = null;
        CostLensText = "";
        CostLensDetail = "";
    }

    /// <summary>
    /// Estimates the statement at <paramref name="caret"/> (called by the editor when typing pauses). Only a single
    /// read-only query is estimated, by asking for its estimated plan: nothing is executed.
    /// </summary>
    public async Task UpdateCostLensAsync(string text, int caret)
    {
        if (!Enabled(ExperimentalFeature.CostLens) || _session is not { } session || IsRunning ||
            SqlScriptTools.StatementAt(text, caret) is not { } range || CostLens.Estimable(range.Of(text)) is not { } statement)
        {
            ClearCostLens();
            return;
        }

        var key = (TargetDatabase ?? "") + "\u001F" + statement;
        if (key == _costStatement) return;
        _costCts?.Cancel();
        _costStatement = key;

        if (!_costCache.TryGetValue(key, out var estimate))
        {
            _costCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ct = _costCts.Token;
            CostLensText = "estimating…";
            CostLensBrush = Brushes.Gray;
            try
            {
                estimate = await CostLens.EstimateAsync(session, TargetDatabase, statement, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Mostly a half-typed statement the planner rejects; say nothing rather than flash errors while typing.
                estimate = null;
            }
            catch (Exception)
            {
                // Cancelled or timed out (not replaced by a newer statement): drop "estimating…" and let the next pause try again.
                if (_costStatement == key) ClearCostLens();
                return;
            }
            if (_costStatement != key || !ReferenceEquals(session, _session)) return;
            if (_costCache.Count > 64) _costCache.Clear();
            _costCache[key] = estimate;
        }

        if (estimate is null)
        {
            CostLensText = "";
            CostLensDetail = "";
            return;
        }
        CostLensText = $"{estimate.Icon} {estimate.Summary}";
        CostLensBrush = estimate.Level switch
        {
            CostLevel.Expensive => new SolidColorBrush(Color.Parse("#D9443A")),
            CostLevel.Moderate => new SolidColorBrush(Color.Parse("#E08A00")),
            _ => new SolidColorBrush(Color.Parse("#2E9D57"))
        };
        CostLensDetail = "Cost lens (experimental): what the planner expects of the statement at the caret, from its estimated plan. " +
                         "The statement was not run." +
                         (estimate.Scans.Count == 0 ? "" : "\nFull scans:\n" + string.Join("\n", estimate.Scans.Select(s => $"  {s.Table} (≈{s.Rows:N0} rows)"))) +
                         "\nTurn it off in Lab ▾.";
    }

    /// <summary>Puts an example <c>-- expect:</c> line above the statement at <paramref name="caret"/>; returns the new caret.</summary>
    public int InsertExpectation(int caret)
    {
        var text = Document.Text;
        var start = SqlScriptTools.StatementAt(text, caret)?.Start ?? caret;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (start == 0) lineStart = 0;
        const string line = "-- expect: rows > 0\n";
        Document.Insert(lineStart, line);
        return lineStart + line.Length - 1;
    }

    /// <summary>Called when an experiment is switched in the Lab menu: drops what it was showing or keeping.</summary>
    public void OnExperimentToggled(ExperimentalFeature feature)
    {
        if (feature == ExperimentalFeature.CostLens && !Enabled(feature)) ClearCostLens();
        if (feature == ExperimentalFeature.ResultDiff && !Enabled(feature))
        {
            _previousResults = null;
            _baseline = null;
            BaselineInfo = "";
            PinBaselineCommand.NotifyCanExecuteChanged();
            ClearBaselineCommand.NotifyCanExecuteChanged();
        }
    }
}
