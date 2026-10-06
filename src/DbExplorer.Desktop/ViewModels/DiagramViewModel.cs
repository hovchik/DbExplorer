using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Diagram;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

public enum DiagramScope
{
    AroundTable,
    WholeSchema
}

/// <summary>ER diagram built from the cached catalog: zero load on the server.</summary>
public partial class DiagramViewModel : ViewModelBase, ISessionAware
{
    private const string AllSchemas = "(all schemas)";
    private DatabaseSession? _session;
    private bool _suspendRebuild;

    public IReadOnlyList<DiagramScope> Scopes { get; } = Enum.GetValues<DiagramScope>();
    public IReadOnlyList<ErColumnMode> ColumnModes { get; } = Enum.GetValues<ErColumnMode>();

    [ObservableProperty] private IReadOnlyList<DbObject> _tables = [];
    [ObservableProperty] private IReadOnlyList<string> _schemas = [AllSchemas];
    [ObservableProperty] private IReadOnlyList<string> _databases = [];
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private DiagramScope _scope = DiagramScope.AroundTable;
    [ObservableProperty] private DbObject? _focusTable;
    [ObservableProperty] private string _selectedSchema = AllSchemas;
    [ObservableProperty] private decimal _depth = 1;
    [ObservableProperty] private ErColumnMode _columnMode = ErColumnMode.KeysOnly;
    [ObservableProperty] private ErDiagram _diagram = ErDiagram.Empty;
    [ObservableProperty] private ErTable? _selectedTable;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private string _status = "Pick a table, or switch to Whole schema.";

    /// <summary>Whole schema only: draw the missing foreign keys <see cref="SchemaSuggester"/> proposes, with their script.</summary>
    [ObservableProperty] private bool _showSuggestions;
    [ObservableProperty] private string _suggestionScript = "";

    public bool IsAroundTable => Scope == DiagramScope.AroundTable;
    public bool IsWholeSchema => Scope == DiagramScope.WholeSchema;
    public bool HasMultipleDatabases => Databases.Count > 1;

