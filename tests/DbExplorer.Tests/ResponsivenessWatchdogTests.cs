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
        for (var i = 0; i < 10; i++)
        {
            _now += TimeSpan.FromSeconds(2);
            watchdog.Check();
        }
        Assert.True(watchdog.IsHung);
    }

    [Fact]
    public void A_pc_that_slept_with_a_ping_out_is_not_reported_as_a_hang()
    {
        var watchdog = Create();
        watchdog.Check();
        // The PC sleeps: no timer tick for 40 minutes, then the first tick after waking.
        _now += TimeSpan.FromMinutes(40);
        watchdog.Check();
        Assert.Empty(_reports);
        Assert.False(watchdog.IsHung);

        // The UI thread answers within the normal interval after waking: nothing at all is logged.
        _now += TimeSpan.FromSeconds(2);
        watchdog.Check();
        RunUi();
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_real_hang_after_waking_is_still_reported()
    {
        var watchdog = Create();
        watchdog.Check();
        _now += TimeSpan.FromMinutes(40);
        watchdog.Check();
        for (var i = 0; i < 6; i++)
        {
            _now += TimeSpan.FromSeconds(2);
            watchdog.Check();
        }
        Assert.Contains("not answered for 10 s", Assert.Single(_reports));
    }

    [Fact]
    public void The_report_carries_the_clues_for_the_time_the_ui_thread_last_answered()
    {
        DateTimeOffset? asked = null;
        var start = _now;
        var watchdog = new ResponsivenessWatchdog(_uiQueue.Enqueue, _reports.Add, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10),
            () => _now, start: false)
        {
            Clues = since => { asked = since; return "Last input before it: F5 in QueryView, 1 s before."; }
        };
        watchdog.Check();
        for (var i = 0; i < 5; i++)
        {
            _now += TimeSpan.FromSeconds(2);
            watchdog.Check();
        }
        Assert.EndsWith("does. Last input before it: F5 in QueryView, 1 s before.", Assert.Single(_reports));
        Assert.Equal(start, asked);
    }

    [Fact]
    public void A_throwing_clue_still_leaves_the_hang_line()
    {
        var watchdog = new ResponsivenessWatchdog(_uiQueue.Enqueue, _reports.Add, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10),
            () => _now, start: false) { Clues = _ => throw new InvalidOperationException() };
        watchdog.Check();
        for (var i = 0; i < 5; i++)
        {
            _now += TimeSpan.FromSeconds(2);
            watchdog.Check();
        }
        Assert.Single(_reports);
    }

    [Fact]
    public void A_long_hang_calls_back_once_per_hang()
    {
        var calls = new List<TimeSpan>();
        var watchdog = new ResponsivenessWatchdog(_uiQueue.Enqueue, _reports.Add, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10),
            () => _now, start: false) { LongHang = TimeSpan.FromSeconds(30), OnLongHang = calls.Add };
        for (var round = 0; round < 2; round++)
        {
            watchdog.Check();
            for (var i = 0; i < 30; i++)
            {
                _now += TimeSpan.FromSeconds(2);
                watchdog.Check();
            }
            RunUi();
        }
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(TimeSpan.FromSeconds(30), c));
    }

    [Fact]
    public void A_hang_shorter_than_the_long_threshold_does_not_call_back()
    {
        var calls = 0;
        var watchdog = new ResponsivenessWatchdog(_uiQueue.Enqueue, _reports.Add, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10),
            () => _now, start: false) { OnLongHang = _ => calls++ };
        watchdog.Check();
        for (var i = 0; i < 10; i++)
        {
            _now += TimeSpan.FromSeconds(2);
            watchdog.Check();
        }
        RunUi();
        Assert.Equal(2, _reports.Count);
        Assert.Equal(0, calls);
    }
}
