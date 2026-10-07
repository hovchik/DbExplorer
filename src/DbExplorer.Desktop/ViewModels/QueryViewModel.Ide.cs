using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Query;
using DbExplorer.Application.Query.Plans;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>What to run: the text and where it starts in the editor (to place error markers).</summary>
public sealed record QueryRun(string Sql, int StartOffset);

/// <summary>IDE features of a query tab: manual transactions, parameters, execution plans and navigation.</summary>
public partial class QueryViewModel
{
    private IScriptSession? _transaction;
    private DateTime _transactionStarted;
    private readonly Dictionary<string, string> _parameterValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rows kept per result set (0 = no limit). Further rows are read and discarded so the app stays responsive.</summary>
    [ObservableProperty] private decimal _maxRows = 10_000;

    /// <summary>Off: statements run inside a transaction that stays open until Commit or Rollback (like DataGrip / SSMS
    /// with implicit transactions), so changes can be checked before they become permanent.</summary>
    [ObservableProperty] private bool _autoCommit = true;

    [ObservableProperty] private bool _hasOpenTransaction;

    public string TransactionInfo => HasOpenTransaction ? $"Transaction open since {_transactionStarted:T} — Commit or Rollback" : "";

    /// <summary>A failed run reported where the error is: (offset of the run in the editor, 1-based line, column).</summary>
    public event Action<int, int, int?>? ErrorLocated;

    public event Action? ErrorCleared;

    /// <summary>Asks the workspace to open text in a new tab (definition of an object, …).</summary>
    public event Action<string, string>? OpenRequested;

    partial void OnHasOpenTransactionChanged(bool value)
    {
        OnPropertyChanged(nameof(TransactionInfo));
        OnPropertyChanged(nameof(CanChangeDatabase));
        CommitCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        NotifyLabCommands();
    }

    partial void OnAutoCommitChanged(bool value)
    {
        if (value && HasOpenTransaction)
            Status = "Auto-commit is on for new runs; the open transaction still needs Commit or Rollback.";
    }

    private async Task RunInTransactionAsync(DatabaseSession session, string sql, CancellationToken ct)
    {
        if (_transaction is null)
        {
            Status = "Starting a transaction…";
            _transaction = await session.Provider.BeginScriptSessionAsync(TargetDatabase, transactional: true, ct);
            _transactionStarted = DateTime.Now;
        }
        HasOpenTransaction = true;
        Status = "Running in the open transaction…";
        var result = await _transaction.QueryAsync(sql, TimeoutSeconds, RowLimit, ct, QueryExecutionService.ReadOnlyFor(sql, RowLimit));
        ShowResult(session, sql, result, prefix: "In transaction · ");
        await SafeAppendHistoryAsync(sql, succeeded: true, error: null);
    }

