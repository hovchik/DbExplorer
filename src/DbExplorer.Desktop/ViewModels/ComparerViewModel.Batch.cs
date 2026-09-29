using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Compare;
using DbExplorer.Application.Copy;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One object of a batch copy: what will be done to it and how it went.</summary>
public sealed partial class BatchCopyItem(DbObject source, IReadOnlyList<CopyActionOption> actions, CopyActionOption? selected, string summary)
    : ObservableObject
{
    public DbObject Source { get; } = source;
    public string FullName => Source.FullName;
    public string TypeText => ObjectCopyService.Humanize(Source.Type);
    public IReadOnlyList<CopyActionOption> Actions { get; } = actions;
    public bool CanCopy => Actions.Count > 0;

    [ObservableProperty] private CopyActionOption? _selectedAction = selected;
    [ObservableProperty] private bool _include = selected is not null;
    [ObservableProperty] private string _status = selected is null ? "Not possible" : "Pending";
    [ObservableProperty] private string _details = summary;
}

/// <summary>
/// Batch mode of the Copy &amp; sync tab: copies several objects picked on the Overview tab in dependency
/// order (parents before children, tables before views before routines before triggers), each with its own
/// suggested action, plan and transaction.
/// </summary>
public partial class ComparerViewModel
{
    private IReadOnlyList<ObjectComparisonEntry> _selectedOverviewEntries = [];

    /// <summary>Set by the view from the Overview grid's multi-selection.</summary>
    public IReadOnlyList<ObjectComparisonEntry> SelectedOverviewEntries
    {
        get => _selectedOverviewEntries;
        set
        {
            _selectedOverviewEntries = value;
            OnPropertyChanged(nameof(CopyOverviewLabel));
            CopyOverviewEntryCommand.NotifyCanExecuteChanged();
        }
    }

    private int SelectedLeftEntryCount => SelectedOverviewEntries.Count(e => e.Left is not null);

    public string CopyOverviewLabel => SelectedLeftEntryCount > 1 ? $"Copy {SelectedLeftEntryCount} to right…" : "Copy to right…";

    [ObservableProperty] private bool _isCopyBatchMode;
    [ObservableProperty] private bool _batchContinueOnError = true;

    public ObservableCollection<BatchCopyItem> BatchItems { get; } = [];

    public string BatchHeader => RightSession is null
        ? $"Batch copy of {BatchItems.Count} object(s)"
        : $"Batch copy of {BatchItems.Count} object(s) to {RightSession.Profile.DisplayName}" +
          (string.IsNullOrEmpty(SelectedRightDatabase) ? "" : $" · {SelectedRightDatabase}") +
          " — in dependency order, each object in its own transaction";

    partial void OnIsCopyBatchModeChanged(bool value) => RefreshCopyCommands();

    private string TargetSchemaFor(DbObject obj) => LeftSession is { } l && RightSession is { } r
        ? ObjectCopyService.MapSchema(obj.Schema, l.Provider.ProviderKey, r.Provider.ProviderKey)
        : obj.Schema;

    private void StartBatch(IEnumerable<DbObject> objects)
    {
        if (LeftSession is null) return;
        BatchItems.Clear();
        foreach (var obj in ObjectCopyService.OrderForCopy(objects, LeftSession.Snapshot))
        {
            var analysis = AnalyzeCopy(obj, TargetSchemaFor(obj), obj.Name);
            var actions = analysis is null ? [] : ActionOptions(analysis);
            var selected = actions.FirstOrDefault(o => o.Action == analysis?.RecommendedAction);
            BatchItems.Add(new BatchCopyItem(obj, actions, selected, analysis?.Summary ?? "Connect both sides."));
        }

        CopyBackupTarget = RightSession?.Profile.IsProduction == true;
        IsCopyBatchMode = true;
        SelectedTabIndex = CopyTab;
        OnPropertyChanged(nameof(BatchHeader));
        CopyStatus = $"{BatchItems.Count(i => i.Include)} of {BatchItems.Count} object(s) can be copied. Review the actions, then Run batch.";
    }

    [RelayCommand]
    private void ExitBatch()
    {
        IsCopyBatchMode = false;
        CopyStatus = "";
    }

