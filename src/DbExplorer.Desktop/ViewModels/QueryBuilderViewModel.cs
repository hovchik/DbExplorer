using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Diagram;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.QueryBuilder;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// Builds a SELECT visually: tables dragged onto a canvas (joined on their foreign keys as they arrive), columns ticked
/// into a grid of aliases, aggregates, sorts and filters, and the SQL rewritten on every change. One way only: the SQL
/// is the output, and editing it happens in a Query tab. Nothing runs here: Run opens the SQL in a new Query tab and
/// runs it there. Every change can be undone (the whole design is kept per step, text typed into one cell counting
/// as one step).
/// </summary>
public partial class QueryBuilderViewModel(SessionService sessions) : ViewModelBase, ISessionAware
{
    public const string AllColumns = "*";
    private DatabaseSession? _session;
    private MetadataSnapshot? _snapshot;
    private int _snapshotVersion;
    private bool _suspend;

    [ObservableProperty] private IReadOnlyList<string> _databases = [];
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private string _tableFilter = "";
    [ObservableProperty] private IReadOnlyList<DbObject> _availableTables = [];
    [ObservableProperty] private DbObject? _selectedAvailableTable;

    /// <summary>The table list as shown: tables a foreign key links to the query first, then the rest.</summary>
    [ObservableProperty] private IReadOnlyList<TableChoice> _tableChoices = [];
    [ObservableProperty] private TableChoice? _selectedChoice;
    [ObservableProperty] private bool _distinct;

    /// <summary>TOP / LIMIT; 0 for every row.</summary>
    [ObservableProperty] private decimal _limit = 1000;

    [ObservableProperty] private string _sql = "";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private string _status = "Drag tables from the list onto the canvas, or double-click them.";
    [ObservableProperty] private JoinItem? _selectedJoin;
    [ObservableProperty] private QueryTableItem? _selectedTable;

    public ObservableCollection<QueryTableItem> Tables { get; } = [];
    public ObservableCollection<JoinItem> Joins { get; } = [];
    public ObservableCollection<ColumnItem> Columns { get; } = [];

    // Static, so the row combo boxes have their items before their selection is bound.
    public static IReadOnlyList<JoinKind> JoinKinds { get; } = Enum.GetValues<JoinKind>();
    public static IReadOnlyList<ColumnAggregate> Aggregates { get; } = Enum.GetValues<ColumnAggregate>();
    public static IReadOnlyList<SortDirection> SortDirections { get; } = Enum.GetValues<SortDirection>();

    public bool HasTables => Tables.Count > 0;
    public bool HasJoins => Joins.Count > 0;
    public bool HasColumns => Columns.Count > 0;
    public bool HasWarnings => Warnings.Length > 0;
    public bool HasMultipleDatabases => Databases.Count > 1;
    public bool HasRelatedTables => TableChoices.Any(c => c.LinksTo is not null);
    public int OutputColumnCount => Columns.Count(c => c.Output);

    /// <summary>"2 tables · 1 join · 3 columns" under the canvas.</summary>
    public string Summary => Tables.Count == 0 ? "" : string.Join(" · ",
        Plural(Tables.Count, "table"),
        Plural(Joins.Count, "join condition"),
        OutputColumnCount == 0 ? "all columns" : Plural(OutputColumnCount, "column"));

    public string ColumnsHeader => Columns.Count == 0 ? "Columns" : $"Columns ({Columns.Count})";
    public string JoinsHeader => Joins.Count == 0 ? "Joins" : $"Joins ({Joins.Count})";

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>Raised whenever what the canvas draws changed (tables, joins, ticked columns, selection).</summary>
    public event Action? CanvasChanged;

    /// <summary>Raised to open SQL in a new query tab, against a database.</summary>
    public event Action<string, string?>? OpenSqlRequested;

    /// <summary>Raised to open SQL in a new query tab, against a database, and run it there.</summary>
    public event Action<string, string?>? RunSqlRequested;

    private SqlDialect Dialect => SqlDialect.For(_session?.Provider.ProviderKey ?? SqlDialect.SqlServerKey);

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.SnapshotChanged -= OnSnapshotChanged;
        _session = session;
        if (_session is not null) _session.SnapshotChanged += OnSnapshotChanged;
        ClearDesign();
        ClearTables();
        ResetHistory();
        if (session is null)
        {
            Databases = [];
            SelectedDatabase = null;
            return;
        }

