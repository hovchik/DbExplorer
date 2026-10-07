using DbExplorer.Application.Connections;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One choice of the Copy &amp; sync tab, with what it does in plain words.</summary>
public sealed record CopyActionOption(CopyAction Action, string Label, string Description);

/// <summary>One step of the copy plan in the steps list, with how it went while the plan runs.</summary>
public sealed partial class CopyStepItem(CopyStep step) : ObservableObject
{
    public CopyStep Step { get; } = step;
    public string Title => Step.Title;
    public CopyStepKind Kind => Step.Kind;

    [ObservableProperty] private CopyStepState _state = CopyStepState.Pending;
    [ObservableProperty] private string _details = "";

    /// <summary>"✓ Done", "✗ Failed", ...: short enough for a narrow column; the details say why.</summary>
    public string StateText => State switch
    {
        CopyStepState.Pending => "",
        CopyStepState.Running => "▶ Running…",
        CopyStepState.Done => "✓ Done",
        CopyStepState.Failed => "✗ Failed",
        CopyStepState.RolledBack => "↺ Rolled back",
        _ => "– Not run"
    };

    public bool IsDone => State == CopyStepState.Done;
    public bool IsFailed => State == CopyStepState.Failed;
    public bool IsRunning => State == CopyStepState.Running;
    public bool IsUndone => State is CopyStepState.RolledBack or CopyStepState.NotRun;

    partial void OnStateChanged(CopyStepState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsUndone));
    }

    public void Apply(CopyStepUpdate update)
    {
        State = update.State;
        Details = update.Details ?? "";
    }

    public void Reset()
    {
        State = CopyStepState.Pending;
        Details = "";
    }
}

/// <summary>
/// Copy &amp; sync tab: creates the left object on the right when it is missing, or merges / replaces the
/// data (or definition) when it exists. The analysis is instant (cached metadata); Preview builds the exact
/// script and row counts; Run executes it on the right after a confirmation.
/// </summary>
public partial class ComparerViewModel
{
    private const int MaxScriptPreviewLength = 200_000;

    private CopyAnalysis? _copyAnalysis;
    private CopyPlan? _copyPlan;

    /// <summary>The plan that last ran: its steps keep showing how each went, and its script can still be saved,
    /// but running again builds a fresh plan against the changed right side.</summary>
    private CopyPlan? _ranCopyPlan;

    [ObservableProperty] private string _copyTargetSchema = "";
    [ObservableProperty] private string _copyTargetName = "";
    [ObservableProperty] private string _copySummary = "Pick an object on the left and connect the right side to see what can be copied.";
    [ObservableProperty] private IReadOnlyList<string> _copyWarnings = [];
    [ObservableProperty] private string _copyColumnInfo = "";
    [ObservableProperty] private IReadOnlyList<CopyActionOption> _copyActions = [];
    [ObservableProperty] private CopyActionOption? _selectedCopyAction;

    [ObservableProperty] private bool _copyIncludeData = true;
    [ObservableProperty] private bool _copyUpdateChanged = true;
    [ObservableProperty] private bool _copyDeleteExtra;
    [ObservableProperty] private bool _copyIndexes = true;
    [ObservableProperty] private bool _copyForeignKeys = true;
    [ObservableProperty] private bool _copyDefaultsAndChecks = true;
    [ObservableProperty] private bool _copyStreamRows = true;
    [ObservableProperty] private bool _copyAddMissingColumns = true;
    [ObservableProperty] private bool _copyIncludeParents;
    [ObservableProperty] private bool _copyBackupTarget;
    [ObservableProperty] private bool _copySingleTransaction = true;
    [ObservableProperty] private bool _copyVerifyAfter = true;
    [ObservableProperty] private bool _copyRefreshAfter = true;
    [ObservableProperty] private string _copyRowFilter = "";
    [ObservableProperty] private decimal _copyBatchSize = 500;
    [ObservableProperty] private decimal _copyMaxRows = 100_000;

    [ObservableProperty] private string _copyPlanSummary = "";
    [ObservableProperty] private IReadOnlyList<CopyStepItem> _copySteps = [];
    [ObservableProperty] private IReadOnlyList<string> _copyNotes = [];
    [ObservableProperty] private string _copyScriptPreview = "";
    [ObservableProperty] private string _copyStatus = "";

    public bool HasCopyWarnings => CopyWarnings.Count > 0;
    public bool HasCopyNotes => CopyNotes.Count > 0;
    public bool HasCopyActions => CopyActions.Count > 0;
    public bool HasCopyPlan => _copyPlan is not null;

