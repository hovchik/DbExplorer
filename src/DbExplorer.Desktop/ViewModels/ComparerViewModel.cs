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

    public ObservableCollection<ConnectionProfile> Profiles { get; set; } = [];

    [ObservableProperty] private ConnectionProfile? _leftProfile;
    [ObservableProperty] private ConnectionProfile? _rightProfile;
    [ObservableProperty] private DatabaseSession? _leftSession;
    [ObservableProperty] private DatabaseSession? _rightSession;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    [ObservableProperty] private IReadOnlyList<DbObject> _leftObjects = [];
    [ObservableProperty] private IReadOnlyList<DbObject> _rightObjects = [];
    [ObservableProperty] private DbObject? _selectedLeftObject;
    [ObservableProperty] private DbObject? _selectedRightObject;

    [ObservableProperty] private IReadOnlyList<DiffLine> _schemaDiff = [];
    [ObservableProperty] private string _schemaStatus = "";

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

    public void Attach(DatabaseSession? session) => _mainSession = session;

    partial void OnLeftProfileChanged(ConnectionProfile? value) => ConnectLeftCommand.NotifyCanExecuteChanged();

    partial void OnRightProfileChanged(ConnectionProfile? value) => ConnectRightCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

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

    private async Task SetLeftSessionAsync(DatabaseSession session)
    {
        if (LeftSession is not null && LeftSession != _mainSession) await LeftSession.DisposeAsync();
        LeftSession = session;
        LeftObjects = session.Snapshot.Objects;
        SelectedLeftObject = null;
        RefreshCommands();
    }

    private async Task SetRightSessionAsync(DatabaseSession session)
    {
        if (RightSession is not null && RightSession != _mainSession) await RightSession.DisposeAsync();
        RightSession = session;
        RightObjects = session.Snapshot.Objects;
        SelectedRightObject = null;
        RefreshCommands();
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
            var diff = await comparer.CompareSchemaAsync(LeftSession, SelectedLeftObject, RightSession, SelectedRightObject);
            SchemaDiff = diff;
            var added = diff.Count(d => d.Kind == DiffLineKind.Added);
            var removed = diff.Count(d => d.Kind == DiffLineKind.Removed);
            SchemaStatus = added == 0 && removed == 0
                ? "Definitions are identical"
                : $"{removed} line(s) only on left \u00B7 {added} line(s) only on right";
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
                : new ResultSetView("Left data", rs.Columns, rs.Rows.Select(r => new ResultRow(r)).ToList());
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
                : new ResultSetView("Right data", rs.Columns, rs.Rows.Select(r => new ResultRow(r)).ToList());
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

    private void RefreshCommands()
    {
        ConnectLeftCommand.NotifyCanExecuteChanged();
        ConnectRightCommand.NotifyCanExecuteChanged();
        UseCurrentAsLeftCommand.NotifyCanExecuteChanged();
        UseCurrentAsRightCommand.NotifyCanExecuteChanged();
        CompareSchemaCommand.NotifyCanExecuteChanged();
        CompareDataCommand.NotifyCanExecuteChanged();
        LoadLeftDataCommand.NotifyCanExecuteChanged();
        LoadRightDataCommand.NotifyCanExecuteChanged();
    }
}
