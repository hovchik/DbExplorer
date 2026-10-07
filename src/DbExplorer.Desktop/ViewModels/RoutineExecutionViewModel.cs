using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One input field shown in the routine-execution dialog for a single parameter.</summary>
public sealed partial class RoutineParameterInput : ObservableObject
{
    public required DbRoutineParameter Parameter { get; init; }
    public string DisplayName => Parameter.Direction == DbParameterDirection.ReturnValue
        ? "(return value)"
        : $"{Parameter.Name} ({Parameter.DataType})" + (IsOutput ? " [out]" : "");
    public bool IsOutput => Parameter.Direction is DbParameterDirection.Output or DbParameterDirection.InputOutput;
    public bool IsEditable => Parameter.Direction is not DbParameterDirection.ReturnValue and not DbParameterDirection.Output;

    [ObservableProperty] private string _value = "";
}

/// <summary>Whether a result row is as read, added in the grid, or marked for deletion; new and deleted rows wait for a
/// commit like edited values do.</summary>
public enum ResultRowState { Unchanged, New, Deleted, Removed }

/// <summary>A grid-friendly wrapper around one row of a <see cref="QueryResultSet"/>. Cells can be edited in place: the row
/// keeps the values it was read with until the edits are committed (<see cref="AcceptChanges"/>) or reverted. A row can also
/// be new (added in the grid, not inserted yet) or marked for deletion.</summary>
public sealed class ResultRow(IReadOnlyList<object?> values) : INotifyPropertyChanged
{
    private Dictionary<int, object?>? _originals;

    /// <summary>An empty row added in the grid; it is inserted on commit.</summary>
    public static ResultRow CreateNew(int columnCount) => new(new object?[columnCount]) { State = ResultRowState.New };

    public ResultRowState State { get; private set; }

    public bool IsNew => State == ResultRowState.New;

    public bool IsDeleted => State == ResultRowState.Deleted;

    /// <summary>Anything to commit: edited values, a new row, or a row to delete.</summary>
    public bool HasChanges => IsModified || State is ResultRowState.New or ResultRowState.Deleted;

    /// <summary>The current values (edited ones included). Replaced, never mutated, so a background filter always sees
    /// a consistent row.</summary>
    public IReadOnlyList<object?> Values { get; private set; } = values;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsModified => _originals is { Count: > 0 };

    public bool IsCellModified(int column) => _originals?.ContainsKey(column) == true;

    public IReadOnlyCollection<int> ModifiedColumns => _originals is null ? [] : _originals.Keys;

    /// <summary>The values as read from the database, before any uncommitted edit.</summary>
    public IReadOnlyList<object?> OriginalValues
    {
        get
        {
            if (!IsModified) return Values;
            var values = Values.ToArray();
            foreach (var (column, value) in _originals!) values[column] = value;
            return values;
        }
    }

    public object? OriginalValue(int column) =>
        _originals is not null && _originals.TryGetValue(column, out var value) ? value : column < Values.Count ? Values[column] : null;

    /// <summary>Changes a cell; setting it back to the value it was read with clears the edit.</summary>
    public void SetValue(int column, object? value)
    {
        if (column < 0 || column >= Values.Count) return;
        var original = OriginalValue(column);
        _originals ??= [];
        if (SameValue(original, value)) _originals.Remove(column);
        else _originals.TryAdd(column, original);
        Replace(column, value);
    }

    public void RevertCell(int column)
    {
        if (_originals is null || !_originals.Remove(column, out var original)) return;
        Replace(column, original);
    }

    /// <summary>Discards the edits and un-marks a deletion. (A new row is reverted by removing it from the result.)</summary>
    public void Revert()
    {
        if (!IsModified && !IsDeleted) return;
        if (IsModified) Values = OriginalValues;
        _originals?.Clear();
        if (IsDeleted) State = ResultRowState.Unchanged;
        Changed();
    }

    /// <summary>Marks the row to be deleted on commit (its edits are kept, in case the deletion is undone).</summary>
    public void MarkDeleted()
    {
        if (State != ResultRowState.Unchanged) return;
        State = ResultRowState.Deleted;
        Changed();
    }

