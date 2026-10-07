using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Query;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

public partial class DataSearchViewModel(
    DataSearchService service, QueryExecutionService queryService, IDialogService dialogs) : ViewModelBase, ISessionAware
{
    private const int MaxDisplayedMatches = 20_000;

    private DatabaseSession? _session;
    private CancellationTokenSource? _cts;
    private string _searchedTerm = "";
    private IReadOnlyList<DataMatch> _selectedMatches = [];

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRecordCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToTableCommand))]
    private DataMatch? _selectedMatch;

    /// <summary>Summary of where the value was found, e.g. "in 3 tables".</summary>
    [ObservableProperty] private string _foundInSummary = "";

    /// <summary>Raised to reveal a table in the Objects tab.</summary>
    public event Action<DbObject>? ShowTableRequested;

    /// <summary>Raised to open SQL (text, database) in a new Query tab.</summary>
    public event Action<string, string?>? OpenSqlRequested;

    public void Attach(DatabaseSession? session)
    {
        _cts?.Cancel();
        _session = session;
        Matches.Clear();
        Skipped.Clear();
        Progress = 0;
        FoundInSummary = "";
        StartCommand.NotifyCanExecuteChanged();
        ShowRelationsCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart => _session is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_session is not { } session) return;
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
        _searchedTerm = Term;
        Matches.Clear();
        Skipped.Clear();
        Progress = 0;
        FoundInSummary = "";
        IsRunning = true;

        var sw = Stopwatch.StartNew();
        var matchCount = 0;
        var tablesDone = 0;

        try
        {
            await foreach (var e in service.SearchAsync(session, request, _cts.Token))
            {
                // Matches already buffered when the connection changed belong to the old one; Attach cleared the list.
                if (!ReferenceEquals(session, _session)) return;
                switch (e)
                {
                    case DataMatchFound found:
                        matchCount++;
                        if (Matches.Count < MaxDisplayedMatches)
                        {
                            Matches.Add(found.Match);
                            if (Matches.Count == 1) ShowRelationsCommand.NotifyCanExecuteChanged();
                            UpdateFoundInSummary();
                        }
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

    /// <summary>Rows currently selected in the results grid (set by the view).</summary>
    public void SetSelectedMatches(IReadOnlyList<DataMatch> matches)
    {
        _selectedMatches = matches;
        ShowRelationsCommand.NotifyCanExecuteChanged();
    }

    private void UpdateFoundInSummary()
    {
        var tables = Matches.Select(m => (m.Database, m.Schema, m.Table)).Distinct().Count();
        FoundInSummary = tables == 1 ? "Found in 1 table" : $"Found in {tables:N0} tables";
    }

    private bool HasSelectedMatch => SelectedMatch is not null;

    /// <summary>Opens the found row (all its columns) in a data window, located by its primary key.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedMatch))]
    private async Task OpenRecordAsync()
    {
        if (_session is null || SelectedMatch is not { } match) return;
        var provider = _session.Provider;
        var filter = DataMatchSql.RowPredicate(match, provider.ProviderKey, provider.QuoteIdentifier);
        var description = match.RowKey ?? $"{match.Column} = {match.Value}";
        await dialogs.ShowGetDataAsync(queryService, _session, ResolveTable(match), filter, description);
    }

    /// <summary>Opens the found row's SELECT in a new Query tab.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedMatch))]
    private void OpenRecordSql()
    {
        if (_session is null || SelectedMatch is not { } match) return;
        var provider = _session.Provider;
        var sql = DataMatchSql.SelectRows(ResolveTable(match), [match], provider.ProviderKey, provider.QuoteIdentifier, limit: 100);
        OpenSqlRequested?.Invoke(sql, match.Database);
    }

    /// <summary>Shows the matched table in the Objects tab (columns, keys, indexes, definition).</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedMatch))]
    private void GoToTable()
    {
        if (SelectedMatch is { } match) ShowTableRequested?.Invoke(ResolveTable(match));
    }

    private bool CanShowRelations => _session is not null && Matches.Count > 0;

    /// <summary>
    /// Diagram of the tables the value was found in, how they relate, and their rows combined. Uses the selected
    /// results when more than one is selected, otherwise every result.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowRelations))]
    private void ShowRelations()
    {
        if (_session is null || Matches.Count == 0) return;
        var matches = _selectedMatches.Count > 1 ? _selectedMatches : Matches.ToList();
        dialogs.ShowDataRelations(queryService, _session, _searchedTerm, matches,
            (sql, database) => OpenSqlRequested?.Invoke(sql, database),
            table => ShowTableRequested?.Invoke(table));
    }

    private DbObject ResolveTable(DataMatch m) =>
        _session?.Snapshot.Objects.FirstOrDefault(o =>
            string.Equals(o.Database, m.Database, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Schema, m.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Name, m.Table, StringComparison.OrdinalIgnoreCase))
        ?? new DbObject { Database = m.Database, Schema = m.Schema, Name = m.Table, Type = DbObjectType.Table };

    private static int ToInt(decimal? value, int fallback, int min) =>
        value is decimal d ? Math.Max(min, (int)d) : fallback;
}
