using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Experimental query tools: "why isn't this row here?", dry run with diff, and lock-impact preview.</summary>
public partial class QueryViewModel
{
    private static readonly DataSearchOptions ProbeOptions = new(1000, 30, 2000);
    private string _lastExpectedRow = "";

    /// <summary>Column values for suggestions finished loading: the view re-opens completion if the caret is still there.</summary>
    public event Action? CompletionValuesArrived;

    private ColumnValueCache? _valueCache;

    /// <summary>One value cache per connection, shared by the tabs (each column is sampled once).</summary>
    private ColumnValueCache? ValueCacheFor(DatabaseSession session)
    {
        if (_valueCache?.Session != session)
        {
            if (_valueCache is not null) _valueCache.ValuesLoaded -= OnValuesLoaded;
            _valueCache = SharedValueCaches.GetValue(session, s => new ColumnValueCache(s));
            _valueCache.ValuesLoaded += OnValuesLoaded;
        }
        return _valueCache;
    }

    /// <summary>Detached (closed or disconnected): the shared cache must not keep this tab alive through its event.</summary>
    private void ReleaseValueCache()
    {
        if (_valueCache is not null) _valueCache.ValuesLoaded -= OnValuesLoaded;
        _valueCache = null;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DatabaseSession, ColumnValueCache> SharedValueCaches = new();

    private void OnValuesLoaded() => Avalonia.Threading.Dispatcher.UIThread.Post(() => CompletionValuesArrived?.Invoke());

    private string? RunText(QueryRun? run) => run is { Sql: { } part } && !string.IsNullOrWhiteSpace(part) ? part : Sql;

    private bool CanUseLab => _session is not null && !IsRunning && !string.IsNullOrWhiteSpace(Sql) && !HasOpenTransaction;

    /// <summary>Asks which row was expected, then replays the statement at the caret step by step to find what drops it.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLab))]
    private async Task WhyNotAsync(QueryRun? run)
    {
        if (_session is not { } session || RunText(run) is not { } sql) return;
        var expected = await dialogs.PromptTextAsync(
            "Why isn't my row here?",
            "Describe the row you expected, as a condition in the query's own aliases. Each join and WHERE condition is then " +
            "checked with a read-only COUNT probe to find the one that drops it.",
            "Expected row", _lastExpectedRow, "o.OrderId = 1001");
        if (string.IsNullOrWhiteSpace(expected)) return;
        _lastExpectedRow = expected;

        await RunLabAsync("Tracing the expected row…", async ct =>
        {
            var progress = new Progress<ProbeStep>(s => Messages.Add($"{s.Icon} {s.Stage}: {s.Message}"));
            var report = await new WhyNotDebugger(session, TargetDatabase, ProbeOptions).AnalyzeAsync(sql, expected, progress, ct);
            var views = new List<ResultSetView>
            {
                Table(session, "Why not", ["Step", "Result", "Detail", "Probe SQL"],
                    report.Steps.Select(s => (IReadOnlyList<object?>)[s.Stage, s.Icon + " " + s.Outcome, s.Message, s.Sql]).ToList())
            };
            views.AddRange(report.Steps.Where(s => s.Sample is not null)
                .Select(s => ResultSetView.From($"Values · {Shorten(s.Stage, 40)}", s.Sample!, session)));
            ResultSets = views;
            Messages.Add("→ " + report.Verdict);
            Status = report.Verdict;
        });
    }

    /// <summary>Runs the script in a transaction that is always rolled back and shows the rows it would change.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLab))]
    private async Task DryRunAsync(QueryRun? run)
    {
        if (_session is not { } session || RunText(run) is not { } sql) return;
        // Rolled back, but it still runs the statements: triggers fire and identities advance (SQL Server has no read-only session).
        if (session.Profile.ReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(session.Profile, "The dry run");
            return;
        }
        var production = session.Profile.IsProduction;
        if (!await dialogs.ConfirmAsync(
                "Dry run executes the script inside a transaction and rolls it back, showing the rows it would change. " +
                "Its locks are held until the rollback (5 s lock timeout), triggers fire, and sequences / identities still advance." +
                (production ? " You are on PRODUCTION." : ""),
                "Dry run", requiredText: production ? "PRODUCTION" : null,
                banner: production ? $"PRODUCTION · {session.Profile.DisplayName}" : null))
            return;

        await RunLabAsync("Dry run…", async ct =>
        {
            var snapshot = TargetDatabase is { } db ? await sessions.GetDatabaseSnapshotAsync(session, db) : session.Snapshot;
            var progress = new Progress<string>(s => Status = s);
            var result = await new DryRunService().RunAsync(session, snapshot, TargetDatabase, sql, TimeoutSeconds, DryRunService.DefaultMaxRows, progress, ct);

            var changes = new List<IReadOnlyList<object?>>();
            for (var i = 0; i < result.Statements.Count; i++)
            {
                var s = result.Statements[i];
                foreach (var c in s.Changes)
                {
                    if (c.Kind == RowChangeKind.Updated)
                        foreach (var col in c.Columns.Where(x => x.Changed))
                            changes.Add([i + 1, s.Table, "UPDATE", c.Key, col.Column, col.Before, col.After]);
                    else
                        changes.Add([i + 1, s.Table, c.Kind == RowChangeKind.Inserted ? "INSERT" : "DELETE", c.Key, "(row)",
                            c.Kind == RowChangeKind.Deleted ? RowText(c, before: true) : null,
                            c.Kind == RowChangeKind.Deleted ? null : RowText(c, before: false)]);
                }
            }

            ResultSets =
            [
                Table(session, "Changes (rolled back)", ["#", "Table", "Change", "Key", "Column", "Before", "After"], changes),
                Table(session, "Statements", ["#", "Kind", "Table", "Rows affected", "Note", "Error"],
                    result.Statements.Select((s, i) => (IReadOnlyList<object?>)[i + 1, s.Kind.ToString().ToUpperInvariant(), s.Table, s.RowsAffected, s.Note, s.Error]).ToList())
            ];
            foreach (var s in result.Statements) Messages.Add(s.Summary);
            foreach (var c in result.ColumnSummary) Messages.Add("  changed " + c);
            Messages.Add("Everything was rolled back.");
            var failed = result.Statements.FirstOrDefault(s => s.Error is not null);
            Status = (failed is null ? "Dry run" : "Dry run stopped at an error") +
                     $" · {result.Statements.Sum(s => s.RowsAffected):N0} row(s) would change · rolled back · {result.Elapsed.TotalMilliseconds:N0} ms";
        });
    }

    /// <summary>Predicts the locks the script would take and who is in the way right now, without running it.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLab))]
    private async Task LockImpactAsync(QueryRun? run)
    {
        if (_session is not { } session || RunText(run) is not { } sql) return;
        await RunLabAsync("Estimating locks…", async ct =>
        {
            var report = await new LockImpactAnalyzer().AnalyzeAsync(session, TargetDatabase, sql, ct);
            ResultSets =
            [
                Table(session, "Lock impact", ["Severity", "Table", "Finding"],
                    report.Findings.Select(f => (IReadOnlyList<object?>)[f.Icon + " " + f.Severity, f.Table, f.Message]).ToList()),
                Table(session, "Targets", ["Table", "Operation", "Estimated rows", "Lock"],
                    report.Targets.Select(t => (IReadOnlyList<object?>)[t.Table, t.Operation, t.EstimatedRows, t.LockKind]).ToList())
            ];
            foreach (var f in report.Findings) Messages.Add($"{f.Icon} {(f.Table.Length > 0 ? f.Table + ": " : "")}{f.Message}");
            Status = report.Worst switch
            {
                ImpactSeverity.Danger => "⛔ Lock impact: this script can block other sessions badly — see the findings.",
                ImpactSeverity.Warning => "⚠ Lock impact: some blocking is likely — see the findings.",
                _ => "Lock impact: no wide blocking expected."
            } + " (estimated plan only; nothing was run)";
        });
    }

    private async Task RunLabAsync(string status, Func<CancellationToken, Task> work)
    {
        if (!await ConfirmDiscardEditsAsync()) return;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        IsRunning = true;
        Messages.Clear();
        ResultSets = [];
        Status = status;
        try
        {
            await work(_runCts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            Messages.Add(ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>The grid binds cells as <c>Values[i]</c> through reflection, which needs a real array (a collection
    /// expression typed as IReadOnlyList hides its indexer), so rows are copied into object arrays.</summary>
    private static ResultSetView Table(DatabaseSession session, string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows) =>
        ResultSetView.From(title, new QueryResultSet
        {
            Columns = columns,
            Rows = rows.Select(r => (IReadOnlyList<object?>)r.ToArray()).ToList(),
            TotalRowCount = rows.Count
        }, session);

    private static string RowText(RowChange change, bool before) =>
        string.Join(", ", change.Columns.Select(c => $"{c.Column}={Format(before ? c.Before : c.After)}"));

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        byte[] b => "0x" + Convert.ToHexString(b.Length > 16 ? b[..16] : b) + (b.Length > 16 ? "…" : ""),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Shorten(value.ToString() ?? "", 60)
    };

    private static string Shorten(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    private void NotifyLabCommands()
    {
        WhyNotCommand.NotifyCanExecuteChanged();
        DryRunCommand.NotifyCanExecuteChanged();
        LockImpactCommand.NotifyCanExecuteChanged();
    }
}
