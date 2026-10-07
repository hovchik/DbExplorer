using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Diagnostics;
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
    [ObservableProperty] private bool _showTree;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockingTree))]
    private IReadOnlyList<BlockingNode> _blockingTree = [];

    /// <summary>For the view: the tree is often an array, which has no Count for a binding to read.</summary>
    public bool HasBlockingTree => BlockingTree.Count > 0;
    [ObservableProperty] private BlockingNode? _selectedNode;
    [ObservableProperty] private string? _selectedSql;

    partial void OnSelectedLockChanged(DbLock? value) => SelectedSql = value?.SqlText;

    partial void OnSelectedNodeChanged(BlockingNode? value) => SelectedSql = value?.SqlText;

    partial void OnShowTreeChanged(bool value)
    {
        if (value) ShowRecorder = false;
        SelectedSql = value ? SelectedNode?.SqlText : SelectedLock?.SqlText;
        OnPropertyChanged(nameof(ShowList));
    }

    partial void OnShowRecorderChanged(bool value)
    {
        if (value) ShowTree = false;
        OnPropertyChanged(nameof(ShowList));
    }

    /// <summary>The flat lock list is shown (neither the blocking tree nor the flight recorder).</summary>
    public bool ShowList
    {
        get => !ShowTree && !ShowRecorder;
        set
        {
            if (!value) return;
            ShowTree = false;
            ShowRecorder = false;
        }
    }
    [ObservableProperty] private string _status = "Needs VIEW SERVER STATE on SQL Server.";

    // ----- Flight recorder: every refresh is kept (an hour at the 5 s auto-refresh) and replayable as incidents -----

    private readonly BlockingRecorder _recorder = new();

    [ObservableProperty] private bool _showRecorder;
    [ObservableProperty] private bool _recordHistory = true;
    [ObservableProperty] private IReadOnlyList<BlockingIncident> _incidents = [];
    [ObservableProperty] private BlockingIncident? _selectedIncident;
    [ObservableProperty] private int _replayIndex;
    [ObservableProperty] private int _replayMaximum;
    [ObservableProperty] private IReadOnlyList<BlockingNode> _replayTree = [];
    [ObservableProperty] private string _replayInfo = "";
    [ObservableProperty] private string _recorderStatus = "Turn on auto-refresh to record: blocking seen by any refresh is kept for replay.";

    public event Func<string, Task>? CopyRequested;

    partial void OnSelectedIncidentChanged(BlockingIncident? value)
    {
        ReplayMaximum = Math.Max(0, (value?.Samples.Count ?? 1) - 1);
        // Start at the worst moment.
        ReplayIndex = value is null ? 0 : value.Samples.Select((s, i) => (s, i)).OrderByDescending(x => x.s.WaitingSessions).First().i;
        OnReplayIndexChanged(ReplayIndex);
        CopyIncidentCommand.NotifyCanExecuteChanged();
    }

    partial void OnReplayIndexChanged(int value)
    {
        if (SelectedIncident is not { } incident || incident.Samples.Count == 0)
        {
            ReplayTree = [];
            ReplayInfo = "";
            return;
        }
        var sample = incident.Samples[Math.Clamp(value, 0, incident.Samples.Count - 1)];
        ReplayTree = BlockingChainBuilder.Build(sample.Locks);
        ReplayInfo = $"{sample.At:HH:mm:ss} · sample {value + 1} of {incident.Samples.Count} · {sample.WaitingSessions} waiting";
    }

    private void Record(IReadOnlyList<DbLock> locks)
    {
        if (!RecordHistory) return;
        _recorder.Add(DateTimeOffset.Now, locks);
        // The list is rebuilt from the samples; keep the incident being replayed and where the slider is.
        var selected = SelectedIncident?.Start;
        var position = ReplayIndex;
        Incidents = _recorder.Incidents(_timer.Interval * 3);
        if (selected is not null && Incidents.FirstOrDefault(i => i.Start == selected) is { } same)
        {
            SelectedIncident = same;
            ReplayIndex = Math.Min(position, ReplayMaximum);
        }
        var span = _recorder.Samples is { Count: > 0 } samples ? samples[^1].At - samples[0].At : TimeSpan.Zero;
        RecorderStatus = $"{_recorder.Count:N0} sample(s) over {(int)span.TotalMinutes} min {span.Seconds} s · {Incidents.Count} blocking incident(s)" +
                         (AutoRefresh ? " · recording" : " · turn on auto-refresh to keep recording");
    }

    [RelayCommand]
    private void ClearRecording()
    {
        _recorder.Clear();
        Incidents = [];
        SelectedIncident = null;
        RecorderStatus = "Cleared.";
    }

    private bool HasIncident => SelectedIncident is not null;

    [RelayCommand(CanExecute = nameof(HasIncident))]
    private async Task CopyIncidentAsync()
    {
        if (SelectedIncident is { } incident && CopyRequested is { } copy) await copy(BlockingRecorder.Report(incident));
    }

    public void Attach(DatabaseSession? session)
    {
        _session = session;
        AutoRefresh = false;
        // A refresh of the previous session may still be running; its result is dropped and must not block this one.
        _refreshing = false;
        _all = [];
        Locks = [];
        BlockingTree = [];
        ClearRecording();
        RecorderStatus = "Turn on auto-refresh to record: blocking seen by any refresh is kept for replay.";
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

    [RelayCommand(CanExecute = nameof(CanRefresh), AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        if (_session is not { } session || _refreshing) return;
        _refreshing = true;
        try
        {
            var all = await session.Provider.GetLocksAsync();
            if (!ReferenceEquals(session, _session)) return;
            _all = all;
            ApplyFilter();
            Record(_all);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(session, _session)) return;
            Status = "Error: " + ex.Message;
            AutoRefresh = false;
        }
        finally
        {
            if (ReferenceEquals(session, _session)) _refreshing = false;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<DbLock> q = _all;
        if (HideNoise) q = q.Where(l => !NoiseResources.Contains(l.ResourceType));

        var blockers = _all.Where(l => l.BlockedBy is not null).Select(l => l.BlockedBy!.Value).ToHashSet();
        if (BlockingOnly) q = q.Where(l => l.IsWaiting || l.BlockedBy is not null || blockers.Contains(l.SessionId));

        Locks = q.ToList();
        var previous = SelectedNode?.SessionId;
        BlockingTree = BlockingChainBuilder.Build(_all);
        SelectedNode = BlockingTree.FirstOrDefault(n => n.SessionId == previous);
        var waiting = _all.Count(l => l.IsWaiting);
        var heads = BlockingTree.Count == 0 ? "" : " · head blockers: " + string.Join(", ", BlockingTree.Select(n => n.SessionId));
        Status = $"{Locks.Count:N0} locks shown · {waiting:N0} waiting · {blockers.Count:N0} blocking sessions{heads} · {DateTime.Now:T}";
    }
}