    private bool CanRunBatch => IsCopyBatchMode && LeftSession is not null && RightSession is not null && !IsBusy && BatchItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task RunBatchAsync()
    {
        if (LeftSession is not { } left || RightSession is not { } right) return;
        var items = BatchItems.Where(i => i.Include && i.SelectedAction is not null).ToList();
        if (items.Count == 0)
        {
            CopyStatus = "Tick at least one object that can be copied.";
            return;
        }

        var counts = string.Join(", ", items.GroupBy(i => i.SelectedAction!.Action)
            .Select(g => $"{g.Count()} × {ObjectCopyService.Humanize(g.Key).ToLowerInvariant()}"));
        var message = $"Copy {items.Count} object(s) to {right.Profile.DisplayName}" +
                      (string.IsNullOrEmpty(SelectedRightDatabase) ? "" : $" · {SelectedRightDatabase}") +
                      $":\n{counts}.\n\nEach object runs " + (CopySingleTransaction ? "in its own transaction." : "step by step without a transaction.") +
                      (BatchContinueOnError ? " An object that fails is reported and the batch goes on." : " The batch stops at the first failure.");
        var confirmed = right.Profile.IsProduction
            ? await dialogs.ConfirmAsync(message + "\n\nThe right side is PRODUCTION.", "Run on production",
                requiredText: "PRODUCTION", banner: $"PRODUCTION · {right.Profile.DisplayName}")
            : await dialogs.ConfirmAsync(message, "Run batch",
                banner: right.Profile.Environment.ShortTag() is { } tag ? $"{tag} · {right.Profile.DisplayName}" : null);
        if (!confirmed) return;

        foreach (var item in items)
        {
            item.Status = "Pending";
            item.Details = "";
        }

        var ct = BeginOperation();
        var watch = Stopwatch.StartNew();
        var created = new List<string>();
        int done = 0, failed = 0, skipped = 0;
        // The batch decides what is copied; each object's own missing parents are items of the batch (or left out on purpose).
        var options = CurrentCopyOptions() with { IncludeMissingParents = false };
        try
        {
            for (var n = 0; n < items.Count; n++)
            {
                var item = items[n];
                ct.ThrowIfCancellationRequested();
                item.Status = "Running…";
                CopyStatus = $"Object {n + 1} of {items.Count}: {item.FullName}";
                try
                {
                    var analysis = AnalyzeCopy(item.Source, TargetSchemaFor(item.Source), item.Source.Name);
                    var action = item.SelectedAction!.Action;
                    if (analysis is null || !analysis.AvailableActions.Contains(action))
                    {
                        item.Status = "Skipped";
                        item.Details = analysis?.Summary ?? "Not possible.";
                        skipped++;
                        continue;
                    }

                    var progress = new Progress<string>(m => item.Details = m);
                    var plan = await copier.BuildPlanAsync(left, right, analysis, action, options, progress, ct, created);
                    if (plan.IsEmpty)
                    {
                        item.Status = "✓ Up to date";
                        item.Details = plan.Summary;
                        done++;
                        continue;
                    }

                    var result = await copier.ExecuteAsync(left, right, plan, progress, ct);
                    created.AddRange(plan.CreatedTables);
                    var details = $"{plan.Summary} · {result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s";

                    var copiedRows = action is CopyAction.MergeData or CopyAction.ReplaceData || options.IncludeData;
                    if (CopyVerifyAfter && analysis.IsTable && copiedRows)
                        details += " · " + (await copier.VerifyAsync(left, right, analysis, options.RowFilter, ct)).Summary;

                    item.Status = "✓ Done";
                    item.Details = details;
                    done++;
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelled";
                    item.Details = CopySingleTransaction ? "Rolled back." : "Steps before the cancel stay applied.";
                    throw;
                }
                catch (Exception ex)
                {
                    item.Status = "✗ Failed";
                    item.Details = ex.Message;
                    failed++;
                    if (!BatchContinueOnError) break;
                }
            }

            CopyStatus = $"Batch finished in {watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s: " +
                         $"{done} done, {failed} failed, {skipped} skipped" +
                         (items.Count - done - failed - skipped > 0 ? $", {items.Count - done - failed - skipped} not run" : "") + ".";
        }
        catch (OperationCanceledException)
        {
            CopyStatus = $"Batch cancelled after {done} object(s).";
        }
        finally
        {
            if (CopyRefreshAfter && done > 0)
            {
                try
                {
                    CopyStatus += " Refreshing right metadata…";
                    await sessions.RefreshMetadataAsync(right, CancellationToken.None);
                    RightDatabases = GetDatabases(right);
                    UpdateRightObjects();
                    CopyStatus = CopyStatus.Replace(" Refreshing right metadata…", "");
                }
                catch (Exception ex)
                {
                    CopyStatus += " Metadata refresh failed: " + ex.Message;
                }
            }
            EndOperation();
        }
    }
}