    /// <summary>A plan is previewed or has just run: its steps and script are on screen.</summary>
    public bool HasCopySteps => _copyPlan is not null || _ranCopyPlan is not null;
    public bool HasCopyColumnInfo => CopyColumnInfo.Length > 0;

    private CopyAction? SelectedAction => SelectedCopyAction?.Action;
    private bool IsTableCopy => _copyAnalysis?.IsTable == true;

    /// <summary>Structure options (indexes, foreign keys, parents) apply when a table is created.</summary>
    public bool ShowCreateTableOptions => IsTableCopy && SelectedAction is CopyAction.CreateObject or CopyAction.DropAndRecreate;
    public bool ShowMergeOptions => SelectedAction == CopyAction.MergeData;
    public bool ShowAddColumnsOption => SelectedAction is CopyAction.MergeData or CopyAction.ReplaceData &&
                                        _copyAnalysis?.ColumnsMissingOnTarget.Count > 0;
    public bool ShowParentsOption => ShowCreateTableOptions && _copyAnalysis?.MissingParents.Count > 0;
    public bool ShowBackupOption => IsTableCopy && _copyAnalysis?.TargetExists == true;
    public bool ShowDataOptions => IsTableCopy && SelectedAction is not null;
    public bool ShowStreamOption => ShowDataOptions && SelectedAction != CopyAction.MergeData;
    public bool ShowRowFilter => ShowDataOptions && (SelectedAction != CopyAction.CreateObject || CopyIncludeData) &&
                                 (SelectedAction != CopyAction.DropAndRecreate || CopyIncludeData);

    public string SelectedCopyActionDescription => SelectedCopyAction?.Description ?? "";

    /// <summary>"Right: t01 · Shop · dbo.Customers (exists)" — where the copy goes.</summary>
    public string CopyTargetInfo =>
        RightSession is null ? "Connect the right side."
        : _copyAnalysis is null ? $"Right: {RightSession.Profile.DisplayName}"
        : $"Right: {RightSession.Profile.DisplayName}" +
          (string.IsNullOrEmpty(_copyAnalysis.TargetDatabase) ? "" : $" · {_copyAnalysis.TargetDatabase}") +
          $" · {_copyAnalysis.TargetFullName} " +
          (_copyAnalysis.TargetExists ? $"(exists{RowsText(_copyAnalysis.Target?.RowCount)})" : "(missing)");

    public string CopyScript => (_copyPlan ?? _ranCopyPlan)?.Script ?? "";

    public string CopyScriptFileStem =>
        $"copy-{_copyAnalysis?.TargetFullName ?? "object"}-{DateTime.Now:yyyyMMdd-HHmmss}".Replace(' ', '_');

    private static string RowsText(long? rows) => rows is long r ? $", ≈ {r.ToString("N0", CultureInfo.CurrentCulture)} rows" : "";

    partial void OnCopyTargetSchemaChanged(string value) => RefreshCopyAnalysis();
    partial void OnCopyTargetNameChanged(string value) => RefreshCopyAnalysis();

    public string CopyWarningsText => string.Join("\n", CopyWarnings.Select(w => "⚠ " + w));
    public string CopyNotesText => string.Join("\n", CopyNotes.Select(n => "• " + n));

