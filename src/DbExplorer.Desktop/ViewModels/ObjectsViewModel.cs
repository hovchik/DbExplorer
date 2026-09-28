using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

public partial class ObjectsViewModel(
    DefinitionService definitions, QueryExecutionService queryService, IDialogService dialogs)
    : ViewModelBase, ISessionAware
{
    private const string AllTypes = "All types";
    private const string AllDatabases = "All databases";

    private DatabaseSession? _session;
    private CancellationTokenSource? _detailsCts;

    public IReadOnlyList<string> TypeFilters { get; } =
        [AllTypes, .. Enum.GetNames<DbObjectType>()];

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _selectedTypeFilter = AllTypes;
    [ObservableProperty] private IReadOnlyList<string> _databaseFilters = [AllDatabases];
    [ObservableProperty] private string _selectedDatabaseFilter = AllDatabases;
    [ObservableProperty] private IReadOnlyList<DbObject> _objects = [];
    [ObservableProperty] private DbObject? _selectedObject;
    [ObservableProperty] private IReadOnlyList<DbColumn> _columns = [];
    [ObservableProperty] private IReadOnlyList<DbIndex> _indexes = [];
    [ObservableProperty] private IReadOnlyList<DbForeignKey> _outgoingForeignKeys = [];
    [ObservableProperty] private IReadOnlyList<DbForeignKey> _incomingForeignKeys = [];
    [ObservableProperty] private string _definition = "";
    [ObservableProperty] private string _summary = "";

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.SnapshotChanged -= OnSnapshotChanged;
        _session = session;
        if (_session is not null) _session.SnapshotChanged += OnSnapshotChanged;
        SelectedObject = null;
        ApplyFilter();
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSelectedTypeFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedDatabaseFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedObjectChanged(DbObject? value)
    {
        _ = LoadDetailsAsync(value);
        ExecuteSelectedCommand.NotifyCanExecuteChanged();
        GetDataCommand.NotifyCanExecuteChanged();
    }

    public bool CanExecuteSelected => SelectedObject?.IsRoutine == true;

    [RelayCommand(CanExecute = nameof(CanExecuteSelected))]
    private async Task ExecuteSelectedAsync()
    {
        if (_session is null || SelectedObject is null || !SelectedObject.IsRoutine) return;
        await dialogs.ShowRoutineExecutionAsync(queryService, _session, SelectedObject);
    }

    public bool CanGetData => SelectedObject?.IsTableLike == true;

    [RelayCommand(CanExecute = nameof(CanGetData))]
    private async Task GetDataAsync()
    {
        if (_session is null || SelectedObject is null || !SelectedObject.IsTableLike) return;
        await dialogs.ShowGetDataAsync(queryService, _session, SelectedObject);
    }

    private void ApplyFilter()
    {
        if (_session is null)
        {
            Objects = [];
            Summary = "";
            return;
        }

        var snapshot = _session.Snapshot;
        IEnumerable<DbObject> query = snapshot.Objects;

        var databases = snapshot.Objects
            .Select(o => o.Database)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var newDatabaseFilters = (IReadOnlyList<string>)[AllDatabases, .. databases!];
        if (!DatabaseFilters.SequenceEqual(newDatabaseFilters, StringComparer.OrdinalIgnoreCase))
            DatabaseFilters = newDatabaseFilters;
        if (SelectedDatabaseFilter != AllDatabases && !databases.Contains(SelectedDatabaseFilter, StringComparer.OrdinalIgnoreCase))
            SelectedDatabaseFilter = AllDatabases;

        var selectedDatabase = SelectedDatabaseFilter;
        if (selectedDatabase != AllDatabases)
            query = query.Where(o => string.Equals(o.Database, selectedDatabase, StringComparison.OrdinalIgnoreCase));

        if (SelectedTypeFilter != AllTypes && Enum.TryParse<DbObjectType>(SelectedTypeFilter, out var type))
        {
            query = type == DbObjectType.Function
                ? query.Where(o => o.Type is DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction)
                : query.Where(o => o.Type == type);
        }

        var filter = FilterText.Trim();
        if (filter.Length > 0)
            query = query.Where(o => o.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        Objects = query.OrderBy(o => o.Schema).ThenBy(o => o.Name).ToList();
        Summary = $"{Objects.Count:N0} of {snapshot.Objects.Count:N0} objects · metadata from {snapshot.RefreshedAt:g}";
    }

    private async Task LoadDetailsAsync(DbObject? obj)
    {
        _detailsCts?.Cancel();
        _detailsCts = new CancellationTokenSource();
        var ct = _detailsCts.Token;

        if (obj is null || _session is null)
        {
            Columns = [];
            Indexes = [];
            OutgoingForeignKeys = [];
            IncomingForeignKeys = [];
            Definition = "";
            return;
        }

        Columns = obj.IsTableLike
            ? _session.Snapshot.ColumnsOf(obj.Database, obj.Schema, obj.Name).OrderBy(c => c.Ordinal).ToList()
            : [];
        OutgoingForeignKeys = obj.IsTableLike
            ? _session.Snapshot.ForeignKeysOf(obj.Database, obj.Schema, obj.Name).OrderBy(f => f.Name).ToList()
            : [];
        IncomingForeignKeys = obj.IsTableLike
            ? _session.Snapshot.ReferencesTo(obj.Database, obj.Schema, obj.Name).OrderBy(f => f.Table).ToList()
            : [];
        Indexes = obj.IsTableLike
            ? _session.Snapshot.IndexesOf(obj.Database, obj.Schema, obj.Name).OrderBy(i => i.Name).ToList()
            : [];
        Definition = "-- loading…";

        try
        {
            var text = await definitions.GetDefinitionAsync(_session, obj, ct);
            if (!ct.IsCancellationRequested)
                Definition = text ?? "-- Definition is not available (encrypted object or insufficient permissions).";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Definition = "-- Error: " + ex.Message;
        }
    }
}
