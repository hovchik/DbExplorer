using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Diagram;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Modeling;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// The ER model tab: draw tables and relationships on a canvas (or read them from a database), edit the selected table
/// with the Table designer's rows and suggestions, and generate the CREATE/ALTER script that makes a database match the
/// model. Nothing reaches the server until Run, which shows the script and asks first. The model is kept between
/// sessions in the app's folder and can be saved to and opened from .dbxmodel files.
/// </summary>
public partial class ErModelViewModel : ViewModelBase, ISessionAware, IForeignKeyRowOwner
{
    private const string AllSchemas = "(all schemas)";
    private const int TimeoutSeconds = 120;
    private const int MaxUndo = 100;

    private readonly SessionService _sessions;
    private readonly IDialogService _dialogs;
    private readonly string _autosavePath;
    private readonly Stack<ErModel> _undo = new();
    private readonly Stack<ErModel> _redo = new();
    private readonly HashSet<string> _dismissed = [];
    private DatabaseSession? _session;
    private MetadataSnapshot? _databaseSnapshot;
    private IReadOnlyDictionary<ErTable, string> _ids = new Dictionary<ErTable, string>();
    private DesignContext? _context;
    private string? _editKey;
    private bool _loadingEditor;
    private CancellationTokenSource? _autosave;

    [ObservableProperty] private ErModel _model;
    [ObservableProperty] private ErDiagram _diagram = ErDiagram.Empty;
    [ObservableProperty] private ErTable? _selectedTable;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private IReadOnlyList<string> _databases = [];
    [ObservableProperty] private string? _sourceDatabase;
    [ObservableProperty] private IReadOnlyList<string> _schemas = [AllSchemas];
    [ObservableProperty] private string _sourceSchema = AllSchemas;
    [ObservableProperty] private string? _targetDatabase;

    // ----- Selected table editor -----
    [ObservableProperty] private string _schema = "";
    [ObservableProperty] private string _tableName = "";
    [ObservableProperty] private IReadOnlyList<string> _typeNames = ColumnTypes.For(SqlDialect.SqlServerKey);
    [ObservableProperty] private IReadOnlyList<string> _tableNames = [];
    [ObservableProperty] private string _suggestionSummary = "";
    [ObservableProperty] private string _editorTitle = "";

    // ----- Generated script -----
    [ObservableProperty] private string _script = "";
    [ObservableProperty] private string _scriptSummary = "";
    [ObservableProperty] private bool _showScript;
    private ModelScript? _generated;
    private string? _generatedFor;

    public ObservableCollection<ColumnRow> Columns { get; } = [];
    public ObservableCollection<ForeignKeyRow> ForeignKeys { get; } = [];
    public ObservableCollection<IndexRow> Indexes { get; } = [];
    public ObservableCollection<DesignSuggestionItem> Suggestions { get; } = [];
    public ObservableCollection<string> ColumnNames { get; } = [];

    public bool HasSelection => SelectedId is not null;
    public bool HasTables => Model.Tables.Count > 0;
    public bool IsConnected => _session is not null;
    public bool HasForeignKeys => ForeignKeys.Count > 0;
    public bool HasIndexes => Indexes.Count > 0;
    public bool HasFixable => Suggestions.Any(s => s.CanFix && !s.Suggestion.Key.StartsWith("alter-", StringComparison.Ordinal));
    public bool CanRun => _generated?.CanRun == true && _session is not null && !IsBusy;
    public string ModelTitle => $"{Model.Name} · {Model.Tables.Count} table(s) · {Engine(Model.ProviderKey)}";

    public string? SelectedId => SelectedTable is not null && _ids.TryGetValue(SelectedTable, out var id) ? id : null;

    /// <summary>Raised to open SQL in a new query tab, against a database.</summary>
    public event Action<string, string?>? OpenSqlRequested;

    public ErModelViewModel(SessionService sessions, IDialogService dialogs, AppPaths paths)
    {
        _sessions = sessions;
        _dialogs = dialogs;
        _autosavePath = Path.Combine(paths.Root, "er-model." + ErModelFile.Extension);
        _model = LoadAutosave() ?? ErModel.Empty(SqlDialect.SqlServerKey);
        TypeNames = ColumnTypes.For(_model.ProviderKey);
        Redraw();
        Status = HasTables
            ? "Your last model is back. Drag titles to move tables and columns onto other tables to link them."
            : "Start with Add table, or read the tables of a database to model changes to them.";
    }