    public void UndoDelete()
    {
        if (!IsDeleted) return;
        State = ResultRowState.Unchanged;
        Changed();
    }

    /// <summary>The changes are now in the database: the current values (or, for a new row, the row as stored when it was
    /// read back) become the original ones; a deleted row becomes <see cref="ResultRowState.Removed"/>.</summary>
    public void AcceptChanges(IReadOnlyList<object?>? stored = null)
    {
        if (!HasChanges) return;
        _originals?.Clear();
        if (stored is not null) Values = stored;
        State = State == ResultRowState.Deleted ? ResultRowState.Removed : ResultRowState.Unchanged;
        Changed();
    }

    // A new value list, so bindings to Values see the change of state.
    private void Changed()
    {
        Values = Values.ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Values)));
    }

    private void Replace(int column, object? value)
    {
        var values = Values.ToArray();
        values[column] = value;
        Values = values;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Values)));
    }

    private static bool SameValue(object? a, object? b) =>
        a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a is DBNull ? null : a, b is DBNull ? null : b);
}

public sealed record ResultSetView(string Title, IReadOnlyList<string> Columns, IReadOnlyList<ResultRow> Rows)
{
    /// <summary>Qualified, quoted table the rows came from; used as the target of "Copy as INSERT".</summary>
    public string? SourceTable { get; init; }

    /// <summary>Connection the rows were read from, e.g. "Prod · db01 · SqlServer 16.0"; shown in exports.</summary>
    public string? Connection { get; init; }

    public SqlDialect Dialect { get; init; }

    public Func<string, string> Quote { get; init; } = id => id;

    /// <summary>More rows existed than the row limit; <see cref="TotalRowCount"/> says how many.</summary>
    public bool IsTruncated { get; init; }
    public long TotalRowCount { get; init; }

    /// <summary>False when reading stopped at the row limit on the server: the full size is unknown.</summary>
    public bool TotalRowCountIsExact { get; init; } = true;

    /// <summary>The tables, keys and foreign keys behind the columns, when the statement and the catalog tell; enables
    /// editing cells and following foreign key values. Null: read-only, no links.</summary>
    public ResultSource? Source { get; init; }

    /// <summary>Writes the edited rows to the database and returns a status line; throws when nothing was saved.
    /// Null where results cannot be edited.</summary>
    public Func<ResultSetView, IReadOnlyList<ResultRow>, Task<string>>? CommitEdits { get; init; }

    /// <summary>Shows the row a foreign key value refers to. Null where navigation is not offered.</summary>
    public Action<ResultSetView, ResultReference, ResultRow>? OpenReference { get; init; }

    /// <summary>Set on a query's Plan tab: the tab draws this plan instead of a grid.</summary>
    public PlanViewModel? Plan { get; init; }

    public bool IsGrid => Plan is null;

    public bool CanEdit(int column) => CommitEdits is not null && Source?.CanEdit(column) == true;

    /// <summary>Rows can be added to and deleted from this result (it reads one table whose key is in the result).</summary>
    public bool CanEditRows => CommitEdits is not null && Source?.RowTable is not null && Rows is List<ResultRow>;

    /// <summary>Adds an empty row at the end, to be inserted on commit; null when rows cannot be added.</summary>
    public ResultRow? AddRow()
    {
        if (!CanEditRows) return null;
        var row = ResultRow.CreateNew(Columns.Count);
        ((List<ResultRow>)Rows).Add(row);
        return row;
    }

    /// <summary>Takes rows that are gone out of the result: new rows that were discarded, deleted rows once committed.</summary>
    public int RemoveRows(Func<ResultRow, bool> predicate) => Rows is List<ResultRow> list ? list.RemoveAll(r => predicate(r)) : 0;

    public ResultReference? ReferenceOf(int column) => OpenReference is null ? null : Source?.ReferenceOf(column);

    /// <summary>"12,345" or, when reading stopped at the limit, "more than 10,000".</summary>
    public string TotalRowsText => TotalRowCountIsExact ? $"{TotalRowCount:N0}" : $"more than {Rows.Count:N0}";

