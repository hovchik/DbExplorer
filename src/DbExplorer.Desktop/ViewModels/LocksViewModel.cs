using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

public partial class LocksViewModel : ViewModelBase, ISessionAware
{
    private static readonly HashSet<string> NoiseResources = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATABASE",    // SQL Server: every session holds a shared DATABASE lock
        "virtualxid"   // Postgres: every transaction holds its own virtual xid
    };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DatabaseSession? _session;
    private IReadOnlyList<DbLock> _all = [];
    private bool _refreshing;

    public LocksViewModel()
    {
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private bool _hideNoise = true;
    [ObservableProperty] private bool _blockingOnly;
    [ObservableProperty] private IReadOnlyList<DbLock> _locks = [];
    [ObservableProperty] private DbLock? _selectedLock;
    [ObservableProperty] private string _status = "Needs VIEW SERVER STATE on SQL Server.";

    public void Attach(DatabaseSession? session)
    {
        _session = session;
        AutoRefresh = false;
        _all = [];
        Locks = [];
        RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value && _session is not null) _timer.Start();
        else _timer.Stop();
    }

    partial void OnHideNoiseChanged(bool value) => ApplyFilter();

    partial void OnBlockingOnlyChanged(bool value) => ApplyFilter();

    private bool CanRefresh => _session is not null;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_session is null || _refreshing) return;
        _refreshing = true;
        try
        {
            _all = await _session.Provider.GetLocksAsync();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            AutoRefresh = false;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<DbLock> q = _all;
        if (HideNoise) q = q.Where(l => !NoiseResources.Contains(l.ResourceType));

        var blockers = _all.Where(l => l.BlockedBy is not null).Select(l => l.BlockedBy!.Value).ToHashSet();
        if (BlockingOnly) q = q.Where(l => l.IsWaiting || l.BlockedBy is not null || blockers.Contains(l.SessionId));

        Locks = q.ToList();
        var waiting = _all.Count(l => l.IsWaiting);
        Status = $"{Locks.Count:N0} locks shown · {waiting:N0} waiting · {blockers.Count:N0} blocking sessions · {DateTime.Now:T}";
    }
}
