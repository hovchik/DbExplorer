using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Compare;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// Compares a schema object (or its data) between two connections, which may target the same
/// server/database or completely different environments (e.g. dev vs t01). The Overview tab compares
/// every object of the two databases at once, from the cached catalog (no server load).
/// </summary>
public partial class ComparerViewModel(SessionService sessions, ObjectComparisonService comparer)
    : ViewModelBase, ISessionAware
{
    public const int OverviewTab = 0;
    public const int SchemaTab = 1;
    public const int StructureTab = 2;
    public const int DataTab = 3;

    private DatabaseSession? _mainSession;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<DataComparisonRow> _allDataRows = [];
    private DataComparisonResult? _lastDataResult;
    private (ReportSide Left, ReportSide Right)? _dataSides;
    private SchemaComparison? _lastSchemaComparison;
    private IReadOnlyList<ObjectComparisonEntry> _allOverview = [];
    private IReadOnlyList<StructureDiffRow> _allStructure = [];
    private IReadOnlyList<int> _changeStarts = [];

    /// <summary>What was compared on the Schema tab, kept so a report can be produced later.</summary>
    private sealed record SchemaComparison(
        DatabaseSession LeftSession, DbObject LeftObject, DatabaseSession RightSession, DbObject RightObject,
        ReportSide Left, ReportSide Right);

    public bool CanExportSchemaReport => _lastSchemaComparison is not null && !IsBusy;
    public bool CanExportDataReport => _lastDataResult is not null && !IsBusy;

    /// <summary>Report for the last schema comparison, always rendered line by line (readable in tickets and diffs).</summary>
    public async Task<string?> BuildSchemaReportAsync(bool html)
    {
        if (_lastSchemaComparison is not { } c) return null;
        var diff = SelectedSchemaCompareMode == SchemaCompareMode.LineByLine
            ? SchemaDiff
            : await comparer.CompareSchemaAsync(c.LeftSession, c.LeftObject, c.RightSession, c.RightObject,
                SchemaCompareMode.LineByLine, SchemaDiffOptions);
        return html
            ? ComparisonReportBuilder.SchemaHtml(c.Left, c.Right, diff, DateTimeOffset.Now)
            : ComparisonReportBuilder.SchemaMarkdown(c.Left, c.Right, diff, DateTimeOffset.Now);
    }

    public string? BuildDataReport(bool html)
    {
        if (_lastDataResult is not { } result || _dataSides is not { } sides) return null;
        var onlyDifferences = !ShowSameRows;
        return html
            ? ComparisonReportBuilder.DataHtml(sides.Left, sides.Right, result, onlyDifferences, DateTimeOffset.Now)
            : ComparisonReportBuilder.DataMarkdown(sides.Left, sides.Right, result, onlyDifferences, DateTimeOffset.Now);
    }

    public string ReportFileStem => (SelectedRightObject?.FullName ?? SelectedLeftObject?.FullName ?? "comparison").Replace(' ', '_');

    private static ReportSide Side(DatabaseSession session, DbObject obj) =>
        new(session.Profile.ToString(), obj.Database, obj.FullName);

    public ObservableCollection<ConnectionProfile> Profiles { get; set; } = [];

    [ObservableProperty] private ConnectionProfile? _leftProfile;
    [ObservableProperty] private ConnectionProfile? _rightProfile;
    [ObservableProperty] private DatabaseSession? _leftSession;
    [ObservableProperty] private DatabaseSession? _rightSession;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _selectedTabIndex = SchemaTab;

    [ObservableProperty] private IReadOnlyList<string> _leftDatabases = [];
    [ObservableProperty] private IReadOnlyList<string> _rightDatabases = [];
    [ObservableProperty] private string? _selectedLeftDatabase;
    [ObservableProperty] private string? _selectedRightDatabase;

    [ObservableProperty] private IReadOnlyList<DbObject> _leftObjects = [];
    [ObservableProperty] private IReadOnlyList<DbObject> _rightObjects = [];
    [ObservableProperty] private DbObject? _selectedLeftObject;
    [ObservableProperty] private DbObject? _selectedRightObject;

    public bool HasMultipleLeftDatabases => LeftDatabases.Count > 1;
    public bool HasMultipleRightDatabases => RightDatabases.Count > 1;

    partial void OnLeftDatabasesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasMultipleLeftDatabases));
    partial void OnRightDatabasesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasMultipleRightDatabases));

    /// <summary>One-line facts about the picked object (type, approximate rows, last modified).</summary>
    public string LeftObjectInfo => Describe(SelectedLeftObject);
    public string RightObjectInfo => Describe(SelectedRightObject);

    /// <summary>Environment tags ("DEV", "PROD", ...) of the connected sides, for the colored badges.</summary>
    public string? LeftEnvironmentTag => LeftSession?.Profile.Environment.ShortTag();
    public string? RightEnvironmentTag => RightSession?.Profile.Environment.ShortTag();
    public ConnectionEnvironment LeftEnvironment => LeftSession?.Profile.Environment ?? ConnectionEnvironment.None;
    public ConnectionEnvironment RightEnvironment => RightSession?.Profile.Environment ?? ConnectionEnvironment.None;

    /// <summary>What the user still has to do before comparing; empty once everything is in place.</summary>
    public string NextStepHint =>
        LeftSession is null && RightSession is null ? "Connect both sides to start: pick a saved connection and Connect, or Use current."
        : LeftSession is null ? "Connect the left side."
        : RightSession is null ? "Connect the right side."
        : SelectedLeftObject is null && SelectedRightObject is null
            ? "Pick an object to compare, or run Compare all objects on the Overview tab."
        : SelectedLeftObject is null ? "Pick the left object."
        : SelectedRightObject is null ? $"No object named {SelectedLeftObject.FullName} on the right; pick one manually."
        : "";

    public bool HasNextStepHint => NextStepHint.Length > 0;

    private static string Describe(DbObject? obj)
    {
        if (obj is null) return "";
        var parts = new List<string> { Humanize(obj.Type) };
        if (obj.RowCount is long rows) parts.Add($"≈ {rows.ToString("N0", CultureInfo.CurrentCulture)} rows");
        if (obj.ModifiedAt is DateTime modified) parts.Add("modified " + modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
        return string.Join(" · ", parts);
    }

    private static string Humanize(DbObjectType type) => type switch
    {
        DbObjectType.MaterializedView => "Materialized view",
        DbObjectType.ForeignTable => "Foreign table",
        DbObjectType.ScalarFunction => "Scalar function",
        DbObjectType.TableFunction => "Table function",
        _ => type.ToString()
    };

    // ----- Overview (whole database) -----

    [ObservableProperty] private IReadOnlyList<ObjectComparisonEntry> _overviewEntries = [];
    [ObservableProperty] private ObjectComparisonEntry? _selectedOverviewEntry;
    [ObservableProperty] private string _overviewStatus = "";
    [ObservableProperty] private string _overviewFilterText = "";
    [ObservableProperty] private bool _showOverviewDifferent = true;
    [ObservableProperty] private bool _showOverviewOnlyLeft = true;
    [ObservableProperty] private bool _showOverviewOnlyRight = true;
    [ObservableProperty] private bool _showOverviewIdentical;
    [ObservableProperty] private bool _showOverviewNotCompared;

    public string OverviewDifferentLabel => $"Different ({CountOf(ObjectCompareStatus.Different):N0})";
    public string OverviewOnlyLeftLabel => $"Only in left ({CountOf(ObjectCompareStatus.OnlyLeft):N0})";
    public string OverviewOnlyRightLabel => $"Only in right ({CountOf(ObjectCompareStatus.OnlyRight):N0})";
    public string OverviewIdenticalLabel => $"Identical ({CountOf(ObjectCompareStatus.Identical):N0})";
    public string OverviewNotComparedLabel => $"Not compared ({CountOf(ObjectCompareStatus.NotCompared):N0})";
    public bool HasOverview => _allOverview.Count > 0;

    private int CountOf(ObjectCompareStatus status) => _allOverview.Count(e => e.Status == status);

    partial void OnOverviewFilterTextChanged(string value) => ApplyOverviewFilter();
    partial void OnShowOverviewDifferentChanged(bool value) => ApplyOverviewFilter();
    partial void OnShowOverviewOnlyLeftChanged(bool value) => ApplyOverviewFilter();
    partial void OnShowOverviewOnlyRightChanged(bool value) => ApplyOverviewFilter();
    partial void OnShowOverviewIdenticalChanged(bool value) => ApplyOverviewFilter();
    partial void OnShowOverviewNotComparedChanged(bool value) => ApplyOverviewFilter();
    partial void OnSelectedOverviewEntryChanged(ObjectComparisonEntry? value) => OpenOverviewEntryCommand.NotifyCanExecuteChanged();

    private bool CanCompareOverview => LeftSession is not null && RightSession is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCompareOverview))]
    private void CompareOverview()
    {
        if (LeftSession is null || RightSession is null) return;
        try
        {
            _allOverview = DatabaseSchemaComparer.Compare(
                LeftSession.Snapshot, SelectedLeftDatabase, RightSession.Snapshot, SelectedRightDatabase, SchemaDiffOptions);
            var different = _allOverview.Count(e => e.Status != ObjectCompareStatus.Identical && e.Status != ObjectCompareStatus.NotCompared);
            OverviewStatus = different == 0
                ? $"No differences in {_allOverview.Count:N0} object(s)"
                : $"{different:N0} of {_allOverview.Count:N0} object(s) differ or exist on one side only";
            OverviewStatus += $" · from cached metadata (left {LeftSession.Snapshot.RefreshedAt.LocalDateTime:g}, right {RightSession.Snapshot.RefreshedAt.LocalDateTime:g}); refresh metadata for the latest";
        }
        catch (Exception ex)
        {
            _allOverview = [];
            OverviewStatus = "Error: " + ex.Message;
        }

        ApplyOverviewFilter();
    }

    private void ApplyOverviewFilter()
    {
        var text = OverviewFilterText.Trim();
        OverviewEntries = _allOverview.Where(e => e.Status switch
            {
                ObjectCompareStatus.Different => ShowOverviewDifferent,
                ObjectCompareStatus.OnlyLeft => ShowOverviewOnlyLeft,
                ObjectCompareStatus.OnlyRight => ShowOverviewOnlyRight,
                ObjectCompareStatus.Identical => ShowOverviewIdentical,
                _ => ShowOverviewNotCompared
            })
            .Where(e => text.Length == 0 || e.FullName.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();

        OnPropertyChanged(nameof(OverviewDifferentLabel));
        OnPropertyChanged(nameof(OverviewOnlyLeftLabel));
        OnPropertyChanged(nameof(OverviewOnlyRightLabel));
        OnPropertyChanged(nameof(OverviewIdenticalLabel));
        OnPropertyChanged(nameof(OverviewNotComparedLabel));
        OnPropertyChanged(nameof(HasOverview));
    }

    private bool CanOpenOverviewEntry => SelectedOverviewEntry is not null && !IsBusy;

    /// <summary>Selects the entry's objects on both sides and shows their detailed comparison.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenOverviewEntry))]
    private async Task OpenOverviewEntryAsync()
    {
        if (SelectedOverviewEntry is not { } entry) return;

        SelectedLeftObject = entry.Left is null ? null : LeftObjects.FirstOrDefault(o => o == entry.Left) ?? entry.Left;
        SelectedRightObject = entry.Right is null ? null : RightObjects.FirstOrDefault(o => o == entry.Right) ?? entry.Right;

        if (entry.Left is null || entry.Right is null)
        {
            Status = $"{entry.FullName} only exists on the {(entry.Left is null ? "right" : "left")}; nothing to diff.";
            return;
        }

        var isTable = entry.Type is DbObjectType.Table or DbObjectType.ForeignTable;
        SelectedTabIndex = isTable ? StructureTab : SchemaTab;
        await CompareSchemaAsync();
    }

    // ----- Schema (definition text) -----

    [ObservableProperty] private IReadOnlyList<DiffLine> _schemaDiff = [];
    [ObservableProperty] private string _schemaStatus = "";
    [ObservableProperty] private bool _schemaIgnoreCase;
    [ObservableProperty] private bool _schemaIgnoreWhitespace;
    [ObservableProperty] private bool _showOnlySchemaChanges;

    /// <summary>What the line-by-line grid shows: the whole diff, or only changes with a few lines of context.</summary>
    [ObservableProperty] private IReadOnlyList<DiffLine> _schemaDiffView = [];
    [ObservableProperty] private DiffLine? _selectedSchemaLine;
    [ObservableProperty] private string _changePosition = "";

    private DiffOptions SchemaDiffOptions => new() { IgnoreCase = SchemaIgnoreCase, IgnoreWhitespace = SchemaIgnoreWhitespace };

    /// <summary>Content-mode tokens present on the left side (equal + removed), grouped into lines
    /// so the results window can render formatted SQL scripts.</summary>
    [ObservableProperty] private IReadOnlyList<DiffLineGroup> _schemaDiffLeft = [];

    /// <summary>Content-mode tokens present on the right side (equal + added), grouped into lines
    /// so the results window can render formatted SQL scripts.</summary>
    [ObservableProperty] private IReadOnlyList<DiffLineGroup> _schemaDiffRight = [];

    partial void OnSchemaDiffChanged(IReadOnlyList<DiffLine> value)
    {
        SchemaDiffLeft = GroupIntoLines(value.Where(d => d.LeftText is not null));
        SchemaDiffRight = GroupIntoLines(value.Where(d => d.RightText is not null));
        RefreshSchemaView();
    }

    partial void OnShowOnlySchemaChangesChanged(bool value) => RefreshSchemaView();

    private void RefreshSchemaView()
    {
        SchemaDiffView = ShowOnlySchemaChanges ? TextDiffer.CollapseUnchanged(SchemaDiff) : SchemaDiff;
        _changeStarts = TextDiffer.ChangeBlockStarts(SchemaDiffView);
        ChangePosition = _changeStarts.Count == 0 ? "" : $"{_changeStarts.Count} change block(s)";
        NextDifferenceCommand.NotifyCanExecuteChanged();
        PreviousDifferenceCommand.NotifyCanExecuteChanged();
    }

    private bool HasChanges => _changeStarts.Count > 0 && IsSchemaLineView;

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void NextDifference() => MoveToChange(forward: true);

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void PreviousDifference() => MoveToChange(forward: false);

    private void MoveToChange(bool forward)
    {
        if (_changeStarts.Count == 0) return;
        var current = SelectedSchemaLine is null ? -1 : IndexOfReference(SchemaDiffView, SelectedSchemaLine);

        int block;
        if (forward)
        {
            block = _changeStarts.ToList().FindIndex(s => s > current);
            if (block < 0) block = 0;
        }
        else
        {
            block = _changeStarts.ToList().FindLastIndex(s => s < current);
            if (block < 0) block = _changeStarts.Count - 1;
        }

        SelectedSchemaLine = SchemaDiffView[_changeStarts[block]];
        ChangePosition = $"Change {block + 1} of {_changeStarts.Count}";
    }

    private static int IndexOfReference(IReadOnlyList<DiffLine> lines, DiffLine line)
    {
        for (var i = 0; i < lines.Count; i++)
            if (ReferenceEquals(lines[i], line)) return i;
        return -1;
    }

    private static IReadOnlyList<DiffLineGroup> GroupIntoLines(IEnumerable<DiffLine> tokens)
    {
        var groups = new List<DiffLineGroup>();
        var current = new List<DiffLine>();
        foreach (var token in tokens)
        {
            current.Add(token);
            if (token.NewLineAfter)
            {
                groups.Add(new DiffLineGroup { Tokens = current });
                current = [];
            }
        }

        if (current.Count > 0)
            groups.Add(new DiffLineGroup { Tokens = current });

        return groups;
    }

    public IReadOnlyList<SchemaCompareMode> SchemaCompareModes { get; } = Enum.GetValues<SchemaCompareMode>();
    [ObservableProperty] private SchemaCompareMode _selectedSchemaCompareMode = SchemaCompareMode.LineByLine;

    public bool IsSchemaLineView => SelectedSchemaCompareMode == SchemaCompareMode.LineByLine;
    public bool IsSchemaContentView => SelectedSchemaCompareMode == SchemaCompareMode.Content;

    partial void OnSchemaIgnoreCaseChanged(bool value) => RecompareSchema();
    partial void OnSchemaIgnoreWhitespaceChanged(bool value) => RecompareSchema();

    private void RecompareSchema()
    {
        if (_lastSchemaComparison is not null && !IsBusy) _ = CompareSchemaAsync();
    }

    // ----- Structure (tables: columns, indexes, foreign keys) -----

    [ObservableProperty] private IReadOnlyList<StructureDiffRow> _structureDiff = [];
    [ObservableProperty] private string _structureStatus = "Compare two tables to see column, index and foreign key differences.";
    [ObservableProperty] private bool _showOnlyStructureChanges = true;

    partial void OnShowOnlyStructureChangesChanged(bool value) => ApplyStructureFilter();

    private void ApplyStructureFilter() =>
        StructureDiff = ShowOnlyStructureChanges ? _allStructure.Where(r => r.Change != StructureChange.Same).ToList() : _allStructure;

    // ----- Data -----

    [ObservableProperty] private IReadOnlyList<DataComparisonRow> _dataDiff = [];
    [ObservableProperty] private string _dataStatus = "";
    [ObservableProperty] private decimal _rowLimit = 1000;
    [ObservableProperty] private bool _showSameRows = true;
    [ObservableProperty] private bool _showDifferentRows = true;
    [ObservableProperty] private bool _showOnlyLeftRows = true;
    [ObservableProperty] private bool _showOnlyRightRows = true;
    [ObservableProperty] private string _dataKeyFilter = "";
    [ObservableProperty] private string? _dataColumnFilter;
    [ObservableProperty] private string _ignoredColumnsText = "";
    [ObservableProperty] private bool _dataIgnoreCase;
    [ObservableProperty] private bool _dataTrimWhitespace;
    [ObservableProperty] private IReadOnlyList<string> _dataNotes = [];
    [ObservableProperty] private IReadOnlyList<ColumnDifferenceCount> _columnDifferences = [];

    public string SameRowsLabel => $"Same ({CountOf(DataRowStatus.Same):N0})";
    public string DifferentRowsLabel => $"Different ({CountOf(DataRowStatus.Different):N0})";
    public string OnlyLeftRowsLabel => $"Only in left ({CountOf(DataRowStatus.OnlyLeft):N0})";
    public string OnlyRightRowsLabel => $"Only in right ({CountOf(DataRowStatus.OnlyRight):N0})";
    public bool HasDataResult => _lastDataResult is not null;
    public bool HasColumnDifferences => ColumnDifferences.Count > 0;
    public bool HasDataColumnFilter => DataColumnFilter is not null;

    private int CountOf(DataRowStatus status) => _allDataRows.Count(r => r.Status == status);

    partial void OnColumnDifferencesChanged(IReadOnlyList<ColumnDifferenceCount> value) => OnPropertyChanged(nameof(HasColumnDifferences));
    partial void OnShowSameRowsChanged(bool value) => ApplyDataFilter();
    partial void OnShowDifferentRowsChanged(bool value) => ApplyDataFilter();
    partial void OnShowOnlyLeftRowsChanged(bool value) => ApplyDataFilter();
    partial void OnShowOnlyRightRowsChanged(bool value) => ApplyDataFilter();
    partial void OnDataKeyFilterChanged(string value) => ApplyDataFilter();

    partial void OnDataColumnFilterChanged(string? value)
    {
        OnPropertyChanged(nameof(HasDataColumnFilter));
        ApplyDataFilter();
    }

    /// <summary>Shows only the rows whose value differs in the given column (click again to clear).</summary>
    [RelayCommand]
    private void FilterByColumn(string? column) =>
        DataColumnFilter = column is null || string.Equals(column, DataColumnFilter, StringComparison.OrdinalIgnoreCase) ? null : column;

    [ObservableProperty] private ResultSetView? _leftDataResult;
    [ObservableProperty] private ResultSetView? _rightDataResult;
    [ObservableProperty] private string _leftDataStatus = "";
    [ObservableProperty] private string _rightDataStatus = "";

    public IReadOnlyList<DataCompareViewMode> DataViewModes { get; } = Enum.GetValues<DataCompareViewMode>();
    [ObservableProperty] private DataCompareViewMode _selectedDataViewMode = DataCompareViewMode.Summary;

    public bool IsSummaryView => SelectedDataViewMode == DataCompareViewMode.Summary;
    public bool IsSideBySideView => SelectedDataViewMode == DataCompareViewMode.SideBySide;
    public bool IsFullDataSetsView => SelectedDataViewMode == DataCompareViewMode.FullDataSets;

    partial void OnSelectedDataViewModeChanged(DataCompareViewMode value)
    {
        OnPropertyChanged(nameof(IsSummaryView));
        OnPropertyChanged(nameof(IsSideBySideView));
        OnPropertyChanged(nameof(IsFullDataSetsView));
    }

    /// <summary>Lets either side's connection panel be hidden to give the other side more room.</summary>
    [ObservableProperty] private bool _isLeftPanelVisible = true;
    [ObservableProperty] private bool _isRightPanelVisible = true;

    public void Attach(DatabaseSession? session)
    {
        // The main session is about to be disposed by the caller (or was just disconnected):
        // drop any reference to it here so we don't operate on / re-dispose a stale session.
        if (LeftSession is not null && LeftSession == _mainSession) ClearLeft();
        if (RightSession is not null && RightSession == _mainSession) ClearRight();

        _mainSession = session;
        RefreshCommands();
    }

    /// <summary>Independently-connected left/right sessions (not the main one) that this tab owns
    /// and must dispose itself; used on application shutdown.</summary>
    public async ValueTask DisposeIndependentSessionsAsync()
    {
        _cts?.Cancel();
        if (LeftSession is not null && LeftSession != _mainSession) await LeftSession.DisposeAsync();
        if (RightSession is not null && RightSession != _mainSession && RightSession != LeftSession) await RightSession.DisposeAsync();
    }

    partial void OnLeftProfileChanged(ConnectionProfile? value) => ConnectLeftCommand.NotifyCanExecuteChanged();

    partial void OnRightProfileChanged(ConnectionProfile? value) => ConnectRightCommand.NotifyCanExecuteChanged();

    partial void OnLeftSessionChanged(DatabaseSession? value)
    {
        OnPropertyChanged(nameof(IsLeftConnected));
        OnPropertyChanged(nameof(LeftEnvironmentTag));
        OnPropertyChanged(nameof(LeftEnvironment));
        ForgetReportSources();
        RefreshHints();
    }

    partial void OnRightSessionChanged(DatabaseSession? value)
    {
        OnPropertyChanged(nameof(IsRightConnected));
        OnPropertyChanged(nameof(RightEnvironmentTag));
        OnPropertyChanged(nameof(RightEnvironment));
        ForgetReportSources();
        RefreshHints();
    }

    /// <summary>A report must not re-query a session that has since been swapped out or disposed.</summary>
    private void ForgetReportSources()
    {
        _lastSchemaComparison = null;
        _lastDataResult = null;
        _dataSides = null;
        OnPropertyChanged(nameof(CanExportSchemaReport));
        OnPropertyChanged(nameof(CanExportDataReport));
        OnPropertyChanged(nameof(HasDataResult));
    }

    public bool IsLeftConnected => LeftSession is not null;
    public bool IsRightConnected => RightSession is not null;

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommands();
        OnPropertyChanged(nameof(CanExportSchemaReport));
        OnPropertyChanged(nameof(CanExportDataReport));
    }

    partial void OnSelectedLeftObjectChanged(DbObject? value)
    {
        if (value is not null)
        {
            var match = RightObjects.FirstOrDefault(o =>
                o.Type == value.Type &&
                string.Equals(o.Schema, value.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.Name, value.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) SelectedRightObject = match;
        }
        OnPropertyChanged(nameof(LeftObjectInfo));
        RefreshHints();
        RefreshCommands();
    }

    partial void OnSelectedRightObjectChanged(DbObject? value)
    {
        OnPropertyChanged(nameof(RightObjectInfo));
        RefreshHints();
        RefreshCommands();
    }

    private void RefreshHints()
    {
        OnPropertyChanged(nameof(NextStepHint));
        OnPropertyChanged(nameof(HasNextStepHint));
    }

    partial void OnSelectedSchemaCompareModeChanged(SchemaCompareMode value)
    {
        OnPropertyChanged(nameof(IsSchemaLineView));
        OnPropertyChanged(nameof(IsSchemaContentView));
        NextDifferenceCommand.NotifyCanExecuteChanged();
        PreviousDifferenceCommand.NotifyCanExecuteChanged();
        RecompareSchema();
    }

    partial void OnSelectedLeftDatabaseChanged(string? value) => UpdateLeftObjects();

    partial void OnSelectedRightDatabaseChanged(string? value) => UpdateRightObjects();

    private bool CanUseMain => _mainSession is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseMain))]
    private async Task UseCurrentAsLeftAsync()
    {
        if (_mainSession is null) return;
        LeftProfile = _mainSession.Profile;
        await SetLeftSessionAsync(_mainSession);
        Status = $"Left uses the current connection ({_mainSession.Profile})";
    }

    [RelayCommand(CanExecute = nameof(CanUseMain))]
    private async Task UseCurrentAsRightAsync()
    {
        if (_mainSession is null) return;
        RightProfile = _mainSession.Profile;
        await SetRightSessionAsync(_mainSession);
        Status = $"Right uses the current connection ({_mainSession.Profile})";
    }

    private bool CanConnectLeft => LeftProfile is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnectLeft))]
    private async Task ConnectLeftAsync()
    {
        if (LeftProfile is not { } profile) return;
        IsBusy = true;
        Status = $"Connecting left to {profile}…";
        try
        {
            var session = await sessions.ConnectAsync(profile);
            await SetLeftSessionAsync(session);
            Status = $"Left connected to {profile}";
        }
        catch (Exception ex)
        {
            Status = "Left connection failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConnectRight => RightProfile is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnectRight))]
    private async Task ConnectRightAsync()
    {
        if (RightProfile is not { } profile) return;
        IsBusy = true;
        Status = $"Connecting right to {profile}…";
        try
        {
            var session = await sessions.ConnectAsync(profile);
            await SetRightSessionAsync(session);
            Status = $"Right connected to {profile}";
        }
        catch (Exception ex)
        {
            Status = "Right connection failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDisconnectLeft => LeftSession is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnectLeft))]
    private async Task DisconnectLeftAsync()
    {
        if (LeftSession is null) return;
        IsBusy = true;
        try
        {
            if (LeftSession != _mainSession && LeftSession != RightSession) await LeftSession.DisposeAsync();
            ClearLeft();
            Status = "Left disconnected";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDisconnectRight => RightSession is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnectRight))]
    private async Task DisconnectRightAsync()
    {
        if (RightSession is null) return;
        IsBusy = true;
        try
        {
            if (RightSession != _mainSession && RightSession != LeftSession) await RightSession.DisposeAsync();
            ClearRight();
            Status = "Right disconnected";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearLeft()
    {
        LeftSession = null;
        LeftDatabases = [];
        SelectedLeftDatabase = null;
        LeftObjects = [];
        SelectedLeftObject = null;
    }

    private void ClearRight()
    {
        RightSession = null;
        RightDatabases = [];
        SelectedRightDatabase = null;
        RightObjects = [];
        SelectedRightObject = null;
    }

    private bool CanSwap => (LeftSession is not null || RightSession is not null) && !IsBusy;

    /// <summary>Exchanges the two sides (connection, database and object), so the baseline becomes the comparand.</summary>
    [RelayCommand(CanExecute = nameof(CanSwap))]
    private void SwapSides()
    {
        var (lp, ls, ldbs, ldb, lobj) = (LeftProfile, LeftSession, LeftDatabases, SelectedLeftDatabase, SelectedLeftObject);
        var (rp, rs, rdbs, rdb, robj) = (RightProfile, RightSession, RightDatabases, SelectedRightDatabase, SelectedRightObject);

        LeftProfile = rp;
        RightProfile = lp;
        LeftSession = rs;
        RightSession = ls;
        LeftDatabases = rdbs;
        RightDatabases = ldbs;
        SelectedLeftDatabase = rdb;
        SelectedRightDatabase = ldb;
        UpdateLeftObjects();
        UpdateRightObjects();
        SelectedLeftObject = robj is null ? null : LeftObjects.FirstOrDefault(o => o == robj);
        SelectedRightObject = lobj is null ? null : RightObjects.FirstOrDefault(o => o == lobj);

        ClearResults();
        Status = "Sides swapped";
        RefreshCommands();
    }

    private void ClearResults()
    {
        SchemaDiff = [];
        SchemaStatus = "";
        _allStructure = [];
        ApplyStructureFilter();
        _allDataRows = [];
        ColumnDifferences = [];
        DataNotes = [];
        DataColumnFilter = null;
        ApplyDataFilter();
        DataStatus = "";
        _allOverview = [];
        ApplyOverviewFilter();
        OverviewStatus = "";
    }

    private async Task SetLeftSessionAsync(DatabaseSession session)
    {
        if (LeftSession is not null && LeftSession != _mainSession && LeftSession != RightSession && LeftSession != session)
            await LeftSession.DisposeAsync();
        LeftSession = session;
        LeftDatabases = GetDatabases(session);
        SelectedLeftDatabase = PreferredDatabase(session, LeftDatabases);
        UpdateLeftObjects();
        RefreshCommands();
    }

    private async Task SetRightSessionAsync(DatabaseSession session)
    {
        if (RightSession is not null && RightSession != _mainSession && RightSession != LeftSession && RightSession != session)
            await RightSession.DisposeAsync();
        RightSession = session;
        RightDatabases = GetDatabases(session);
        SelectedRightDatabase = PreferredDatabase(session, RightDatabases);
        UpdateRightObjects();
        RefreshCommands();
    }

    /// <summary>The profile's own database when the server exposes several, otherwise the first one.</summary>
    private static string? PreferredDatabase(DatabaseSession session, IReadOnlyList<string> databases) =>
        databases.FirstOrDefault(d => string.Equals(d, session.Profile.Database, StringComparison.OrdinalIgnoreCase))
        ?? (databases.Count > 0 ? databases[0] : null);

    private static IReadOnlyList<string> GetDatabases(DatabaseSession session) =>
        session.Snapshot.Objects
            .Select(o => o.Database)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void UpdateLeftObjects()
    {
        LeftObjects = ObjectsOf(LeftSession, SelectedLeftDatabase);
        SelectedLeftObject = null;
    }

    private void UpdateRightObjects()
    {
        RightObjects = ObjectsOf(RightSession, SelectedRightDatabase);
        SelectedRightObject = null;
    }

    private static IReadOnlyList<DbObject> ObjectsOf(DatabaseSession? session, string? database) =>
        (session?.Snapshot.Objects ?? [])
            .Where(o => database is null || string.Equals(o.Database, database, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool CanCompare => LeftSession is not null && RightSession is not null &&
                                SelectedLeftObject is not null && SelectedRightObject is not null && !IsBusy;

    private bool CanCancel => IsBusy && _cts is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private CancellationToken BeginOperation()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        return _cts.Token;
    }

    private void EndOperation()
    {
        _cts?.Dispose();
        _cts = null;
        IsBusy = false;
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareSchemaAsync()
    {
        if (LeftSession is null || RightSession is null || SelectedLeftObject is null || SelectedRightObject is null) return;

        var (leftSession, leftObject, rightSession, rightObject) = (LeftSession, SelectedLeftObject, RightSession, SelectedRightObject);
        var ct = BeginOperation();
        SchemaStatus = "Comparing definitions…";
        try
        {
            var diff = await comparer.CompareSchemaAsync(leftSession, leftObject, rightSession, rightObject,
                SelectedSchemaCompareMode, SchemaDiffOptions, ct);
            SchemaDiff = diff;
            _lastSchemaComparison = new SchemaComparison(leftSession, leftObject, rightSession, rightObject,
                Side(leftSession, leftObject), Side(rightSession, rightObject));
            OnPropertyChanged(nameof(CanExportSchemaReport));

            var stats = TextDiffer.Statistics(diff);
            var unit = SelectedSchemaCompareMode == SchemaCompareMode.Content ? "token(s)" : "line(s)";
            var what = $"{leftObject.FullName} ↔ {rightObject.FullName}";
            SchemaStatus = stats.IsIdentical
                ? $"{what}: definitions are identical"
                : $"{what}: −{stats.Removed} {unit} only on left · +{stats.Added} {unit} only on right · " +
                  $"{stats.ChangeBlocks} change block(s) · {stats.SimilarityPercent.ToString("0.#", CultureInfo.CurrentCulture)}% similar";

            UpdateStructure(leftSession, leftObject, rightSession, rightObject);
        }
        catch (OperationCanceledException)
        {
            SchemaStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            SchemaStatus = "Error: " + ex.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private void UpdateStructure(DatabaseSession leftSession, DbObject leftObject, DatabaseSession rightSession, DbObject rightObject)
    {
        if (leftObject.Type is not (DbObjectType.Table or DbObjectType.ForeignTable) ||
            rightObject.Type is not (DbObjectType.Table or DbObjectType.ForeignTable))
        {
            _allStructure = [];
            StructureStatus = "Structure comparison is available for tables; the Schema tab shows the source of other objects.";
            ApplyStructureFilter();
            return;
        }

        _allStructure = TableStructureComparer.Compare(leftSession.Snapshot, leftObject, rightSession.Snapshot, rightObject);
        var changes = _allStructure.Where(r => r.Change != StructureChange.Same).ToList();
        StructureStatus = changes.Count == 0
            ? $"{leftObject.FullName}: columns, indexes and foreign keys are identical"
            : $"{leftObject.FullName}: " + string.Join(" · ", changes.GroupBy(r => r.Category)
                .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()} difference(s)"));
        ApplyStructureFilter();
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareDataAsync()
    {
        if (LeftSession is null || RightSession is null || SelectedLeftObject is null || SelectedRightObject is null) return;

        if (!SelectedLeftObject.IsTableLike || !SelectedRightObject.IsTableLike)
        {
            DataStatus = "Data comparison only supports tables and views.";
            return;
        }

        var (leftSession, leftObject, rightSession, rightObject) = (LeftSession, SelectedLeftObject, RightSession, SelectedRightObject);
        var ct = BeginOperation();
        DataStatus = "Comparing data…";
        try
        {
            var limit = Math.Max(1, (int)RowLimit);
            var options = new DataCompareOptions
            {
                IgnoredColumns = IgnoredColumnsText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                IgnoreCase = DataIgnoreCase,
                TrimWhitespace = DataTrimWhitespace
            };
            var result = await comparer.CompareDataAsync(leftSession, leftObject, rightSession, rightObject, limit, options, ct);
            _allDataRows = result.Rows;
            _lastDataResult = result;
            _dataSides = (Side(leftSession, leftObject), Side(rightSession, rightObject));
            OnPropertyChanged(nameof(CanExportDataReport));
            OnPropertyChanged(nameof(HasDataResult));

            DataNotes = ComparisonReportBuilder.DataNotes(result);
            ColumnDifferences = result.ColumnDifferences;
            DataColumnFilter = null;
            ApplyDataFilter();
        }
        catch (OperationCanceledException)
        {
            DataStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            DataStatus = "Error: " + ex.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private void ApplyDataFilter()
    {
        var key = DataKeyFilter.Trim();
        var column = DataColumnFilter;
        DataDiff = _allDataRows.Where(r => r.Status switch
            {
                DataRowStatus.Same => ShowSameRows,
                DataRowStatus.Different => ShowDifferentRows,
                DataRowStatus.OnlyLeft => ShowOnlyLeftRows,
                _ => ShowOnlyRightRows
            })
            .Where(r => key.Length == 0 || r.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
            .Where(r => column is null || (r.Status == DataRowStatus.Different &&
                                           r.Cells.Any(c => c.IsDifferent && string.Equals(c.Column, column, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        OnPropertyChanged(nameof(SameRowsLabel));
        OnPropertyChanged(nameof(DifferentRowsLabel));
        OnPropertyChanged(nameof(OnlyLeftRowsLabel));
        OnPropertyChanged(nameof(OnlyRightRowsLabel));

        if (_lastDataResult is not { } result) return;
        var identical = _allDataRows.All(r => r.Status == DataRowStatus.Same);
        DataStatus = (identical ? $"All {_allDataRows.Count:N0} row(s) are identical" : $"Showing {DataDiff.Count:N0} of {_allDataRows.Count:N0} row(s)") +
                     (column is null ? "" : $" · filtered to rows where {column} differs") +
                     $" · {result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s";
    }

    private bool CanLoadLeftData => LeftSession is not null && SelectedLeftObject is { IsTableLike: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoadLeftData))]
    private async Task LoadLeftDataAsync()
    {
        if (LeftSession is null || SelectedLeftObject is null) return;

        var ct = BeginOperation();
        LeftDataStatus = "Loading…";
        try
        {
            var limit = Math.Max(1, (int)RowLimit);
            var result = await comparer.LoadTableDataAsync(LeftSession, SelectedLeftObject, limit, ct);
            var rs = result.ResultSets.FirstOrDefault();
            LeftDataResult = rs is null
                ? null
                : ResultSetView.From("Left data", rs, LeftSession, QualifiedName(LeftSession, SelectedLeftObject));
            LeftDataStatus = $"{(rs?.Rows.Count ?? 0):N0} row(s) in {result.Elapsed.TotalMilliseconds:N0} ms";
        }
        catch (OperationCanceledException)
        {
            LeftDataStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            LeftDataStatus = "Error: " + ex.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private bool CanLoadRightData => RightSession is not null && SelectedRightObject is { IsTableLike: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoadRightData))]
    private async Task LoadRightDataAsync()
    {
        if (RightSession is null || SelectedRightObject is null) return;

        var ct = BeginOperation();
        RightDataStatus = "Loading…";
        try
        {
            var limit = Math.Max(1, (int)RowLimit);
            var result = await comparer.LoadTableDataAsync(RightSession, SelectedRightObject, limit, ct);
            var rs = result.ResultSets.FirstOrDefault();
            RightDataResult = rs is null
                ? null
                : ResultSetView.From("Right data", rs, RightSession, QualifiedName(RightSession, SelectedRightObject));
            RightDataStatus = $"{(rs?.Rows.Count ?? 0):N0} row(s) in {result.Elapsed.TotalMilliseconds:N0} ms";
        }
        catch (OperationCanceledException)
        {
            RightDataStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            RightDataStatus = "Error: " + ex.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private static string QualifiedName(DatabaseSession session, DbObject obj) =>
        session.Provider.QuoteIdentifier(obj.Schema) + "." + session.Provider.QuoteIdentifier(obj.Name);

    private void RefreshCommands()
    {
        ConnectLeftCommand.NotifyCanExecuteChanged();
        ConnectRightCommand.NotifyCanExecuteChanged();
        DisconnectLeftCommand.NotifyCanExecuteChanged();
        DisconnectRightCommand.NotifyCanExecuteChanged();
        UseCurrentAsLeftCommand.NotifyCanExecuteChanged();
        UseCurrentAsRightCommand.NotifyCanExecuteChanged();
        SwapSidesCommand.NotifyCanExecuteChanged();
        CompareOverviewCommand.NotifyCanExecuteChanged();
        OpenOverviewEntryCommand.NotifyCanExecuteChanged();
        CompareSchemaCommand.NotifyCanExecuteChanged();
        CompareDataCommand.NotifyCanExecuteChanged();
        LoadLeftDataCommand.NotifyCanExecuteChanged();
        LoadRightDataCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