    public static ResultSetView From(string title, QueryResultSet rs, DatabaseSession session, string? sourceTable = null) =>
        new(title, rs.Columns, rs.Rows.Select(r => new ResultRow(r)).ToList())
        {
            IsTruncated = rs.IsTruncated,
            TotalRowCount = rs.TotalRowCount,
            TotalRowCountIsExact = rs.TotalRowCountIsExact,
            SourceTable = sourceTable,
            Connection = DescribeConnection(session),
            Dialect = ResultExporter.DialectFor(session.Provider.ProviderKey),
            Quote = session.Provider.QuoteIdentifier
        };

    public static string DescribeConnection(DatabaseSession session) =>
        string.Join(" · ", new[]
        {
            session.Profile.Name,
            session.Profile.Host,
            $"{session.Provider.ProviderKey} {session.ServerVersion}".Trim()
        }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}

public partial class RoutineExecutionViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isLoadingParameters = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<ResultSetView> _resultSets = [];
    [ObservableProperty] private string _outputSummary = "";

    /// <summary>Asked before each run (set for procedures on production connections).</summary>
    public Func<Task<bool>>? ConfirmBeforeRun { get; set; }

    public ObservableCollection<RoutineParameterInput> Parameters { get; } = [];

    private QueryExecutionService? _service;
    private DatabaseSession? _session;
    private DbObject? _routine;
    private IReadOnlyList<DbRoutineParameter> _routineParameters = [];

    public async Task InitializeAsync(
        QueryExecutionService service, DatabaseSession session, DbObject routine, CancellationToken ct = default)
    {
        _service = service;
        _session = session;
        _routine = routine;
        Title = $"Run {routine.Schema}.{routine.Name}";

        try
        {
            _routineParameters = await service.GetRoutineParametersAsync(session, routine, ct);
            Parameters.Clear();
            foreach (var p in _routineParameters.OrderBy(p => p.Ordinal))
                Parameters.Add(new RoutineParameterInput { Parameter = p });
            Status = Parameters.Count == 0
                ? "This routine takes no parameters."
                : $"{_routineParameters.Count(p => p.Direction != DbParameterDirection.ReturnValue)} parameter(s).";
        }
        catch (Exception ex)
        {
            Status = "Could not read parameters: " + ex.Message;
        }
        finally
        {
            IsLoadingParameters = false;
        }
    }

    private bool CanRun => !IsRunning && !IsLoadingParameters;

    partial void OnIsRunningChanged(bool value) => RunCommand.NotifyCanExecuteChanged();
    partial void OnIsLoadingParametersChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_service is null || _session is null || _routine is null) return;
        if (ConfirmBeforeRun is not null && !await ConfirmBeforeRun()) return;

        IsRunning = true;
        Status = "Running…";
        try
        {
            var arguments = Parameters
                .Where(p => p.IsEditable)
                .ToDictionary(p => p.Parameter.Name, object? (p) => p.Value.Length == 0 ? null : p.Value);

            var result = await _service.ExecuteRoutineAsync(
                _session, _routine, _routineParameters, arguments, timeoutSeconds: 60);

            ResultSets = result.ResultSets
                .Select((rs, i) => ResultSetView.From($"Result set {i + 1}", rs, _session))
                .ToList();

            foreach (var p in Parameters)
            {
                if (p.IsOutput && result.OutputValues.TryGetValue(p.Parameter.Name, out var v))
                    p.Value = v?.ToString() ?? "";
            }

            OutputSummary = result.OutputValues.Count > 0
                ? string.Join("  ·  ", result.OutputValues.Select(kv => $"{kv.Key} = {kv.Value ?? "NULL"}"))
                : "";

            Status = $"Completed in {result.Elapsed.TotalMilliseconds:N0} ms" +
                      (result.RowsAffected > 0 ? $" · {result.RowsAffected} row(s) affected" : "") +
                      (result.Messages.Count > 0 ? " · " + string.Join(" | ", result.Messages) : "");
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
