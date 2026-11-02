using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Application.Query;
using DbExplorer.Application.Workspace;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class MainWindow : Window
{
    private WindowNotificationManager? _notifications;
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        MainTabs.TemplateApplied += OnMainTabsTemplateApplied;
        MainTabs.SizeChanged += (_, e) => { if (e.WidthChanged) FitMainTabHeaders(); };
        MainTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, MainTabs)) Dispatcher.UIThread.Post(RevealSelectedMainTab, DispatcherPriority.Loaded);
        };
        NavigatorSplitter.DragCompleted += (_, _) =>
        {
            if (_vm is not null && _vm.ShowNavigator) _vm.NavigatorWidth = WorkArea.ColumnDefinitions[0].ActualWidth;
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Query.RunFinished -= OnRunFinished;
                _vm.ShowTeamRequested -= OnShowTeam;
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
            _vm = DataContext as MainWindowViewModel;
            if (_vm is not null)
            {
                _vm.Query.RunFinished += OnRunFinished;
                _vm.ShowTeamRequested += OnShowTeam;
                _vm.PropertyChanged += OnViewModelPropertyChanged;
                _vm.SetClipboardText = async text =>
                {
                    if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
                };
            }
            ApplyNavigatorWidth();
            if (_vm is not null && !_placementApplied)
            {
                _placementApplied = true;
                ApplySavedPlacement(_vm.SavedWorkspace.Window);
            }
        };
        PositionChanged += (_, _) => ScheduleTrackPlacement();
        Resized += (_, _) => ScheduleTrackPlacement();
        Closing += (_, _) => TrackPlacement();

        // .sql files dropped anywhere on the window open in Query tabs. Tunnelling, so the editor (which takes text
        // drops) and the canvases (which take tables) never see a file drop.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnFileDrop, RoutingStrategies.Tunnel);
        // Back in the window: a script another program changed meanwhile is reloaded, or the tab says so.
        Activated += (_, _) => { if (_vm is not null) _ = _vm.Query.CheckFilesOnDiskAsync(); };
    }

    /// <summary>Shows the window in front for files sent from a later start (restored when it was minimized).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = _maximizedBeforeMinimize ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    private static void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (_vm is null || !e.Data.Contains(DataFormats.Files)) return;
        e.Handled = true;
        var paths = (e.Data.GetFiles() ?? []).OfType<IStorageFile>().Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0)
        {
            _vm.StatusText = "Only files can be dropped here; folders are not opened.";
            return;
        }
        await _vm.OpenFilesAsync(paths);
    }

    // ---- Window placement: saved on close, restored (fitted to today's monitors) at the next start ----

    private bool _placementApplied;
    private bool _trackPending;
    private WindowPlacement? _normalBounds;
    private bool _maximizedBeforeMinimize;

    private void ApplySavedPlacement(WindowPlacement? saved)
    {
        if (Screens is not { } screens) return;
        var areas = screens.All
            .Select(s => new ScreenArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height, s.Scaling, s.IsPrimary))
            .ToList();
        if (WindowPlacementFitter.Fit(saved, areas) is not { } fit) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(fit.X, fit.Y);
        Width = fit.Width;
        Height = fit.Height;
        _normalBounds = fit with { Maximized = false };
        // Placed on its monitor first, so maximizing fills that monitor and un-maximizing returns to these bounds.
        if (fit.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Moves and resizes come in bursts, and a maximize reports its new position before the new state:
    /// read the bounds once things settle.</summary>
    private void ScheduleTrackPlacement()
    {
        if (_trackPending) return;
        _trackPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _trackPending = false;
            TrackPlacement();
        }, DispatcherPriority.Background);
    }

    private void TrackPlacement()
    {
        if (_vm is null) return;
        if (WindowState == WindowState.Normal && ClientSize.Width > 0 && ClientSize.Height > 0)
            _normalBounds = new WindowPlacement(Position.X, Position.Y, Math.Round(ClientSize.Width), Math.Round(ClientSize.Height), false);
        if (_normalBounds is null) return;
        var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen ||
                        (WindowState == WindowState.Minimized && _maximizedBeforeMinimize);
        _vm.WindowPlacement = _normalBounds with { Maximized = maximized };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ShowNavigator)) ApplyNavigatorWidth();
    }

    /// <summary>The navigator column: its saved width while shown, nothing while hidden (the tabs take it all).</summary>
    private void ApplyNavigatorWidth()
    {
        var column = WorkArea.ColumnDefinitions[0];
        if (_vm is { ShowNavigator: true } vm)
        {
            column.MinWidth = Application.AppSettingsService.MinNavigatorWidth;
            column.MaxWidth = Application.AppSettingsService.MaxNavigatorWidth;
            column.Width = new GridLength(vm.NavigatorWidth);
        }
        else
        {
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
    }

    // ---- Main tab headers: one row; smaller headers when the full-size ones overflow, then sideways scrolling ----

    private ScrollViewer? _mainTabScroll;
    private Button? _allTabsButton;
    private double _compactBelow;

    private void OnMainTabsTemplateApplied(object? sender, Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        _mainTabScroll = e.NameScope.Find<ScrollViewer>("PART_TabScroll");
        _allTabsButton = e.NameScope.Find<Button>("PART_AllTabs");
        if (_mainTabScroll is not null)
        {
            _mainTabScroll.AddHandler(PointerWheelChangedEvent, OnMainTabsWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _mainTabScroll.ScrollChanged += (_, _) => UpdateAllTabsButton();
        }
        if (_allTabsButton is not null) _allTabsButton.Click += (_, _) => ShowAllTabsMenu();
    }

    private bool MainTabsOverflow => _mainTabScroll is { } scroll && scroll.Extent.Width > scroll.Viewport.Width + 1;

    private void FitMainTabHeaders()
    {
        var width = MainTabs.Bounds.Width;
        if (MainTabs.Classes.Contains("compact"))
        {
            if (width <= _compactBelow) return;
            MainTabs.Classes.Remove("compact"); // wider now: try full size again, and shrink back if it still overflows
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (MainTabsOverflow)
            {
                _compactBelow = MainTabs.Bounds.Width;
                MainTabs.Classes.Add("compact");
            }
            Dispatcher.UIThread.Post(() => { UpdateAllTabsButton(); RevealSelectedMainTab(); }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    private void UpdateAllTabsButton()
    {
        if (_allTabsButton is not null) _allTabsButton.IsVisible = MainTabsOverflow;
    }

    private void OnMainTabsWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if (_mainTabScroll is not { } scroll || !MainTabsOverflow) return;
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        scroll.Offset = scroll.Offset.WithX(Math.Clamp(scroll.Offset.X - delta * 60, 0, scroll.Extent.Width - scroll.Viewport.Width));
        e.Handled = true;
    }

    private void RevealSelectedMainTab()
    {
        if (MainTabs.ContainerFromIndex(MainTabs.SelectedIndex) is { } tab) tab.BringIntoView();
    }

    /// <summary>Every main tab in a menu, so a tab scrolled out of view is one click away.</summary>
    private void ShowAllTabsMenu()
    {
        if (_allTabsButton is null) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        for (var i = 0; i < MainTabs.ItemCount; i++)
        {
            if (MainTabs.ContainerFromIndex(i) is not TabItem { IsVisible: true } tab) continue;
            var index = i;
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = tab.Header?.ToString() ?? "" },
                IsEnabled = tab.IsEnabled,
                FontWeight = index == MainTabs.SelectedIndex ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => MainTabs.SelectedIndex = index)
            });
        }
        menu.ShowAt(_allTabsButton);
    }

    private bool _askingToClose;

    /// <summary>
    /// Closing the window with an open transaction, uncommitted grid edits, a running query or other work that only
    /// lives on the connection asks first and lists what would be lost. Once the user agrees, the shutdown in
    /// <see cref="App"/> does not ask again.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _vm is not { QuitConfirmed: false } vm ||
            e.CloseReason is not (WindowCloseReason.WindowClosing or WindowCloseReason.ApplicationShutdown) ||
            vm.DescribeUnsavedWork(LeaveReason.Quit).Count == 0)
            return;

        e.Cancel = true;
        if (_askingToClose) return;
        _askingToClose = true;
        try
        {
            if (await vm.ConfirmLeaveAsync(LeaveReason.Quit)) Close();
        }
        finally
        {
            _askingToClose = false;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;

        var state = change.GetNewValue<WindowState>();
        if (state == WindowState.Minimized)
            _maximizedBeforeMinimize = change.GetOldValue<WindowState>() is WindowState.Maximized or WindowState.FullScreen;
        ScheduleTrackPlacement();
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
