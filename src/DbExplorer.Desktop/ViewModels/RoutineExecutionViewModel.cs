using System.Collections.ObjectModel;
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

/// <summary>A grid-friendly wrapper around one row of a <see cref="QueryResultSet"/>.</summary>
public sealed record ResultRow(IReadOnlyList<object?> Values);

public sealed record ResultSetView(string Title, IReadOnlyList<string> Columns, IReadOnlyList<ResultRow> Rows)
{
    /// <summary>Qualified, quoted table the rows came from; used as the target of "Copy as INSERT".</summary>
    public string? SourceTable { get; init; }

    public SqlDialect Dialect { get; init; }

    public Func<string, string> Quote { get; init; } = id => id;

    /// <summary>More rows existed than the row limit; <see cref="TotalRowCount"/> says how many.</summary>
    public bool IsTruncated { get; init; }
    public long TotalRowCount { get; init; }

    /// <summary>False when reading stopped at the row limit on the server: the full size is unknown.</summary>
    public bool TotalRowCountIsExact { get; init; } = true;

    /// <summary>"12,345" or, when reading stopped at the limit, "more than 10,000".</summary>
    public string TotalRowsText => TotalRowCountIsExact ? $"{TotalRowCount:N0}" : $"more than {Rows.Count:N0}";

    public static ResultSetView From(string title, QueryResultSet rs, DatabaseSession session, string? sourceTable = null) =>
        new(title, rs.Columns, rs.Rows.Select(r => new ResultRow(r)).ToList())
        {
            IsTruncated = rs.IsTruncated,
            TotalRowCount = rs.TotalRowCount,
            TotalRowCountIsExact = rs.TotalRowCountIsExact,
            SourceTable = sourceTable,
            Dialect = ResultExporter.DialectFor(session.Provider.ProviderKey),
            Quote = session.Provider.QuoteIdentifier
        };
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
