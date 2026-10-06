using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Re-running the tab's last query on a timer.</summary>
public partial class QueryViewModel
{
    /// <summary>The last run, after its parameters were filled and it was confirmed: what auto refresh repeats.</summary>
    private sealed record RepeatableRun(string Sql, int StartOffset, IReadOnlyList<string> Targets);

    private RepeatableRun? _lastRun;
    private DispatcherTimer? _refreshTimer;
    private DateTime? _lastRefreshAt;

    /// <summary>True while a timer run is going; such runs are not added to the history.</summary>
    private bool _isRefreshRun;

    public IReadOnlyList<AutoRefreshInterval> AutoRefreshIntervals => QueryAutoRefresh.Intervals;

    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private AutoRefreshInterval _autoRefreshInterval = QueryAutoRefresh.DefaultInterval;

    /// <summary>When the last timer run happened, shown next to the toggle while it is on.</summary>
    public string AutoRefreshInfo => !AutoRefresh ? ""
        : IsRunning && _isRefreshRun ? "refreshing…"
        : _lastRefreshAt is { } at ? $"refreshed {at:HH:mm:ss}"
        : $"every {AutoRefreshInterval.Label}";

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value && (_session is null ? "Connect to a server first." : QueryAutoRefresh.WhyNotRepeatable(_lastRun?.Sql)) is { } why)
        {
            Status = why;
            Dispatcher.UIThread.Post(() => AutoRefresh = false);
            return;
        }
        _lastRefreshAt = null;
        RestartRefreshTimer();
        OnPropertyChanged(nameof(AutoRefreshInfo));
    }

    partial void OnAutoRefreshIntervalChanged(AutoRefreshInterval value)
    {
        RestartRefreshTimer();
        OnPropertyChanged(nameof(AutoRefreshInfo));
    }

    private void RestartRefreshTimer()
    {
        _refreshTimer?.Stop();
        if (!AutoRefresh) return;
        if (_refreshTimer is null)
        {
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        }
        _refreshTimer.Interval = AutoRefreshInterval.Period;
        _refreshTimer.Start();
    }

    /// <summary>
    /// One timer tick: repeats the last run without asking anything. A tick is skipped while a run is still going or the
    /// results hold uncommitted edits; auto refresh turns itself off when the query fails.
    /// </summary>
    private async Task RefreshAsync()
    {
        if (!AutoRefresh || IsRunning || _session is not { } session || _lastRun is not { } run) return;
        if (ResultEditing.HasEdits(ResultSets)) return;
        // A manual run since auto refresh was turned on replaced the query it repeats; never repeat one that writes.
        if (QueryAutoRefresh.WhyNotRepeatable(run.Sql) is { } why)
        {
            AutoRefresh = false;
            Status = why;
            return;
        }

        _isRefreshRun = true;
        OnPropertyChanged(nameof(AutoRefreshInfo));
        bool succeeded;
        try
        {
            succeeded = await RunAsync(session, run.Sql, run.StartOffset, run.Targets);
        }
        finally
        {
            _isRefreshRun = false;
        }

        if (succeeded) _lastRefreshAt = DateTime.Now;
        else if (AutoRefresh)
        {
            AutoRefresh = false;
            Status += " · auto refresh stopped";
        }
        OnPropertyChanged(nameof(AutoRefreshInfo));
    }
}
