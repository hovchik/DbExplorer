using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Compare;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// Compares a schema object (or its data) between two connections, which may target the same
/// server/database or completely different environments (e.g. dev vs t01).
/// </summary>
public partial class ComparerViewModel(SessionService sessions, ObjectComparisonService comparer)
    : ViewModelBase, ISessionAware
{
    private DatabaseSession? _mainSession;
    private IReadOnlyList<DataComparisonRow> _allDataRows = [];
    private DataComparisonResult? _lastDataResult;
    private (ReportSide Left, ReportSide Right)? _dataSides;
    private SchemaComparison? _lastSchemaComparison;

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
            : await comparer.CompareSchemaAsync(c.LeftSession, c.LeftObject, c.RightSession, c.RightObject, SchemaCompareMode.LineByLine);
        return html
            ? ComparisonReportBuilder.SchemaHtml(c.Left, c.Right, diff, DateTimeOffset.Now)
            : ComparisonReportBuilder.SchemaMarkdown(c.Left, c.Right, diff, DateTimeOffset.Now);
    }

    public string? BuildDataReport(bool html)
    {
        if (_lastDataResult is not { } result || _dataSides is not { } sides) return null;
        return html
            ? ComparisonReportBuilder.DataHtml(sides.Left, sides.Right, result, OnlyShowDifferences, DateTimeOffset.Now)
            : ComparisonReportBuilder.DataMarkdown(sides.Left, sides.Right, result, OnlyShowDifferences, DateTimeOffset.Now);
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

    [ObservableProperty] private IReadOnlyList<string> _leftDatabases = [];
    [ObservableProperty] private IReadOnlyList<string> _rightDatabases = [];
    [ObservableProperty] private string? _selectedLeftDatabase;
    [ObservableProperty] private string? _selectedRightDatabase;

    [ObservableProperty] private IReadOnlyList<DbObject> _leftObjects = [];
    [ObservableProperty] private IReadOnlyList<DbObject> _rightObjects = [];
    [ObservableProperty] private DbObject? _selectedLeftObject;
    [ObservableProperty] private DbObject? _selectedRightObject;

    [ObservableProperty] private IReadOnlyList<DiffLine> _schemaDiff = [];
    [ObservableProperty] private string _schemaStatus = "";

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

    [ObservableProperty] private IReadOnlyList<DataComparisonRow> _dataDiff = [];
    [ObservableProperty] private string _dataStatus = "";
    [ObservableProperty] private decimal _rowLimit = 1000;
    [ObservableProperty] private bool _onlyShowDifferences;

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
        if (LeftSession is not null && LeftSession == _mainSession)
        {
            LeftSession = null;
            LeftDatabases = [];
            SelectedLeftDatabase = null;
            LeftObjects = [];
            SelectedLeftObject = null;
        }

        if (RightSession is not null && RightSession == _mainSession)
        {
            RightSession = null;
            RightDatabases = [];
            SelectedRightDatabase = null;
            RightObjects = [];
            SelectedRightObject = null;
        }

        _mainSession = session;
        RefreshCommands();
    }

    /// <summary>Independently-connected left/right sessions (not the main one) that this tab owns
    /// and must dispose itself; used on application shutdown.</summary>
    public async ValueTask DisposeIndependentSessionsAsync()
    {
        if (LeftSession is not null && LeftSession != _mainSession) await LeftSession.DisposeAsync();
        if (RightSession is not null && RightSession != _mainSession) await RightSession.DisposeAsync();
    }

    partial void OnLeftProfileChanged(ConnectionProfile? value) => ConnectLeftCommand.NotifyCanExecuteChanged();

    partial void OnRightProfileChanged(ConnectionProfile? value) => ConnectRightCommand.NotifyCanExecuteChanged();

    partial void OnLeftSessionChanged(DatabaseSession? value)
    {
        OnPropertyChanged(nameof(IsLeftConnected));
        ForgetReportSources();
    }

    partial void OnRightSessionChanged(DatabaseSession? value)
    {
        OnPropertyChanged(nameof(IsRightConnected));
        ForgetReportSources();
    }

    /// <summary>A report must not re-query a session that has since been swapped out or disposed.</summary>
    private void ForgetReportSources()
    {
        _lastSchemaComparison = null;
        _lastDataResult = null;
        _dataSides = null;
        OnPropertyChanged(nameof(CanExportSchemaReport));
        OnPropertyChanged(nameof(CanExportDataReport));
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
        RefreshCommands();
    }

    partial void OnSelectedRightObjectChanged(DbObject? value) => RefreshCommands();

    partial void OnSelectedSchemaCompareModeChanged(SchemaCompareMode value)
    {
        OnPropertyChanged(nameof(IsSchemaLineView));
        OnPropertyChanged(nameof(IsSchemaContentView));
        if (SchemaDiff.Count > 0) _ = CompareSchemaAsync();
    }

    partial void OnSelectedLeftDatabaseChanged(string? value) => UpdateLeftObjects();

    partial void OnSelectedRightDatabaseChanged(string? value) => UpdateRightObjects();

    partial void OnOnlyShowDifferencesChanged(bool value) => ApplyDataFilter();

    private bool CanUseMain => _mainSession is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseMain))]
    private async Task UseCurrentAsLeftAsync()
    {
        if (_mainSession is null) return;
        LeftProfile = _mainSession.Profile;
        await SetLeftSessionAsync(_mainSession);
    }

    [RelayCommand(CanExecute = nameof(CanUseMain))]
    private async Task UseCurrentAsRightAsync()
    {
        if (_mainSession is null) return;
        RightProfile = _mainSession.Profile;
        await SetRightSessionAsync(_mainSession);
    }

    private bool CanConnectLeft => LeftProfile is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnectLeft))]
    private async Task ConnectLeftAsync()
    {
        if (LeftProfile is not { } profile) return;
        IsBusy = true;
        Status = $"Connecting left to {profile}\u2026";
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
        Status = $"Connecting right to {profile}\u2026";
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
            if (LeftSession != _mainSession) await LeftSession.DisposeAsync();
            LeftSession = null;
            LeftDatabases = [];
            SelectedLeftDatabase = null;
            LeftObjects = [];
            SelectedLeftObject = null;
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
            if (RightSession != _mainSession) await RightSession.DisposeAsync();
            RightSession = null;
            RightDatabases = [];
            SelectedRightDatabase = null;
            RightObjects = [];
            SelectedRightObject = null;
            Status = "Right disconnected";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SetLeftSessionAsync(DatabaseSession session)
    {
        if (LeftSession is not null && LeftSession != _mainSession) await LeftSession.DisposeAsync();
        LeftSession = session;
        LeftDatabases = GetDatabases(session);
        SelectedLeftDatabase = LeftDatabases.Count > 0 ? LeftDatabases[0] : null;
        UpdateLeftObjects();
        RefreshCommands();
    }

    private async Task SetRightSessionAsync(DatabaseSession session)
    {
        if (RightSession is not null && RightSession != _mainSession) await RightSession.DisposeAsync();
        RightSession = session;
        RightDatabases = GetDatabases(session);
        SelectedRightDatabase = RightDatabases.Count > 0 ? RightDatabases[0] : null;
        UpdateRightObjects();
        RefreshCommands();
    }

    private static IReadOnlyList<string> GetDatabases(DatabaseSession session) =>
        session.Snapshot.Objects
            .Select(o => o.Database)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void UpdateLeftObjects()
    {
        var all = LeftSession?.Snapshot.Objects ?? [];
        LeftObjects = SelectedLeftDatabase is null
            ? all
            : all.Where(o => string.Equals(o.Database, SelectedLeftDatabase, StringComparison.OrdinalIgnoreCase)).ToList();
        SelectedLeftObject = null;
    }

    private void UpdateRightObjects()
    {
        var all = RightSession?.Snapshot.Objects ?? [];
        RightObjects = SelectedRightDatabase is null
            ? all
            : all.Where(o => string.Equals(o.Database, SelectedRightDatabase, StringComparison.OrdinalIgnoreCase)).ToList();
        SelectedRightObject = null;
    }

    private bool CanCompare => LeftSession is not null && RightSession is not null &&
                                SelectedLeftObject is not null && SelectedRightObject is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareSchemaAsync()
    {
        if (LeftSession is null || RightSession is null || SelectedLeftObject is null || SelectedRightObject is null) return;

        IsBusy = true;
        SchemaStatus = "Comparing definitions\u2026";
        try
        {
            var diff = await comparer.CompareSchemaAsync(LeftSession, SelectedLeftObject, RightSession, SelectedRightObject, SelectedSchemaCompareMode);
            SchemaDiff = diff;
            _lastSchemaComparison = new SchemaComparison(LeftSession, SelectedLeftObject, RightSession, SelectedRightObject,
                Side(LeftSession, SelectedLeftObject), Side(RightSession, SelectedRightObject));
            OnPropertyChanged(nameof(CanExportSchemaReport));
            var added = diff.Count(d => d.Kind == DiffLineKind.Added);
            var removed = diff.Count(d => d.Kind == DiffLineKind.Removed);
            var unit = SelectedSchemaCompareMode == SchemaCompareMode.Content ? "token(s)" : "line(s)";
            SchemaStatus = added == 0 && removed == 0
                ? "Definitions are identical"
                : $"{removed} {unit} only on left \u00B7 {added} {unit} only on right";
        }
        catch (Exception ex)
        {
            SchemaStatus = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
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

        IsBusy = true;
        DataStatus = "Comparing data\u2026";
        try
        {
            var limit = Math.Max(1, (int)RowLimit);
            var result = await comparer.CompareDataAsync(LeftSession, SelectedLeftObject, RightSession, SelectedRightObject, limit);
            _allDataRows = result.Rows;
            _lastDataResult = result;
            _dataSides = (Side(LeftSession, SelectedLeftObject), Side(RightSession, SelectedRightObject));
            OnPropertyChanged(nameof(CanExportDataReport));

            var keyDescription = result.UsedFallbackKey
                ? "no primary key in common; matched by full row"
                : $"matched by {string.Join(", ", result.KeyColumns)}";
            var truncated = (result.LeftTruncated ? " \u00B7 left truncated at limit" : "") +
                             (result.RightTruncated ? " \u00B7 right truncated at limit" : "");
            ApplyDataFilter(keyDescription + truncated);
        }
        catch (Exception ex)
        {
            DataStatus = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyDataFilter(string? suffix = null)
    {
        var rows = OnlyShowDifferences ? _allDataRows.Where(r => r.Status != DataRowStatus.Same).ToList() : _allDataRows;
        DataDiff = rows;

        var same = _allDataRows.Count(r => r.Status == DataRowStatus.Same);
        var different = _allDataRows.Count(r => r.Status == DataRowStatus.Different);
        var onlyLeft = _allDataRows.Count(r => r.Status == DataRowStatus.OnlyLeft);
        var onlyRight = _allDataRows.Count(r => r.Status == DataRowStatus.OnlyRight);

        DataStatus = $"{same:N0} same \u00B7 {different:N0} different \u00B7 {onlyLeft:N0} only in left \u00B7 " +
                     $"{onlyRight:N0} only in right (showing {DataDiff.Count:N0})" +
                     (suffix is null ? "" : " \u00B7 " + suffix);
    }

    private bool CanLoadLeftData => LeftSession is not null && SelectedLeftObject is { IsTableLike: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoadLeftData))]
    private async Task LoadLeftDataAsync()
    {
        if (LeftSession is null || SelectedLeftObject is null) return;

        IsBusy = true;
        LeftDataStatus = "Loading\u2026";
        try
        {
            var limit = Math.Max(1, (int)RowLimit);
            var result = await comparer.LoadTableDataAsync(LeftSession, SelectedLeftObject, limit);
            var rs = result.ResultSets.FirstOrDefault();
            LeftDataResult = rs is null
                ? null
                : ResultSetView.From("Left data", rs, LeftSession, QualifiedName(LeftSession, SelectedLeftObject));
            LeftDataStatus = $"{(rs?.Rows.Count ?? 0):N0} row(s) in {result.Elapsed.TotalMilliseconds:N0} ms";
        }
        catch (Exception ex)
        {
            LeftDataStatus = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLoadRightData => RightSession is not null && SelectedRightObject is { IsTableLike: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoadRightData))]
    private async Task LoadRightDataAsync()
    {
        if (RightSession is null || SelectedRightObject is null) return;

        IsBusy = true;
        RightDataStatus = "Loading\u2026";
        try
        {
            var limit = Math.Max(1, (int)RowLimit);
            var result = await comparer.LoadTableDataAsync(RightSession, SelectedRightObject, limit);
            var rs = result.ResultSets.FirstOrDefault();
            RightDataResult = rs is null
                ? null
                : ResultSetView.From("Right data", rs, RightSession, QualifiedName(RightSession, SelectedRightObject));
            RightDataStatus = $"{(rs?.Rows.Count ?? 0):N0} row(s) in {result.Elapsed.TotalMilliseconds:N0} ms";
        }
        catch (Exception ex)
        {
            RightDataStatus = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
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
        CompareSchemaCommand.NotifyCanExecuteChanged();
        CompareDataCommand.NotifyCanExecuteChanged();
        LoadLeftDataCommand.NotifyCanExecuteChanged();
        LoadRightDataCommand.NotifyCanExecuteChanged();
    }
}
