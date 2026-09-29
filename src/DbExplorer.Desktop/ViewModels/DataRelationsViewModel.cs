using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Diagram;
using DbExplorer.Application.Query;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A table the searched value was found in.</summary>
public sealed record FoundInTable(DbObject Table, int Rows, string Columns)
{
    public string Name => Table.FullName;
}

public enum RelatedResultKind
{
    /// <summary>Found rows and related rows joined into one result.</summary>
    Combined,

    /// <summary>The rows of one table the value was found in.</summary>
    Found,

    /// <summary>Rows of a related table that belong to the found rows (through one foreign key).</summary>
    Related
}

/// <summary>One generated query of the relations window and its outcome.</summary>
public sealed partial class RelatedResult(DataMatchQuery query, RelatedResultKind kind) : ObservableObject
{
    public DataMatchQuery Query { get; } = query;
    public string Title => Query.Title;
    public RelatedResultKind Kind { get; } = kind;
    public bool IsCombined => Kind == RelatedResultKind.Combined;

    /// <summary>Table the rows come from (for combined results, the root table).</summary>
    public DbObject Table => Query.Tables[0];

    public string Database => Table.Database;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasView))]
    private ResultSetView? _view;

    [ObservableProperty] private string _message = "Waiting…";

    /// <summary>Loaded without error and with at least one row.</summary>
    [ObservableProperty] private bool _hasRows;

    public bool HasView => View is not null;
}

/// <summary>
/// Where a searched value lives: an ER diagram of the tables it was found in, the tables directly related to them and
/// the tables linking them; the found rows of each table; the rows of every related table that belong to them; and
/// all of it combined along the foreign keys.
/// </summary>
public partial class DataRelationsViewModel : ViewModelBase
{
    private const int RowLimit = 1000;

    private readonly QueryExecutionService _service;
    private readonly DatabaseSession _session;
    private readonly IReadOnlyList<DataMatch> _matches;
    private readonly IReadOnlyList<DbObject> _tables;
    private CancellationTokenSource? _cts;

    public DataRelationsViewModel(QueryExecutionService service, DatabaseSession session, string term, IReadOnlyList<DataMatch> matches)
    {
        _service = service;
        _session = session;

        _matches = matches;

        FoundIn = _matches
            .GroupBy(m => (m.Database, m.Schema, m.Table), TableComparer.Instance)
            .Select(g => new FoundInTable(
                Resolve(g.First()),
                g.Select(m => m.RowKey ?? m.Value).Distinct().Count(),
                string.Join(", ", g.Select(m => m.Column).Distinct(StringComparer.OrdinalIgnoreCase))))
            .OrderByDescending(f => f.Rows).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _tables = FoundIn.Select(f => f.Table).ToList();

        var databases = _tables.Select(t => t.Database).Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Title = $"\"{term}\" found in {FoundIn.Count:N0} table(s)" + databases.Count switch
        {
            0 => "",
            1 => $" · {databases[0]}",
            _ => $" across {databases.Count} databases ({string.Join(", ", databases)})"
        };
        RebuildDiagram();
    }

    /// <summary>Opens SQL (text, database) in the Query tab; set by the dialog service.</summary>
    public Action<string, string?>? OpenSql { get; init; }

    /// <summary>Reveals a table in the Objects tab; set by the dialog service.</summary>
    public Action<DbObject>? OpenObject { get; init; }

    /// <summary>Double-click on a diagram table.</summary>
    public void Activate(ErTable table) => OpenObject?.Invoke(table.Object);

