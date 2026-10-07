using DbExplorer.Application.Connections;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// Designs a new table in a chosen database, or changes an existing one: columns, primary key, foreign keys to existing
/// tables and indexes, with the CREATE TABLE (or ALTER TABLE) script and <see cref="TableDesignAdvisor"/>'s suggestions
/// updated on every edit. Nothing reaches the server until Execute, which shows the exact script and asks first.
/// </summary>
public partial class TableDesignerViewModel(SessionService sessions, IDialogService dialogs) : ViewModelBase, ISessionAware, IForeignKeyRowOwner
{
    private const int TimeoutSeconds = 60;
    private DatabaseSession? _session;
    private DesignContext _context = DesignContext.Empty(SqlDialect.SqlServerKey);
    private readonly HashSet<string> _dismissed = [];
    private bool _loading;
    private bool _edited;
    private int _contextVersion;

    /// <summary>The table as it is on the server while an existing table is open; null while designing a new one.</summary>
    private TableDesign? _original;
    private bool _opening;

    [ObservableProperty] private IReadOnlyList<string> _databases = [];
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private IReadOnlyList<string> _schemas = [];
    [ObservableProperty] private string _schema = "";
    [ObservableProperty] private string _tableName = "";
    [ObservableProperty] private string _script = "";
    [ObservableProperty] private string _status = "Pick a database, name the table and add its columns.";
    [ObservableProperty] private string _suggestionSummary = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private IReadOnlyList<string> _typeNames = ColumnTypes.For(SqlDialect.SqlServerKey);

    /// <summary>Existing tables of the database, as schema.table, for foreign keys.</summary>
    [ObservableProperty] private IReadOnlyList<string> _tableNames = [];

    /// <summary>The table the last Execute created, for "Open in Objects".</summary>
    [ObservableProperty] private DbObject? _lastCreated;

    /// <summary>schema.table typed or picked in the "Open table" box.</summary>
    [ObservableProperty] private string _openTableName = "";

    /// <summary>"New table" or "Changing dbo.Orders", shown above the columns.</summary>
    [ObservableProperty] private string _modeText = "New table";

    public ObservableCollection<ColumnRow> Columns { get; } = [];
    public ObservableCollection<ForeignKeyRow> ForeignKeys { get; } = [];
    public ObservableCollection<IndexRow> Indexes { get; } = [];
    public ObservableCollection<DesignSuggestionItem> Suggestions { get; } = [];

    /// <summary>The design's column names, for the foreign key and index pickers.</summary>
    public ObservableCollection<string> ColumnNames { get; } = [];

    public bool HasErrors { get; private set; }
    public bool HasDismissed => _dismissed.Count > 0;
    /// <summary>"Apply all" leaves out the undo buttons of risky changes: those are the user's own edits.</summary>
    public bool HasFixable => Suggestions.Any(s => s.CanFix && !s.Suggestion.Key.StartsWith("alter-", StringComparison.Ordinal));
    public bool HasForeignKeys => ForeignKeys.Count > 0;
    public bool HasIndexes => Indexes.Count > 0;
    public bool IsAltering => _original is not null;
    public string ExecuteLabel => IsAltering ? "Apply changes…" : "Execute…";

    /// <summary>Raised to open SQL in a new query tab, against a database.</summary>
    public event Action<string, string?>? OpenSqlRequested;

    /// <summary>Raised to show the created table in the Objects tab.</summary>
    public event Action<DbObject>? OpenObjectRequested;

    private string ProviderKey => _session?.Provider.ProviderKey ?? SqlDialect.SqlServerKey;

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.SnapshotChanged -= OnSnapshotChanged;
        _session = session;
        if (_session is not null) _session.SnapshotChanged += OnSnapshotChanged;
        LastCreated = null;
        _dismissed.Clear();
        _context = DesignContext.Empty(ProviderKey);
        TypeNames = ColumnTypes.For(ProviderKey);
        if (session is null)
        {
            Databases = [];
            SelectedDatabase = null;
            return;
        }