        Databases = session.Snapshot.Databases
            .Concat(string.IsNullOrEmpty(session.Profile.Database) ? [] : [session.Profile.Database])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var preferred = Databases.FirstOrDefault(d => Same(d, session.Profile.Database)) ?? Databases.FirstOrDefault();
        if (SelectedDatabase == preferred) _ = LoadSnapshotAsync();
        else SelectedDatabase = preferred;
    }

    partial void OnDatabasesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasMultipleDatabases));
    partial void OnAvailableTablesChanged(IReadOnlyList<DbObject> value) => OnPropertyChanged(nameof(AvailableCount));

    public int AvailableCount => AvailableTables.Count;

    private void OnSnapshotChanged(object? sender, EventArgs e) => _ = LoadSnapshotAsync();

    partial void OnSelectedDatabaseChanged(string? value)
    {
        // Tables of one database do not join to another's: start over, without offering the previous database's
        // tables while the new one loads.
        ClearDesign();
        ClearTables();
        ResetHistory();
        _ = LoadSnapshotAsync();
    }

    private void ClearTables()
    {
        _snapshot = null;
        AvailableTables = [];
        TableChoices = [];
    }

    private async Task LoadSnapshotAsync()
    {
        if (_session is not { } session) return;
        var version = ++_snapshotVersion;
        var database = SelectedDatabase;
        try
        {
            if (!string.IsNullOrEmpty(database) && !session.Snapshot.ContainsDatabase(database)) Status = $"Reading the tables of {database}…";
            var snapshot = string.IsNullOrEmpty(database) ? session.Snapshot : await sessions.GetDatabaseSnapshotAsync(session, database);
            if (version != _snapshotVersion || !ReferenceEquals(session, _session)) return;
            _snapshot = snapshot;
            FilterTables();
            if (Tables.Count == 0) Status = $"{AvailableTables.Count:N0} table(s) and view(s). Drag them onto the canvas, or double-click them.";
        }
        catch (Exception ex)
        {
            if (version == _snapshotVersion) Status = $"Could not read the tables of {database}: {ex.Message}";
        }
    }

    partial void OnTableFilterChanged(string value) => FilterTables();

    private void FilterTables()
    {
        var filter = TableFilter.Trim();
        AvailableTables = (_snapshot?.Objects ?? [])
            .Where(o => o.IsTableLike)
            .Where(o => filter.Length == 0 || o.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Type is DbObjectType.Table or DbObjectType.ForeignTable ? 0 : 1)
            .ThenBy(o => o.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        UpdateChoices();
    }

    /// <summary>Rebuilds the shown list: related tables (a foreign key links them to a placed table) first.</summary>
    private void UpdateChoices()
    {
        if (_snapshot is not { } snapshot)
        {
            TableChoices = [];
            return;
        }
        var selected = SelectedChoice?.Object;
        var related = Tables.Count == 0 || AvailableTables.Count == 0
            ? new Dictionary<(string, string), string>()
            : JoinSuggester.Related(snapshot, SelectedDatabase ?? AvailableTables[0].Database, Tables.Select(t => t.ToModel()).ToList());
        var placed = Tables.GroupBy(t => (t.Object.Schema.ToLowerInvariant(), t.Object.Name.ToLowerInvariant())).ToDictionary(g => g.Key, g => g.Count());
        TableChoices = AvailableTables
            .Select(o =>
            {
                var linksTo = related.TryGetValue((o.Schema, o.Name), out var alias) ? alias : null;
                var count = placed.GetValueOrDefault((o.Schema.ToLowerInvariant(), o.Name.ToLowerInvariant()));
                return new TableChoice(o, linksTo, count);
            })
            .OrderBy(c => c.LinksTo is null ? 1 : 0)
            .ToList();
        SelectedChoice = selected is null ? null : TableChoices.FirstOrDefault(c => c.Object == selected);
        OnPropertyChanged(nameof(HasRelatedTables));
    }

    partial void OnSelectedChoiceChanged(TableChoice? value) => SelectedAvailableTable = value?.Object;

    // ----- Tables -----

    [RelayCommand]
    private void AddSelectedTable()
    {
        if (SelectedAvailableTable is { } table) AddTable(table, null);
    }

    /// <summary>Places a table (at <paramref name="x"/>,<paramref name="y"/> when dropped there, else beside the table
    /// it joins to, or right of the others) and joins it to the tables already placed on their foreign keys.</summary>
    public QueryTableItem? AddTable(DbObject table, double? x, double? y = null)
    {
        if (_snapshot is null) return null;
        var columns = _snapshot.ColumnsOf(table.Database, table.Schema, table.Name).OrderBy(c => c.Ordinal).ToList();
        var fkColumns = _snapshot.ForeignKeysOf(table.Database, table.Schema, table.Name)
            .SelectMany(f => ErDiagramBuilder.SplitColumns(f.Columns)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alias = JoinSuggester.AliasFor(table.Name, Tables.Select(t => t.Alias));
        var item = new QueryTableItem(table, alias, columns.Select(c => new CanvasColumn(c.Name, c.DataType, c.IsPrimaryKey, fkColumns.Contains(c.Name))).ToList());

        var placed = Tables.Select(t => t.ToModel()).Append(item.ToModel()).ToList();
        var suggestions = JoinSuggester.Suggest(_snapshot, table.Database, placed, item.ToModel());
        var position = x is { } px && y is { } py
            ? (X: Math.Max(0, px), Y: Math.Max(0, py))
            : FreeSpotBeside(suggestions.SelectMany(s => s.Conditions).Select(c => Same(c.LeftAlias, alias) ? c.RightAlias : c.LeftAlias).FirstOrDefault(), item.Height);
        item.X = position.X;
        item.Y = position.Y;
        Tables.Add(item);

        foreach (var s in suggestions)
            foreach (var c in s.Conditions)
                Joins.Add(new JoinItem(this, c.LeftAlias, c.LeftColumn, c.RightAlias, c.RightColumn, c.Kind, s.FromForeignKey));

        SelectedTable = item;
        Status = suggestions.Count switch
        {
            _ when Tables.Count == 1 => $"Added {table.FullName} as {alias}. Click columns to select them.",
            0 => $"Added {table.FullName} as {alias}. No foreign key links it to the other tables: drag a column onto another table's column to join them.",
            _ => $"Added {table.FullName} as {alias}, joined on " + string.Join("; ", suggestions.Select(s => s.Reason)) + "."
        };
        Changed();
        return item;
    }

    /// <summary>Right of the table <paramref name="alias"/> (below whatever is already there), else right of every table.</summary>
    private (double X, double Y) FreeSpotBeside(string? alias, double height)
    {
        if (Tables.Count == 0) return (QueryLayout.Margin, QueryLayout.Margin);
        var x = Tables.FirstOrDefault(t => Same(t.Alias, alias)) is { } anchor
            ? anchor.X + QueryTableItem.Width + QueryLayout.ColumnGap
            : Tables.Max(t => t.X + QueryTableItem.Width) + QueryLayout.ColumnGap;
        var y = QueryLayout.Margin;
        // Take the first gap in that column tall enough for the box.
        foreach (var t in Tables.Where(t => t.X < x + QueryTableItem.Width && t.X + QueryTableItem.Width > x).OrderBy(t => t.Y))
        {
            if (t.Y >= y + height + QueryLayout.RowGap) break;
            y = Math.Max(y, t.Y + t.Height + QueryLayout.RowGap);
        }
        return (x, y);
    }

    /// <summary>Tables a foreign key links to <paramref name="table"/>, for its "Add related" menu.</summary>
    public IReadOnlyList<DbObject> RelatedTo(QueryTableItem table)
    {
        if (_snapshot is not { } snapshot) return [];
        var related = JoinSuggester.Related(snapshot, table.Object.Database, [table.ToModel()]);
        return (snapshot.Objects)
            .Where(o => o.IsTableLike && related.ContainsKey((o.Schema, o.Name)))
            .OrderBy(o => o.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Lays the tables out in columns that follow the joins (first table on the left).</summary>
    [RelayCommand]
    private void Arrange()
    {
        if (Tables.Count == 0) return;
        var spots = QueryLayout.Arrange(Tables.Select(t => new LayoutBox(t.Alias, t.Height)).ToList(),
            Joins.Select(j => (j.LeftAlias, j.RightAlias)), QueryTableItem.Width);
        foreach (var t in Tables) (t.X, t.Y) = spots[t.Alias];
        Status = "Tables arranged along their joins.";
        Changed();
    }

    [RelayCommand]
    public void RemoveTable(QueryTableItem? table)
    {
        table ??= SelectedTable;
        if (table is null) return;
        Tables.Remove(table);
        foreach (var j in Joins.Where(j => j.Involves(table.Alias)).ToList()) Joins.Remove(j);
        foreach (var c in Columns.Where(c => Same(c.TableAlias, table.Alias)).ToList()) Columns.Remove(c);
        if (SelectedTable == table) SelectedTable = null;
        Status = $"Removed {table.Object.FullName}.";
        Changed();
    }

    public void MoveTable(QueryTableItem table, double x, double y)
    {
        table.X = Math.Max(0, x);
        table.Y = Math.Max(0, y);
        CanvasChanged?.Invoke();
    }

    /// <summary>A drag of a table ended: one undo step for the whole move.</summary>
    public void EndMove() => Record(null);

    // ----- Columns -----

    /// <summary>Is <paramref name="column"/> of <paramref name="table"/> in the SELECT list (drawn ticked)?</summary>
    public bool IsOutput(QueryTableItem table, string column) =>
        Columns.Any(c => c.Output && Same(c.TableAlias, table.Alias) && Same(c.Column, column));

    /// <summary>Canvas click on a column: ticks it into the grid, or unticks it (a row that also filters, sorts or
    /// aggregates stays, as a non-output row, so that work is not lost).</summary>
    public void ToggleColumn(QueryTableItem table, string column)
    {
        var rows = Columns.Where(c => Same(c.TableAlias, table.Alias) && Same(c.Column, column)).ToList();
        var output = rows.FirstOrDefault(r => r.Output);
        _suspend = true;
        try
        {
            if (output is null)
            {
                var hidden = rows.FirstOrDefault();
                if (hidden is not null) hidden.Output = true;
                else Columns.Add(new ColumnItem(this, table.Alias, column));
            }
            else if (output.IsPlain) Columns.Remove(output);
            else output.Output = false;
        }
        finally
        {
            _suspend = false;
        }
        Changed();
    }

    /// <summary>Ticks every column of <paramref name="table"/> into the grid (in table order), or unticks them all
    /// (rows that also filter, sort or aggregate stay, as non-output rows).</summary>
    public void SetAllColumns(QueryTableItem table, bool output)
    {
        _suspend = true;
        try
        {
            foreach (var column in table.Columns)
            {
                var rows = Columns.Where(c => Same(c.TableAlias, table.Alias) && Same(c.Column, column.Name)).ToList();
                if (output)
                {
                    if (rows.Any(r => r.Output)) continue;
                    if (rows.FirstOrDefault() is { } hidden) hidden.Output = true;
                    else Columns.Add(new ColumnItem(this, table.Alias, column.Name));
                }
                else
                {
                    foreach (var r in rows.Where(r => r.Output))
                        if (r.IsPlain) Columns.Remove(r);
                        else r.Output = false;
                }
            }
            if (!output)
                foreach (var star in Columns.Where(c => Same(c.TableAlias, table.Alias) && c.Column == AllColumns && c.Output).ToList())
                    if (star.IsPlain) Columns.Remove(star);
                    else star.Output = false;
        }
        finally
        {
            _suspend = false;
        }
        Status = output ? $"Selected every column of {table.Alias}." : $"Cleared the columns of {table.Alias}.";
        Changed();
    }

    [RelayCommand]
    private void RemoveColumn(ColumnItem? column)
    {
        if (column is null) return;
        Columns.Remove(column);
        Changed();
    }

    [RelayCommand]
    private void MoveColumnUp(ColumnItem? column) => MoveColumn(column, -1);

    [RelayCommand]
    private void MoveColumnDown(ColumnItem? column) => MoveColumn(column, 1);

    private void MoveColumn(ColumnItem? column, int delta)
    {
        if (column is null) return;
        var index = Columns.IndexOf(column);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Columns.Count) return;
        Columns.Move(index, target);
        Changed();
    }

    // ----- Joins -----

    /// <summary>A column dragged onto another table's column: joins them (inner), unless that pair is joined already.</summary>
    public void AddJoin(QueryTableItem left, string leftColumn, QueryTableItem right, string rightColumn)
    {
        if (left == right || leftColumn == AllColumns || rightColumn == AllColumns) return;
        if (Joins.Any(j => j.Matches(left.Alias, leftColumn, right.Alias, rightColumn))) return;
        var kind = Joins.FirstOrDefault(j => j.Involves(left.Alias) && j.Involves(right.Alias)) is { } same
            ? Same(same.LeftAlias, left.Alias) ? same.Kind : QuerySqlBuilder.Flip(same.Kind)
            : JoinKind.Inner;
        var join = new JoinItem(this, left.Alias, leftColumn, right.Alias, rightColumn, kind, false);
        Joins.Add(join);
        SelectedJoin = join;
        Status = $"Joined {join.Text}.";
        Changed();
    }

    [RelayCommand]
    public void RemoveJoin(JoinItem? join)
    {
        join ??= SelectedJoin;
        if (join is null) return;
        Joins.Remove(join);
        if (SelectedJoin == join) SelectedJoin = null;
        Changed();
    }

    /// <summary>The kind belongs to the pair of tables (one ON clause): changing one condition changes its siblings.</summary>
    internal void JoinKindChanged(JoinItem join)
    {
        if (_suspend) return;
        _suspend = true;
        try
        {
            foreach (var other in Joins.Where(j => j != join && j.Involves(join.LeftAlias) && j.Involves(join.RightAlias)))
                other.Kind = Same(other.LeftAlias, join.LeftAlias) ? join.Kind : QuerySqlBuilder.Flip(join.Kind);
        }
        finally
        {
            _suspend = false;
        }
        Changed();
    }

    public IReadOnlyList<string> ColumnsOf(string alias) =>
        Tables.FirstOrDefault(t => Same(t.Alias, alias))?.Columns.Select(c => c.Name).ToList() ?? [];

    partial void OnSelectedJoinChanged(JoinItem? value) => CanvasChanged?.Invoke();
    partial void OnSelectedTableChanged(QueryTableItem? value) => CanvasChanged?.Invoke();
    partial void OnDistinctChanged(bool value) => Changed();
    partial void OnLimitChanged(decimal value) => Changed("limit");

    /// <summary>Sets a join's kind from the canvas menu (its siblings between the same two tables follow).</summary>
    public void SetJoinKind(JoinItem join, JoinKind kind)
    {
        join.Kind = kind;
        Status = $"{join.LeftAlias} {QuerySqlBuilder.JoinKeywordFor(kind)} {join.RightAlias}: {join.Description}.";
    }

    // ----- SQL -----

    /// <summary>Rebuilds the SQL after a change and records an undo step. Changes with the same
    /// <paramref name="coalesce"/> key in a row (typing into one cell) make one step.</summary>
    internal void Changed(string? coalesce = null)
    {
        if (_suspend) return;
        Refresh();
        Record(coalesce);
    }

    private void Refresh()
    {
        var built = QuerySqlBuilder.Build(BuildDesign(), Dialect);
        Sql = built.Sql;
        Warnings = string.Join("\n", built.Warnings);
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasTables));
        OnPropertyChanged(nameof(HasJoins));
        OnPropertyChanged(nameof(HasColumns));
        OnPropertyChanged(nameof(OutputColumnCount));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ColumnsHeader));
        OnPropertyChanged(nameof(JoinsHeader));
        OpenInQueryTabCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
        CopySqlCommand.NotifyCanExecuteChanged();
        ArrangeCommand.NotifyCanExecuteChanged();
        UpdateChoices();
        CanvasChanged?.Invoke();
    }

    // ----- Undo -----

    private readonly Stack<BuilderState> _undo = new();
    private readonly Stack<BuilderState> _redo = new();
    private string? _lastCoalesce;
    private bool _restoring;

    public bool CanUndo => _undo.Count > 1;
    public bool CanRedo => _redo.Count > 0;

    private void Record(string? coalesce)
    {
        if (_restoring || _suspend) return;
        var state = BuilderState.Capture(this);
        if (_undo.TryPeek(out var top) && top.Key == state.Key)
        {
            // Nothing new to record, but a change outside the group still ends it (an aggregate sets its default
            // alias under the alias's key; the alias typed next must be a step of its own).
            _lastCoalesce = coalesce;
            return;
        }
        if (coalesce is not null && coalesce == _lastCoalesce && _undo.Count > 1) _undo.Pop();
        _undo.Push(state);
        _lastCoalesce = coalesce;
        _redo.Clear();
        HistoryChanged();
    }

    private void ResetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _lastCoalesce = null;
        _undo.Push(BuilderState.Capture(this));
        HistoryChanged();
    }

    private void HistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo()
    {
        if (_undo.Count < 2) return;
        _redo.Push(_undo.Pop());
        Restore(_undo.Peek());
        Status = "Undone.";
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo()
    {
        if (!_redo.TryPop(out var state)) return;
        _undo.Push(state);
        Restore(state);
        Status = "Redone.";
    }

    private void Restore(BuilderState state)
    {
        _restoring = true;
        _suspend = true;
        try
        {
            Tables.Clear();
            foreach (var (table, x, y) in state.Tables)
            {
                table.X = x;
                table.Y = y;
                Tables.Add(table);
            }
            Joins.Clear();
            foreach (var j in state.Joins) Joins.Add(new JoinItem(this, j.LeftAlias, j.LeftColumn, j.RightAlias, j.RightColumn, j.Kind, j.FromForeignKey));
            Columns.Clear();
            foreach (var c in state.Columns)
            {
                var item = new ColumnItem(this, c.TableAlias, c.Column) { Aggregate = c.Aggregate };
                item.OutputAlias = c.OutputAlias;
                item.Output = c.Output;
                item.Sort = c.Sort;
                item.Filter = c.Filter;
                Columns.Add(item);
            }
            Distinct = state.Distinct;
            Limit = state.Limit;
            if (SelectedTable is not null && !Tables.Contains(SelectedTable)) SelectedTable = null;
            SelectedJoin = null;
        }
        finally
        {
            _suspend = false;
        }
        Refresh();
        _restoring = false;
        _lastCoalesce = null;
        HistoryChanged();
    }

    /// <summary>The whole design at one step of the undo history; <see cref="Key"/> tells two steps apart.</summary>
    private sealed record BuilderState(
        IReadOnlyList<(QueryTableItem Table, double X, double Y)> Tables,
        IReadOnlyList<(string LeftAlias, string LeftColumn, string RightAlias, string RightColumn, JoinKind Kind, bool FromForeignKey)> Joins,
        IReadOnlyList<QueryColumn> Columns, bool Distinct, decimal Limit, string Key)
    {
        public static BuilderState Capture(QueryBuilderViewModel vm)
        {
            var tables = vm.Tables.Select(t => (t, t.X, t.Y)).ToList();
            var joins = vm.Joins.Select(j => (j.LeftAlias, j.LeftColumn, j.RightAlias, j.RightColumn, j.Kind, j.FromForeignKey)).ToList();
            var columns = vm.Columns.Select(c => c.ToModel()).ToList();
            var key = string.Join("|", tables.Select(t => $"{t.Item1.Alias}@{t.Item2:0},{t.Item3:0}")) + "#" +
                      string.Join("|", joins) + "#" + string.Join("|", columns) + "#" + vm.Distinct + "#" + vm.Limit;
            return new BuilderState(tables, joins, columns, vm.Distinct, vm.Limit, key);
        }
    }

    public QueryDesign BuildDesign() => new()
    {
        Tables = Tables.Select(t => t.ToModel()).ToList(),
        Joins = Joins.Select(j => j.ToModel()).ToList(),
        Columns = Columns.Select(c => c.ToModel()).ToList(),
        Distinct = Distinct,
        Limit = (int)Math.Clamp(Limit, 0, int.MaxValue)
    };

    private bool CanOpen => Sql.Length > 0;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void OpenInQueryTab() => OpenSqlRequested?.Invoke(Sql, SelectedDatabase);

    /// <summary>Opens the SQL in a new Query tab and runs it there (the builder itself never runs anything).</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Run() => RunSqlRequested?.Invoke(Sql, SelectedDatabase);

    /// <summary>Asks the view to put the SQL on the clipboard (the view owns the clipboard).</summary>
    public event Action<string>? CopyRequested;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void CopySql()
    {
        CopyRequested?.Invoke(Sql);
        Status = "SQL copied to the clipboard.";
    }

    [RelayCommand]
    private void Clear()
    {
        ClearDesign();
        Status = "Cleared. Undo (Ctrl+Z) brings the design back.";
    }

    private void ClearDesign()
    {
        Tables.Clear();
        Joins.Clear();
        Columns.Clear();
        SelectedJoin = null;
        SelectedTable = null;
        Changed();
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A column as the canvas draws it.</summary>
public sealed record CanvasColumn(string Name, string DataType, bool IsPrimaryKey, bool IsForeignKey);

/// <summary>A table placed on the canvas: where it is and what it holds. The first row is <c>*</c> (all columns).</summary>
public sealed class QueryTableItem(DbObject obj, string alias, IReadOnlyList<CanvasColumn> columns)
{
    public const double Width = 240;
    public const double HeaderHeight = 30;
    public const double RowHeight = 22;
    public const double Padding = 4;

    public DbObject Object { get; } = obj;
    public string Alias { get; } = alias;
    public IReadOnlyList<CanvasColumn> Columns { get; } = columns;
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>The rows drawn: <c>*</c> first, then the columns.</summary>
    public int RowCount => Columns.Count + 1;
    public double Height => HeaderHeight + Padding * 2 + RowCount * RowHeight;

    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    /// <summary>The row at canvas y: -1 for the header, 0 for <c>*</c>, then 1.. for the columns; null outside.</summary>
    public int? RowAt(double y)
    {
        if (y < Y || y > Y + Height) return null;
        if (y < Y + HeaderHeight) return -1;
        var row = (int)Math.Floor((y - Y - HeaderHeight - Padding) / RowHeight);
        return Math.Clamp(row, 0, RowCount - 1);
    }

    public string ColumnAtRow(int row) => row <= 0 ? QueryBuilderViewModel.AllColumns : Columns[row - 1].Name;

    /// <summary>Vertical middle of a column's row, for join lines.</summary>
    public double RowCenter(string column)
    {
        var index = column == QueryBuilderViewModel.AllColumns ? 0
            : Columns.ToList().FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)) + 1;
        return index < 0 ? Y + HeaderHeight / 2 : Y + HeaderHeight + Padding + index * RowHeight + RowHeight / 2;
    }

    public string Title => $"{Object.FullName}  {Alias}";

    public QueryTable ToModel() => new(Alias, Object.Schema, Object.Name);
}

