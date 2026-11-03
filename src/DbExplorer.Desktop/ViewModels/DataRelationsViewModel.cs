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
    private int _done;

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
        Status = $"Preparing the diagram of {FoundIn.Count:N0} table(s)…";
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
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    /// <summary>Queries finished so far (of <see cref="ProgressMaximum"/>); drives the progress bar.</summary>
    [ObservableProperty] private int _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private int _progressMaximum;

    /// <summary>No query count yet: the diagram and the queries are still being built.</summary>
    public bool IsProgressIndeterminate => ProgressMaximum == 0;

    /// <summary>Queries run at the same time (like the data search's "3 at a time").</summary>
    public int Parallelism { get; init; } = 3;

    /// <summary>Changing the columns shown rebuilds the diagram in the background, keeping the loaded rows.</summary>
    partial void OnColumnModeChanged(ErColumnMode value)
    {
        if (Diagram.Tables.Count == 0) return;
        _ = RebuildDiagramAsync();
    }

    private async Task RebuildDiagramAsync()
    {
        var mode = ColumnMode;
        var diagram = await Task.Run(() => ErDiagramBuilder.ForTables(_session.Snapshot, _tables, mode));
        if (mode == ColumnMode) SetDiagram(diagram);
    }

    private void SetDiagram(ErDiagram diagram)
    {
        var selected = SelectedTable?.Title;
        Diagram = diagram;
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

    /// <summary>
    /// Builds the diagram and the queries, then runs the queries a few at a time. Everything heavy (the diagram, the
    /// generated SQL, running each query and reading its rows) happens off the UI thread, so a value found in hundreds of
    /// tables keeps the window responsive; Stop or closing the window cancels what is left.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReload))]
    public async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        IsRunning = true;
        _done = 0;
        Progress = 0;
        ProgressMaximum = 0;
        try
        {
            if (Diagram.Tables.Count == 0 && _tables.Count > 0)
            {
                Status = $"Preparing the diagram of {FoundIn.Count:N0} table(s)…";
                var mode = ColumnMode;
                var diagram = await Task.Run(() => ErDiagramBuilder.ForTables(_session.Snapshot, _tables, mode), ct);
                if (ct.IsCancellationRequested) return;
                SetDiagram(diagram);
            }

            Status = "Preparing the queries…";
            var plan = Diagram;
            var results = await Task.Run(() => BuildResults(plan), ct);
            if (ct.IsCancellationRequested) return;
            Results = results;
            UpdateVisibleResults();
            ProgressMaximum = Results.Count;

            var summary = Summary();
            var gate = new SemaphoreSlim(Math.Max(1, Parallelism));
            var tasks = Results.Select(r => RunOneAsync(r, gate, ct)).ToList();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }

            UpdateVisibleResults();
            if (ct.IsCancellationRequested)
            {
                if (ReferenceEquals(_cts, cts))
                    Status = $"Stopped after {Progress:N0} of {Results.Count:N0} queries · Reload data runs them again";
                return;
            }
            var empty = Results.Count(r => r.Kind == RelatedResultKind.Related && r is { HasView: true, HasRows: false });
            Status = summary + (empty > 0 && HideEmpty ? $" · {empty:N0} related result(s) with no rows hidden" : "") +
                     " · click a table or a result to switch · double-click a table to open it in Objects";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cts, cts)) Status = "Stopped · Reload data starts again";
        }
        finally
        {
            // A newer load (Reload while stopping) owns the flags now.
            if (ReferenceEquals(_cts, cts)) IsRunning = false;
        }
    }

    private List<RelatedResult> BuildResults(ErDiagram diagram)
    {
        var providerKey = _session.Provider.ProviderKey;
        var quote = _session.Provider.QuoteIdentifier;
        var byTable = _matches.ToLookup(m => (m.Database, m.Schema, m.Table), TableComparer.Instance);
        var combined = DataMatchSql.Combined(_session.Snapshot, diagram, _matches, providerKey, quote, RowLimit)
            .Select(q => new RelatedResult(q, RelatedResultKind.Combined));
        var found = FoundIn.Select(f => new RelatedResult(new DataMatchQuery(
                $"{Qualified(f.Table)} · {f.Rows:N0} found",
                DataMatchSql.SelectRows(f.Table, byTable[(f.Table.Database, f.Table.Schema, f.Table.Name)], providerKey, quote, RowLimit),
                [f.Table]), RelatedResultKind.Found));
        var related = DataMatchSql.Related(diagram, _matches, providerKey, quote, RowLimit)
            .Select(q => new RelatedResult(q, RelatedResultKind.Related));
        return [.. combined, .. found, .. related];
    }

    private string Summary()
    {
        var focus = Diagram.Tables.Count(t => t.IsFocus);
        var links = Diagram.Tables.Count(t => t.IsLink);
        return $"Found in {FoundIn.Count:N0} table(s) · {Diagram.Tables.Count - focus:N0} related table(s)" +
               (links > 0 ? $" ({links:N0} linking found tables)" : "") +
               $" · {Diagram.Edges.Count:N0} relationship(s)" +
               (Diagram.OmittedTables > 0 ? $" · {Diagram.OmittedTables:N0} more table(s) not shown (limit reached)" : "") +
               (FoundIn.Count > focus ? " · views and tables missing from the metadata cache are not in the diagram" : "");
    }

    /// <summary>Runs one result's query on a worker thread; only the finished result is set here (on the caller's thread).</summary>
    private async Task RunOneAsync(RelatedResult result, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            result.Message = "Loading…";
            Status = $"Loading {Progress + 1:N0}/{Results.Count:N0}: {result.Title}…";
            var quote = _session.Provider.QuoteIdentifier;
            var sourceTable = result.IsCombined ? null : quote(result.Table.Schema) + "." + quote(result.Table.Name);
            var database = string.IsNullOrEmpty(result.Database) ? null : result.Database;
            try
            {
                var (view, hasRows, message) = await Task.Run(async () =>
                {
                    var outcome = await _service.ExecuteScriptAsync(_session, result.Query.Sql, database, timeoutSeconds: 60, ct, maxRows: RowLimit)
                        .ConfigureAwait(false);
                    if (outcome.ResultSets.Count == 0) return ((ResultSetView?)null, false, "The query returned no result set.");
                    var rows = outcome.ResultSets[0];
                    return (ResultSetView.From(result.Title, rows, _session, sourceTable), rows.Rows.Count > 0,
                        $"{rows.Rows.Count:N0} row(s)" + (rows.IsTruncated ? $" (first {RowLimit:N0})" : ""));
                }, ct);
                result.View = view;
                result.HasRows = hasRows;
                result.Message = message;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result.Message = "Stopped.";
                throw;
            }
            catch (Exception ex)
            {
                result.Message = "Error: " + ex.Message;
            }
            Progress = Interlocked.Increment(ref _done);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool CanStop => IsRunning;

    /// <summary>Stops loading; rows already loaded stay.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

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