        Databases = Merge(session.Snapshot.Databases, [session.Profile.Database]);
        var preferred = Databases.FirstOrDefault(d => Same(d, session.Profile.Database)) ?? Databases.FirstOrDefault();
        if (string.Equals(SelectedDatabase, preferred, StringComparison.Ordinal)) _ = LoadContextAsync();
        else SelectedDatabase = preferred;
        NewDesign();
        _ = LoadServerDatabasesAsync(session);
    }

    private async Task LoadServerDatabasesAsync(DatabaseSession session)
    {
        try
        {
            var names = await session.Factory.ListDatabasesAsync(session.Profile);
            if (!ReferenceEquals(session, _session)) return;
            var selected = SelectedDatabase;
            Databases = Merge(names, session.Snapshot.Databases, [session.Profile.Database]);
            SelectedDatabase = Databases.FirstOrDefault(d => Same(d, selected ?? "")) ?? selected;
        }
        catch
        {
            // Without the server list the picker still offers the databases already in the catalog.
        }
    }

    private void OnSnapshotChanged(object? sender, EventArgs e) => _ = LoadContextAsync();

    partial void OnSelectedDatabaseChanged(string? value)
    {
        // Another database than the open table's: back to designing a new table there.
        if (!_opening && _original is not null && !Same(value, _original.Database))
        {
            SetOriginal(null);
            _edited = false;
        }
        _ = LoadContextAsync();
    }

    /// <summary>Reads the conventions and tables of the selected database from the catalog (cached; another database of
    /// the server is read once over its own connection).</summary>
    private async Task LoadContextAsync()
    {
        if (_session is not { } session) return;
        var version = ++_contextVersion;
        var database = SelectedDatabase;
        var provider = session.Provider.ProviderKey;
        try
        {
            if (!string.IsNullOrEmpty(database) && !session.Snapshot.ContainsDatabase(database) &&
                !Same(database, session.Profile.Database))
                Status = $"Reading the tables of {database}…";
            var snapshot = string.IsNullOrEmpty(database) ? session.Snapshot : await sessions.GetDatabaseSnapshotAsync(session, database);
            var context = await Task.Run(() => DesignContext.From(snapshot, provider));
            if (version != _contextVersion || !ReferenceEquals(session, _session)) return;
            _context = context;
            Schemas = context.Schemas.Count > 0 ? context.Schemas : [TableScriptBuilder.DefaultSchema(provider, database)];
            TableNames = context.Tables.Select(t => t.FullName).ToList();
            foreach (var fk in ForeignKeys) fk.RefreshChoices();
            if (!_edited && _original is null) NewDesign();
            else Refresh();
            if (Status.StartsWith("Reading the tables", StringComparison.Ordinal) || Status.StartsWith("Pick a database", StringComparison.Ordinal)) Status = $"{context.Tables.Count:N0} existing table(s) in {database ?? "this database"}.";
        }
        catch (Exception ex)
        {
            if (version == _contextVersion) Status = $"Could not read the tables of {database}: {ex.Message}. Suggestions that need them are off.";
        }
    }

    // ----- Design <-> rows -----

    private TableDesign BuildDesign() => new()
    {
        Database = SelectedDatabase ?? "",
        Schema = Schema,
        Name = TableName,
        Columns = Columns.Select(c => c.ToDesign()).ToList(),
        ForeignKeys = ForeignKeys.Select(f => f.ToDesign()).ToList(),
        Indexes = Indexes.Select(i => i.ToDesign()).ToList()
    };

    private void LoadDesign(TableDesign design)
    {
        _loading = true;
        try
        {
            Schema = design.Schema;
            TableName = design.Name;
            Columns.Clear();
            foreach (var c in design.Columns) Columns.Add(ColumnRow.From(c, Changed, ColumnRenamed));
            ForeignKeys.Clear();
            foreach (var f in design.ForeignKeys) ForeignKeys.Add(ForeignKeyRow.From(f, this));
            Indexes.Clear();
            foreach (var i in design.Indexes) Indexes.Add(IndexRow.From(i, Changed));
        }
        finally
        {
            _loading = false;
        }
        Refresh();
    }

    /// <summary>A fresh design: the database's usual schema and a key column named like its other keys.</summary>
    private void NewDesign()
    {
        _edited = false;
        SetOriginal(null);
        var provider = ProviderKey;
        var draft = new TableDesign { Schema = _context.MainSchema ?? TableScriptBuilder.DefaultSchema(provider, SelectedDatabase) };
        var (type, _) = ColumnTypes.Preferred(provider, TypeFamily.Integer);
        LoadDesign(draft.AddColumn(new ColumnDesign
        {
            Name = TableDesignAdvisor.KeyColumnName(draft, _context), Type = type, IsNullable = false, IsPrimaryKey = true, IsIdentity = true
        }));
    }

    /// <summary>Foreign keys and indexes follow a column while its name is typed, as they do on the server; otherwise the
    /// key's column picker would lose its selection and an existing key would be dropped.</summary>
    private void ColumnRenamed(string oldName, string newName)
    {
        if (_loading || oldName.Trim().Length == 0 || newName.Trim().Length == 0) return;
        var from = oldName.Trim();
        var to = newName.Trim();
        if (!ColumnNames.Contains(to)) ColumnNames.Add(to);
        foreach (var fk in ForeignKeys.Where(f => Same(f.Column, from))) fk.Column = to;
        foreach (var index in Indexes)
        {
            var columns = index.ToDesign().Columns;
            if (columns.Contains(from, StringComparer.OrdinalIgnoreCase))
                index.Columns = string.Join(", ", columns.Select(c => Same(c, from) ? to : c));
        }
    }

    internal void Changed()
    {
        if (_loading) return;
        _edited = true;
        Refresh();
    }

    partial void OnSchemaChanged(string value) => Changed();

    partial void OnTableNameChanged(string? oldValue, string newValue)
    {
        if (_loading) return;
        // A key still named after the table (new tables only) (InvoiceId) follows the table's name while it is typed.
        if (_original is null && _context.KeyNaming == KeyNaming.TableId && Columns.FirstOrDefault(c => c.IsPrimaryKey) is { } key)
        {
            var before = TableDesignAdvisor.KeyColumnName(BuildDesign() with { Name = oldValue ?? "" }, _context);
            if (Same(key.Name, before) || key.Name is "Id" or "id")
            {
                _loading = true;
                key.Name = TableDesignAdvisor.KeyColumnName(BuildDesign(), _context);
                _loading = false;
            }
        }
        Changed();
    }

    /// <summary>Recomputes the script and the suggestions from the rows.</summary>
    private void Refresh()
    {
        var design = BuildDesign();
        Script = TableCreator.Script(design, _context, _original);

        var names = design.Columns.Select(c => c.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SyncColumnNames(names);

        var review = TableDesignAdvisor.Review(design, _context, _original);
        HasErrors = review.Any(s => s.Severity == DesignSeverity.Error);
        Suggestions.Clear();
        foreach (var s in review.Where(s => s.Severity == DesignSeverity.Error || !_dismissed.Contains(s.Key)))
            Suggestions.Add(new DesignSuggestionItem(s));
        var errors = review.Count(s => s.Severity == DesignSeverity.Error);
        var others = Suggestions.Count - errors;
        SuggestionSummary = errors > 0 ? $"{errors} to fix before Execute" + (others > 0 ? $" · {others} suggestion(s)" : "")
            : others > 0 ? $"{others} suggestion(s). Apply them with one click, or dismiss the ones you don't want."
            : IsAltering ? "Nothing to flag in these changes." : "Nothing to suggest. The table looks good.";
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasDismissed));
        OnPropertyChanged(nameof(HasFixable));
        OnPropertyChanged(nameof(HasForeignKeys));
        OnPropertyChanged(nameof(HasIndexes));
        ExecuteCommand.NotifyCanExecuteChanged();
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

    // ----- Editing commands -----

    [RelayCommand]
    private void AddColumn()
    {
        Columns.Add(ColumnRow.From(new ColumnDesign { Type = "" }, Changed, ColumnRenamed));
        Changed();
    }

    [RelayCommand]
    private void RemoveColumn(ColumnRow? row)
    {
        if (row is null) return;
        Columns.Remove(row);
        // The column's keys and indexes go with it, as they do on the server.
        var name = row.Name?.Trim() ?? "";
        if (name.Length > 0)
        {
            foreach (var fk in ForeignKeys.Where(f => Same(f.Column, name)).ToList()) ForeignKeys.Remove(fk);
            foreach (var index in Indexes.Where(i => i.ToDesign().Columns.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList()) Indexes.Remove(index);
        }
        Changed();
    }

    [RelayCommand]
    private void MoveColumnUp(ColumnRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveColumnDown(ColumnRow? row) => Move(row, 1);

    private void Move(ColumnRow? row, int by)
    {
        if (row is null) return;
        var index = Columns.IndexOf(row);
        if (index < 0 || index + by < 0 || index + by >= Columns.Count) return;
        Columns.Move(index, index + by);
        Changed();
    }

    [RelayCommand]
    private void AddForeignKey()
    {
        ForeignKeys.Add(ForeignKeyRow.From(new ForeignKeyDesign(), this));
        Changed();
    }

    [RelayCommand]
    private void RemoveForeignKey(ForeignKeyRow? row)
    {
        if (row is null) return;
        ForeignKeys.Remove(row);
        Changed();
    }

    [RelayCommand]
    private void AddIndex()
    {
        Indexes.Add(IndexRow.From(new IndexDesign(), Changed));
        Changed();
    }

    [RelayCommand]
    private void RemoveIndex(IndexRow? row)
    {
        if (row is null) return;
        Indexes.Remove(row);
        Changed();
    }

    [RelayCommand]
    private void ApplySuggestion(DesignSuggestionItem? item)
    {
        if (item?.Suggestion.Fix is not { } fix) return;
        _edited = true;
        LoadDesign(fix(BuildDesign()));
        Status = $"Applied: {item.Suggestion.FixLabel}.";
    }

    [RelayCommand]
    private void DismissSuggestion(DesignSuggestionItem? item)
    {
        if (item is null || item.Suggestion.Severity == DesignSeverity.Error) return;
        _dismissed.Add(item.Suggestion.Key);
        Refresh();
    }

    [RelayCommand]
    private void ShowDismissed()
    {
        _dismissed.Clear();
        Refresh();
    }

    [RelayCommand]
    private void ApplyAllSuggestions()
    {
        // One at a time: each fix changes the design the next suggestions are made from. Each finding is applied at most
        // once, so two fixes that undo each other cannot loop.
        var applied = new HashSet<string>();
        while (applied.Count < 100)
        {
            var design = BuildDesign();
            var next = TableDesignAdvisor.Review(design, _context, _original)
                .FirstOrDefault(s => s.Fix is not null && !_dismissed.Contains(s.Key) && !applied.Contains(s.Key) &&
                                     !s.Key.StartsWith("alter-", StringComparison.Ordinal));
            if (next is null) break;
            applied.Add(next.Key);
            LoadDesign(next.Fix!(design));
        }
        _edited = true;
        Status = applied.Count == 0 ? "No suggestion has a fix to apply." : $"Applied {applied.Count} suggestion(s). Review the script before you execute it.";
    }

    /// <summary>Reads an existing table from the catalog (and its column defaults from the server) and opens it for
    /// changing: the script becomes the ALTER statements for whatever is edited.</summary>
    public async Task OpenTableAsync(DbObject table)
    {
        if (_session is not { } session) return;
        _opening = true;
        try
        {
            var database = string.IsNullOrEmpty(table.Database) ? SelectedDatabase ?? session.Profile.Database : table.Database;
            if (!Same(SelectedDatabase, database))
            {
                if (!string.IsNullOrEmpty(database) && !Databases.Contains(database, StringComparer.OrdinalIgnoreCase)) Databases = Merge(Databases, [database]);
                SelectedDatabase = Databases.FirstOrDefault(d => Same(d, database)) ?? database;
            }
            Status = $"Reading {table.FullName}…";
            await LoadContextAsync();
            // The session's own catalog first: it is the one Execute refreshes.
            var snapshot = !string.IsNullOrEmpty(database) && session.Snapshot.ContainsDatabase(database) ? session.Snapshot.ForDatabase(database)
                : string.IsNullOrEmpty(database) || Same(database, session.Profile.Database) ? session.Snapshot
                : await sessions.GetDatabaseSnapshotAsync(session, database);
            var found = snapshot.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table && Same(o.Schema, table.Schema) && Same(o.Name, table.Name));
            if (found is null)
            {
                Status = $"{table.FullName} is not a table in {database}, or the catalog does not have it yet.";
                return;
            }
            var constraints = await session.Provider.GetTableConstraintsAsync(found);
            if (!ReferenceEquals(session, _session)) return;
            var loaded = TableDesignLoader.Load(found, snapshot, constraints, ProviderKey);
            _dismissed.Clear();
            LastCreated = null;
            SetOriginal(loaded.Design with { Database = database ?? "" });
            _edited = false;
            LoadDesign(_original!);
            OpenTableName = "";
            var kept = loaded.KeptIndexes.Count + loaded.KeptForeignKeys.Count;
            Status = $"Opened {found.FullName}. Edit it and the ALTER script shows up below." +
                     (kept > 0 ? $" {kept} index(es) or key(s) the designer cannot show stay as they are: {string.Join(", ", loaded.KeptIndexes.Concat(loaded.KeptForeignKeys))}." : "");
        }
        catch (Exception ex)
        {
            Status = $"Could not read {table.FullName}: {ex.Message}";
        }
        finally
        {
            _opening = false;
        }
    }

    [RelayCommand]
    private async Task OpenExistingTableAsync()
    {
        var name = OpenTableName.Trim();
        var table = _context.Tables.FirstOrDefault(t => Same(t.FullName, name)) ?? _context.Tables.FirstOrDefault(t => Same(t.Name, name));
        if (table is null)
        {
            Status = name.Length == 0 ? "Pick a table to open." : $"No table {name} in {SelectedDatabase ?? "this database"}.";
            return;
        }
        await OpenTableAsync(table.Table);
    }

    /// <summary>Undoes every edit of the open table.</summary>
    [RelayCommand]
    private void RevertChanges()
    {
        if (_original is null) return;
        _dismissed.Clear();
        _edited = false;
        LoadDesign(_original);
        Status = "Back to the table as it is on the server.";
    }

    private void SetOriginal(TableDesign? original)
    {
        _original = original;
        ModeText = original is null ? "New table" : $"Changing {original.Schema}.{original.Name}";
        OnPropertyChanged(nameof(IsAltering));
        OnPropertyChanged(nameof(ExecuteLabel));
    }

    [RelayCommand]
    private void NewTable()
    {
        _dismissed.Clear();
        LastCreated = null;
        NewDesign();
        Status = "New table.";
    }

    [RelayCommand]
    private void OpenInQueryTab() => OpenSqlRequested?.Invoke(Script, SelectedDatabase);

    [RelayCommand]
    private async Task CopyScriptAsync()
    {
        await dialogs.CopyTextAsync(Script);
        Status = "Script copied to the clipboard.";
    }

    [RelayCommand]
    private void OpenCreated()
    {
        if (LastCreated is { } table) OpenObjectRequested?.Invoke(table);
    }

    private bool CanExecute => _session is not null && !IsBusy && !HasErrors;

    partial void OnIsBusyChanged(bool value) => ExecuteCommand.NotifyCanExecuteChanged();

    /// <summary>Shows the exact script, asks (typing PRODUCTION on a production connection), then runs it in one
    /// transaction so a failure leaves nothing behind, and re-reads the catalog so the table shows up everywhere.</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (_session is not { } session) return;
        var design = BuildDesign();
        var review = TableDesignAdvisor.Review(design, _context, _original);
        if (review.FirstOrDefault(s => s.Severity == DesignSeverity.Error) is { } error)
        {
            Status = "Fix this first: " + error.Title;
            return;
        }
        if (_original is { } original)
        {
            await AlterAsync(session, original, design, review);
            return;
        }

        var database = SelectedDatabase;
        var schema = TableScriptBuilder.SchemaOf(design, ProviderKey);
        var name = design.Name.Trim();
        var script = Script;
        if (session.Profile.ReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(session.Profile, "CREATE TABLE") + " Open in Query tab still hands the script over.";
            return;
        }
        var production = session.Profile.IsProduction;
        var where = string.IsNullOrEmpty(database) ? "the connection's database" : database;
        var ok = await dialogs.ConfirmAsync(
            $"Create {schema}.{name} in {where}? {RunsInOneTransaction}",
            "Create table", production ? "PRODUCTION" : null,
            production ? $"PRODUCTION · {session.Profile.DisplayName}" : null, script);
        if (!ok) return;

        IsBusy = true;
        Status = $"Creating {schema}.{name}…";
        try
        {
            await TableCreator.CreateAsync(session, design with { Database = database ?? "" }, script, TimeoutSeconds);
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Status = "The server did not create the table, nothing was changed: " + ex.Message;
            return;
        }

        Status = $"Created {schema}.{name}. Reading the catalog so it shows up everywhere…";
        try
        {
            await sessions.RefreshDatabaseSnapshotAsync(session, database);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("table designer refresh", ex);
        }
        finally
        {
            IsBusy = false;
        }

        LastCreated = session.Snapshot.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table &&
            Same(o.Schema, schema) && Same(o.Name, name) &&
            (string.IsNullOrEmpty(o.Database) || Same(o.Database, database ?? "")));
        await LoadContextAsync();
        _dismissed.Clear();
        NewDesign();
        Status = $"Created {schema}.{name} in {where}. The designer is ready for the next table.";
    }

    /// <summary>MySQL commits each DDL statement on its own, so a failure part way leaves the earlier steps applied.</summary>
    private string RunsInOneTransaction => ProviderKey == SqlDialect.MySqlKey
        ? "This runs the script below. MySQL applies each statement as it goes, so if one fails the steps before it stay."
        : "This runs the script below in one transaction.";

    /// <summary>Execute for an open table: the same confirm-then-one-transaction flow as creating, then the table is read
    /// again so the designer shows it as it now is.</summary>
    private async Task AlterAsync(DatabaseSession session, TableDesign original, TableDesign design, IReadOnlyList<DesignSuggestion> review)
    {
        if (TableAlterScriptBuilder.IsUnchanged(original, design, ProviderKey))
        {
            Status = "Nothing to change yet.";
            return;
        }
        var database = SelectedDatabase;
        var schema = TableScriptBuilder.SchemaOf(design, ProviderKey);
        var name = design.Name.Trim();
        var script = Script;
        if (session.Profile.ReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(session.Profile, "ALTER TABLE") + " Open in Query tab still hands the script over.";
            return;
        }
        var production = session.Profile.IsProduction;
        var where = string.IsNullOrEmpty(database) ? "the connection's database" : database;
        var risks = review.Count(s => s.Severity == DesignSeverity.Warning && s.Key.StartsWith("alter-", StringComparison.Ordinal));
        var ok = await dialogs.ConfirmAsync(
            $"Change {original.Schema}.{original.Name} in {where}? {RunsInOneTransaction}" +
            (risks > 0 ? $" {risks} change(s) are flagged in Suggestions as risky for existing data." : ""),
            "Alter table", production ? "PRODUCTION" : null,
            production ? $"PRODUCTION · {session.Profile.DisplayName}" : null, script);
        if (!ok) return;

        IsBusy = true;
        Status = $"Changing {original.Schema}.{original.Name}…";
        try
        {
            await TableCreator.CreateAsync(session, design with { Database = database ?? "" }, script, TimeoutSeconds);
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Status = "The server did not apply the changes, nothing was changed: " + ex.Message;
            return;
        }

        Status = $"Changed {schema}.{name}. Reading the catalog so it shows up everywhere…";
        try
        {
            await sessions.RefreshDatabaseSnapshotAsync(session, database);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("table designer refresh", ex);
        }
        finally
        {
            IsBusy = false;
        }

        await OpenTableAsync(new DbObject { Database = database ?? "", Schema = schema, Name = name, Type = DbObjectType.Table });
        LastCreated = session.Snapshot.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table && Same(o.Schema, schema) && Same(o.Name, name));
        Status = $"Changed {schema}.{name} in {where}. The designer shows the table as it is now.";
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Merge(params IEnumerable<string?>[] lists) =>
        lists.SelectMany(l => l).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    IReadOnlyList<string> IForeignKeyRowOwner.ColumnsOfTable(string fullName, out string? key) => ColumnsOfTable(fullName, out key);
    void IForeignKeyRowOwner.Changed() => Changed();

    internal IReadOnlyList<string> ColumnsOfTable(string fullName, out string? key)
    {
        key = null;
        var table = _context.Tables.FirstOrDefault(t => Same(t.FullName, fullName));
        if (table is null) return [];
        key = table.Key?.Name;
        return table.Columns.Select(c => c.Name).ToList();
    }
}

