using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Desktop.ViewModels;

public partial class DataSearchViewModel(DataSearchService service) : ViewModelBase, ISessionAware
{
    private const int MaxDisplayedMatches = 20_000;

    private DatabaseSession? _session;
    private CancellationTokenSource? _cts;

    public IReadOnlyList<SearchMatchMode> Modes { get; } = Enum.GetValues<SearchMatchMode>();

    public ObservableCollection<DataMatch> Matches { get; } = [];
    public ObservableCollection<TableSkipped> Skipped { get; } = [];

    [ObservableProperty] private string _term = "";
    [ObservableProperty] private SearchMatchMode _mode = SearchMatchMode.Contains;
    [ObservableProperty] private string _schemas = "";
    [ObservableProperty] private string _tableFilter = "";
    [ObservableProperty] private bool _includeViews;
    [ObservableProperty] private bool _includeNumeric = true;
    [ObservableProperty] private bool _includeGuid = true;
    [ObservableProperty] private decimal? _maxMatchesPerTable = 50;
    [ObservableProperty] private decimal? _parallelism = 3;
    [ObservableProperty] private decimal? _queryTimeoutSeconds = 30;
    [ObservableProperty] private decimal? _lockTimeoutMs = 1000;
    [ObservableProperty] private decimal? _maxTableRows;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Each table is read with dirty/snapshot reads, a lock timeout and a query timeout. Blocked tables are skipped, never waited on.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    public void Attach(DatabaseSession? session)
    {
        _cts?.Cancel();
        _session = session;
        Matches.Clear();
        Skipped.Clear();
        Progress = 0;
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart => _session is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_session is null) return;
        if (string.IsNullOrWhiteSpace(Term)) { Status = "Enter a value to search for."; return; }

        var request = new DataSearchRequest
        {
            Term = Term,
            Mode = Mode,
            Schemas = Schemas.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            TableNameFilter = TableFilter,
            IncludeViews = IncludeViews,
            IncludeNumericColumns = IncludeNumeric,
            IncludeGuidColumns = IncludeGuid,
            MaxMatchesPerTable = ToInt(MaxMatchesPerTable, 50, 1),
            MaxDegreeOfParallelism = ToInt(Parallelism, 3, 1),
            QueryTimeoutSeconds = ToInt(QueryTimeoutSeconds, 30, 1),
            LockTimeoutMs = ToInt(LockTimeoutMs, 1000, 0),
            MaxTableRows = MaxTableRows is > 0m ? (long)MaxTableRows.Value : null
        };

        _cts = new CancellationTokenSource();
        Matches.Clear();
        Skipped.Clear();
        Progress = 0;
        IsRunning = true;

        var sw = Stopwatch.StartNew();
        var matchCount = 0;
        var tablesDone = 0;

        try
        {
            await foreach (var e in service.SearchAsync(_session, request, _cts.Token))
            {
                switch (e)
                {
                    case DataMatchFound found:
                        matchCount++;
                        if (Matches.Count < MaxDisplayedMatches) Matches.Add(found.Match);
                        break;

                    case TableSearched t:
                        tablesDone = t.Done;
                        Progress = t.Total == 0 ? 100 : 100.0 * t.Done / t.Total;
                        Status = $"{t.Done:N0}/{t.Total:N0} tables · {matchCount:N0} matches · last: {t.Schema}.{t.Table}";
                        break;

                    case TableSkipped s:
                        tablesDone = s.Done;
                        Skipped.Add(s);
                        Progress = s.Total == 0 ? 100 : 100.0 * s.Done / s.Total;
                        break;
                }
            }

            Progress = 100;
            Status = $"Done in {sw.Elapsed:mm\\:ss} · {tablesDone:N0} tables · {matchCount:N0} matches" +
                     (Skipped.Count > 0 ? $" · {Skipped.Count:N0} skipped" : "") +
                     (matchCount > MaxDisplayedMatches ? $" (showing first {MaxDisplayedMatches:N0})" : "");
        }
        catch (OperationCanceledException)
        {
            Status = $"Cancelled after {sw.Elapsed:mm\\:ss} · {matchCount:N0} matches";
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

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    private static int ToInt(decimal? value, int fallback, int min) =>
        value is decimal d ? Math.Max(min, (int)d) : fallback;
}
