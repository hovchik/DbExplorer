using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>A grid-friendly wrapper around one row of a <see cref="QueryResultSet"/>.</summary>
public sealed record ResultRow(IReadOnlyList<object?> Values);

public sealed record ResultSetView(string Title, IReadOnlyList<string> Columns, IReadOnlyList<ResultRow> Rows);

public partial class RoutineExecutionViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isLoadingParameters = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<ResultSetView> _resultSets = [];
    [ObservableProperty] private string _outputSummary = "";

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

    [RelayCommand]
    private async Task RunAsync()
    {
        if (_service is null || _session is null || _routine is null) return;

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
                .Select((rs, i) => new ResultSetView(
                    $"Result set {i + 1}", rs.Columns, rs.Rows.Select(r => new ResultRow(r)).ToList()))
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
