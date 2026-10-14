using DbExplorer.Application.Connections;
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
    DefinitionService definitions, QueryExecutionService queryService, SessionService sessions, IDialogService dialogs)
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

    /// <summary>The outcome of the last Modify / Drop from the right-click menu; empty when there is nothing to say.</summary>
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DropTableCommand))]
    [NotifyCanExecuteChangedFor(nameof(ModifyCommand))]
    private bool _isBusy;

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
        DebugSelectedCommand.NotifyCanExecuteChanged();
        GetDataCommand.NotifyCanExecuteChanged();
        ProfileCommand.NotifyCanExecuteChanged();
        ShowInDiagramCommand.NotifyCanExecuteChanged();
        ModifyCommand.NotifyCanExecuteChanged();
        DeleteRowsCommand.NotifyCanExecuteChanged();
        DropTableCommand.NotifyCanExecuteChanged();
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

    /// <summary>Raised when the user asks to change the selected table in the Table designer.</summary>
    public event Action<DbObject>? DesignTableRequested;

    /// <summary>Raised with a script to open in a new Query tab: the SQL, the database to run it in, and the tab title.</summary>
    public event Action<string, string?, string>? OpenSqlRequested;

    private const int DropTimeoutSeconds = 60;

    private bool CanModify => !IsBusy && SelectedObject is { } obj &&
                              (obj.Type == DbObjectType.Table || ObjectScripts.CanModify(obj.Type));

    /// <summary>Tables open in the Table designer; every other object opens in a Query tab as a script that replaces
    /// it (CREATE OR ALTER, CREATE OR REPLACE, ALTER SEQUENCE…). Nothing runs until the user runs that tab.</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ModifyAsync()
    {
        if (_session is not { } session || SelectedObject is not { } obj) return;
        if (obj.Type == DbObjectType.Table)
        {
            DesignTableRequested?.Invoke(obj);
            return;
        }

        Status = "";
        IsBusy = true;
        try
        {
            var texts = await definitions.GetDefinitionsAsync(session, obj);
            if (texts.Count == 0)
            {
                Status = $"The definition of {obj.FullName} is not available (encrypted object or insufficient permissions), so there is nothing to modify.";
                return;
            }
            OpenSqlRequested?.Invoke(ObjectScripts.ModifyScript(session.Provider.ProviderKey, obj, texts),
                NullIfEmpty(obj.Database), "Modify " + obj.Name);
        }
        catch (Exception ex)
        {
            Status = $"Could not read the definition of {obj.FullName}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsTable => SelectedObject?.Type == DbObjectType.Table;

    /// <summary>Opens a DELETE for the table in a Query tab, its WHERE still to fill in; nothing is deleted from here.</summary>
    [RelayCommand(CanExecute = nameof(IsTable))]
    private void DeleteRows()
    {
        if (_session is not { } session || SelectedObject is not { Type: DbObjectType.Table } table) return;
        var columns = session.Snapshot.ColumnsOf(table.Database, table.Schema, table.Name);
        OpenSqlRequested?.Invoke(ObjectScripts.DeleteTemplate(session.Provider.ProviderKey, table, columns),
            NullIfEmpty(table.Database), "Delete " + table.Name);
    }

    private bool CanDropTable => IsTable && !IsBusy;

    /// <summary>Shows the DROP TABLE, asks (typing PRODUCTION on a production connection), runs it, then re-reads the
    /// catalog so the table leaves every list. Read-only connections refuse it before asking.</summary>
    [RelayCommand(CanExecute = nameof(CanDropTable))]
    private async Task DropTableAsync()
    {
        if (_session is not { } session || SelectedObject is not { Type: DbObjectType.Table } table) return;
        var sql = ObjectScripts.DropTable(session.Provider.ProviderKey, table);
        if (session.Profile.ReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(session.Profile, "DROP TABLE");
            return;
        }

        var database = NullIfEmpty(table.Database);
        var where = database ?? "the connection's database";
        var referencing = session.Snapshot.ReferencesTo(table.Database, table.Schema, table.Name)
            .Where(f => !(Same(f.Schema, table.Schema) && Same(f.Table, table.Name)))
            .Select(f => $"{f.Schema}.{f.Table}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = table.RowCount is { } n ? $" and its {n:N0} row(s) (estimated)" : " and all its rows";
        var message = $"Drop {table.FullName} in {where}? This deletes the table{rows}, its indexes and triggers. It cannot be undone." +
                      (referencing.Count > 0
                          ? $"\n\n{string.Join(", ", referencing.Take(5))}{(referencing.Count > 5 ? $" and {referencing.Count - 5} more" : "")} " +
                            "reference it with a foreign key, so the server will refuse until those keys are dropped."
                          : "");
        var production = session.Profile.IsProduction;
        var ok = await dialogs.ConfirmAsync(message, "Drop table", production ? "PRODUCTION" : null,
            production ? $"PRODUCTION · {session.Profile.DisplayName}" : null, sql);
        if (!ok) return;

        IsBusy = true;
        Status = $"Dropping {table.FullName}…";
        try
        {
            await queryService.ExecuteScriptAsync(session, sql, database, DropTimeoutSeconds);
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Status = $"The server did not drop {table.FullName}: {ex.Message}";
            return;
        }

        Status = $"Dropped {table.FullName}. Reading the catalog so it leaves every list…";
        try
        {
            await sessions.RefreshDatabaseSnapshotAsync(session, database);
            Status = $"Dropped {table.FullName} in {where}.";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("objects drop refresh", ex);
            Status = $"Dropped {table.FullName} in {where}. Refresh metadata to update the lists: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

    [RelayCommand(CanExecute = nameof(CanExecuteSelected))]
    private void DebugSelected()
    {
        if (_session is null || SelectedObject is null || !SelectedObject.IsRoutine) return;
        dialogs.ShowRoutineDebugger(_session, SelectedObject);
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