/// <summary>One editable column row.</summary>
public partial class ColumnRow : ObservableObject
{
    private Action _changed = () => { };
    private Action<string, string> _renamed = (_, _) => { };

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _type = "";
    [ObservableProperty] private string _size = "";
    [ObservableProperty] private bool _isNullable = true;
    [ObservableProperty] private bool _isPrimaryKey;
    [ObservableProperty] private bool _isIdentity;
    [ObservableProperty] private string _default = "";

    /// <summary>The column's name on the server when an existing table is open; null for a column added here.</summary>
    public string? OriginalName { get; private init; }

    /// <summary>"New column", "Renamed from CustomerNo" or "Column on the server", for the name box's tooltip.</summary>
    public string Origin => OriginalName is null ? "New column" : string.Equals(OriginalName, Name?.Trim(), StringComparison.Ordinal)
        ? "Column on the server" : $"Renamed from {OriginalName}";

    public static ColumnRow From(ColumnDesign c, Action changed, Action<string, string>? renamed = null)
    {
        var row = new ColumnRow
        {
            OriginalName = c.OriginalName,
            Name = c.Name, Type = c.Type, Size = c.Size ?? "", IsNullable = c.IsNullable && !c.IsPrimaryKey,
            IsPrimaryKey = c.IsPrimaryKey, IsIdentity = c.IsIdentity, Default = c.Default ?? ""
        };
        row._changed = changed;
        row._renamed = renamed ?? row._renamed;
        return row;
    }

