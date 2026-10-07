using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Compare;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>The Lab tab: experimental tools that work on the whole connection.</summary>
public sealed class LabViewModel(ChangeRecorderViewModel recorder, SchemaHistoryViewModel history, RelationshipsViewModel relationships)
    : ViewModelBase, ISessionAware
{
    public ChangeRecorderViewModel Recorder { get; } = recorder;
    public SchemaHistoryViewModel History { get; } = history;
    public RelationshipsViewModel Relationships { get; } = relationships;

    public void Attach(DatabaseSession? session)
    {
        Recorder.Attach(session);
        History.Attach(session);
        Relationships.Attach(session);
    }
}

/// <summary>A changed row flattened for the grid.</summary>
public sealed record RecordedRow(string Table, string Change, string Key, string Columns, RowChange Source);

/// <summary>One column of the selected changed row.</summary>
public sealed record RecordedValue(string Column, string Before, string After, bool Changed);

public sealed partial class ChangeRecorderViewModel(ChangeRecorder recorder, IDialogService dialogs) : ViewModelBase, ISessionAware
{
    private DatabaseSession? _session;
    private RecordingStart? _start;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private IReadOnlyList<string> _databases = [];
    [ObservableProperty] private string? _database;
    [ObservableProperty] private bool _snapshotSmallTables = true;
    [ObservableProperty] private decimal _snapshotMaxRows = 2000;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Start recording, do something in your application, then Stop to see the tables and rows it wrote.";
    [ObservableProperty] private IReadOnlyList<TableActivity> _tables = [];
    [ObservableProperty] private TableActivity? _selectedTable;
    [ObservableProperty] private IReadOnlyList<RecordedRow> _rows = [];
    [ObservableProperty] private RecordedRow? _selectedRow;
    [ObservableProperty] private IReadOnlyList<RecordedValue> _values = [];
    [ObservableProperty] private IReadOnlyList<string> _notes = [];
    private IReadOnlyList<RecordedRow> _allRows = [];

    public void Attach(DatabaseSession? session)
    {
        _cts?.Cancel();
        _session = session;
        _start = null;
        IsRecording = false;
        Tables = [];
        _allRows = [];
        Rows = [];
        Values = [];
        Notes = [];
        Databases = session?.Snapshot.Databases ?? [];
        Database = string.IsNullOrEmpty(session?.Profile.Database) ? Databases.FirstOrDefault() : session!.Profile.Database;
        NotifyCommands();
    }

    partial void OnIsRecordingChanged(bool value) => NotifyCommands();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    partial void OnSelectedTableChanged(TableActivity? value) =>
        Rows = value is null ? _allRows : _allRows.Where(r => string.Equals(r.Table, value.FullName, StringComparison.OrdinalIgnoreCase)).ToList();

    partial void OnSelectedRowChanged(RecordedRow? value) =>
        Values = value?.Source.Columns.Select(c => new RecordedValue(c.Column,
            value.Source.Kind is RowChangeKind.Inserted or RowChangeKind.Written ? "" : Format(c.Before),
            value.Source.Kind is RowChangeKind.Deleted ? "" : Format(c.After), c.Changed)).ToList() ?? [];

    private RecorderOptions Options => new()
    {
        SnapshotMaxRows = SnapshotSmallTables ? (int)Math.Clamp(SnapshotMaxRows, 1, 100_000) : 0,
        Query = new DataSearchOptions(1000, 30, 2000)
    };