    private bool CanEndTransaction => HasOpenTransaction && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanEndTransaction))]
    private Task CommitAsync() => EndTransactionAsync(commit: true, reason: null);

    [RelayCommand(CanExecute = nameof(CanEndTransaction))]
    private Task RollbackAsync() => EndTransactionAsync(commit: false, reason: null);

    /// <summary>Commits or rolls back the tab's transaction and closes its connection.</summary>
    public async Task EndTransactionAsync(bool commit, string? reason)
    {
        if (_transaction is not { } tx) return;
        _transaction = null;
        try
        {
            if (commit) await tx.CommitAsync();
            else await tx.RollbackAsync();
            Status = (commit ? "Committed." : "Rolled back.") + (reason is null ? "" : $" ({reason})");
        }
        catch (Exception ex)
        {
            Status = (commit ? "Commit failed: " : "Rollback failed: ") + ex.Message;
        }
        finally
        {
            await tx.DisposeAsync();
            HasOpenTransaction = false;
        }
    }

    /// <summary>Asks for the values of undeclared @variables / :placeholders (remembering the last ones) and substitutes them.</summary>
    private async Task<string?> FillParametersAsync(string sql, string providerKey)
    {
        var names = SqlEditorAnalysis.FindParameters(sql);
        if (names.Count == 0) return sql;

        var values = await dialogs.PromptParametersAsync(names,
            names.ToDictionary(n => n, n => _parameterValues.GetValueOrDefault(n, ""), StringComparer.OrdinalIgnoreCase));
        if (values is null) return null;

        foreach (var (name, value) in values) _parameterValues[name] = value;
        return SqlEditorAnalysis.SubstituteParameters(sql,
            values.ToDictionary(kv => kv.Key, kv => SqlEditorAnalysis.ParameterLiteral(kv.Value, providerKey), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Shows the execution plan. Estimated plans do not run anything; Analyze runs the statements inside a
    /// transaction that is rolled back, to report real row counts and timings.</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExplainAsync(QueryRun? run) => await ExplainCoreAsync(run, analyze: false);

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExplainAnalyzeAsync(QueryRun? run) => await ExplainCoreAsync(run, analyze: true);

    private async Task ExplainCoreAsync(QueryRun? run, bool analyze)
    {
        if (_session is not { } session) return;
        var sql = run is { Sql: { } part } && !string.IsNullOrWhiteSpace(part) ? part : Sql;
        if (string.IsNullOrWhiteSpace(sql)) return;
        ErrorCleared?.Invoke();

        sql = await FillParametersAsync(sql, session.Provider.ProviderKey);
        if (sql is null) return;
        if (analyze && queryService.IsPotentiallyDestructive(sql) &&
            !await dialogs.ConfirmAsync("Explain Analyze runs the statements to measure them, inside a transaction that is rolled back afterwards. Continue?",
                "Analyze", requiredText: session.Profile.IsProduction ? "PRODUCTION" : null,
                banner: session.Profile.IsProduction ? $"PRODUCTION · {session.Profile.DisplayName}" : null))
            return;
        if (!await ConfirmDiscardEditsAsync()) return;

        var key = session.Provider.ProviderKey;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        IsRunning = true;
        Messages.Clear();
        Status = analyze ? "Running and measuring…" : "Getting the estimated plan…";
        try
        {
            var result = await queryService.ExecuteScriptAsync(session, PlanReader.BuildScript(sql, key, analyze), TargetDatabase, TimeoutSeconds * 5, _runCts.Token);
            IReadOnlyList<ExecutionPlan> plans;
            try
            {
                plans = PlanReader.Read(result.ResultSets, key, sql);
            }
            catch (Exception ex) when (PlanReader.IsUnreadable(ex))
            {
                plans = [];
            }

            if (plans.Count > 0)
                ShowPlans(session, plans, result, analyze);
            else if (!analyze)
                // A plan this version cannot draw: show the engine's own plan rows, as before.
                await ShowPlanRowsAsync(session, sql, analyze, _runCts.Token);
            else
            {
                // Already run once; running it again for the rows would repeat its work.
                ResultSets = result.ResultSets.Select((rs, i) => ResultSetView.From($"Plan {i + 1}", rs, session)).ToList();
                foreach (var m in result.Messages) Messages.Add(m);
                Status = $"Measured in {result.Elapsed.TotalMilliseconds:N0} ms (the plan could not be drawn)";
            }
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

    /// <summary>One Plan tab per statement (the drawn plan), then a Plan rows tab with every operator as a grid row.
    /// Warnings go to Messages too.</summary>
    private void ShowPlans(DatabaseSession session, IReadOnlyList<ExecutionPlan> plans, QueryExecutionResult result, bool analyze)
    {
        var views = plans.Select((plan, i) => new ResultSetView(plans.Count == 1 ? "Plan" : $"Plan {i + 1}", [], [])
        {
            Plan = new PlanViewModel(plan),
            Connection = ResultSetView.DescribeConnection(session)
        }).ToList();

        var columns = plans.Count == 1 ? PlanTable.Columns : ["Statement", .. PlanTable.Columns];
        var rows = plans.SelectMany((plan, i) => PlanTable.Rows(plan)
            .Select(r => plans.Count == 1 ? r : (IReadOnlyList<object?>)[i + 1, .. r])).ToList();
        views.Add(ResultSetView.From("Plan rows", new QueryResultSet { Columns = columns, Rows = rows, TotalRowCount = rows.Count }, session));
        ResultSets = views;

        var warnings = 0;
        for (var i = 0; i < plans.Count; i++)
            foreach (var warning in plans[i].Warnings)
            {
                Messages.Add("⚠ " + (plans.Count == 1 ? "" : $"Statement {i + 1}: ") + warning.Message);
                warnings++;
            }
        foreach (var m in result.Messages) Messages.Add(m);
        Status = $"{(analyze ? "Measured plan" : "Estimated plan")} in {result.Elapsed.TotalMilliseconds:N0} ms" +
                 (warnings > 0 ? $" · {warnings} warning(s), click one in the Plan tab to find its operator" : "");
    }

    /// <summary>The plan as the engine's own rows (SHOWPLAN_ALL / EXPLAIN text) with hints, when it cannot be drawn.</summary>
    private async Task ShowPlanRowsAsync(DatabaseSession session, string sql, bool analyze, CancellationToken ct)
    {
        var key = session.Provider.ProviderKey;
        var script = QueryPlanTools.BuildExplainScript(sql, key, analyze);
        var result = await queryService.ExecuteScriptAsync(session, script, TargetDatabase, TimeoutSeconds * 5, ct);
        // SQL Server's STATISTICS PROFILE interleaves the statements' own results with the plans; keep the plans.
        var plans = result.ResultSets
            .Where(rs => key != "SqlServer" || rs.Columns.Contains("StmtText", StringComparer.OrdinalIgnoreCase))
            .ToList();
        ResultSets = plans.Select((rs, i) => ResultSetView.From(plans.Count == 1 ? "Plan" : $"Plan {i + 1}", rs, session)).ToList();

        var insights = QueryPlanTools.Insights(plans.Select(p => (p.Columns, p.Rows)).ToList(), key);
        foreach (var insight in insights) Messages.Add("💡 " + insight);
        foreach (var m in result.Messages) Messages.Add(m);
        Status = $"{(analyze ? "Measured plan" : "Estimated plan")} in {result.Elapsed.TotalMilliseconds:N0} ms" +
                 (insights.Count > 0 ? $" · {insights.Count} hint(s) in Messages" : "");
    }

    /// <summary>The table/view/routine at <paramref name="offset"/> opened as its CREATE script in a new tab.</summary>
    public async Task<bool> GoToDefinitionAsync(string text, int offset)
    {
        if (_session is not { } session || _completion?.ResolveObjectAt(text, offset) is not { } obj) return false;
        try
        {
            var definition = await definitions.GetDefinitionAsync(session, obj);
            if (string.IsNullOrWhiteSpace(definition))
            {
                Status = $"No definition is available for {obj.FullName}.";
                return false;
            }
            OpenRequested?.Invoke(obj.FullName, definition);
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Could not read the definition of {obj.FullName}: {ex.Message}";
            return false;
        }
    }

    public (string Signature, int Argument)? SignatureAt(string text, int caret) => _completion?.SignatureAt(text, caret);
}