    public ColumnDesign ToDesign() => new()
    {
        Name = Name ?? "", OriginalName = OriginalName, Type = Type ?? "", Size = string.IsNullOrWhiteSpace(Size) ? null : Size.Trim(), IsNullable = IsNullable,
        IsPrimaryKey = IsPrimaryKey, IsIdentity = IsIdentity, Default = string.IsNullOrWhiteSpace(Default) ? null : Default.Trim()
    };

    partial void OnNameChanged(string? oldValue, string newValue)
    {
        OnPropertyChanged(nameof(Origin));
        _renamed(oldValue ?? "", newValue ?? "");
        _changed();
    }

    partial void OnTypeChanged(string value) => _changed();
    partial void OnSizeChanged(string value) => _changed();
    partial void OnIsNullableChanged(bool value) => _changed();
    partial void OnDefaultChanged(string value) => _changed();
    partial void OnIsIdentityChanged(bool value) => _changed();

    partial void OnIsPrimaryKeyChanged(bool value)
    {
        // Key columns are never null; the checkbox says so.
        if (value) IsNullable = false;
        _changed();
    }
}

/// <summary>What a <see cref="ForeignKeyRow"/> picks from: the Table designer's table, or a table of the ER model.</summary>
internal interface IForeignKeyRowOwner
{
    /// <summary>The design's column names.</summary>
    ObservableCollection<string> ColumnNames { get; }

