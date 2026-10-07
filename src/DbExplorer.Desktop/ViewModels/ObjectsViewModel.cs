using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
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

    /// <summary>The definition exactly as the server returned it; <see cref="Definition"/> shows the formatted copy
    /// unless <see cref="ShowOriginal"/> is on. Formatting is display only: nothing is sent to the server.</summary>
    [ObservableProperty] private string _originalDefinition = "";
    private string _formattedDefinition = "";

    [ObservableProperty] private bool _showOriginal;
    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private decimal _profileSampleRows = 10_000;
    [ObservableProperty] private IReadOnlyList<ColumnProfile> _columnProfiles = [];
    [ObservableProperty] private ColumnProfile? _selectedColumnProfile;
    [ObservableProperty] private IReadOnlyList<ValueFrequency> _topValues = [];
    [ObservableProperty] private string _profileStatus = "Profiles the first N rows in one read-only scan (lock and statement timeouts apply).";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProfileCommand))]
    private bool _isProfiling;

    private static readonly DataSearchOptions ProfileLimits = new(MaxMatchesPerTable: 0, QueryTimeoutSeconds: 60, LockTimeoutMs: 3000);
    private CancellationTokenSource? _topValuesCts;

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
        ProfileCommand.NotifyCanExecuteChanged();
        ShowInDiagramCommand.NotifyCanExecuteChanged();
        ColumnProfiles = [];
        TopValues = [];
    }

    private bool CanProfile => SelectedObject?.IsTableLike == true && !IsProfiling;

    [RelayCommand(CanExecute = nameof(CanProfile))]
    private async Task ProfileAsync()
    {
        if (_session is null || SelectedObject is not { IsTableLike: true } table) return;

        var columns = _session.Snapshot.ColumnsOf(table.Database, table.Schema, table.Name).OrderBy(c => c.Ordinal).ToList();
        if (columns.Count == 0)
        {
            ProfileStatus = "No columns in the metadata cache for this object; try Refresh metadata.";
            return;
        }

        IsProfiling = true;
        ProfileStatus = "Profiling…";
        TopValues = [];
        try
        {
            var sample = (int)Math.Max(1, ProfileSampleRows);
            var profile = await _session.Provider.ProfileTableAsync(table, columns, sample, ProfileLimits);
            if (!ReferenceEquals(SelectedObject, table)) return;
            ColumnProfiles = profile.Columns;
            ProfileStatus = $"{profile.SampledRows:N0} row(s) profiled in {profile.Elapsed.TotalMilliseconds:N0} ms" +
                            (profile.IsPartial ? $" · first {sample:N0} rows only (sample)" : " · whole table") +
                            " · select a column for its most frequent values";
        }
        catch (Exception ex)
        {
            ProfileStatus = "Error: " + ex.Message;
        }
        finally
        {
            IsProfiling = false;
        }
    }

    partial void OnSelectedColumnProfileChanged(ColumnProfile? value) => _ = LoadTopValuesAsync(value);

    private async Task LoadTopValuesAsync(ColumnProfile? profile)
    {
        _topValuesCts?.Cancel();
        _topValuesCts = new CancellationTokenSource();
        var ct = _topValuesCts.Token;
        TopValues = [];

        if (profile is null || _session is null || SelectedObject is not { IsTableLike: true } table) return;
        var column = _session.Snapshot.ColumnsOf(table.Database, table.Schema, table.Name)
            .FirstOrDefault(c => c.Name == profile.Column);
        if (column is null) return;

        try
        {
            var values = await _session.Provider.GetTopValuesAsync(
                table, column, (int)Math.Max(1, ProfileSampleRows), top: 20, ProfileLimits, ct);
            if (!ct.IsCancellationRequested) TopValues = values;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) TopValues = [new ValueFrequency { Value = "Error: " + ex.Message }];
        }
    }

    /// <summary>Raised when the user asks to see the selected table in the Diagram tab.</summary>
    public event Action<DbObject>? ShowInDiagramRequested;

    [RelayCommand(CanExecute = nameof(CanShowInDiagram))]
    private void ShowInDiagram()
    {
        if (SelectedObject is { } obj) ShowInDiagramRequested?.Invoke(obj);
    }

    private bool CanShowInDiagram => SelectedObject?.Type is DbObjectType.Table or DbObjectType.ForeignTable;

    /// <summary>Clears the filters and selects <paramref name="target"/> (matched by database/schema/name).</summary>
    public void Reveal(DbObject target)
    {
        FilterText = "";
        SelectedTypeFilter = AllTypes;
        SelectedDatabaseFilter = AllDatabases;
        SelectedObject = Objects.FirstOrDefault(o =>
            string.Equals(o.Database, target.Database, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Schema, target.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Name, target.Name, StringComparison.OrdinalIgnoreCase));
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

    partial void OnShowOriginalChanged(bool value) => ShowDefinition();

    private void ShowDefinition() => Definition = ShowOriginal ? OriginalDefinition : _formattedDefinition;

    /// <summary>A status line ("loading…", an error) shown as is, with nothing to format or copy.</summary>
    private void SetDefinitionMessage(string message)
    {
        OriginalDefinition = "";
        _formattedDefinition = message;
        Definition = message;
    }

    private void SetDefinition(string text, DbObjectType type)
    {
        OriginalDefinition = text;
        try
        {
            _formattedDefinition = DefinitionFormatter.Format(text, type);
        }
        catch (Exception)
        {
            _formattedDefinition = text; // formatting is a nicety; never lose the definition over it
        }
        ShowDefinition();
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
            SetDefinitionMessage("");
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
        SetDefinitionMessage("-- loading…");

        try
        {
            var text = await definitions.GetDefinitionAsync(_session, obj, ct);
            if (ct.IsCancellationRequested) return;
            if (text is null) SetDefinitionMessage("-- Definition is not available (encrypted object or insufficient permissions).");
            else SetDefinition(text, obj.Type);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) SetDefinitionMessage("-- Error: " + ex.Message);
        }
    }
}