    private bool CanStart => _session is not null && !IsRecording && !IsBusy;
    private bool CanStop => IsRecording && !IsBusy;
    private bool CanCheck => _start is not null && !IsRecording && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_session is not { } session) return;
        await BusyAsync(async ct =>
        {
            var progress = new Progress<string>(s => Status = s);
            _start = await recorder.StartAsync(session, Database, Options, progress, ct);
            IsRecording = true;
            Tables = [];
            _allRows = [];
            Rows = [];
            Notes = [];
            Status = $"Recording since {_start.StartedAt:T} · {_start.Counters.Count:N0} tables watched" +
                     (_start.Snapshots.Count > 0 ? $", {_start.Snapshots.Count:N0} small tables snapshotted for an exact diff" : "") +
                     (_start.Marker is null ? "" : " · rows written after this point can be listed") +
                     ". Do the action now, then press Stop.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => CollectAsync();

    /// <summary>Reads the counters again for the same recording (PostgreSQL publishes other sessions' counts with a delay).</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAgainAsync() => CollectAsync();

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task CollectAsync()
    {
        if (_session is not { } session || _start is not { } start) return;
        await BusyAsync(async ct =>
        {
            var progress = new Progress<string>(s => Status = s);
            var result = await recorder.StopAsync(session, start, Options, progress, ct);
            IsRecording = false;
            Tables = result.Tables;
            _allRows = result.Rows.Select(r => new RecordedRow($"{r.Schema}.{r.Table}", r.Kind.ToString(), r.Key,
                r.Kind == RowChangeKind.Updated ? r.ChangedColumns : $"{r.Columns.Count} column(s)", r)).ToList();
            Rows = _allRows;
            Notes = result.Notes;
            Status = $"Recorded {result.Duration.TotalSeconds:N0} s · {result.Tables.Count} table(s) changed: " +
                     $"{result.Tables.Sum(t => t.Inserts):N0} inserted, {result.Tables.Sum(t => t.Updates):N0} updated, " +
                     $"{result.Tables.Sum(t => t.Deletes):N0} deleted · {result.Rows.Count:N0} row(s) listed";
        });
    }

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Status);
        foreach (var t in Tables) sb.AppendLine($"{t.FullName}\t+{t.Inserts} ~{t.Updates} -{t.Deletes}\t{t.Detail}");
        sb.AppendLine();
        foreach (var r in _allRows)
        {
            sb.AppendLine($"{r.Change}\t{r.Table}\t{r.Key}");
            foreach (var c in r.Source.Columns.Where(c => c.Changed || r.Source.Kind != RowChangeKind.Updated))
                sb.AppendLine($"\t{c.Column}: {(r.Source.Kind == RowChangeKind.Updated ? Format(c.Before) + " → " : "")}{Format(r.Source.Kind == RowChangeKind.Deleted ? c.Before : c.After)}");
        }
        await dialogs.CopyTextAsync(sb.ToString());
    }

    private async Task BusyAsync(Func<CancellationToken, Task> work)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message +
                     _session?.Provider.ProviderKey switch
                     {
                         "SqlServer" => " (the counters need VIEW DATABASE STATE)",
                         "MySql" => " (the counters come from performance_schema)",
                         _ => ""
                     };
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CheckAgainCommand.NotifyCanExecuteChanged();
    }

    internal static string Format(object? value) => value switch
    {
        null => "NULL",
        byte[] b => "0x" + Convert.ToHexString(b.Length > 64 ? b[..64] : b) + (b.Length > 64 ? "…" : ""),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}

public sealed partial class SchemaHistoryViewModel(SchemaHistoryStore store) : ViewModelBase, ISessionAware
{
    private DatabaseSession? _session;
    private string? _key;

