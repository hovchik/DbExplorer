using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>What is running right now, and which cached statements cost the most. Read-only DMV / catalog views.</summary>
public partial class ActivityViewModel : ViewModelBase, ISessionAware
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DatabaseSession? _session;
    private bool _refreshing;

    public ActivityViewModel()
    {
        _timer.Tick += async (_, _) => await RefreshRequestsAsync();
    }

    public IReadOnlyList<QueryStatOrder> Orders { get; } = Enum.GetValues<QueryStatOrder>();

    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private IReadOnlyList<DbActiveRequest> _requests = [];
    [ObservableProperty] private DbActiveRequest? _selectedRequest;
    [ObservableProperty] private string _requestsStatus = "Press Refresh. Needs VIEW SERVER STATE on SQL Server.";

    [ObservableProperty] private QueryStatOrder _selectedOrder = QueryStatOrder.TotalCpu;
    [ObservableProperty] private decimal _topCount = 50;
    [ObservableProperty] private IReadOnlyList<DbQueryStat> _topQueries = [];
    [ObservableProperty] private DbQueryStat? _selectedQuery;
    [ObservableProperty] private string _topQueriesStatus =
        "Press Load. Uses the plan cache on SQL Server and pg_stat_statements on PostgreSQL.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadTopQueriesCommand))]
    private bool _isLoadingTopQueries;

    public void Attach(DatabaseSession? session)
    {
        _session = session;
        AutoRefresh = false;
        Requests = [];
        TopQueries = [];
        RefreshRequestsCommand.NotifyCanExecuteChanged();
        LoadTopQueriesCommand.NotifyCanExecuteChanged();
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value && _session is not null) _timer.Start();
        else _timer.Stop();
    }

    partial void OnSelectedOrderChanged(QueryStatOrder value)
    {
        if (TopQueries.Count > 0) _ = LoadTopQueriesAsync();
    }

    private bool CanRefresh => _session is not null;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshRequestsAsync()
    {
        if (_session is null || _refreshing) return;
        _refreshing = true;
        try
        {
            var previous = SelectedRequest?.SessionId;
            Requests = await _session.Provider.GetActiveRequestsAsync();
            SelectedRequest = Requests.FirstOrDefault(r => r.SessionId == previous);
            var blocked = Requests.Count(r => r.BlockedBy is not null);
            RequestsStatus = $"{Requests.Count:N0} active request(s) · {blocked:N0} blocked · {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            RequestsStatus = "Error: " + ex.Message;
            AutoRefresh = false;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool CanLoadTopQueries => _session is not null && !IsLoadingTopQueries;

    [RelayCommand(CanExecute = nameof(CanLoadTopQueries))]
    private async Task LoadTopQueriesAsync()
    {
        if (_session is null) return;
        IsLoadingTopQueries = true;
        TopQueriesStatus = "Loading…";
        try
        {
            TopQueries = await _session.Provider.GetTopQueriesAsync(SelectedOrder, (int)Math.Max(1, TopCount));
            var order = Converters.HumanizeConverter.Instance.Convert(SelectedOrder, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture);
            TopQueriesStatus = $"Top {TopQueries.Count:N0} statements by {order?.ToString()?.ToLowerInvariant()} · {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            TopQueriesStatus = "Error: " + ex.Message;
        }
        finally
        {
            IsLoadingTopQueries = false;
        }
    }
}
