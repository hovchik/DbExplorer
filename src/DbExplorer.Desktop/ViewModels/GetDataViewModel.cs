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

    private QueryExecutionService? _service;
    private DatabaseSession? _session;
    private DbObject? _table;

    public void Initialize(QueryExecutionService service, DatabaseSession session, DbObject table)
    {
        _service = service;
        _session = session;
        _table = table;
        Title = $"Data: {table.Schema}.{table.Name}";
        _ = LoadAsync();
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
            var sql = BuildSelect(_table, limit, _session.Provider.ProviderKey, _session.Provider.QuoteIdentifier);
            var result = await _service.ExecuteScriptAsync(_session, sql, _table.Database, timeoutSeconds: 60);

            ResultSets = result.ResultSets
                .Select((rs, i) => new ResultSetView(
                    $"Rows", rs.Columns, rs.Rows.Select(r => new ResultRow(r)).ToList()))
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

    private static string BuildSelect(DbObject table, int limit, string providerKey, Func<string, string> quote)
    {
        var schema = quote(table.Schema);
        var name = quote(table.Name);

        return providerKey == SqlServerProviderKey
            ? $"SELECT TOP ({limit}) * FROM {schema}.{name};"
            : $"SELECT * FROM {schema}.{name} LIMIT {limit};";
    }
}