    [ObservableProperty] private IReadOnlyList<SchemaVersion> _versions = [];
    [ObservableProperty] private SchemaVersion? _fromVersion;
    [ObservableProperty] private SchemaVersion? _toVersion;
    [ObservableProperty] private IReadOnlyList<SchemaChange> _changes = [];
    [ObservableProperty] private SchemaChange? _selectedChange;
    [ObservableProperty] private string _diffText = "";
    [ObservableProperty] private string _objectName = "";
    [ObservableProperty] private IReadOnlyList<ObjectRevision> _revisions = [];
    [ObservableProperty] private ObjectRevision? _selectedRevision;
    [ObservableProperty] private string _status = "A version is stored every time the catalog is read from the server (connect, Refresh metadata) and something changed.";

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.SnapshotChanged -= OnSnapshotChanged;
        _session = session;
        _key = session is null ? null : MetadataCache.CacheKey(session.Profile);
        if (session is not null) session.SnapshotChanged += OnSnapshotChanged;
        Versions = [];
        Changes = [];
        Revisions = [];
        DiffText = "";
        if (session is not null) _ = LoadVersionsAsync();
    }

    /// <summary>A refresh may have stored a new version (written in the background, so look a moment later).</summary>
    private async void OnSnapshotChanged(object? sender, EventArgs e)
    {
        await Task.Delay(1500);
        await LoadVersionsAsync();
    }

    [RelayCommand]
    private async Task LoadVersionsAsync()
    {
        if (_key is not { } key) return;
        try
        {
            Versions = await store.GetVersionsAsync(key);
            ToVersion = Versions.FirstOrDefault();
            FromVersion = Versions.Skip(1).FirstOrDefault() ?? Versions.FirstOrDefault();
            Status = Versions.Count switch
            {
                0 => "No versions yet: they are stored when the catalog is read from the server.",
                1 => $"1 version ({Versions[0].TakenAt:g}). The next refresh that finds a change adds another.",
                _ => $"{Versions.Count} versions, {Versions[^1].TakenAt:g} – {Versions[0].TakenAt:g}."
            };
            if (Versions.Count >= 2) await CompareAsync();
        }
        catch (Exception ex)
        {
            Status = "Could not read the history: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (_key is not { } key || FromVersion is not { } from || ToVersion is not { } to) return;
        try
        {
            Changes = await store.CompareAsync(key, from.Id, to.Id);
            Status = $"{Changes.Count(c => c.Kind == SchemaChangeKind.Added)} added, {Changes.Count(c => c.Kind == SchemaChangeKind.Changed)} changed, " +
                     $"{Changes.Count(c => c.Kind == SchemaChangeKind.Removed)} removed between {from.TakenAt:g} and {to.TakenAt:g}.";
            SelectedChange = Changes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = "Compare failed: " + ex.Message;
        }
    }

    partial void OnSelectedChangeChanged(SchemaChange? value)
    {
        if (value is null) { DiffText = ""; return; }
        ObjectName = value.Name;
        _ = ShowDiffAsync(value.BeforeHash, value.AfterHash);
    }

    partial void OnSelectedRevisionChanged(ObjectRevision? value)
    {
        if (value is null) return;
        var index = Revisions.ToList().IndexOf(value);
        var previous = index + 1 < Revisions.Count ? Revisions[index + 1].Hash : null;
        _ = ShowDiffAsync(previous, value.Hash);
    }

    private async Task ShowDiffAsync(string? beforeHash, string? afterHash)
    {
        if (_key is not { } key) return;
        var before = await store.GetTextAsync(key, beforeHash);
        var after = await store.GetTextAsync(key, afterHash);
        DiffText = UnifiedDiff(before, after);
    }

    /// <summary>"- old line" / "+ new line" with three lines of context, for a read-only text box.</summary>
    public static string UnifiedDiff(string? before, string? after)
    {
        if (before is null && after is null) return "";
        if (before is null) return "(added)\n\n" + after;
        if (after is null) return "(removed)\n\n" + before;
        var diff = TextDiffer.CollapseUnchanged(TextDiffer.Diff(before, after), context: 3);
        var sb = new StringBuilder();
        foreach (var line in diff)
        {
            sb.AppendLine(line.Kind switch
            {
                Core.Models.DiffLineKind.Removed => "- " + line.LeftText,
                Core.Models.DiffLineKind.Added => "+ " + line.RightText,
                Core.Models.DiffLineKind.Skipped => "  ⋯",
                _ => "  " + line.LeftText
            });
        }
        return sb.ToString();
    }

    [RelayCommand]
    private async Task ShowObjectHistoryAsync()
    {
        if (_key is not { } key || _session is not { } session || string.IsNullOrWhiteSpace(ObjectName)) return;
        var name = ObjectName.Trim();
        var parts = name.Split('.');
        var match = session.Snapshot.Objects.FirstOrDefault(o =>
                        string.Equals(o.Name, parts[^1], StringComparison.OrdinalIgnoreCase) &&
                        (parts.Length < 2 || string.Equals(o.Schema, parts[^2], StringComparison.OrdinalIgnoreCase)))
                    ?? (SelectedChange is { } c ? new DbObject { Database = c.Database, Schema = c.Schema, Name = c.Name } : null);
        if (match is null)
        {
            Status = $"{name} is not in the current catalog; pick it from the list of changes to see a removed object's history.";
            return;
        }
        Revisions = await store.GetObjectHistoryAsync(key, match.Database, match.Schema, match.Name);
        Status = $"{match.FullName}: {Revisions.Count} revision(s) in the recorded history.";
        SelectedRevision = Revisions.FirstOrDefault();
    }
}

/// <summary>A suggested relationship with its verification state and whether the user accepted it.</summary>
public sealed partial class RelationshipItem(InferredRelationship relationship, bool accepted) : ObservableObject
{
    public InferredRelationship Relationship { get; } = relationship;
    [ObservableProperty] private string _verification = "";
    [ObservableProperty] private bool _isAccepted = accepted;
    public double? MatchPercent { get; set; }
}

