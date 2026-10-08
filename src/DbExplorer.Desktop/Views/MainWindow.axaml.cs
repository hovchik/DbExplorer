using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class MainWindow : Window
{
    private WindowNotificationManager? _notifications;
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Query.RunFinished -= OnRunFinished;
                _vm.ShowTeamRequested -= OnShowTeam;
            }
            _vm = DataContext as MainWindowViewModel;
            if (_vm is not null)
            {
                _vm.Query.RunFinished += OnRunFinished;
                _vm.ShowTeamRequested += OnShowTeam;
            }
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;

        var state = change.GetNewValue<WindowState>();
        if (App.Watchdog is { } watchdog) watchdog.Context = state.ToString().ToLowerInvariant();
        if (change.GetOldValue<WindowState>() == WindowState.Minimized && state != WindowState.Minimized)
        {
            // Back from the taskbar: ask for a fresh layout and frame instead of relying on the one from before the
            // minimize, which the compositor may have dropped while the window was hidden (the "only the frame" symptom).
            Dispatcher.UIThread.Post(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                InvalidateVisual();
                if (Content is Visual content) content.InvalidateVisual();
            }, DispatcherPriority.Render);
        }
    }

    private TeamWindow? _team;

    /// <summary>One Team window, left open beside the main one while the user works.</summary>
    private void OnShowTeam(TeamViewModel team)
    {
        if (_team is { } open)
        {
            open.Activate();
            return;
        }
        _team = new TeamWindow { DataContext = team };
        _team.Closed += (_, _) => _team = null;
        _team.Show(this);
        _ = team.RefreshAsync();
    }

    /// <summary>
    /// A long query finished while the user looked elsewhere (another app, tab or Query tab): a toast that opens its
    /// tab when clicked, and on Windows a flashing taskbar button until the window is back in front.
    /// </summary>
    private void OnRunFinished(QueryViewModel tab, TimeSpan elapsed, RunOutcome outcome)
    {
        if (_vm is null) return;
        var tabVisible = _vm.SelectedTab == AppTab.Query && ReferenceEquals(_vm.Query.SelectedDocument, tab);
        if (!RunNotification.ShouldNotify(elapsed, IsActive, tabVisible)) return;

        _notifications ??= new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
        var type = outcome switch
        {
            RunOutcome.Succeeded => NotificationType.Success,
            RunOutcome.Failed => NotificationType.Error,
            _ => NotificationType.Warning
        };
        _notifications.Show(new Notification(
            RunNotification.Message(tab.Title, elapsed, outcome),
            tab.Status + "\nClick to open the tab.",
            type,
            TimeSpan.FromSeconds(15),
            onClick: () =>
            {
                _vm.SelectedTab = AppTab.Query;
                _vm.Query.SelectedDocument = tab;
                Activate();
            }));

        if (!IsActive) FlashTaskbar();
    }

    private void FlashTaskbar()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = FlashTray | FlashUntilForeground,
            Count = uint.MaxValue
        };
        FlashWindowEx(ref info);
    }

    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
