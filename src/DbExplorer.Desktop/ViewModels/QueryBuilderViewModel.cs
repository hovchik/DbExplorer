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
/// is the output, and editing it happens in a Query tab. Nothing runs here.
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

    /// <summary>Raised whenever what the canvas draws changed (tables, joins, ticked columns, selection).</summary>
    public event Action? CanvasChanged;

    /// <summary>Raised to open SQL in a new query tab, against a database.</summary>
    public event Action<string, string?>? OpenSqlRequested;

    private SqlDialect Dialect => SqlDialect.For(_session?.Provider.ProviderKey ?? SqlDialect.SqlServerKey);

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.SnapshotChanged -= OnSnapshotChanged;
        _session = session;
        if (_session is not null) _session.SnapshotChanged += OnSnapshotChanged;
        ClearDesign();
        if (session is null)
        {
            Databases = [];
            SelectedDatabase = null;
            _snapshot = null;
            AvailableTables = [];
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

    private void OnSnapshotChanged(object? sender, EventArgs e) => _ = LoadSnapshotAsync();

    partial void OnSelectedDatabaseChanged(string? value)
    {
        // Tables of one database do not join to another's: start over.
        ClearDesign();
        _ = LoadSnapshotAsync();
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
    }

    // ----- Tables -----

    [RelayCommand]
    private void AddSelectedTable()
    {
        if (SelectedAvailableTable is { } table) AddTable(table, null);
    }

    /// <summary>Places a table (at <paramref name="x"/>,<paramref name="y"/> when dropped there, else right of the
    /// others) and joins it to the tables already placed on their foreign keys.</summary>
    public QueryTableItem? AddTable(DbObject table, double? x, double? y = null)
    {
        if (_snapshot is null) return null;
        var columns = _snapshot.ColumnsOf(table.Database, table.Schema, table.Name).OrderBy(c => c.Ordinal).ToList();
        var fkColumns = _snapshot.ForeignKeysOf(table.Database, table.Schema, table.Name)
            .SelectMany(f => ErDiagramBuilder.SplitColumns(f.Columns)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alias = JoinSuggester.AliasFor(table.Name, Tables.Select(t => t.Alias));
        var position = x is { } px && y is { } py
            ? (X: Math.Max(0, px), Y: Math.Max(0, py))
            : Tables.Count == 0 ? (X: 24.0, Y: 24.0) : (X: Tables.Max(t => t.X + QueryTableItem.Width) + 60, Y: 24.0);

        var item = new QueryTableItem(table, alias, columns.Select(c => new CanvasColumn(c.Name, c.DataType, c.IsPrimaryKey, fkColumns.Contains(c.Name))).ToList())
        {
            X = position.X,
            Y = position.Y
        };
        Tables.Add(item);

        var placed = Tables.Select(t => t.ToModel()).ToList();
        var suggestions = JoinSuggester.Suggest(_snapshot, table.Database, placed, item.ToModel());
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
    partial void OnLimitChanged(decimal value) => Changed();

    // ----- SQL -----

    internal void Changed()
    {
        if (_suspend) return;
        var built = QuerySqlBuilder.Build(BuildDesign(), Dialect);
        Sql = built.Sql;
        Warnings = string.Join("\n", built.Warnings);
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasTables));
        OnPropertyChanged(nameof(HasJoins));
        OnPropertyChanged(nameof(HasColumns));
        OpenInQueryTabCommand.NotifyCanExecuteChanged();
        CanvasChanged?.Invoke();
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

    [RelayCommand]
    private void Clear()
    {
        ClearDesign();
        Status = "Cleared. Drag tables onto the canvas to start again.";
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
    public string Origin => FromForeignKey ? "foreign key" : "";

    partial void OnLeftColumnChanged(string value) { OnPropertyChanged(nameof(Text)); _owner.Changed(); }
    partial void OnRightColumnChanged(string value) { OnPropertyChanged(nameof(Text)); _owner.Changed(); }
    partial void OnKindChanged(JoinKind value) => _owner.JoinKindChanged(this);

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

    partial void OnOutputChanged(bool value) => owner.Changed();
    partial void OnOutputAliasChanged(string value) => owner.Changed();
    partial void OnSortChanged(SortDirection value) => owner.Changed();
    partial void OnFilterChanged(string value) => owner.Changed();

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
