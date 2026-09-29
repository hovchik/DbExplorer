using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

public partial class GetDataViewModel : ViewModelBase
{
    private const string SqlServerProviderKey = "SqlServer";
    private const string PostgresProviderKey = "Postgres";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private decimal _limit = 200;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<ResultSetView> _resultSets = [];

    /// <summary>WHERE condition limiting the rows (e.g. one record found by data search); null = whole table.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    [NotifyCanExecuteChangedFor(nameof(ShowAllRowsCommand))]
    private string? _filter;

    [ObservableProperty] private string _filterDescription = "";

    public bool IsFiltered => Filter is not null;

    private QueryExecutionService? _service;
    private DatabaseSession? _session;
    private DbObject? _table;

    /// <param name="filter">Optional WHERE condition, e.g. the primary key of one record.</param>
    public void Initialize(QueryExecutionService service, DatabaseSession session, DbObject table,
        string? filter = null, string? filterDescription = null)
    {
        _service = service;
        _session = session;
        _table = table;
        Filter = filter;
        FilterDescription = filterDescription ?? filter ?? "";
        UpdateTitle();
        _ = LoadAsync();
    }

    private void UpdateTitle() =>
        Title = $"Data: {_table?.Schema}.{_table?.Name}" + (IsFiltered ? $" — {FilterDescription}" : "");

    [RelayCommand(CanExecute = nameof(IsFiltered))]
    private async Task ShowAllRowsAsync()
    {
        Filter = null;
        UpdateTitle();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_service is null || _session is null || _table is null) return;

        IsRunning = true;
        Status = "Loading…";
        try
        {
            var limit = Math.Max(1, (int)Limit);
            var sql = BuildSelect(_table, limit, _session.Provider.ProviderKey, _session.Provider.QuoteIdentifier, Filter);
            var result = await _service.ExecuteScriptAsync(_session, sql, _table.Database, timeoutSeconds: 60);

            ResultSets = result.ResultSets
                .Select(rs => ResultSetView.From("Rows", rs, _session,
                    _session.Provider.QuoteIdentifier(_table.Schema) + "." + _session.Provider.QuoteIdentifier(_table.Name)))
                .ToList();

            var rowCount = result.ResultSets.Count > 0 ? result.ResultSets[0].Rows.Count : 0;
            Status = $"{rowCount:N0} row(s) in {result.Elapsed.TotalMilliseconds:N0} ms (limit {limit:N0})";
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

    private static string BuildSelect(DbObject table, int limit, string providerKey, Func<string, string> quote, string? filter)
    {
        var schema = quote(table.Schema);
        var name = quote(table.Name);
        var where = filter is null ? "" : $" WHERE {filter}";

        return providerKey == SqlServerProviderKey
            ? $"SELECT TOP ({limit}) * FROM {schema}.{name}{where};"
            : $"SELECT * FROM {schema}.{name}{where} LIMIT {limit};";
    }
}