    partial void OnCopyWarningsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasCopyWarnings));
        OnPropertyChanged(nameof(CopyWarningsText));
    }

    partial void OnCopyNotesChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasCopyNotes));
        OnPropertyChanged(nameof(CopyNotesText));
    }
    partial void OnCopyColumnInfoChanged(string value) => OnPropertyChanged(nameof(HasCopyColumnInfo));
    partial void OnCopyActionsChanged(IReadOnlyList<CopyActionOption> value) => OnPropertyChanged(nameof(HasCopyActions));

    partial void OnSelectedCopyActionChanged(CopyActionOption? value)
    {
        OnPropertyChanged(nameof(SelectedCopyActionDescription));
        RefreshCopyVisibility();
        InvalidateCopyPlan();
    }

    partial void OnCopyIncludeDataChanged(bool value) { RefreshCopyVisibility(); InvalidateCopyPlan(); }
    partial void OnCopyUpdateChangedChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopyDeleteExtraChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopyIndexesChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopyForeignKeysChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopyDefaultsAndChecksChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopyStreamRowsChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopyAddMissingColumnsChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopyIncludeParentsChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopyBackupTargetChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopySingleTransactionChanged(bool value) => InvalidateCopyPlan();
    partial void OnCopyRowFilterChanged(string value) => InvalidateCopyPlan();
    partial void OnCopyBatchSizeChanged(decimal value) => InvalidateCopyPlan();
    partial void OnCopyMaxRowsChanged(decimal value) => InvalidateCopyPlan();

    private void SetCopyTarget(string schema, string name)
    {
        // Assign both before analyzing once, instead of analyzing a half-updated name.
        _settingCopyTarget = true;
        CopyTargetSchema = schema;
        CopyTargetName = name;
        _settingCopyTarget = false;
        RefreshCopyAnalysis();
    }

    private bool _settingCopyTarget;

    private void RefreshCopyVisibility()
    {
        OnPropertyChanged(nameof(ShowCreateTableOptions));
        OnPropertyChanged(nameof(ShowMergeOptions));
        OnPropertyChanged(nameof(ShowAddColumnsOption));
        OnPropertyChanged(nameof(ShowParentsOption));
        OnPropertyChanged(nameof(ShowBackupOption));
        OnPropertyChanged(nameof(ShowDataOptions));
        OnPropertyChanged(nameof(ShowStreamOption));
        OnPropertyChanged(nameof(ShowRowFilter));
    }

    /// <summary>Re-evaluates the left object against the right database (cached metadata only, instant).</summary>
    private void RefreshCopyAnalysis()
    {
        if (_settingCopyTarget) return;

        if (LeftSession is null || RightSession is null || SelectedLeftObject is null)
        {
            _copyAnalysis = null;
            CopySummary = LeftSession is null || SelectedLeftObject is null
                ? "Pick an object on the left to copy it to the right."
                : "Connect the right side to see what can be copied.";
            CopyWarnings = [];
            CopyColumnInfo = "";
            CopyActions = [];
            SelectedCopyAction = null;
            AfterCopyAnalysis();
            return;
        }

        var analysis = AnalyzeCopy(SelectedLeftObject, CopyTargetSchema, CopyTargetName)!;
        var right = RightSession;

        var previous = SelectedAction;
        _copyAnalysis = analysis;
        CopySummary = analysis.Summary;
        CopyWarnings = analysis.Warnings;
        CopyColumnInfo = DescribeColumns(analysis);
        CopyActions = ActionOptions(analysis);
        SelectedCopyAction = CopyActions.FirstOrDefault(o => o.Action == previous) ??
                             CopyActions.FirstOrDefault(o => o.Action == analysis.RecommendedAction);

        // Production data deserves a way back: default the backup on when a production table is changed.
        CopyBackupTarget = analysis.TargetExists && analysis.IsTable && right.Profile.IsProduction;
        AfterCopyAnalysis();
    }

    /// <summary>The left object against the selected right database under the given target name (cached metadata only).</summary>
    private CopyAnalysis? AnalyzeCopy(DbObject source, string targetSchema, string targetName)
    {
        if (LeftTarget is not { } left || RightTarget is not { } right) return null;
        var sameServer = left == right ||
                         (left.Profile.ProviderKey == right.Profile.ProviderKey &&
                          string.Equals(left.Profile.Host, right.Profile.Host, StringComparison.OrdinalIgnoreCase) &&
                          left.Profile.Port == right.Profile.Port);
        var targetDatabase = SelectedRightDatabase ?? right.Profile.Database ?? "";

        return ObjectCopyService.Analyze(
            left.Snapshot, left.Provider.ProviderKey, source,
            right.Snapshot, right.Provider.ProviderKey, targetDatabase,
            targetSchema, targetName, sameServer);
    }

    private static IReadOnlyList<CopyActionOption> ActionOptions(CopyAnalysis analysis) =>
        analysis.AvailableActions
            .Select(a => new CopyActionOption(a,
                ObjectCopyService.Humanize(a) + (a == analysis.RecommendedAction ? "  (suggested)" : ""),
                Describe(a, analysis)))
            .ToList();

    private void AfterCopyAnalysis()
    {
        OnPropertyChanged(nameof(CopyTargetInfo));
        RefreshCopyVisibility();
        InvalidateCopyPlan();
    }

    private static string DescribeColumns(CopyAnalysis a)
    {
        var parts = new List<string>();
        if (a.KeyColumns.Count > 0) parts.Add("Key: " + string.Join(", ", a.KeyColumns));
        if (a.ColumnsMissingOnTarget.Count > 0) parts.Add("Only on the left: " + string.Join(", ", a.ColumnsMissingOnTarget));
        if (a.ColumnsOnlyOnTarget.Count > 0) parts.Add("Only on the right: " + string.Join(", ", a.ColumnsOnlyOnTarget));
        if (a.ColumnTypeDifferences.Count > 0) parts.Add("Type changes: " + string.Join("; ", a.ColumnTypeDifferences));
        if (a.MissingParents.Count > 0) parts.Add("Missing parents: " + string.Join(", ", a.MissingParents.Select(p => p.FullName)));
        return string.Join("   ·   ", parts);
    }

    private static string Describe(CopyAction action, CopyAnalysis a) => action switch
    {
        CopyAction.CreateObject when a.IsTable =>
            "Create the table on the right with its primary key, then copy its rows, indexes and foreign keys. " +
            "Optionally create the tables it references first.",
        CopyAction.CreateObject => "Run the left definition on the right to create the object.",
        CopyAction.MergeData =>
            $"Match rows by {string.Join(", ", a.KeyColumns)}: insert the rows missing on the right and update the rows whose values differ. " +
            "Optionally delete the rows that exist only on the right, to make it an exact copy.",
        CopyAction.ReplaceData =>
            "Delete the rows on the right (or only those matching the row filter) and insert the left rows. The right table structure is kept.",
        CopyAction.DropAndRecreate =>
            "Drop the right table and re-create it from the left structure, then copy the rows. Right-only columns, indexes and rows are lost.",
        CopyAction.ReplaceDefinition =>
            "Replace the right definition with the left one (CREATE OR ALTER on SQL Server, CREATE OR REPLACE on PostgreSQL).",
        _ => ""
    };

    private CopyOptions CurrentCopyOptions() => new()
    {
        IncludeData = CopyIncludeData,
        UpdateChanged = CopyUpdateChanged,
        DeleteExtra = CopyDeleteExtra,
        CopyIndexes = CopyIndexes,
        CopyForeignKeys = CopyForeignKeys,
        CopyDefaultsAndChecks = CopyDefaultsAndChecks,
        StreamRows = CopyStreamRows,
        AddMissingColumns = CopyAddMissingColumns,
        IncludeMissingParents = CopyIncludeParents,
        BackupTarget = CopyBackupTarget,
        RowFilter = string.IsNullOrWhiteSpace(CopyRowFilter) ? null : CopyRowFilter.Trim(),
        BatchSize = (int)Math.Clamp(CopyBatchSize, 1, 1000),
        MaxRows = (int)Math.Clamp(CopyMaxRows, 1, 5_000_000),
        SingleTransaction = CopySingleTransaction
    };

    private void InvalidateCopyPlan()
    {
        if (_copyPlan is null && _ranCopyPlan is null && CopySteps.Count == 0 && CopyPlanSummary.Length == 0) return;
        _copyPlan = null;
        _ranCopyPlan = null;
        CopySteps = [];
        CopyNotes = [];
        CopyScriptPreview = "";
        CopyPlanSummary = "";
        OnPropertyChanged(nameof(HasCopyPlan));
        OnPropertyChanged(nameof(HasCopySteps));
        OnPropertyChanged(nameof(CopyScript));
        RefreshCopyCommands();
    }

    private void ShowCopyPlan(CopyPlan plan, IReadOnlyList<CopyStepItem>? steps = null)
    {
        _copyPlan = plan;
        _ranCopyPlan = null;
        CopySteps = steps ?? plan.Steps.Select(s => new CopyStepItem(s)).ToList();
        CopyNotes = plan.Notes;
        var script = plan.Script;
        CopyScriptPreview = script.Length <= MaxScriptPreviewLength
            ? script
            : script[..MaxScriptPreviewLength] +
              $"\n\n-- … preview truncated: the full script is {script.Length:N0} characters; use Save script or Copy script.";
        CopyPlanSummary = plan.Summary;
        OnPropertyChanged(nameof(HasCopyPlan));
        OnPropertyChanged(nameof(HasCopySteps));
        OnPropertyChanged(nameof(CopyScript));
        RefreshCopyCommands();
    }

    /// <summary>After a run the plan is spent (the right side changed), but its steps stay on screen with their outcome.</summary>
    private void KeepRunResult(CopyPlan plan, IReadOnlyList<CopyStepItem> steps)
    {
        ShowCopyPlan(plan, steps);
        _copyPlan = null;
        _ranCopyPlan = plan;
        OnPropertyChanged(nameof(HasCopyPlan));
        OnPropertyChanged(nameof(HasCopySteps));
        OnPropertyChanged(nameof(CopyScript));
        RefreshCopyCommands();
    }

    private bool CanPlanCopy => LeftSession is not null && RightSession is not null && _copyAnalysis is not null &&
                                SelectedAction is not null && !IsBusy;

    /// <summary>Builds the exact script and counts the rows it inserts, updates and deletes; nothing is changed.</summary>
    [RelayCommand(CanExecute = nameof(CanPlanCopy))]
    private async Task PreviewCopyAsync()
    {
        var ct = BeginOperation();
        try
        {
            await BuildCopyPlanAsync(ct);
        }
        catch (OperationCanceledException)
        {
            CopyStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            CopyStatus = "Error: " + ex.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<CopyPlan?> BuildCopyPlanAsync(CancellationToken ct)
    {
        if (LeftTarget is not { } left || RightTarget is not { } right || _copyAnalysis is null || SelectedAction is not { } action) return null;
        CopyStatus = "Preparing the copy…";
        var progress = new Progress<string>(message => CopyStatus = message);
        var plan = await copier.BuildPlanAsync(left, right, _copyAnalysis, action, CurrentCopyOptions(), progress, ct);
        ShowCopyPlan(plan);
        CopyStatus = plan.IsEmpty ? "Nothing to do: the right side already matches." : $"Ready: {plan.Steps.Count:N0} step(s). Review the script, then Run.";
        return plan;
    }

    /// <summary>Runs the reviewed plan on the right (building it first when options changed since the preview).</summary>
    [RelayCommand(CanExecute = nameof(CanPlanCopy))]
    private async Task RunCopyAsync()
    {
        if (LeftTarget is not { } left || RightTarget is not { } right || _copyAnalysis is not { } analysis) return;

        var ct = BeginOperation();
        CopyPlan? ran = null;
        IReadOnlyList<CopyStepItem> steps = [];
        try
        {
            var plan = _copyPlan ?? await BuildCopyPlanAsync(ct);
            if (plan is null) return;
            if (plan.IsEmpty)
            {
                CopyStatus = "Nothing to do: the right side already matches.";
                return;
            }

            if (!await ConfirmCopyAsync(right, plan))
            {
                CopyStatus = "Not run.";
                return;
            }

            // Every step is marked running, done or failed as it goes (and rolled back / not run after a failure).
            steps = CopySteps;
            foreach (var step in steps) step.Reset();
            ran = plan;
            var progress = new Progress<string>(message => CopyStatus = message);
            var stepProgress = new Progress<CopyStepUpdate>(u =>
            {
                if (u.Index >= 0 && u.Index < steps.Count) steps[u.Index].Apply(u);
            });
            var result = await copier.ExecuteAsync(left, right, plan, progress, ct, stepProgress);
            var done = $"{ObjectCopyService.Humanize(plan.Action)} finished in {result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s" +
                       (result.RowsAffected > 0 ? $" · {result.RowsAffected:N0} row(s) affected" : "");
            CopyStatus = done;

            var copiedRows = plan.Action is CopyAction.MergeData or CopyAction.ReplaceData || plan.Options.IncludeData;
            if (CopyVerifyAfter && analysis.IsTable && copiedRows)
            {
                CopyStatus = done + " · verifying…";
                var verification = await copier.VerifyAsync(left, right, analysis, plan.Options.RowFilter, ct);
                done += " · " + verification.Summary;
                CopyStatus = done;
            }

            if (CopyRefreshAfter)
            {
                CopyStatus = done + " · refreshing right metadata…";
                await RefreshRightCatalogAsync(ct);
                ReloadRightAfterCopy(analysis);
                CopyStatus = done;
            }

            KeepRunResult(plan, steps);
            ran = null;
        }
        catch (OperationCanceledException)
        {
            CopyStatus = "Cancelled. A single-transaction run is rolled back; step by step, the steps before the cancel stay applied.";
        }
        catch (Exception ex)
        {
            CopyStatus = "Error: " + ex.Message;
        }
        finally
        {
            // A later refresh may have replaced the list; the executed steps and their outcome stay visible.
            if (ran is not null && !ReferenceEquals(CopySteps, steps)) KeepRunResult(ran, steps);
            EndOperation();
        }
    }

    /// <summary>Re-reads the right catalog of the selected database after a change there, keeping the side scoped to it.</summary>
    private async Task RefreshRightCatalogAsync(CancellationToken ct)
    {
        if (RightSession is not { } right) return;
        var database = SelectedRightDatabase;
        var snapshot = await sessions.RefreshDatabaseSnapshotAsync(right, database, ct);
        if (right != RightSession || !string.Equals(database, SelectedRightDatabase, StringComparison.Ordinal)) return;
        _rightScope = NeedsOwnCatalog(right, database) ? (right.WithSnapshot(snapshot), database!) : null;
        SetDatabases(isLeft: false, MergeNames(RightDatabases, GetDatabases(right)));
    }

    private Task<bool> ConfirmCopyAsync(DatabaseSession right, CopyPlan plan)
    {
        if (right.Profile.ReadOnly)
        {
            CopyStatus = ReadOnlyGuard.Refusal(right.Profile, "The copy");
            return Task.FromResult(false);
        }
        var a = plan.Analysis;
        var where = string.Join(" · ", new[] { right.Profile.DisplayName, a.TargetDatabase, a.TargetFullName }.Where(s => !string.IsNullOrEmpty(s)));
        var message = $"{ObjectCopyService.Humanize(plan.Action)}: {a.Source.FullName} → {where}\n\n{plan.Summary}" +
                      (plan.Options.SingleTransaction ? "\n\nRuns in one transaction: all or nothing." : "\n\nRuns step by step without a transaction.");

        if (right.Profile.IsProduction)
        {
            return dialogs.ConfirmAsync(message + "\n\nThe right side is PRODUCTION.", "Run on production",
                requiredText: "PRODUCTION", banner: $"PRODUCTION · {right.Profile.DisplayName}");
        }

        var destructive = plan.Action is CopyAction.DropAndRecreate or CopyAction.ReplaceData ||
                          (plan.Action == CopyAction.MergeData && plan.RowsToDelete > 0);
        var tag = right.Profile.Environment.ShortTag();
        return dialogs.ConfirmAsync(message, destructive ? "Replace on the right" : "Copy to the right",
            banner: tag is null ? null : $"{tag} · {right.Profile.DisplayName}");
    }

    /// <summary>After a copy the right catalog changed: reload its object list and point at the copied object.</summary>
    private void ReloadRightAfterCopy(CopyAnalysis analysis)
    {
        if (RightSession is null) return;
        if (!string.IsNullOrEmpty(analysis.TargetDatabase) &&
            RightDatabases.Contains(analysis.TargetDatabase, StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(SelectedRightDatabase, analysis.TargetDatabase, StringComparison.OrdinalIgnoreCase))
        {
            SelectedRightDatabase = RightDatabases.First(d => string.Equals(d, analysis.TargetDatabase, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            UpdateRightObjects();
        }

        SelectedRightObject = RightObjects.FirstOrDefault(o =>
            o.Type == analysis.Source.Type &&
            string.Equals(o.Schema, analysis.TargetSchema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Name, analysis.TargetName, StringComparison.OrdinalIgnoreCase));
        RefreshCopyAnalysis();
    }

    private bool CanCopyOverviewEntry => (SelectedOverviewEntry?.Left is not null || SelectedLeftEntryCount > 0) && !IsBusy;

    /// <summary>Opens the selected Overview entry on the Copy &amp; sync tab (e.g. an object only on the left);
    /// with several entries selected, opens them all as a batch.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyOverviewEntry))]
    private void CopyOverviewEntry()
    {
        if (SelectedLeftEntryCount > 1)
        {
            StartBatch(SelectedOverviewEntries.Where(e => e.Left is not null).Select(e => e.Left!));
            return;
        }

        IsCopyBatchMode = false;
        var entry = SelectedOverviewEntry;
        if (entry?.Left is not { } left) return;
        SelectedLeftObject = LeftObjects.FirstOrDefault(o => o == left) ?? left;
        if (entry.Right is not null) SelectedRightObject = RightObjects.FirstOrDefault(o => o == entry.Right) ?? entry.Right;
        SelectedTabIndex = CopyTab;
    }

    private void RefreshCopyCommands()
    {
        PreviewCopyCommand.NotifyCanExecuteChanged();
        RunCopyCommand.NotifyCanExecuteChanged();
        CopyOverviewEntryCommand.NotifyCanExecuteChanged();
        RunBatchCommand.NotifyCanExecuteChanged();
    }
}
