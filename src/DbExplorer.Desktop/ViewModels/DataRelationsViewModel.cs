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

/// <summary>One generated query of the relations window and its outcome.</summary>
public sealed partial class RelatedResult(DataMatchQuery query, bool isCombined) : ObservableObject
{
    public DataMatchQuery Query { get; } = query;
    public string Title => Query.Title;
    public bool IsCombined { get; } = isCombined;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasView))]
    private ResultSetView? _view;

    [ObservableProperty] private string _message = "Waiting…";

    public bool HasView => View is not null;
}

/// <summary>
/// Where a searched value lives: an ER diagram of the tables it was found in (plus the tables linking them), the
/// found rows of each table, and the rows of related tables combined along their foreign keys.
/// </summary>
public partial class DataRelationsViewModel : ViewModelBase
{
    private const int RowLimit = 1000;

    private readonly QueryExecutionService _service;
    private readonly DatabaseSession _session;
    private readonly IReadOnlyList<DataMatch> _matches;
    private readonly string? _database;
    private readonly IReadOnlyList<DbObject> _tables;
    private CancellationTokenSource? _cts;

    public DataRelationsViewModel(QueryExecutionService service, DatabaseSession session, string term, IReadOnlyList<DataMatch> matches)
    {
        _service = service;
        _session = session;

        // One database at a time: foreign keys never cross databases. Take the one with the most matches.
        var byDatabase = matches.GroupBy(m => m.Database, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ToList();
        _matches = byDatabase.FirstOrDefault()?.ToList() ?? [];
        _database = string.IsNullOrEmpty(byDatabase.FirstOrDefault()?.Key) ? null : byDatabase[0].Key;
        OtherDatabasesNote = byDatabase.Count > 1
            ? $" · matches in {string.Join(", ", byDatabase.Skip(1).Select(g => g.Key))} not included (foreign keys do not cross databases)"
            : "";

        FoundIn = _matches
            .GroupBy(m => (m.Schema, m.Table), TableComparer.Instance)
            .Select(g => new FoundInTable(
                Resolve(g.First()),
                g.Select(m => m.RowKey ?? m.Value).Distinct().Count(),
                string.Join(", ", g.Select(m => m.Column).Distinct(StringComparer.OrdinalIgnoreCase))))
            .OrderByDescending(f => f.Rows).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _tables = FoundIn.Select(f => f.Table).ToList();

        Title = $"\"{term}\" found in {FoundIn.Count:N0} table(s)" + (_database is null ? "" : $" · {_database}");
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
    private string OtherDatabasesNote { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private ErDiagram _diagram = ErDiagram.Empty;
    [ObservableProperty] private ErTable? _selectedTable;
    [ObservableProperty] private ErColumnMode _columnMode = ErColumnMode.KeysOnly;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<RelatedResult> _results = [];
    [ObservableProperty] private FoundInTable? _selectedFoundIn;

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
        Diagram = ErDiagramBuilder.ForTables(_session.Snapshot, _database, _tables, ColumnMode);
        SelectedTable = Diagram.Tables.FirstOrDefault(t => t.Title == selected);
    }

    /// <summary>Clicking a table in the diagram shows its rows (or the combined rows it takes part in).</summary>
    partial void OnSelectedTableChanged(ErTable? value)
    {
        if (value is null) return;
        SelectedResult = Results.FirstOrDefault(r => !r.IsCombined && Same(r.Query.Tables[0], value.Object))
                         ?? Results.FirstOrDefault(r => r.Query.Tables.Any(t => Same(t, value.Object)))
                         ?? SelectedResult;
    }

    partial void OnSelectedFoundInChanged(FoundInTable? value)
    {
        if (value is null) return;
        SelectedTable = Diagram.Tables.FirstOrDefault(t => Same(t.Object, value.Table)) ?? SelectedTable;
        SelectedResult = Results.FirstOrDefault(r => !r.IsCombined && Same(r.Query.Tables[0], value.Table)) ?? SelectedResult;
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
            .Select(q => new RelatedResult(q, isCombined: true));
        var perTable = FoundIn.Select(f => new RelatedResult(new DataMatchQuery(
                $"{f.Table.Name} ({f.Rows:N0})",
                DataMatchSql.SelectRows(f.Table, _matches.Where(m => Same(m, f.Table)), providerKey, quote, RowLimit),
                [f.Table]), isCombined: false));
        Results = [.. combined, .. perTable];
        SelectedResult = Results.FirstOrDefault();

        var combinedCount = Results.Count(r => r.IsCombined);
        var linked = Diagram.Tables.Count(t => t.IsFocus);
        var summary = $"{FoundIn.Count:N0} table(s) · {Diagram.Tables.Count - linked:N0} linking table(s) · {Diagram.Edges.Count:N0} relationship(s)" +
                      (combinedCount == 0
                          ? FoundIn.Count > 1 ? " · the tables are not linked by foreign keys (within 3 hops), so there is no combined view" : ""
                          : $" · {combinedCount:N0} combined view(s)") +
                      OtherDatabasesNote;

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
                    var outcome = await _service.ExecuteScriptAsync(_session, result.Query.Sql, _database, timeoutSeconds: 60, ct, maxRows: RowLimit);
                    if (outcome.ResultSets.Count == 0)
                    {
                        result.Message = "The query returned no result set.";
                        continue;
                    }
                    var table = result.Query.Tables[0];
                    result.View = ResultSetView.From(result.Title, outcome.ResultSets[0], _session,
                        result.IsCombined ? null : quote(table.Schema) + "." + quote(table.Name));
                    result.Message = $"{outcome.ResultSets[0].Rows.Count:N0} row(s)";
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
            Status = summary + " · click a table to see its rows · double-click a table to open it in the Objects tab";
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(HasSelectedResult))]
    private void OpenInEditor()
    {
        if (SelectedResult is { } r) OpenSql?.Invoke(r.Query.Sql, _database);
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

    private sealed class TableComparer : IEqualityComparer<(string Schema, string Table)>
    {
        public static readonly TableComparer Instance = new();

        public bool Equals((string Schema, string Table) x, (string Schema, string Table) y) =>
            string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Schema, string Table) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table));
    }
}
