using System.Runtime.InteropServices;
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
            if (_vm is not null) _vm.Query.RunFinished -= OnRunFinished;
            _vm = DataContext as MainWindowViewModel;
            if (_vm is not null) _vm.Query.RunFinished += OnRunFinished;
        };
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