    /// <summary>Tables a key can reference, as schema.table.</summary>
    IReadOnlyList<string> TableNames { get; }

    IReadOnlyList<string> ColumnsOfTable(string fullName, out string? key);

    void Changed();
}

/// <summary>One foreign key row: a column of the new table and the existing table and column it references.</summary>
public partial class ForeignKeyRow : ObservableObject
{
    private IForeignKeyRowOwner? _owner;
    private bool _loading;

    [ObservableProperty] private string? _column;
    [ObservableProperty] private string? _referencedTable;
    [ObservableProperty] private string? _referencedColumn;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private IReadOnlyList<string> _referencedColumnChoices = [];

    public ObservableCollection<string> ColumnChoices => _owner?.ColumnNames ?? [];
    public IReadOnlyList<string> TableChoices => _owner?.TableNames ?? [];

    internal static ForeignKeyRow From(ForeignKeyDesign f, IForeignKeyRowOwner owner)
    {
        var row = new ForeignKeyRow { _owner = owner, _loading = true };
        row.Column = f.Column.Length > 0 ? f.Column : null;
        row.ReferencedTable = f.ReferencedTable.Length > 0 ? $"{f.ReferencedSchema}.{f.ReferencedTable}" : null;
        row.ReferencedColumn = f.ReferencedColumn.Length > 0 ? f.ReferencedColumn : null;
        row.Name = f.Name;
        row.RefreshChoices();
        row._loading = false;
        return row;
    }

