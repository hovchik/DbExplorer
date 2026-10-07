using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

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
    private IDialogService? _dialogs;

    /// <param name="filter">Optional WHERE condition, e.g. the primary key of one record.</param>
    /// <param name="dialogs">Confirms production edits and opens referenced rows; without it the rows are read-only.</param>
    public void Initialize(QueryExecutionService service, DatabaseSession session, DbObject table,
        string? filter = null, string? filterDescription = null, IDialogService? dialogs = null)
    {
        _service = service;
        _session = session;
        _dialogs = dialogs;
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
        if (!await ConfirmDiscardEditsAsync()) return;
        Filter = null;
        UpdateTitle();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_service is null || _session is null || _table is null) return;
        if (!await ConfirmDiscardEditsAsync()) return;

        IsRunning = true;
        Status = "Loading…";
        try
        {
            var limit = Math.Max(1, (int)Limit);
            var sql = BuildSelect(_table, limit, _session.Provider.ProviderKey, _session.Provider.QuoteIdentifier, Filter);
            var result = await _service.ExecuteScriptAsync(_session, sql, _table.Database, timeoutSeconds: 60);

            var session = _session;
            var sources = ResolveSources(session, sql, result);
            ResultSets = result.ResultSets
                .Select(rs => ResultSetView.From("Rows", rs, session,
                    session.Provider.QuoteIdentifier(_table.Schema) + "." + session.Provider.QuoteIdentifier(_table.Name)))
                .Select((view, i) => sources[i] is { } source && _dialogs is not null
                    ? view with { Source = source, CommitEdits = CommitEditsAsync, OpenReference = OpenReference }
                    : view)
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

    private IReadOnlyList<ResultSource?> ResolveSources(DatabaseSession session, string sql, QueryExecutionResult result)
    {
        try
        {
            return ResultSourceResolver.ResolveScript(sql, result.ResultSets.Select(r => r.Columns).ToList(), session.Snapshot,
                session.Provider.ProviderKey, string.IsNullOrEmpty(_table?.Database) ? null : _table.Database);
        }
        catch
        {
            return result.ResultSets.Select(_ => (ResultSource?)null).ToList();
        }
    }

    private async Task<string> CommitEditsAsync(ResultSetView view, IReadOnlyList<ResultRow> rows)
    {
        if (_session is not { } session || view.Source is not { } source) throw new InvalidOperationException("Not connected.");
        var status = await ResultEditing.CommitAsync(session, source, rows, openTransaction: null, _dialogs) ?? "Not committed.";
        Status = status;
        return status;
    }

    /// <summary>Opens the referenced row in another data window, filtered to it.</summary>
    private void OpenReference(ResultSetView view, ResultReference reference, ResultRow row)
    {
        if (_service is null || _session is not { } session || _dialogs is null) return;
        var condition = ResultEditSql.ReferenceCondition(reference, row.Values,
            ResultExporter.DialectFor(session.Provider.ProviderKey), session.Provider.QuoteIdentifier);
        if (condition is null) return;
        var description = string.Join(", ", reference.Columns.Select(c =>
            $"{c.ReferencedColumn} = {ResultExporter.FormatInvariant(row.Values[c.ResultColumn])}"));
        _ = _dialogs.ShowGetDataAsync(_service, session, ResultEditSql.ReferencedTable(reference), condition, description);
    }

    private async Task<bool> ConfirmDiscardEditsAsync() =>
        !ResultEditing.HasEdits(ResultSets) || _dialogs is null ||
        await _dialogs.ConfirmAsync("The rows have changes that are not committed yet (edited, new or deleted rows). Reload and discard them?", "Discard and reload");

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
