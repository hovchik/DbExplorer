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
    private const int MaxRecentTerms = 10;
    private const int PreviewTimeoutSeconds = 15;

    private static readonly IReadOnlyDictionary<SearchMatchMode, string> ModeLabels = new Dictionary<SearchMatchMode, string>
    {
        [SearchMatchMode.Contains] = "Contains",
        [SearchMatchMode.StartsWith] = "Starts with",
        [SearchMatchMode.EndsWith] = "Ends with",
        [SearchMatchMode.Exact] = "Whole value",
    };

    private DatabaseSession? _session;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _previewCts;
    private string _searchedTerm = "";
    private SearchMatchMode _searchedMode;
    private IReadOnlyList<DataMatchRow> _selectedMatches = [];
    private readonly List<DataMatchRow> _allMatches = [];
    private readonly Dictionary<(string, string, string), DataSearchTableHit> _tableHits = [];
    private DataSearchRequest? _lastRequest;
    private int _totalMatches;

    public IReadOnlyList<SearchMatchMode> Modes { get; } = Enum.GetValues<SearchMatchMode>();

    /// <summary>Display name of a match mode ("Whole value" for Exact).</summary>
    public static string ModeLabel(SearchMatchMode mode) => ModeLabels.TryGetValue(mode, out var l) ? l : mode.ToString();

    /// <summary>The results shown: every match, or those of the selected table, narrowed by <see cref="ResultFilter"/>.</summary>
    public ObservableCollection<DataMatchRow> Matches { get; } = [];

    /// <summary>"All tables" first, then each table the value was found in, with its number of results.</summary>
    public ObservableCollection<DataSearchTableHit> Tables { get; } = [];

    public ObservableCollection<TableSkipped> Skipped { get; } = [];

    /// <summary>Every column of the selected result's row, read when the result is selected.</summary>
    public ObservableCollection<DataRecordField> RecordFields { get; } = [];

    /// <summary>The last searched values, newest first (this session only).</summary>
    public ObservableCollection<string> RecentTerms { get; } = [];

    [ObservableProperty] private string _term = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LimitsSummary))] private SearchMatchMode _mode = SearchMatchMode.Contains;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScopeChanged))] private string _schemas = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScopeChanged))] private string _tableFilter = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScopeChanged))] private bool _includeViews;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScopeChanged))] private bool _includeNumeric = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScopeChanged))] private bool _includeGuid = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LimitsSummary))] private decimal? _maxMatchesPerTable = 50;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LimitsSummary))] private decimal? _parallelism = 3;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LimitsSummary))] private decimal? _queryTimeoutSeconds = 30;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LimitsSummary))] private decimal? _lockTimeoutMs = 1000;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LimitsSummary))] private decimal? _maxTableRows;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches))]
    [NotifyPropertyChangedFor(nameof(ShowNothingFound))]
    [NotifyPropertyChangedFor(nameof(ShowResults))]
    private bool _hasSearched;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetrySkippedCommand))]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches))]
    [NotifyPropertyChangedFor(nameof(ShowNothingFound))]
    [NotifyPropertyChangedFor(nameof(ShowResults))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRecordCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToTableCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRecordSqlCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyValueCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyRowKeyCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedMatch))]
    [NotifyPropertyChangedFor(nameof(SelectedTitle))]
    private DataMatchRow? _selectedMatch;

    [ObservableProperty] private DataSearchTableHit? _selectedTable;
    [ObservableProperty] private string _resultFilter = "";

    /// <summary>Summary of where the value was found, e.g. "12 matches in 3 tables".</summary>
    [ObservableProperty] private string _foundInSummary = "";

    [ObservableProperty] private string _recordStatus = "Select a result to see its whole row.";
    [ObservableProperty] private bool _isRecordLoading;

    /// <summary>0 = the found row, 1 = skipped tables (bottom panel).</summary>
    [ObservableProperty] private int _detailTabIndex;

    /// <summary>Set by the view: picking a recent term in its list runs that search.</summary>
    [ObservableProperty] private string? _selectedRecentTerm;

    /// <summary>Set by the view to copy text.</summary>
    public Func<string, Task>? SetClipboardText { get; set; }

    /// <summary>Raised to reveal a table in the Objects tab.</summary>
    public event Action<DbObject>? ShowTableRequested;

    /// <summary>Raised to open SQL (text, database) in a new Query tab.</summary>
    public event Action<string, string?>? OpenSqlRequested;

    public bool ShowWelcome => !HasSearched;
    private bool NothingFound => HasSearched && !IsRunning && _allMatches.Count == 0;

    /// <summary>Nothing found, but some tables were skipped: the results stay up with the skipped list open.</summary>
    public bool ShowNoMatches => NothingFound && Skipped.Count > 0;

    /// <summary>Nothing found anywhere: a full-size message replaces the results.</summary>
    public bool ShowNothingFound => NothingFound && Skipped.Count == 0;

    public bool ShowResults => HasSearched && !ShowNothingFound;
    public bool HasSkipped => Skipped.Count > 0;
    public string SkippedHeader => Skipped.Count == 0 ? "Skipped tables" : $"Skipped tables ({Skipped.Count:N0})";
    public string SkippedBadge => Skipped.Count == 1 ? "1 table skipped" : $"{Skipped.Count:N0} tables skipped";
    public string NoMatchesTitle => $"No value matching “{_searchedTerm}” was found";
    public string NoMatchesHint => Skipped.Count > 0
        ? $"{SkippedBadge} (timeouts, locks or errors) and may still hold it: open Skipped tables below and retry them with longer timeouts."
        : _searchedMode == SearchMatchMode.Contains
            ? "Check the spelling, search fewer characters, or widen the scope (schemas, table name, views) above."
            : $"The match mode is “{ModeLabel(_searchedMode)}”. Try Contains, or widen the scope (schemas, table name, views) above.";

    public bool HasSelectedMatch => SelectedMatch is not null;

    public string SelectedTitle => SelectedMatch is { } m
        ? $"{m.TableLabel} · {m.Column}" + (m.Match.RowKey is { } key ? $" · {key}" : "")
        : "Found row";

    /// <summary>True when the scope differs from the defaults (all schemas and tables, tables only, every column kind).</summary>
    public bool ScopeChanged =>
        !string.IsNullOrWhiteSpace(Schemas) || !string.IsNullOrWhiteSpace(TableFilter) || IncludeViews || !IncludeNumeric || !IncludeGuid;

    /// <summary>The safety limits in one line, shown next to the Limits button.</summary>
    public string LimitsSummary
    {
        get
        {
            var parts = new List<string>
            {
                $"{ToInt(MaxMatchesPerTable, 50, 1):N0} rows/table",
                $"{ToInt(QueryTimeoutSeconds, 30, 1)} s timeout",
                $"{ToInt(Parallelism, 3, 1)} at a time"
            };
            if (MaxTableRows is > 0m) parts.Add($"skip > {MaxTableRows.Value:N0} rows");
            return string.Join(" · ", parts);
        }
    }

    public void Attach(DatabaseSession? session)
    {
        _cts?.Cancel();
        _previewCts?.Cancel();
        _session = session;
        ClearResults();
        Skipped.Clear();
        OnSkippedChanged();
        HasSearched = false;
        Status = "";
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart => _session is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(Term)) { Status = "Type a value to search for."; return; }

        var request = new DataSearchRequest
        {
            Term = Term.Trim(),
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
        RememberTerm(request.Term);
        await RunAsync(request, retry: false);
    }

    /// <summary>Searches the skipped tables again with four times the timeouts and no table size limit, adding to the results.</summary>
    [RelayCommand(CanExecute = nameof(CanRetrySkipped))]
    private async Task RetrySkippedAsync()
    {
        if (_lastRequest is not { } last || Skipped.Count == 0) return;
        var request = last with
        {
            OnlyTables = Skipped.Select(s => (s.Schema, s.Table)).Distinct().ToList(),
            QueryTimeoutSeconds = Math.Min(3600, last.QueryTimeoutSeconds * 4),
            LockTimeoutMs = Math.Min(60_000, Math.Max(1000, last.LockTimeoutMs * 4)),
            MaxTableRows = null,
            MaxDegreeOfParallelism = 1
        };
        await RunAsync(request, retry: true);
    }

    private bool CanRetrySkipped => _session is not null && !IsRunning && Skipped.Count > 0 && _lastRequest is not null;

    private async Task RunAsync(DataSearchRequest request, bool retry)
    {
        if (_session is not { } session) return;

        _cts = new CancellationTokenSource();
        _lastRequest = retry ? _lastRequest : request;
        if (!retry)
        {
            _searchedTerm = request.Term;
            _searchedMode = request.Mode;
            ClearResults();
        }
        Skipped.Clear();
        OnSkippedChanged();
        Progress = 0;
        HasSearched = true;
        IsRunning = true;
        DetailTabIndex = 0;

        var sw = Stopwatch.StartNew();
        var found = 0;
        var tablesDone = 0;

        try
        {
            await foreach (var e in service.SearchAsync(session, request, _cts.Token))
            {
                // Matches already buffered when the connection changed belong to the old one; Attach cleared the list.
                if (!ReferenceEquals(session, _session)) return;
                switch (e)
                {
                    case DataMatchFound f:
                        found++;
                        AddMatch(f.Match);
                        break;

                    case TableSearched t:
                        tablesDone = t.Done;
                        Progress = t.Total == 0 ? 100 : 100.0 * t.Done / t.Total;
                        Status = $"Searching… {t.Done:N0} of {t.Total:N0} tables · {_totalMatches:N0} found · last: {t.Schema}.{t.Table}";
                        break;

                    case TableSkipped s:
                        tablesDone = s.Done;
                        Skipped.Add(s);
                        OnSkippedChanged();
                        Progress = s.Total == 0 ? 100 : 100.0 * s.Done / s.Total;
                        break;
                }
            }

            Progress = 100;
            Status = (retry ? $"Retried {tablesDone:N0} skipped tables in " : $"Searched {tablesDone:N0} tables in ") +
                     $"{sw.Elapsed:mm\\:ss}" +
                     (retry ? $" · {found:N0} more found" : "") +
                     (_totalMatches > MaxDisplayedMatches ? $" · showing the first {MaxDisplayedMatches:N0} results" : "");
        }
        catch (OperationCanceledException)
        {
            Status = $"Stopped after {sw.Elapsed:mm\\:ss} · {tablesDone:N0} tables searched";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            UpdateSummary();
            OnPropertyChanged(nameof(NoMatchesTitle));
            OnPropertyChanged(nameof(NoMatchesHint));
            if (_allMatches.Count == 0 && Skipped.Count > 0) DetailTabIndex = 1;
            if (SelectedMatch is null && Matches.Count > 0) SelectedMatch = Matches[0];
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    private void RememberTerm(string term)
    {
        var existing = RecentTerms.IndexOf(term);
        if (existing == 0) return;
        if (existing > 0) RecentTerms.RemoveAt(existing);
        RecentTerms.Insert(0, term);
        while (RecentTerms.Count > MaxRecentTerms) RecentTerms.RemoveAt(RecentTerms.Count - 1);
    }

    partial void OnSelectedRecentTermChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Term = value;
        SelectedRecentTerm = null;
        if (StartCommand.CanExecute(null)) StartCommand.Execute(null);
    }

    /// <summary>Puts the scope back to every schema and table, tables only, all column kinds.</summary>
    [RelayCommand]
    private void ResetScope()
    {
        Schemas = "";
        TableFilter = "";
        IncludeViews = false;
        IncludeNumeric = true;
        IncludeGuid = true;
    }

    private void ClearResults()
    {
        _previewCts?.Cancel();
        _allMatches.Clear();
        _tableHits.Clear();
        _totalMatches = 0;
        Matches.Clear();
        Tables.Clear();
        SelectedTable = null;
        SelectedMatch = null;
        _selectedMatches = [];
        RecordFields.Clear();
        RecordStatus = "Select a result to see its whole row.";
        Progress = 0;
        FoundInSummary = "";
        ShowRelationsCommand.NotifyCanExecuteChanged();
        NotifyResultStates();
    }

    private void NotifyResultStates()
    {
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowNothingFound));
        OnPropertyChanged(nameof(ShowResults));
    }

    private void AddMatch(DataMatch match)
    {
        _totalMatches++;
        if (_allMatches.Count >= MaxDisplayedMatches) return;

        var key = (match.Database.ToUpperInvariant(), match.Schema.ToUpperInvariant(), match.Table.ToUpperInvariant());
        if (!_tableHits.TryGetValue(key, out var hit))
        {
            if (Tables.Count == 0) Tables.Add(DataSearchTableHit.All());
            hit = new DataSearchTableHit(match.Database, match.Schema, match.Table, TableLabel(match));
            _tableHits[key] = hit;
            InsertSorted(hit);
        }
        hit.Count++;
        Tables[0].Count = _allMatches.Count + 1;

        var row = new DataMatchRow(match, DataMatchHighlight.Split(match.Value, _searchedTerm, _searchedMode), hit.Label);
        _allMatches.Add(row);
        if (PassesFilter(row)) Matches.Add(row);

        if (_allMatches.Count == 1)
        {
            ShowRelationsCommand.NotifyCanExecuteChanged();
            NotifyResultStates();
        }
        UpdateSummary();
    }

    /// <summary>Tables stay in name order after "All tables", so the list doesn't jump while results arrive.</summary>
    private void InsertSorted(DataSearchTableHit hit)
    {
        var i = 1;
        while (i < Tables.Count && string.Compare(Tables[i].Label, hit.Label, StringComparison.OrdinalIgnoreCase) < 0) i++;
        Tables.Insert(i, hit);
    }

    /// <summary>"schema.table", with the database in front when the connection spans several.</summary>
    private string TableLabel(DataMatch m)
    {
        var several = _session?.Snapshot.Objects.Select(o => o.Database).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any() ?? false;
        var name = string.IsNullOrEmpty(m.Schema) ? m.Table : $"{m.Schema}.{m.Table}";
        return several && !string.IsNullOrEmpty(m.Database) ? $"{m.Database} › {name}" : name;
    }

    private bool PassesFilter(DataMatchRow row) =>
        (SelectedTable is null || SelectedTable.Contains(row.Match)) &&
        (string.IsNullOrWhiteSpace(ResultFilter) || row.Matches(ResultFilter.Trim()));

    partial void OnSelectedTableChanged(DataSearchTableHit? value) => ApplyFilter();
    partial void OnResultFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var selected = SelectedMatch;
        Matches.Clear();
        foreach (var row in _allMatches)
            if (PassesFilter(row)) Matches.Add(row);
        SelectedMatch = selected is not null && Matches.Contains(selected) ? selected : Matches.FirstOrDefault();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_allMatches.Count == 0) { FoundInSummary = ""; return; }
        var tables = _tableHits.Count;
        var summary = $"{_totalMatches:N0} {(_totalMatches == 1 ? "match" : "matches")} in {tables:N0} {(tables == 1 ? "table" : "tables")}";
        if (Matches.Count != _allMatches.Count) summary += $" · showing {Matches.Count:N0}";
        FoundInSummary = summary;
    }

    private void OnSkippedChanged()
    {
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(SkippedHeader));
        OnPropertyChanged(nameof(SkippedBadge));
        NotifyResultStates();
        RetrySkippedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the skipped tables list under the results.</summary>
    [RelayCommand]
    private void ShowSkipped() => DetailTabIndex = 1;

    /// <summary>Rows currently selected in the results grid (set by the view).</summary>
    public void SetSelectedMatches(IReadOnlyList<DataMatchRow> matches)
    {
        _selectedMatches = matches;
        ShowRelationsCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedMatchChanged(DataMatchRow? value) => _ = LoadRecordAsync(value);

    /// <summary>Reads every column of the selected result's row (by primary key, else by the matched value).</summary>
    private async Task LoadRecordAsync(DataMatchRow? row)
    {
        _previewCts?.Cancel();
        RecordFields.Clear();
        if (row is null || _session is not { } session)
        {
            IsRecordLoading = false;
            RecordStatus = "Select a result to see its whole row.";
            return;
        }

        var cts = _previewCts = new CancellationTokenSource();
        IsRecordLoading = true;
        RecordStatus = "Reading the row…";
        try
        {
            await Task.Delay(120, cts.Token); // arrow keys through the results don't fire a query per row
            var match = row.Match;
            var provider = session.Provider;
            var sql = DataMatchSql.SelectRows(ResolveTable(match), [match], provider.ProviderKey, provider.QuoteIdentifier, limit: 1);
            var result = await queryService.ExecuteScriptAsync(session, sql, match.Database, PreviewTimeoutSeconds, cts.Token, maxRows: 1);
            if (cts.IsCancellationRequested) return;

            var set = result.ResultSets.FirstOrDefault();
            if (set is null || set.Rows.Count == 0)
            {
                RecordStatus = "The row is no longer there (changed or deleted since the search).";
                return;
            }

            var keys = match.KeyValues.Select(k => k.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var values = set.Rows[0];
            for (var i = 0; i < set.Columns.Count; i++)
            {
                var value = i < values.Count ? values[i] : null;
                var isNull = value is null or DBNull;
                RecordFields.Add(new DataRecordField(
                    set.Columns[i], FormatValue(value),
                    string.Equals(set.Columns[i], match.Column, StringComparison.OrdinalIgnoreCase),
                    keys.Contains(set.Columns[i]), isNull));
            }
            RecordStatus = match.KeyValues.Count > 0 ? "" : "This table has no primary key: showing the first row with this value.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) RecordStatus = "Couldn't read the row: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(cts, _previewCts)) IsRecordLoading = false;
        }
    }

    internal static string FormatValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => $"binary, {bytes.Length:N0} bytes",
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss"),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz"),
        _ => Truncate(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "", 2000)
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Opens the found row (all its columns) in a data window, located by its primary key.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedMatch))]
    private async Task OpenRecordAsync()
    {
        if (_session is null || SelectedMatch?.Match is not { } match) return;
        var provider = _session.Provider;
        var filter = DataMatchSql.RowPredicate(match, provider.ProviderKey, provider.QuoteIdentifier);
        var description = match.RowKey ?? $"{match.Column} = {match.Value}";
        await dialogs.ShowGetDataAsync(queryService, _session, ResolveTable(match), filter, description);
    }

    /// <summary>Opens the found row's SELECT in a new Query tab.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedMatch))]
    private void OpenRecordSql()
    {
        if (_session is null || SelectedMatch?.Match is not { } match) return;
        var provider = _session.Provider;
        var sql = DataMatchSql.SelectRows(ResolveTable(match), [match], provider.ProviderKey, provider.QuoteIdentifier, limit: 100);
        OpenSqlRequested?.Invoke(sql, match.Database);
    }

    /// <summary>Shows the matched table in the Objects tab (columns, keys, indexes, definition).</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedMatch))]
    private void GoToTable()
    {
        if (SelectedMatch?.Match is { } match) ShowTableRequested?.Invoke(ResolveTable(match));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMatch))]
    private async Task CopyValueAsync()
    {
        if (SelectedMatch is { } row && SetClipboardText is { } set) await set(row.Match.Value ?? "");
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMatch))]
    private async Task CopyRowKeyAsync()
    {
        if (SelectedMatch is { } row && SetClipboardText is { } set) await set(row.Match.RowKey ?? "");
    }

    private bool CanShowRelations => _session is not null && _allMatches.Count > 0;

    /// <summary>
    /// Diagram of the tables the value was found in, how they relate, and their rows combined. Uses the selected
    /// results when more than one is selected, otherwise every result shown.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowRelations))]
    private void ShowRelations()
    {
        if (_session is null || _allMatches.Count == 0) return;
        IReadOnlyList<DataMatchRow> rows = _selectedMatches.Count > 1 ? _selectedMatches : Matches.Count > 0 ? Matches : _allMatches;
        dialogs.ShowDataRelations(queryService, _session, _searchedTerm, rows.Select(r => r.Match).ToList(),
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