/// <summary>A join condition as the joins list edits it.</summary>
public partial class JoinItem : ObservableObject
{
    private readonly QueryBuilderViewModel _owner;

    public JoinItem(QueryBuilderViewModel owner, string leftAlias, string leftColumn, string rightAlias, string rightColumn, JoinKind kind, bool fromForeignKey)
    {
        _owner = owner;
        LeftAlias = leftAlias;
        RightAlias = rightAlias;
        _leftColumn = leftColumn;
        _rightColumn = rightColumn;
        _kind = kind;
        FromForeignKey = fromForeignKey;
    }

    public string LeftAlias { get; }
    public string RightAlias { get; }
    public bool FromForeignKey { get; }

    [ObservableProperty] private string _leftColumn;
    [ObservableProperty] private string _rightColumn;
    [ObservableProperty] private JoinKind _kind;

    public IReadOnlyList<string> LeftColumns => _owner.ColumnsOf(LeftAlias);
    public IReadOnlyList<string> RightColumns => _owner.ColumnsOf(RightAlias);
    public string Text => $"{LeftAlias}.{LeftColumn} = {RightAlias}.{RightColumn}";
    public string Origin => FromForeignKey ? "foreign key" : "drawn";

    /// <summary>Which rows the kind keeps, in words.</summary>
    public string Description => Kind switch
    {
        JoinKind.Left => $"every {LeftAlias} row, with {RightAlias} where it matches",
        JoinKind.Right => $"every {RightAlias} row, with {LeftAlias} where it matches",
        JoinKind.Full => $"every row of {LeftAlias} and {RightAlias}, matched where they can be",
        _ => $"only rows with a match in both {LeftAlias} and {RightAlias}"
    };