    public void RefreshChoices()
    {
        OnPropertyChanged(nameof(TableChoices));
        ReferencedColumnChoices = _owner is null || string.IsNullOrEmpty(ReferencedTable) ? [] : _owner.ColumnsOfTable(ReferencedTable, out _);
    }

    public ForeignKeyDesign ToDesign()
    {
        var table = ReferencedTable ?? "";
        var dot = table.IndexOf('.');
        return new ForeignKeyDesign
        {
            Name = Name ?? "",
            Column = Column ?? "",
            ReferencedSchema = dot < 0 ? "" : table[..dot],
            ReferencedTable = dot < 0 ? table : table[(dot + 1)..],
            ReferencedColumn = ReferencedColumn ?? ""
        };
    }

    partial void OnColumnChanged(string? value) => Changed();
    partial void OnReferencedColumnChanged(string? value) => Changed();
    partial void OnNameChanged(string value) => Changed();

    partial void OnReferencedTableChanged(string? value)
    {
        if (_owner is null) return;
        string? key = null;
        ReferencedColumnChoices = string.IsNullOrEmpty(value) ? [] : _owner.ColumnsOfTable(value, out key);
        // A newly picked table references its primary key unless the user picks another column.
        if (!_loading) ReferencedColumn = key ?? ReferencedColumnChoices.FirstOrDefault();
        Changed();
    }

