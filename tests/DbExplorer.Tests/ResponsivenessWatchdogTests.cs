using DbExplorer.Application.Diagnostics;

namespace DbExplorer.Tests;

public class ResponsivenessWatchdogTests
{
    private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly Queue<Action> _uiQueue = new();
    private readonly List<string> _reports = [];

    private ResponsivenessWatchdog Create() =>
        new(_uiQueue.Enqueue, _reports.Add, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), () => _now, start: false);

    private void RunUi()
    {
        while (_uiQueue.TryDequeue(out var action)) action();
    }

    [Fact]
    public void A_responsive_ui_thread_is_never_reported()
    {
        var watchdog = Create();
        for (var i = 0; i < 100; i++)
        {
            watchdog.Check();
            _now += TimeSpan.FromSeconds(2);
            RunUi();
        }
        Assert.Empty(_reports);
        Assert.False(watchdog.IsHung);
    }

    [Fact]
    public void Only_one_ping_is_out_at_a_time()
    {
        var watchdog = Create();
        watchdog.Check();
        _now += TimeSpan.FromSeconds(2);
        watchdog.Check();
        watchdog.Check();
        Assert.Single(_uiQueue);
    }

    [Fact]
    public void A_hang_is_reported_once_and_its_end_once_with_the_window_state()
    {
        var watchdog = Create();
        watchdog.Context = "minimized";
        watchdog.Check();
        for (var i = 0; i < 30; i++)
        {
            _now += TimeSpan.FromSeconds(2);
            watchdog.Check();
        }
        var hang = Assert.Single(_reports);
        Assert.Contains("not answered for 10 s", hang);
        Assert.Contains("minimized", hang);
        Assert.True(watchdog.IsHung);

        watchdog.Context = "normal";
        RunUi();
        Assert.Equal(2, _reports.Count);
        Assert.Contains("answered again after 60 s", _reports[1]);
        Assert.Contains("normal", _reports[1]);
        Assert.False(watchdog.IsHung);

        // Back to normal: the next ping goes out and nothing more is reported.
        watchdog.Check();
        _now += TimeSpan.FromSeconds(1);
        RunUi();
        Assert.Equal(2, _reports.Count);
    }

    [Fact]
    public void A_short_stall_below_the_threshold_is_not_reported()
    {
        var watchdog = Create();
        watchdog.Check();
        _now += TimeSpan.FromSeconds(9);
        watchdog.Check();
        RunUi();
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_failing_report_or_post_never_throws_from_the_timer()
    {
        var watchdog = new ResponsivenessWatchdog(_ => throw new InvalidOperationException("dispatcher gone"),
            _ => throw new IOException("disk full"), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), () => _now, start: false);
        watchdog.Check();
        _now += TimeSpan.FromSeconds(20);
        watchdog.Check();
        Assert.True(watchdog.IsHung);
    }
}