    partial void OnLeftColumnChanged(string value) { OnPropertyChanged(nameof(Text)); _owner.Changed(); }
    partial void OnRightColumnChanged(string value) { OnPropertyChanged(nameof(Text)); _owner.Changed(); }
    partial void OnKindChanged(JoinKind value)
    {
        OnPropertyChanged(nameof(Description));
        _owner.JoinKindChanged(this);
    }

    public bool Involves(string alias) =>
        string.Equals(LeftAlias, alias, StringComparison.OrdinalIgnoreCase) || string.Equals(RightAlias, alias, StringComparison.OrdinalIgnoreCase);

    public bool Matches(string leftAlias, string leftColumn, string rightAlias, string rightColumn)
    {
        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return Eq(LeftAlias, leftAlias) && Eq(LeftColumn, leftColumn) && Eq(RightAlias, rightAlias) && Eq(RightColumn, rightColumn) ||
               Eq(LeftAlias, rightAlias) && Eq(LeftColumn, rightColumn) && Eq(RightAlias, leftAlias) && Eq(RightColumn, leftColumn);
    }

    public JoinCondition ToModel() => new(LeftAlias, LeftColumn, RightAlias, RightColumn, Kind);
}

/// <summary>A row of the column grid.</summary>
public partial class ColumnItem(QueryBuilderViewModel owner, string tableAlias, string column) : ObservableObject
{
    public string TableAlias { get; } = tableAlias;
    public string Column { get; } = column;
    public string Source => $"{TableAlias}.{Column}";