    // ----- Session -----

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.SnapshotChanged -= OnSnapshotChanged;
        _session = session;
        if (_session is not null) _session.SnapshotChanged += OnSnapshotChanged;
        _databaseSnapshot = null;
        _context = null;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
        if (session is null)
        {
            Databases = [];
            SourceDatabase = TargetDatabase = null;
            RefreshEditor();
            return;
        }
        Databases = Merge(session.Snapshot.Databases, [session.Profile.Database]);
        var preferred = Databases.FirstOrDefault(d => Same(d, Model.Database)) ??
                        Databases.FirstOrDefault(d => Same(d, session.Profile.Database)) ?? Databases.FirstOrDefault();
        SourceDatabase = preferred;
        TargetDatabase = preferred;
        if (!HasTables && Model.ProviderKey != session.Provider.ProviderKey) SetModel(ErModel.Empty(session.Provider.ProviderKey, preferred ?? ""), undoable: false);
        _ = LoadDatabasesAsync(session);
        _ = LoadDatabaseSnapshotAsync();
    }

    private async Task LoadDatabasesAsync(DatabaseSession session)
    {
        try
        {
            var names = await session.Factory.ListDatabasesAsync(session.Profile);
            if (!ReferenceEquals(session, _session)) return;
            var (source, target) = (SourceDatabase, TargetDatabase);
            Databases = Merge(names, session.Snapshot.Databases, [session.Profile.Database]);
            SourceDatabase = Databases.FirstOrDefault(d => Same(d, source)) ?? source;
            TargetDatabase = Databases.FirstOrDefault(d => Same(d, target)) ?? target;
        }
        catch
        {
            // The pickers still offer the databases already in the catalog.
        }
    }

    private void OnSnapshotChanged(object? sender, EventArgs e) => _ = LoadDatabaseSnapshotAsync();

    partial void OnSourceDatabaseChanged(string? value) => _ = LoadSchemasAsync();

    partial void OnTargetDatabaseChanged(string? value)
    {
        CloseScript();
        _ = LoadDatabaseSnapshotAsync();
    }

    private Task<MetadataSnapshot> SnapshotOf(DatabaseSession session, string? database) =>
        string.IsNullOrEmpty(database) || Same(database, session.Profile.Database) || session.Snapshot.ContainsDatabase(database)
            ? Task.FromResult(string.IsNullOrEmpty(database) || !session.Snapshot.ContainsDatabase(database) ? session.Snapshot : session.Snapshot.ForDatabase(database))
            : _sessions.GetDatabaseSnapshotAsync(session, database);

    /// <summary>The target database's catalog, so the editor's suggestions know the tables the model does not have.</summary>
    private async Task LoadDatabaseSnapshotAsync()
    {
        if (_session is not { } session) return;
        try
        {
            var snapshot = await SnapshotOf(session, TargetDatabase);
            if (!ReferenceEquals(session, _session)) return;
            _databaseSnapshot = snapshot;
            _context = null;
            Redraw(SelectedId);
            RefreshEditor();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("er model catalog", ex);
        }
    }

    private async Task LoadSchemasAsync()
    {
        if (_session is not { } session) return;
        try
        {
            var snapshot = await SnapshotOf(session, SourceDatabase);
            var schemas = snapshot.Objects.Where(o => o.Type == DbObjectType.Table).Select(o => o.Schema)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            Schemas = [AllSchemas, .. schemas];
            if (!Schemas.Contains(SourceSchema)) SourceSchema = AllSchemas;
        }
        catch (Exception ex)
        {
            Status = $"Could not read the tables of {SourceDatabase}: {ex.Message}";
        }
    }

    // ----- Model changes, undo, autosave -----

    /// <param name="fromEditor">The change was typed in the editor: its rows already show it, so they are not rebuilt
    /// (that would move the caret out of the box being typed in).</param>
    /// <param name="coalesce">Edits with the same key in a row make one undo step (typing a name, dragging a table).</param>
    private void SetModel(ErModel model, bool undoable = true, bool fromEditor = false, string? coalesce = null, bool layoutOnly = false)
    {
        if (ReferenceEquals(model, Model)) return;
        if (undoable && (coalesce is null || coalesce != _editKey))
        {
            _undo.Push(Model);
            if (_undo.Count > MaxUndo) TrimUndo();
            _redo.Clear();
        }
        _editKey = undoable ? coalesce : null;
        var selected = SelectedId;
        var providerChanged = Model.ProviderKey != model.ProviderKey;
        Model = model;
        if (providerChanged) TypeNames = ColumnTypes.For(model.ProviderKey);
        if (!fromEditor && !layoutOnly) _context = null;
        Redraw(selected);
        if (!fromEditor && !layoutOnly) LoadEditor();
        if (ShowScript && !layoutOnly) MarkScriptStale();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasTables));
        OnPropertyChanged(nameof(ModelTitle));
        // A table being dragged is saved once, when it is dropped.
        if (!layoutOnly) ScheduleAutosave();
    }

    private void TrimUndo()
    {
        var keep = _undo.Take(MaxUndo).Reverse().ToList();
        _undo.Clear();
        foreach (var m in keep) _undo.Push(m);
    }

    /// <summary>Rebuilds the drawing; the selection follows its table into the new drawing.</summary>
    private void Redraw(string? selectedId = null)
    {
        var (diagram, ids) = ErModelLayout.ToDiagram(Model);
        _ids = ids;
        Diagram = diagram;
        var box = selectedId is null ? null : ids.FirstOrDefault(kv => kv.Value == selectedId).Key;
        if (!ReferenceEquals(SelectedTable, box))
        {
            _suppressSelection = true;
            SelectedTable = box;
            _suppressSelection = false;
        }
        var names = Model.Tables.Where(t => t.Name.Length > 0).Select(t => t.FullName(Model.ProviderKey))
            .Concat(DatabaseTables()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!names.SequenceEqual(TableNames, StringComparer.Ordinal)) TableNames = names;
    }

    /// <summary>Only when a table name changes: new choice lists while a key's column is picked would drop the pick. A
    /// picker emptied meanwhile (rows of the previous model, just before undo reloads them) writes nothing back.</summary>
    partial void OnTableNamesChanged(IReadOnlyList<string> value)
    {
        var loading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            foreach (var fk in ForeignKeys) fk.RefreshChoices();
        }
        finally
        {
            _loadingEditor = loading;
        }
    }

    private bool _suppressSelection;

    partial void OnSelectedTableChanged(ErTable? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        RemoveTableCommand.NotifyCanExecuteChanged();
        if (_suppressSelection) return;
        _editKey = null;
        _context = null;
        _dismissed.Clear();
        LoadEditor();
    }

    private IEnumerable<string> DatabaseTables()
    {
        if (_databaseSnapshot is not { } snapshot) return [];
        var covered = Model.Tables.Where(t => t.Baseline is not null).Select(t => $"{t.Baseline!.Schema}.{t.Baseline.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.Objects.Where(o => o.Type == DbObjectType.Table && !covered.Contains(o.FullName)).Select(o => o.FullName);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Model);
        Restore(_undo.Pop());
    }

    private bool CanUndo() => _undo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Model);
        Restore(_redo.Pop());
    }

    private bool CanRedo() => _redo.Count > 0;

    private void Restore(ErModel model)
    {
        var selected = SelectedId;
        _editKey = null;
        Model = model;
        TypeNames = ColumnTypes.For(model.ProviderKey);
        _context = null;
        Redraw(selected);
        LoadEditor();
        if (ShowScript) MarkScriptStale();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasTables));
        OnPropertyChanged(nameof(ModelTitle));
        ScheduleAutosave();
    }

    private ErModel? LoadAutosave()
    {
        try
        {
            return File.Exists(_autosavePath) ? ErModelFile.Read(File.ReadAllText(_autosavePath)) : null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("er model autosave read", ex);
            return null;
        }
    }

    private void ScheduleAutosave()
    {
        _autosave?.Cancel();
        var cts = _autosave = new CancellationTokenSource();
        var text = ErModelFile.Write(Model);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, cts.Token);
                var temp = _autosavePath + ".tmp";
                await File.WriteAllTextAsync(temp, text, cts.Token);
                File.Move(temp, _autosavePath, overwrite: true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ErrorLog.Write("er model autosave", ex);
            }
        });
    }

    // ----- Model commands -----

    [RelayCommand]
    private async Task NewModelAsync()
    {
        if (HasTables && !await _dialogs.ConfirmAsync("Start a new, empty model? Undo brings the current one back.", "New model")) return;
        SetModel(ErModel.Empty(_session?.Provider.ProviderKey ?? Model.ProviderKey, TargetDatabase ?? ""));
        Status = "New model. Add a table, or double-click the canvas where it should go.";
    }

    /// <summary>Replaces the model with the tables of the picked database (and schema), read from the cached catalog,
    /// with their column defaults read from the server.</summary>
    [RelayCommand]
    private async Task ReadFromDatabaseAsync()
    {
        if (_session is not { } session) return;
        var database = SourceDatabase ?? session.Profile.Database;
        var schema = SourceSchema == AllSchemas ? null : SourceSchema;
        IsBusy = true;
        try
        {
            Status = $"Reading the tables of {database}…";
            var snapshot = await SnapshotOf(session, database);
            var tables = ErModelReader.TablesOf(snapshot, schema).Take(ErModelReader.MaxTables).ToList();
            if (tables.Count == 0)
            {
                Status = $"{database}{(schema is null ? "" : "." + schema)} has no tables.";
                return;
            }
            if (HasTables && !await _dialogs.ConfirmAsync(
                    $"Replace the current model with the {tables.Count} table(s) of {database}{(schema is null ? "" : "." + schema)}? Undo brings it back.", "Read tables"))
                return;

            var constraints = await ReadConstraintsAsync(session, tables, "Reading column defaults");
            if (!ReferenceEquals(session, _session)) return;
            var model = ErModelReader.Read(snapshot, session.Provider.ProviderKey, database ?? "", schema, constraints);
            SetModel(model);
            TargetDatabase = database;
            var total = ErModelReader.TablesOf(snapshot, schema).Count();
            Status = $"Read {model.Tables.Count} table(s) from {database}" +
                     (total > model.Tables.Count ? $" (the {model.Tables.Count} most connected of {total})" : "") +
                     ". Edit them here; Generate DDL writes the ALTER script.";
        }
        catch (Exception ex)
        {
            Status = "Could not read the tables: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<Dictionary<DbObject, DbTableConstraints>> ReadConstraintsAsync(DatabaseSession session, IReadOnlyList<DbObject> tables, string what)
    {
        var result = new Dictionary<DbObject, DbTableConstraints>();
        for (var i = 0; i < tables.Count; i++)
        {
            if (tables.Count > 5) Status = $"{what}… {i + 1}/{tables.Count}";
            try
            {
                result[tables[i]] = await session.Provider.GetTableConstraintsAsync(tables[i]);
            }
            catch (Exception ex)
            {
                // Without them the table still loads; its defaults show as none.
                ErrorLog.Write("er model constraints", ex);
            }
        }
        return result;
    }

    [RelayCommand]
    private async Task OpenModelAsync()
    {
        var file = await _dialogs.OpenTextFileAsync("Open ER model", ErModelFile.Extension, ErModelFile.TypeName);
        if (file is not { } f) return;
        try
        {
            var model = ErModelFile.Read(f.Content);
            SetModel(model);
            Status = $"Opened {f.Name}: {model.Tables.Count} table(s).";
        }
        catch (FormatException ex)
        {
            Status = $"Could not open {f.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveModelAsync()
    {
        try
        {
            var name = await _dialogs.SaveTextFileAsync("Save ER model", FileName(Model.Name), ErModelFile.Extension, ErModelFile.TypeName, ErModelFile.Write(Model));
            if (name is null) return;
            var title = Path.GetFileNameWithoutExtension(name);
            if (title.Length > 0 && title != Model.Name) SetModel(Model with { Name = title }, undoable: false);
            Status = $"Saved {name}.";
        }
        catch (Exception ex)
        {
            Status = "Could not save the model: " + ex.Message;
        }
    }

    [RelayCommand]
    private void AddTable() => AddTableAt(null, null);

    /// <summary>A new table with a key column named like the model's other keys, at the given spot or a free one.</summary>
    public void AddTableAt(double? x, double? y)
    {
        var provider = Model.ProviderKey;
        var context = ErModelContext.For(Model, _databaseSnapshot, provider);
        var schema = context.MainSchema ?? TableScriptBuilder.DefaultSchema(provider, Model.Database);
        var name = Model.UniqueTableName(schema, context.TableStyle == NamingStyle.Snake ? "new_table" : "NewTable");
        var draft = new TableDesign { Schema = schema, Name = name };
        var (type, _) = ColumnTypes.Preferred(provider, TypeFamily.Integer);
        draft = draft.AddColumn(new ColumnDesign
        {
            Name = TableDesignAdvisor.KeyColumnName(draft, context), Type = type, IsNullable = false, IsPrimaryKey = true, IsIdentity = true
        });
        var spot = ErModelLayout.FreeSpot(Model);
        var table = new ModelTable { Design = draft, X = x ?? spot.X, Y = y ?? spot.Y };
        SetModel(Model.Add(table));
        Select(table.Id);
        Status = $"Added {schema}.{name}. Name it and add its columns on the right.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveTable()
    {
        if (SelectedId is not { } id || Model.Find(id) is not { } table) return;
        SetModel(Model.Remove(id));
        Status = $"Removed {table.FullName(Model.ProviderKey)} from the model" +
                 (table.Baseline is not null ? ". It stays in the database: the script never drops tables." : ".") + " Undo brings it back.";
    }

    [RelayCommand]
    private void Arrange()
    {
        SetModel(ErModelLayout.Arrange(Model), layoutOnly: true);
        ScheduleAutosave();
        Status = "Arranged: referenced tables left of the tables that reference them.";
    }

    public void MoveTable(ErTable box, double x, double y, bool final)
    {
        if (!_ids.TryGetValue(box, out var id)) return;
        SetModel(Model.Move(id, Math.Round(x), Math.Round(y)), coalesce: "move:" + id, layoutOnly: true);
        if (!final) return;
        _editKey = null;
        ScheduleAutosave();
    }

    public void Link(ErTable childBox, string? childColumn, ErTable parentBox, string? parentColumn)
    {
        if (!_ids.TryGetValue(childBox, out var child) || !_ids.TryGetValue(parentBox, out var parent)) return;
        var parentTable = Model.Find(parent)!;
        var linked = parentColumn is null
            ? Model.LinkToTable(child, childColumn, parent)
            : childColumn is null ? null : Model.Link(child, childColumn, parent, parentColumn);
        if (linked is null)
        {
            Status = parentColumn is null && parentTable.Design.PrimaryKey.Count() != 1
                ? $"{parentTable.FullName(Model.ProviderKey)} needs a single-column primary key to be referenced by its title. Drop onto one of its columns instead."
                : childColumn is null ? "Drop a title onto another table's title to add a key column for it."
                : "That foreign key is there already.";
            return;
        }
        SetModel(linked);
        Select(child);
        var fk = Model.Find(child)!.Design.ForeignKeys[^1];
        Status = $"{Model.Find(child)!.FullName(Model.ProviderKey)}.{fk.Column} now references {fk.ReferencedSchema}.{fk.ReferencedTable}.{fk.ReferencedColumn}.";
    }

    private void Select(string id)
    {
        var box = _ids.FirstOrDefault(kv => kv.Value == id).Key;
        if (box is not null) SelectedTable = box;
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(2.5, Math.Round(Zoom + 0.1, 1));

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(0.3, Math.Round(Zoom - 0.1, 1));

    [RelayCommand]
    private void ResetZoom() => Zoom = 1.0;

    // ----- Editor for the selected table -----

    private ModelTable? Selected => SelectedId is { } id ? Model.Find(id) : null;

    private void LoadEditor()
    {
        _loadingEditor = true;
        try
        {
            Columns.Clear();
            ForeignKeys.Clear();
            Indexes.Clear();
            if (Selected is not { } table)
            {
                Schema = TableName = "";
                EditorTitle = "";
            }
            else
            {
                var design = table.Design;
                Schema = design.Schema;
                TableName = design.Name;
                EditorTitle = table.Baseline is null ? "New table" : $"Read from {table.Baseline.Schema}.{table.Baseline.Name}";
                foreach (var c in design.Columns) Columns.Add(ColumnRow.From(c, EditorChanged, ColumnRenamed));
                SyncColumnNames(design.Columns.Select(c => c.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                foreach (var f in design.ForeignKeys) ForeignKeys.Add(ForeignKeyRow.From(f, this));
                foreach (var i in design.Indexes) Indexes.Add(IndexRow.From(i, EditorChanged));
            }
        }
        finally
        {
            _loadingEditor = false;
        }
        OnPropertyChanged(nameof(HasForeignKeys));
        OnPropertyChanged(nameof(HasIndexes));
        RefreshEditor();
    }

    private TableDesign BuildDesign() => (Selected?.Design ?? new TableDesign()) with
    {
        Schema = Schema,
        Name = TableName,
        Columns = Columns.Select(c => c.ToDesign()).ToList(),
        ForeignKeys = ForeignKeys.Select(f => f.ToDesign()).ToList(),
        Indexes = Indexes.Select(i => i.ToDesign()).ToList()
    };

    private void EditorChanged()
    {
        if (_loadingEditor || SelectedId is not { } id || Selected is not { } table) return;
        var design = BuildDesign();
        // Pickers of rows that were just replaced (undo, a link drawn on the canvas) can still write; nothing changed then.
        if (SameDesign(design, table.Design)) return;
        SetModel(Model.Update(id, design), fromEditor: true, coalesce: "edit:" + id);
        SyncColumnNames(design.Columns.Select(c => c.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        OnPropertyChanged(nameof(HasForeignKeys));
        OnPropertyChanged(nameof(HasIndexes));
        RefreshEditor();
    }

    void IForeignKeyRowOwner.Changed() => EditorChanged();

    partial void OnSchemaChanged(string value) => EditorChanged();
    partial void OnTableNameChanged(string value) => EditorChanged();

    /// <summary>The table's own keys and indexes follow a column while its name is typed, and so do the other tables'
    /// keys that reference it.</summary>
    private void ColumnRenamed(string oldName, string newName)
    {
        if (_loadingEditor || SelectedId is not { } id || oldName.Trim().Length == 0 || newName.Trim().Length == 0) return;
        var from = oldName.Trim();
        var to = newName.Trim();
        if (!Columns.Any(c => string.Equals(c.Name?.Trim(), to, StringComparison.Ordinal))) return;
        _loadingEditor = true;
        try
        {
            if (!ColumnNames.Contains(to)) ColumnNames.Add(to);
            foreach (var fk in ForeignKeys.Where(f => Same(f.Column, from))) fk.Column = to;
            foreach (var index in Indexes)
            {
                var columns = index.ToDesign().Columns;
                if (columns.Contains(from, StringComparer.OrdinalIgnoreCase))
                    index.Columns = string.Join(", ", columns.Select(c => Same(c, from) ? to : c));
            }
        }
        finally
        {
            _loadingEditor = false;
        }
        SetModel(Model.RenameColumn(id, from, to), fromEditor: true, coalesce: "edit:" + id);
    }

    /// <summary>Updates the column list in place: clearing it would make every foreign key picker drop its selection.</summary>
    private void SyncColumnNames(IReadOnlyList<string> names)
    {
        for (var i = ColumnNames.Count - 1; i >= 0; i--)
            if (!names.Contains(ColumnNames[i], StringComparer.Ordinal)) ColumnNames.RemoveAt(i);
        for (var i = 0; i < names.Count; i++)
        {
            var at = ColumnNames.IndexOf(names[i]);
            if (at < 0) ColumnNames.Insert(i, names[i]);
            else if (at != i) ColumnNames.Move(at, i);
        }
    }

    /// <summary>The Table designer's rules on the selected table, against the rest of the model and the target database.</summary>
    private void RefreshEditor()
    {
        Suggestions.Clear();
        if (Selected is not { } table)
        {
            SuggestionSummary = HasTables ? "Select a table to edit it." : "";
            OnPropertyChanged(nameof(HasFixable));
            return;
        }
        _context ??= ErModelContext.For(Model, _databaseSnapshot, Model.ProviderKey, table.Id);
        var review = TableDesignAdvisor.Review(table.Design, _context, table.Baseline);
        foreach (var s in review.Where(s => s.Severity == DesignSeverity.Error || !_dismissed.Contains(s.Key)))
            Suggestions.Add(new DesignSuggestionItem(s));
        var errors = review.Count(s => s.Severity == DesignSeverity.Error);
        var others = Suggestions.Count - errors;
        SuggestionSummary = errors > 0 ? $"{errors} to fix before the script can run" + (others > 0 ? $" · {others} suggestion(s)" : "")
            : others > 0 ? $"{others} suggestion(s)." : "Nothing to suggest for this table.";
        OnPropertyChanged(nameof(HasFixable));
    }

    IReadOnlyList<string> IForeignKeyRowOwner.ColumnsOfTable(string fullName, out string? key)
    {
        key = null;
        var dot = fullName.IndexOf('.');
        var (schema, name) = dot < 0 ? (TableScriptBuilder.DefaultSchema(Model.ProviderKey, Model.Database), fullName) : (fullName[..dot], fullName[(dot + 1)..]);
        if (Model.FindByName(schema, name) is { } table)
        {
            var keys = table.Design.PrimaryKey.ToList();
            key = keys.Count == 1 ? keys[0].Name : null;
            return table.Design.Columns.Select(c => c.Name.Trim()).Where(n => n.Length > 0).ToList();
        }
        var existing = _databaseSnapshot is null ? null : ErModelContext.For(Model, _databaseSnapshot, Model.ProviderKey).FindTable(schema, name);
        key = existing?.Key?.Name;
        return existing?.Columns.Select(c => c.Name).ToList() ?? [];
    }

    [RelayCommand]
    private void AddColumn()
    {
        Columns.Add(ColumnRow.From(new ColumnDesign { Type = "" }, EditorChanged, ColumnRenamed));
        EditorChanged();
    }

    [RelayCommand]
    private void RemoveColumn(ColumnRow? row)
    {
        if (row is null) return;
        Columns.Remove(row);
        var name = row.Name?.Trim() ?? "";
        if (name.Length > 0)
        {
            foreach (var fk in ForeignKeys.Where(f => Same(f.Column, name)).ToList()) ForeignKeys.Remove(fk);
            foreach (var index in Indexes.Where(i => i.ToDesign().Columns.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList()) Indexes.Remove(index);
        }
        EditorChanged();
    }

    [RelayCommand]
    private void MoveColumnUp(ColumnRow? row) => MoveColumn(row, -1);

    [RelayCommand]
    private void MoveColumnDown(ColumnRow? row) => MoveColumn(row, 1);

    private void MoveColumn(ColumnRow? row, int by)
    {
        if (row is null) return;
        var index = Columns.IndexOf(row);
        if (index < 0 || index + by < 0 || index + by >= Columns.Count) return;
        Columns.Move(index, index + by);
        EditorChanged();
    }

    [RelayCommand]
    private void AddForeignKey()
    {
        ForeignKeys.Add(ForeignKeyRow.From(new ForeignKeyDesign(), this));
        EditorChanged();
    }

    [RelayCommand]
    private void RemoveForeignKey(ForeignKeyRow? row)
    {
        if (row is null) return;
        ForeignKeys.Remove(row);
        EditorChanged();
    }

    [RelayCommand]
    private void AddIndex()
    {
        Indexes.Add(IndexRow.From(new IndexDesign(), EditorChanged));
        EditorChanged();
    }

    [RelayCommand]
    private void RemoveIndex(IndexRow? row)
    {
        if (row is null) return;
        Indexes.Remove(row);
        EditorChanged();
    }

    [RelayCommand]
    private void ApplySuggestion(DesignSuggestionItem? item)
    {
        if (item?.Suggestion.Fix is not { } fix || SelectedId is not { } id) return;
        SetModel(Model.Update(id, fix(BuildDesign())));
        Status = $"Applied: {item.Suggestion.FixLabel}.";
    }

    [RelayCommand]
    private void DismissSuggestion(DesignSuggestionItem? item)
    {
        if (item is null || item.Suggestion.Severity == DesignSeverity.Error) return;
        _dismissed.Add(item.Suggestion.Key);
        RefreshEditor();
    }

    [RelayCommand]
    private void ApplyAllSuggestions()
    {
        if (SelectedId is not { } id || Selected is not { } table) return;
        var context = ErModelContext.For(Model, _databaseSnapshot, Model.ProviderKey, id);
        var design = table.Design;
        var applied = new HashSet<string>();
        while (applied.Count < 100)
        {
            var next = TableDesignAdvisor.Review(design, context, table.Baseline)
                .FirstOrDefault(s => s.Fix is not null && !_dismissed.Contains(s.Key) && !applied.Contains(s.Key) &&
                                     !s.Key.StartsWith("alter-", StringComparison.Ordinal));
            if (next is null) break;
            applied.Add(next.Key);
            design = next.Fix!(design);
        }
        SetModel(Model.Update(id, design));
        Status = applied.Count == 0 ? "No suggestion has a fix to apply." : $"Applied {applied.Count} suggestion(s).";
    }

    // ----- Generate DDL -----

    /// <summary>Diffs the model against the target database (read fresh, with column defaults from the server) into
    /// the script that makes the database match it. Shown only: Run asks before anything reaches the server.</summary>
    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (_session is not { } session)
        {
            Status = "Connect to the database the script is for. The model itself works without a connection.";
            return;
        }
        var database = TargetDatabase ?? session.Profile.Database;
        IsBusy = true;
        try
        {
            Status = $"Comparing the model with {database}…";
            var snapshot = await SnapshotOf(session, database);
            var provider = session.Provider.ProviderKey;
            var matches = ErModelScriptBuilder.Matches(Model, snapshot, provider, database);
            var constraints = await ReadConstraintsAsync(session, matches.Values.ToList(), "Reading the current tables");
            if (!ReferenceEquals(session, _session)) return;
            var model = Model;
            var result = await Task.Run(() => ErModelScriptBuilder.Build(model, snapshot, provider, constraints, database));
            _generated = result;
            _generatedFor = database;
            Script = result.Script;
            ScriptSummary = $"For {database}: {result.Summary}";
            ShowScript = true;
            Status = result.Errors.Count > 0 ? "Fix the problems listed at the top of the script, then generate again."
                : result.HasChanges ? "Review the script. Run asks before anything is changed." : result.Summary;
        }
        catch (Exception ex)
        {
            Status = "Could not generate the script: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanRun));
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    private void MarkScriptStale()
    {
        if (_generated is null) return;
        _generated = null;
        ScriptSummary = "The model changed since this script was generated. Generate again before running it.";
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CloseScript()
    {
        ShowScript = false;
        _generated = null;
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenScriptInQuery() => OpenSqlRequested?.Invoke(Script, _generatedFor ?? TargetDatabase);

    [RelayCommand]
    private async Task CopyScriptAsync()
    {
        await _dialogs.CopyTextAsync(Script);
        Status = "Script copied to the clipboard. Nothing has been run.";
    }

    partial void OnIsBusyChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

    /// <summary>Shows the exact script, asks (typing PRODUCTION on a production connection), runs it in one transaction
    /// so a failure leaves nothing behind, then reads the changed tables back into the model.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_session is not { } session || _generated is not { CanRun: true } generated) return;
        var database = _generatedFor;
        if (session.Profile.ReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(session.Profile, "CREATE/ALTER TABLE") + " Open in Query tab still hands the script over.";
            return;
        }
        var production = session.Profile.IsProduction;
        var where = string.IsNullOrEmpty(database) ? "the connection's database" : database;
        var ok = await _dialogs.ConfirmAsync(
            $"Change {where}? {generated.Summary} This runs the script below in one transaction.",
            "Run script", production ? "PRODUCTION" : null,
            production ? $"PRODUCTION · {session.Profile.DisplayName}" : null, generated.Script);
        if (!ok) return;

        IsBusy = true;
        Status = $"Running the script on {where}…";
        try
        {
            await using var run = await session.Provider.BeginScriptSessionAsync(string.IsNullOrEmpty(database) ? null : database, transactional: true);
            await run.ExecuteAsync(generated.Script, TimeoutSeconds);
            await run.CommitAsync();
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Status = "The server did not run the script, nothing was changed: " + ex.Message;
            return;
        }

        try
        {
            Status = "Done. Reading the catalog so the changes show up everywhere…";
            var snapshot = await _sessions.RefreshDatabaseSnapshotAsync(session, database);
            if (!string.IsNullOrEmpty(database) && snapshot.ContainsDatabase(database)) snapshot = snapshot.ForDatabase(database);
            var changed = generated.Tables.Where(t => t.Action != ModelTableAction.Unchanged).ToList();
            var found = changed.Select(t => (t.Table.Id, Object: snapshot.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table &&
                    Same(o.Schema, t.Table.Schema(session.Provider.ProviderKey)) && Same(o.Name, t.Table.Name))))
                .Where(t => t.Object is not null).ToList();
            var constraints = await ReadConstraintsAsync(session, found.Select(f => f.Object!).ToList(), "Reading the changed tables");
            // The tables the script did not touch move to the target database with the model.
            var model = ErModelScriptBuilder.ForDatabase(Model, session.Provider.ProviderKey, database);
            foreach (var (id, obj) in found)
                model = ErModelReader.Rebase(model, id, ErModelReader.Table(obj!, snapshot, session.Provider.ProviderKey, database ?? "", constraints));
            SetModel(model with { Database = database ?? model.Database }, undoable: false);
            CloseScript();
            Status = $"Changed {where}: {generated.Created} table(s) created, {generated.Altered} changed. The model now matches it.";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("er model refresh", ex);
            Status = $"The script ran on {where}, but the catalog could not be read again: {ex.Message}. Refresh metadata to see the changes.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ----- Helpers -----

    private static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length > 0 ? clean : "model";
    }

    private static string Engine(string providerKey) =>
        SqlDialect.EngineName(providerKey);

    private static bool SameDesign(TableDesign a, TableDesign b) =>
        a.Schema == b.Schema && a.Name == b.Name && a.PrimaryKeyName == b.PrimaryKeyName &&
        a.Columns.SequenceEqual(b.Columns) && a.ForeignKeys.SequenceEqual(b.ForeignKeys) &&
        a.Indexes.Count == b.Indexes.Count &&
        a.Indexes.Zip(b.Indexes).All(p => p.First.Name == p.Second.Name && p.First.IsUnique == p.Second.IsUnique &&
                                          p.First.Columns.SequenceEqual(p.Second.Columns, StringComparer.Ordinal));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Merge(params IEnumerable<string?>[] lists) =>
        lists.SelectMany(l => l).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
}