    private void Changed()
    {
        if (!_loading) _owner?.Changed();
    }
}

/// <summary>One index row; columns are typed as a comma-separated list.</summary>
public partial class IndexRow : ObservableObject
{
    private Action _changed = () => { };

    [ObservableProperty] private string _columns = "";
    [ObservableProperty] private bool _isUnique;
    [ObservableProperty] private string _name = "";

    public static IndexRow From(IndexDesign i, Action changed)
    {
        var row = new IndexRow { Columns = string.Join(", ", i.Columns), IsUnique = i.IsUnique, Name = i.Name };
        row._changed = changed;
        return row;
    }

    public IndexDesign ToDesign() => new()
    {
        Name = Name ?? "",
        IsUnique = IsUnique,
        Columns = (Columns ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    };

    partial void OnColumnsChanged(string value) => _changed();
    partial void OnIsUniqueChanged(bool value) => _changed();
    partial void OnNameChanged(string value) => _changed();
}

/// <summary>A suggestion card.</summary>
public sealed class DesignSuggestionItem(DesignSuggestion suggestion)
{
    public DesignSuggestion Suggestion { get; } = suggestion;
    public string Title => Suggestion.Title;
    public string Detail => Suggestion.Detail;
    /// <summary>The button text, with underscores doubled so "Keep legacy_code" is not read as an access key.</summary>
    public string? FixLabel => Suggestion.FixLabel?.Replace("_", "__");
    public bool CanFix => Suggestion.CanFix;
    public bool CanDismiss => Suggestion.Severity != DesignSeverity.Error;
    public string SeverityText => Suggestion.Severity switch { DesignSeverity.Error => "Fix", DesignSeverity.Warning => "Check", _ => "Tip" };
    public IBrush SeverityColor => Suggestion.Severity switch { DesignSeverity.Error => ErrorBrush, DesignSeverity.Warning => WarningBrush, _ => TipBrush };

    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#D13438"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#E87A00"));
    private static readonly IBrush TipBrush = new SolidColorBrush(Color.Parse("#2B88D8"));
}