    [ObservableProperty] private bool _output = true;
    [ObservableProperty] private string _outputAlias = "";
    [ObservableProperty] private ColumnAggregate _aggregate;
    [ObservableProperty] private SortDirection _sort;
    [ObservableProperty] private string _filter = "";

    /// <summary>Only selected: nothing filters, sorts, aggregates or renames it.</summary>
    public bool IsPlain => OutputAlias.Length == 0 && Aggregate == ColumnAggregate.None && Sort == SortDirection.None && Filter.Trim().Length == 0;

    // Typing into one cell is one undo step.
    private readonly string _key = Guid.NewGuid().ToString("N");

    partial void OnOutputChanged(bool value) => owner.Changed();
    partial void OnOutputAliasChanged(string value) => owner.Changed("alias:" + _key);
    partial void OnSortChanged(SortDirection value)
    {
        OnPropertyChanged(nameof(SortText));
        OnPropertyChanged(nameof(SortTip));
        owner.Changed();
    }

    /// <summary>The sort button's face: none, ascending, descending.</summary>
    public string SortText => Sort switch
    {
        SortDirection.Ascending => "↑ Asc",
        SortDirection.Descending => "↓ Desc",
        _ => "—"
    };

    public string SortTip => Sort switch
    {
        SortDirection.Ascending => "Sorted ascending. Click for descending.",
        SortDirection.Descending => "Sorted descending. Click to stop sorting by it.",
        _ => "Not sorted. Click to sort ascending."
    };