    public IReadOnlyList<ErColumnMode> ColumnModes { get; } = Enum.GetValues<ErColumnMode>();
    public IReadOnlyList<FoundInTable> FoundIn { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private ErDiagram _diagram = ErDiagram.Empty;
    [ObservableProperty] private ErTable? _selectedTable;
    [ObservableProperty] private ErColumnMode _columnMode = ErColumnMode.KeysOnly;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<RelatedResult> _results = [];

    /// <summary>Hide related-table results that returned no rows.</summary>
    [ObservableProperty] private bool _hideEmpty = true;

    [ObservableProperty] private IReadOnlyList<RelatedResult> _visibleResults = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInEditorCommand))]
    private RelatedResult? _selectedResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private bool _isRunning;

    partial void OnColumnModeChanged(ErColumnMode value) => RebuildDiagram();

    private void RebuildDiagram()
    {
        var selected = SelectedTable?.Title;
        Diagram = ErDiagramBuilder.ForTables(_session.Snapshot, _tables, ColumnMode);
        SelectedTable = Diagram.Tables.FirstOrDefault(t => t.Title == selected);
    }

    /// <summary>Clicking a table in the diagram shows its rows (or the combined rows it takes part in).</summary>
    partial void OnSelectedTableChanged(ErTable? value)
    {
        if (value is null || (SelectedResult is { IsCombined: false } current && Same(current.Table, value.Object))) return;
        SelectedResult = VisibleResults.FirstOrDefault(r => r.Kind == RelatedResultKind.Found && Same(r.Table, value.Object))
                         ?? VisibleResults.FirstOrDefault(r => r.Kind == RelatedResultKind.Related && Same(r.Table, value.Object))
                         ?? VisibleResults.FirstOrDefault(r => r.Query.Tables.Any(t => Same(t, value.Object)))
                         ?? SelectedResult;
    }

    /// <summary>Selecting a result highlights its table in the diagram.</summary>
    partial void OnSelectedResultChanged(RelatedResult? value)
    {
        if (value is null || value.IsCombined) return;
        if (Diagram.Tables.FirstOrDefault(t => Same(t.Object, value.Table)) is { } table) SelectedTable = table;
    }

    partial void OnHideEmptyChanged(bool value) => UpdateVisibleResults();

    private void UpdateVisibleResults()
    {
        var selected = SelectedResult;
        VisibleResults = Results.Where(r => !HideEmpty || r.Kind != RelatedResultKind.Related || r.HasRows || !r.HasView).ToList();
        SelectedResult = selected is not null && VisibleResults.Contains(selected) ? selected : VisibleResults.FirstOrDefault();
    }

    private bool CanReload => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanReload))]
    public async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var providerKey = _session.Provider.ProviderKey;
        var quote = _session.Provider.QuoteIdentifier;
        var combined = DataMatchSql.Combined(_session.Snapshot, Diagram, _matches, providerKey, quote, RowLimit)
            .Select(q => new RelatedResult(q, RelatedResultKind.Combined));
        var found = FoundIn.Select(f => new RelatedResult(new DataMatchQuery(
                $"{Qualified(f.Table)} · {f.Rows:N0} found",
                DataMatchSql.SelectRows(f.Table, _matches.Where(m => Same(m, f.Table)), providerKey, quote, RowLimit),
                [f.Table]), RelatedResultKind.Found));
        var related = DataMatchSql.Related(Diagram, _matches, providerKey, quote, RowLimit)
            .Select(q => new RelatedResult(q, RelatedResultKind.Related));
        Results = [.. combined, .. found, .. related];
        UpdateVisibleResults();

        var focus = Diagram.Tables.Count(t => t.IsFocus);
        var links = Diagram.Tables.Count(t => t.IsLink);
        var summary = $"Found in {FoundIn.Count:N0} table(s) · {Diagram.Tables.Count - focus:N0} related table(s)" +
                      (links > 0 ? $" ({links:N0} linking found tables)" : "") +
                      $" · {Diagram.Edges.Count:N0} relationship(s)" +
                      (Diagram.OmittedTables > 0 ? $" · {Diagram.OmittedTables:N0} more table(s) not shown (limit reached)" : "") +
                      (FoundIn.Count > Diagram.Tables.Count(t => t.IsFocus) ? " · views and tables missing from the metadata cache are not in the diagram" : "");

        IsRunning = true;
        try
        {
            var done = 0;
            foreach (var result in Results)
            {
                if (ct.IsCancellationRequested) return;
                Status = $"Loading {++done}/{Results.Count}: {result.Title}…";
                result.Message = "Loading…";
                try
                {
                    var database = string.IsNullOrEmpty(result.Database) ? null : result.Database;
                    var outcome = await _service.ExecuteScriptAsync(_session, result.Query.Sql, database, timeoutSeconds: 60, ct, maxRows: RowLimit);
                    if (outcome.ResultSets.Count == 0)
                    {
                        result.Message = "The query returned no result set.";
                        continue;
                    }
                    var rows = outcome.ResultSets[0];
                    result.View = ResultSetView.From(result.Title, rows, _session,
                        result.IsCombined ? null : quote(result.Table.Schema) + "." + quote(result.Table.Name));
                    result.HasRows = rows.Rows.Count > 0;
                    result.Message = $"{rows.Rows.Count:N0} row(s)" + (rows.IsTruncated ? $" (first {RowLimit:N0})" : "");
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    result.Message = "Error: " + ex.Message;
                }
            }
            UpdateVisibleResults();
            var empty = Results.Count(r => r.Kind == RelatedResultKind.Related && r is { HasView: true, HasRows: false });
            Status = summary + (empty > 0 && HideEmpty ? $" · {empty:N0} related result(s) with no rows hidden" : "") +
                     " · click a table or a result to switch · double-click a table to open it in Objects";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private string Qualified(DbObject table) =>
        FoundIn.Select(f => f.Table.Database).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 && !string.IsNullOrEmpty(table.Database)
            ? $"{table.Database}.{table.FullName}"
            : table.FullName;

    public void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(HasSelectedResult))]
    private void OpenInEditor()
    {
        if (SelectedResult is { } r) OpenSql?.Invoke(r.Query.Sql, r.Database);
    }

    private bool HasSelectedResult => SelectedResult is not null;

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(2.5, Math.Round(Zoom + 0.1, 1));
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.3, Math.Round(Zoom - 0.1, 1));
    [RelayCommand] private void ResetZoom() => Zoom = 1.0;

    public string ToMermaid() => ErDiagramBuilder.ToMermaid(Diagram);

    private DbObject Resolve(DataMatch m) =>
        _session.Snapshot.Objects.FirstOrDefault(o => Same(m, o))
        ?? new DbObject { Database = m.Database, Schema = m.Schema, Name = m.Table, Type = DbObjectType.Table };

    private static bool Same(DataMatch m, DbObject o) =>
        string.Equals(m.Database, o.Database, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(m.Schema, o.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(m.Table, o.Name, StringComparison.OrdinalIgnoreCase);

    private static bool Same(DbObject a, DbObject b) =>
        string.Equals(a.Database, b.Database, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private sealed class TableComparer : IEqualityComparer<(string Database, string Schema, string Table)>
    {
        public static readonly TableComparer Instance = new();

        public bool Equals((string Database, string Schema, string Table) x, (string Database, string Schema, string Table) y) =>
            string.Equals(x.Database, y.Database, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Database, string Schema, string Table) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Database),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table));
    }
}
