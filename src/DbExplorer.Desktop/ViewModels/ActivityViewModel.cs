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
    private int _topQueriesVersion;

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
        // Reads of the previous session may still be running; their results are dropped and must not block this one.
        _refreshing = false;
        _topQueriesVersion++;
        IsLoadingTopQueries = false;
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

    [RelayCommand(CanExecute = nameof(CanRefresh), AllowConcurrentExecutions = true)]
    private async Task RefreshRequestsAsync()
    {
        if (_session is not { } session || _refreshing) return;
        _refreshing = true;
        try
        {
            var previous = SelectedRequest?.SessionId;
            var requests = await session.Provider.GetActiveRequestsAsync();
            if (!ReferenceEquals(session, _session)) return;
            Requests = requests;
            SelectedRequest = Requests.FirstOrDefault(r => r.SessionId == previous);
            var blocked = Requests.Count(r => r.BlockedBy is not null);
            RequestsStatus = $"{Requests.Count:N0} active request(s) · {blocked:N0} blocked · {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(session, _session)) return;
            RequestsStatus = "Error: " + ex.Message;
            AutoRefresh = false;
        }
        finally
        {
            if (ReferenceEquals(session, _session)) _refreshing = false;
        }
    }

    private bool CanLoadTopQueries => _session is not null && !IsLoadingTopQueries;

    [RelayCommand(CanExecute = nameof(CanLoadTopQueries), AllowConcurrentExecutions = true)]
    private async Task LoadTopQueriesAsync()
    {
        if (_session is not { } session) return;
        // A newer load (another order picked meanwhile, or another session) wins over this one.
        var version = ++_topQueriesVersion;
        IsLoadingTopQueries = true;
        TopQueriesStatus = "Loading…";
        try
        {
            var queries = await session.Provider.GetTopQueriesAsync(SelectedOrder, (int)Math.Max(1, TopCount));
            if (version != _topQueriesVersion) return;
            TopQueries = queries;
            var order = Converters.HumanizeConverter.Instance.Convert(SelectedOrder, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture);
            TopQueriesStatus = $"Top {TopQueries.Count:N0} statements by {order?.ToString()?.ToLowerInvariant()} · {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            if (version == _topQueriesVersion) TopQueriesStatus = "Error: " + ex.Message;
        }
        finally
        {
            if (version == _topQueriesVersion) IsLoadingTopQueries = false;
        }
    }
}