    /// <summary>None → ascending → descending → none.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CycleSort() => Sort = Sort switch
    {
        SortDirection.None => SortDirection.Ascending,
        SortDirection.Ascending => SortDirection.Descending,
        _ => SortDirection.None
    };
    partial void OnFilterChanged(string value) => owner.Changed("filter:" + _key);

    /// <summary>An aggregate gets a readable result name (count_OrderId) unless one was typed.</summary>
    partial void OnAggregateChanged(ColumnAggregate oldValue, ColumnAggregate newValue)
    {
        var oldDefault = DefaultAlias(oldValue);
        if (OutputAlias.Length == 0 || OutputAlias == oldDefault) OutputAlias = DefaultAlias(newValue);
        owner.Changed();
    }

    private string DefaultAlias(ColumnAggregate aggregate)
    {
        if (aggregate == ColumnAggregate.None) return "";
        var prefix = aggregate == ColumnAggregate.CountDistinct ? "count_distinct" : aggregate.ToString().ToLowerInvariant();
        return Column == QueryBuilderViewModel.AllColumns ? "row_count" : $"{prefix}_{Column}".Replace(' ', '_');
    }

    public QueryColumn ToModel() => new()
    {
        TableAlias = TableAlias,
        Column = Column,
        Output = Output,
        OutputAlias = OutputAlias,
        Aggregate = Aggregate,
        Sort = Sort,
        Filter = Filter
    };
}

/// <summary>A row of the table list: the table, the alias of the placed table a foreign key links it to, and how many
/// copies are on the canvas.</summary>
public sealed record TableChoice(DbObject Object, string? LinksTo, int OnCanvas)
{
    public string FullName => Object.FullName;
    public DbObjectType Type => Object.Type;

    /// <summary>"joins o", "on canvas", or nothing.</summary>
    public string Hint => LinksTo is not null ? $"joins {LinksTo}" : OnCanvas > 0 ? "on canvas" : "";

    public bool HasHint => Hint.Length > 0;
    public bool IsRelated => LinksTo is not null;

    public string Tip => LinksTo is not null
        ? $"{Object.FullName}: a foreign key links it to {LinksTo}. Double-click or press + to add it, joined."
        : $"{Object.FullName} ({Object.Type}). Double-click, press +, or drag it onto the canvas.";
}
