using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Copy;
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
    [ObservableProperty] private SuggestionItem? _selectedSuggestion;
    [ObservableProperty] private string _suggestionSummary = "";

    /// <summary>One card per suggested foreign key; the script holds the ones left ticked.</summary>
    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];
    public bool HasSuggestions => Suggestions.Count > 0;
    private SqlDialect _suggestionDialect = SqlDialect.SqlServer;
    private string? _suggestionDatabase;
    private string? _suggestionSchema;

    public bool IsAroundTable => Scope == DiagramScope.AroundTable;
    public bool IsWholeSchema => Scope == DiagramScope.WholeSchema;
    public bool HasMultipleDatabases => Databases.Count > 1;

    /// <summary>Raised when the user asks to open the selected table in the Objects tab.</summary>
    public event Action<DbObject>? OpenObjectRequested;

    /// <summary>Raised to open SQL (the suggestions script) in a new query tab, against a database.</summary>
    public event Action<string, string?>? OpenSqlRequested;

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

    private int _rebuildVersion;

    /// <summary>Lays the diagram (and, with suggestions on, the missing-key analysis) out on the thread pool: a whole
    /// schema can be thousands of tables. Only the newest request's result is shown.</summary>
    private async void Rebuild()
    {
        if (_suspendRebuild) return;
        var version = ++_rebuildVersion;
        SetSuggestions(null);
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
            var (focus, depth, mode) = (FocusTable, (int)Math.Clamp(Depth, 0, 6), ColumnMode);
            var diagram = await BuildAsync(version, () => ErDiagramBuilder.AroundTable(snapshot, focus, depth, mode));
            if (diagram is not null) ShowDiagram(diagram);
        }
        else if (ShowSuggestions)
        {
            var (provider, database, schema, mode) = (_session.Provider.ProviderKey, SelectedDatabase, SelectedSchema == AllSchemas ? null : SelectedSchema, ColumnMode);
            var suggestions = await BuildAsync(version, () => SchemaSuggester.Suggest(snapshot, provider, database, schema, mode));
            if (suggestions is null) return;
            Diagram = suggestions.Diagram;
            _suggestionDialect = SqlDialect.For(_session.Provider.ProviderKey);
            _suggestionDatabase = SelectedDatabase;
            _suggestionSchema = schema;
            SetSuggestions(suggestions.ForeignKeys);
            SelectedTable = null;
            var drawn = Diagram.Edges.Count(e => e.IsSuggested);
            Status = suggestions.ForeignKeys.Count == 0
                ? "No missing foreign keys found in this schema."
                : $"{suggestions.ForeignKeys.Count:N0} suggested foreign key(s), drawn in orange" +
                  (drawn < suggestions.ForeignKeys.Count ? $" ({drawn:N0} on screen, table limit reached)" : "") +
                  " · click one on the right to find it · nothing has been run";
        }
        else
        {
            var (database, schema, mode) = (SelectedDatabase, SelectedSchema == AllSchemas ? null : SelectedSchema, ColumnMode);
            var diagram = await BuildAsync(version, () => ErDiagramBuilder.WholeSchema(snapshot, database, schema, mode));
            if (diagram is not null) ShowDiagram(diagram);
        }
    }

    /// <summary>Runs <paramref name="build"/> on the thread pool; null when it failed or a newer rebuild started.</summary>
    private async Task<T?> BuildAsync<T>(int version, Func<T> build) where T : class
    {
        Status = "Laying out the diagram…";
        try
        {
            var result = await Task.Run(build);
            return version == _rebuildVersion ? result : null;
        }
        catch (Exception ex)
        {
            if (version == _rebuildVersion) Status = "Could not build the diagram: " + ex.Message;
            return null;
        }
    }

    private void ShowDiagram(ErDiagram diagram)
    {
        Diagram = diagram;
        SelectedTable = Diagram.Tables.FirstOrDefault(t => t.IsFocus);
        Status = $"{Diagram.Tables.Count:N0} table(s) · {Diagram.Edges.Count:N0} relationship(s)" +
                 (Diagram.OmittedTables > 0 ? $" · {Diagram.OmittedTables:N0} more not shown (limit reached)" : "") +
                 " · built from cached metadata · double-click a table to focus it · Ctrl+wheel to zoom";
    }

    private void SetSuggestions(IReadOnlyList<SuggestedForeignKey>? foreignKeys)
    {
        SelectedSuggestion = null;
        Suggestions.Clear();
        foreach (var fk in foreignKeys ?? [])
            Suggestions.Add(new SuggestionItem(fk, SchemaSuggester.Statement(_suggestionDialect, fk), UpdateSuggestionScript));
        OnPropertyChanged(nameof(HasSuggestions));
        UpdateSuggestionScript();
    }

    private void UpdateSuggestionScript()
    {
        var included = Suggestions.Where(s => s.Include).Select(s => s.ForeignKey).ToList();
        SuggestionScript = ShowSuggestions ? SchemaSuggester.Script(_suggestionDialect, _suggestionDatabase, _suggestionSchema, included) : "";
        SuggestionSummary = Suggestions.Count == 0
            ? "No missing foreign keys found: every column that looks like a reference already has one."
            : $"{included.Count} of {Suggestions.Count} in the script. Untick the ones you do not want.";
        CopyScriptReady = included.Count > 0;
        OpenScriptCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty] private bool _copyScriptReady;

    /// <summary>Selecting a card selects its child table, so the orange line is highlighted on the canvas.</summary>
    partial void OnSelectedSuggestionChanged(SuggestionItem? value)
    {
        if (value is null) return;
        var fk = value.ForeignKey.ForeignKey;
        SelectedTable = Diagram.Tables.FirstOrDefault(t =>
            string.Equals(t.Object.Schema, fk.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Object.Name, fk.Table, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand] private void IncludeAll() { foreach (var s in Suggestions) s.Include = true; }
    [RelayCommand] private void IncludeNone() { foreach (var s in Suggestions) s.Include = false; }

    [RelayCommand(CanExecute = nameof(CopyScriptReady))]
    private void OpenScript() => OpenSqlRequested?.Invoke(SuggestionScript, _suggestionDatabase);

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

/// <summary>A suggested foreign key as the suggestions panel shows it.</summary>
public partial class SuggestionItem(SuggestedForeignKey foreignKey, string statement, Action changed) : ObservableObject
{
    public SuggestedForeignKey ForeignKey { get; } = foreignKey;
    public string Statement { get; } = statement;
    public string Child => $"{ForeignKey.ForeignKey.Schema}.{ForeignKey.ForeignKey.Table}.{ForeignKey.ForeignKey.Columns}";
    public string Parent => $"{ForeignKey.ForeignKey.ReferencedSchema}.{ForeignKey.ForeignKey.ReferencedTable}.{ForeignKey.ForeignKey.ReferencedColumns}";
    public string Confidence => ForeignKey.Accepted ? "accepted" : ForeignKey.Confidence;
    public string Reason => ForeignKey.Reason;
    public string? Warning => ForeignKey.TypeWarning;
    public bool HasWarning => Warning is not null;

    [ObservableProperty] private bool _include = true;

    partial void OnIncludeChanged(bool value) => changed();
}