public sealed partial class RelationshipsViewModel(VirtualForeignKeyStore store, SessionService sessions) : ViewModelBase, ISessionAware
{
    private const int SampleRows = 2000;
    private DatabaseSession? _session;
    private string? _key;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private IReadOnlyList<RelationshipItem> _items = [];
    [ObservableProperty] private RelationshipItem? _selectedItem;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Finds foreign keys the schema does not declare (Orders.CustomerId → Customers.Id), checks them against a sample of the data, " +
                                                  "and lets you accept them: accepted ones show in the Diagram, Relations and JOIN suggestions (never in the database).";

    public void Attach(DatabaseSession? session)
    {
        _cts?.Cancel();
        _session = session;
        _key = session is null ? null : MetadataCache.CacheKey(session.Profile);
        Items = [];
        NotifyCommands();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSelectedItemChanged(RelationshipItem? value) => NotifyCommands();

    private bool CanFind => _session is not null && !IsBusy;
    private bool HasSelection => SelectedItem is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync()
    {
        if (_session is not { } session || _key is not { } key) return;
        IsBusy = true;
        try
        {
            var accepted = store.Load(key);
            var inferred = await Task.Run(() => RelationshipInference.Infer(session.Snapshot));
            // Accepted keys are skipped by inference (they are in the snapshot as foreign keys): list them too.
            var acceptedItems = accepted.Select(f => new RelationshipItem(new InferredRelationship(f.Database, f.Schema, f.Table, f.Columns ?? "",
                f.ReferencedSchema, f.ReferencedTable, f.ReferencedColumns ?? "", 100, "accepted earlier"), true));
            Items = acceptedItems.Concat(inferred.Select(r => new RelationshipItem(r, false))).ToList();
            Status = $"{inferred.Count} candidate(s) from names and types · {accepted.Count} accepted. Verify checks {SampleRows:N0} sampled values per candidate.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task VerifySelectedAsync() => VerifyAsync(SelectedItem is { } item ? [item] : []);

    [RelayCommand(CanExecute = nameof(CanFind))]
    private Task VerifyAllAsync() => VerifyAsync(Items.Where(i => i.Verification.Length == 0).ToList());

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task VerifyAsync(IReadOnlyList<RelationshipItem> items)
    {
        if (_session is not { } session || items.Count == 0) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        var done = 0;
        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = _cts.Token }, async (item, ct) =>
            {
                string text;
                double? percent = null;
                try
                {
                    var result = await RelationshipInference.VerifyAsync(session, item.Relationship, SampleRows, new DataSearchOptions(1000, 30, 2000), ct);
                    percent = result.Sampled == 0 ? null : result.Percent;
                    text = result.Summary;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    text = "error: " + ex.Message;
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    item.Verification = text;
                    item.MatchPercent = percent;
                    Status = $"Verified {++done} of {items.Count}…";
                });
            });
            Status = $"Verified {items.Count} candidate(s). 100% means every sampled value exists in the parent.";
        }
        catch (OperationCanceledException)
        {
            Status = "Verification cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Accept() => SetAccepted(SelectedItem, true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove() => SetAccepted(SelectedItem, false);

    private void SetAccepted(RelationshipItem? item, bool accepted)
    {
        if (item is null || _key is not { } key || _session is not { } session) return;
        var keys = store.Load(key).ToList();
        var fk = item.Relationship.ToForeignKey();
        keys.RemoveAll(k => Same(k, fk));
        if (accepted) keys.Add(fk);
        try
        {
            store.Save(key, keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not save the relationship: " + ex.Message;
            return;
        }
        item.IsAccepted = accepted;
        sessions.ReloadVirtualForeignKeys(session);
        Status = accepted
            ? $"Accepted {item.Relationship.Child} → {item.Relationship.Parent}: it now appears in the Diagram, Relations and JOIN suggestions."
            : $"Removed {item.Relationship.Child} → {item.Relationship.Parent}.";
    }

    private static bool Same(DbForeignKey a, DbForeignKey b) =>
        string.Equals(a.Database, b.Database, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Table, b.Table, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Columns, b.Columns, StringComparison.OrdinalIgnoreCase);

    private void NotifyCommands()
    {
        FindCommand.NotifyCanExecuteChanged();
        VerifySelectedCommand.NotifyCanExecuteChanged();
        VerifyAllCommand.NotifyCanExecuteChanged();
        AcceptCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }
}