    /// <summary>Raised when the user asks to open the selected table in the Objects tab.</summary>
    public event Action<DbObject>? OpenObjectRequested;

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.SnapshotChanged -= OnSnapshotChanged;
        _session = session;
        if (_session is not null) _session.SnapshotChanged += OnSnapshotChanged;
        _suspendRebuild = true;
        try
        {
            FocusTable = null;
            SelectedTable = null;
            LoadLists();
        }
        finally
        {
            _suspendRebuild = false;
        }
        Rebuild();
    }

    /// <summary>Centers the diagram on <paramref name="table"/> (used by "Show in diagram").</summary>
    public void ShowTable(DbObject table)
    {
        _suspendRebuild = true;
        try
        {
            if (!string.IsNullOrEmpty(table.Database)) SelectedDatabase = table.Database;
            Scope = DiagramScope.AroundTable;
            FocusTable = Tables.FirstOrDefault(t =>
                string.Equals(t.Database, table.Database, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Schema, table.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase)) ?? table;
        }
        finally
        {
            _suspendRebuild = false;
        }
        Rebuild();
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        LoadLists();
        Rebuild();
    }

    private void LoadLists()
    {
        var objects = _session?.Snapshot.Objects ?? [];
        Databases = objects.Select(o => o.Database).Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        OnPropertyChanged(nameof(HasMultipleDatabases));
        if (SelectedDatabase is null || !Databases.Contains(SelectedDatabase, StringComparer.OrdinalIgnoreCase))
            SelectedDatabase = Databases.FirstOrDefault();
        LoadTablesForDatabase();
    }

    private void LoadTablesForDatabase()
    {
        var tables = (_session?.Snapshot.Objects ?? [])
            .Where(o => o.Type is DbObjectType.Table or DbObjectType.ForeignTable && InSelectedDatabase(o.Database))
            .OrderBy(o => o.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Tables = tables;
        Schemas = [AllSchemas, .. tables.Select(t => t.Schema).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s)];
        if (!Schemas.Contains(SelectedSchema)) SelectedSchema = AllSchemas;
        if (FocusTable is not null && !tables.Contains(FocusTable)) FocusTable = null;
    }

    private bool InSelectedDatabase(string database) =>
        SelectedDatabase is null || string.Equals(database, SelectedDatabase, StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedDatabaseChanged(string? value)
    {
        LoadTablesForDatabase();
        Rebuild();
    }

    partial void OnScopeChanged(DiagramScope value)
    {
        if (value != DiagramScope.WholeSchema) ShowSuggestions = false;
        OnPropertyChanged(nameof(IsAroundTable));
        OnPropertyChanged(nameof(IsWholeSchema));
        Rebuild();
    }

    partial void OnFocusTableChanged(DbObject? value) => Rebuild();
    partial void OnSelectedSchemaChanged(string value) => Rebuild();
    partial void OnDepthChanged(decimal value) => Rebuild();
    partial void OnColumnModeChanged(ErColumnMode value) => Rebuild();
    partial void OnShowSuggestionsChanged(bool value) => Rebuild();
    partial void OnSelectedTableChanged(ErTable? value) => OpenSelectedCommand.NotifyCanExecuteChanged();

    private void Rebuild()
    {
        if (_suspendRebuild) return;
        SuggestionScript = "";
        if (_session is null)
        {
            Diagram = ErDiagram.Empty;
            return;
        }

        var snapshot = _session.Snapshot;
        if (Scope == DiagramScope.AroundTable)
        {
            if (FocusTable is null)
            {
                Diagram = ErDiagram.Empty;
                Status = "Pick a table to see its relationships, or switch to Whole schema.";
                return;
            }
            Diagram = ErDiagramBuilder.AroundTable(snapshot, FocusTable, (int)Math.Clamp(Depth, 0, 6), ColumnMode);
        }
        else if (ShowSuggestions)
        {
            var schema = SelectedSchema == AllSchemas ? null : SelectedSchema;
            var suggestions = SchemaSuggester.Suggest(snapshot, _session.Provider.ProviderKey, SelectedDatabase, schema, ColumnMode);
            Diagram = suggestions.Diagram;
            SuggestionScript = suggestions.Script;
            SelectedTable = null;
            var drawn = Diagram.Edges.Count(e => e.IsSuggested);
            Status = suggestions.ForeignKeys.Count == 0
                ? "No missing foreign keys found in this schema."
                : $"{suggestions.ForeignKeys.Count:N0} suggested foreign key(s), drawn in orange" +
                  (drawn < suggestions.ForeignKeys.Count ? $" ({drawn:N0} on screen, table limit reached)" : "") +
                  " · the script on the right adds them · nothing has been run";
            return;
        }
        else
        {
            var schema = SelectedSchema == AllSchemas ? null : SelectedSchema;
            Diagram = ErDiagramBuilder.WholeSchema(snapshot, SelectedDatabase, schema, ColumnMode);
        }

        SelectedTable = Diagram.Tables.FirstOrDefault(t => t.IsFocus);
        Status = $"{Diagram.Tables.Count:N0} table(s) · {Diagram.Edges.Count:N0} relationship(s)" +
                 (Diagram.OmittedTables > 0 ? $" · {Diagram.OmittedTables:N0} more not shown (limit reached)" : "") +
                 " · built from cached metadata · double-click a table to focus it · Ctrl+wheel to zoom";
    }

    public string ToMermaid() => ErDiagramBuilder.ToMermaid(Diagram);

    public void Refocus(ErTable table) => ShowTable(table.Object);

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(2.5, Math.Round(Zoom + 0.1, 1));
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.3, Math.Round(Zoom - 0.1, 1));
    [RelayCommand] private void ResetZoom() => Zoom = 1.0;

    [RelayCommand(CanExecute = nameof(HasSelectedTable))]
    private void OpenSelected()
    {
        if (SelectedTable is not null) OpenObjectRequested?.Invoke(SelectedTable.Object);
    }

    private bool HasSelectedTable => SelectedTable is not null;
}
