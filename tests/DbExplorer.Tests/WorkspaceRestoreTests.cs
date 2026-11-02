using DbExplorer.Application;
using DbExplorer.Application.Workspace;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>Window bounds, main tab, active query tab and connection restored at the next start.</summary>
public class WorkspaceRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-workspace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly ScreenArea Primary = new(0, 0, 1920, 1040, 1.0, IsPrimary: true);
    private static readonly ScreenArea RightHiDpi = new(1920, 0, 2560, 1400, 1.5);

    // ---- Store ----

    [Fact]
    public void State_survives_a_restart()
    {
        var store = new WorkspaceStore(new AppPaths(_root));
        var id = Guid.NewGuid();
        store.Save(new WorkspaceState
        {
            Window = new WindowPlacement(100, 80, 1200, 800, true), MainTab = nameof(AppTab.Query), QueryTabIndex = 2,
            ConnectionId = id, WasConnected = true
        });

        var loaded = new WorkspaceStore(new AppPaths(_root)).Load();
        Assert.Equal(new WindowPlacement(100, 80, 1200, 800, true), loaded.Window);
        Assert.Equal("Query", loaded.MainTab);
        Assert.Equal(2, loaded.QueryTabIndex);
        Assert.Equal(id, loaded.ConnectionId);
        Assert.True(loaded.WasConnected);
    }

    [Fact]
    public void Missing_or_damaged_file_starts_with_defaults()
    {
        var store = new WorkspaceStore(new AppPaths(_root));
        Assert.Null(store.Load().Window);
        File.WriteAllText(store.FilePath, "{ not json");
        var loaded = store.Load();
        Assert.Null(loaded.Window);
        Assert.Null(loaded.ConnectionId);
        Assert.False(loaded.WasConnected);
    }

    [Fact]
    public void Reconnect_setting_is_on_by_default_and_survives_a_restart()
    {
        Assert.True(new AppSettingsService(new AppPaths(_root)).ReconnectOnStart);
        new AppSettingsService(new AppPaths(_root)).SetReconnectOnStart(false);
        Assert.False(new AppSettingsService(new AppPaths(_root)).ReconnectOnStart);
    }

    // ---- Window placement ----

    [Fact]
    public void Nothing_saved_keeps_the_default_placement() =>
        Assert.Null(WindowPlacementFitter.Fit(null, [Primary]));

    [Fact]
    public void Window_fully_on_a_screen_is_restored_as_saved()
    {
        var saved = new WindowPlacement(200, 100, 1300, 820, false);
        Assert.Equal(saved, WindowPlacementFitter.Fit(saved, [Primary, RightHiDpi]));
    }

    [Fact]
    public void Window_on_a_monitor_that_is_gone_opens_centred_by_default()
    {
        var saved = new WindowPlacement(2200, 100, 1300, 820, false);
        Assert.Null(WindowPlacementFitter.Fit(saved, [Primary]));
    }

    [Fact]
    public void Maximized_window_on_a_monitor_that_is_gone_opens_maximized_on_the_primary()
    {
        var fit = WindowPlacementFitter.Fit(new WindowPlacement(2200, 100, 1300, 820, true), [Primary]);
        Assert.NotNull(fit);
        Assert.True(fit.Maximized);
        Assert.InRange(fit.X, Primary.X, Primary.Right - 1);
        Assert.InRange(fit.Y, Primary.Y, Primary.Bottom - 1);
    }

    [Fact]
    public void Window_hanging_off_the_edge_slides_back_on_screen()
    {
        var fit = WindowPlacementFitter.Fit(new WindowPlacement(1500, -50, 1000, 700, false), [Primary])!;
        Assert.Equal(1920 - 1000, fit.X);
        Assert.Equal(0, fit.Y); // title bar reachable
        Assert.Equal(1000, fit.Width);
    }

    [Fact]
    public void Window_larger_than_the_screen_now_shrinks_to_fit()
    {
        var small = new ScreenArea(0, 0, 1366, 728, 1.0, true);
        var fit = WindowPlacementFitter.Fit(new WindowPlacement(0, 0, 1900, 1000, false), [small])!;
        Assert.Equal(1366, fit.Width);
        Assert.Equal(728, fit.Height);
        Assert.Equal(0, fit.X);
        Assert.Equal(0, fit.Y);
    }

    [Fact]
    public void Size_on_a_scaled_monitor_is_measured_in_that_monitors_pixels()
    {
        // 1700 x 900 DIPs at 150 % is 2550 x 1350 px: fits the 2560 x 1400 monitor, so nothing moves.
        var saved = new WindowPlacement(1925, 10, 1700, 900, false);
        Assert.Equal(saved, WindowPlacementFitter.Fit(saved, [Primary, RightHiDpi]));

        // 1800 DIPs would be 2700 px: shrinks to the monitor's 2560 px / 1.5.
        var wide = WindowPlacementFitter.Fit(saved with { Width = 1800 }, [Primary, RightHiDpi])!;
        Assert.Equal(Math.Round(2560 / 1.5), wide.Width);
        Assert.Equal(1920, wide.X);
    }

    [Fact]
    public void Tiny_or_invalid_sizes_are_not_trusted()
    {
        Assert.Null(WindowPlacementFitter.Fit(new WindowPlacement(0, 0, double.NaN, 700, false), [Primary]));
        var tiny = WindowPlacementFitter.Fit(new WindowPlacement(10, 10, 100, 50, false), [Primary])!;
        Assert.Equal(WindowPlacementFitter.MinWidth, tiny.Width);
        Assert.Equal(WindowPlacementFitter.MinHeight, tiny.Height);
    }

    // ---- Connection at start ----

    private static ConnectionProfile Profile(ConnectionEnvironment env = ConnectionEnvironment.None, string? password = "pw") =>
        new() { Name = "local", Host = "localhost", Database = "app", Password = password, Environment = env };

    [Fact]
    public void Open_connection_is_reopened()
    {
        var p = Profile();
        var (action, profile, note) = StartupConnection.Decide(new WorkspaceState { ConnectionId = p.Id, WasConnected = true }, [Profile(), p], true);
        Assert.Equal(StartupConnectionAction.Connect, action);
        Assert.Same(p, profile);
        Assert.Null(note);
    }

    [Fact]
    public void Closed_connection_is_only_selected()
    {
        var p = Profile();
        var (action, profile, _) = StartupConnection.Decide(new WorkspaceState { ConnectionId = p.Id }, [Profile(), p], true);
        Assert.Equal(StartupConnectionAction.Select, action);
        Assert.Same(p, profile);
    }

    [Fact]
    public void Setting_off_only_selects()
    {
        var p = Profile();
        Assert.Equal(StartupConnectionAction.Select,
            StartupConnection.Decide(new WorkspaceState { ConnectionId = p.Id, WasConnected = true }, [p], false).Action);
    }

    [Fact]
    public void Production_is_never_opened_by_itself()
    {
        var p = Profile(ConnectionEnvironment.Production);
        var (action, _, note) = StartupConnection.Decide(new WorkspaceState { ConnectionId = p.Id, WasConnected = true }, [p], true);
        Assert.Equal(StartupConnectionAction.Select, action);
        Assert.Contains("production", note);
    }

    [Fact]
    public void Connection_without_a_saved_password_is_not_opened()
    {
        var p = Profile(password: null);
        var (action, _, note) = StartupConnection.Decide(new WorkspaceState { ConnectionId = p.Id, WasConnected = true }, [p], true);
        Assert.Equal(StartupConnectionAction.Select, action);
        Assert.Contains("password", note);

        p.IntegratedSecurity = true;
        Assert.Equal(StartupConnectionAction.Connect,
            StartupConnection.Decide(new WorkspaceState { ConnectionId = p.Id, WasConnected = true }, [p], true).Action);
    }

    [Fact]
    public void Deleted_connection_falls_back_to_the_default()
    {
        var (action, profile, _) = StartupConnection.Decide(new WorkspaceState { ConnectionId = Guid.NewGuid(), WasConnected = true }, [Profile()], true);
        Assert.Equal(StartupConnectionAction.None, action);
        Assert.Null(profile);
    }
}
