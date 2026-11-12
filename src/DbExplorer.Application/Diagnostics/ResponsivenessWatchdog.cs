namespace DbExplorer.Application.Diagnostics;

/// <summary>
/// Notices when the UI thread stops answering. Every <see cref="Interval"/> a ping is posted to the UI thread; when one
/// has gone unanswered for <see cref="Threshold"/> that is reported once, and again when the thread answers. The log then
/// tells a hung UI thread (both lines, with how long it was stuck) apart from a window that merely stopped repainting
/// (no line at all), which look the same to the user.
/// A tick that comes far later than <see cref="Interval"/> means the whole process was paused (the PC slept or hibernated,
/// or a debugger stopped it), not the UI thread alone, so the outstanding ping's clock starts again instead of reporting a
/// hang that never happened.
/// </summary>
public sealed class ResponsivenessWatchdog : IDisposable
{
    private readonly Action<Action> _post;
    private readonly Action<string> _report;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer? _timer;
    private readonly object _gate = new();
    private DateTimeOffset? _pingSentAt;
    private DateTimeOffset? _lastCheck;
    private bool _reported;
    private bool _longReported;
    private int _generation;

    /// <param name="post">Queues an action on the UI thread (never runs it inline).</param>
    /// <param name="report">Receives one line per hang and one per recovery; called on a timer or the UI thread.</param>
    /// <param name="start">False leaves the checks to <see cref="Check"/> (tests).</param>
    public ResponsivenessWatchdog(Action<Action> post, Action<string> report, TimeSpan? interval = null, TimeSpan? threshold = null,
        Func<DateTimeOffset>? clock = null, bool start = true)
    {
        _post = post;
        _report = report;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Interval = interval ?? TimeSpan.FromSeconds(2);
        Threshold = threshold ?? TimeSpan.FromSeconds(10);
        if (start) _timer = new Timer(_ => Check(), null, Interval, Interval);
    }

    public TimeSpan Interval { get; }
    public TimeSpan Threshold { get; }

    /// <summary>What the window was doing (e.g. "minimized"), added to the report. Set from the UI thread.</summary>
    public volatile string? Context;

    /// <summary>Extra text for the hang report, given the time the UI thread was last known to answer (e.g. the last
    /// input before it). Called on the timer thread, so it must not touch the UI.</summary>
    public Func<DateTimeOffset, string?>? Clues { get; init; }

    /// <summary>How long a hang lasts before <see cref="OnLongHang"/> is called (once per hang).</summary>
    public TimeSpan LongHang { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Called on the timer thread once a hang has lasted <see cref="LongHang"/>, e.g. to save a dump while the
    /// UI thread is still stuck where it is.</summary>
    public Action<TimeSpan>? OnLongHang { get; init; }

    /// <summary>A gap between two ticks this much longer than the interval means the process itself was paused.</summary>
    private TimeSpan PauseGap => Interval * 4 > Threshold ? Interval * 4 : Threshold;

    /// <summary>Whether a hang is being reported right now (between the two report lines).</summary>
    public bool IsHung
    {
        get { lock (_gate) return _reported; }
    }

    /// <summary>One tick: sends a ping when none is out, or reports the one that has been out too long.</summary>
    public void Check()
    {
        string? line = null;
        Action? ping = null;
        TimeSpan? longHang = null;
        DateTimeOffset? stuckSince = null;
        lock (_gate)
        {
            var now = _clock();
            var paused = _lastCheck is { } last && now - last >= PauseGap;
            _lastCheck = now;
            if (paused && _pingSentAt is not null && !_reported)
            {
                // Woken from sleep: the UI thread has not had a chance to answer yet. Give it a fresh interval.
                _pingSentAt = now;
            }
            else if (_pingSentAt is not { } sent)
            {
                _pingSentAt = now;
                var generation = ++_generation;
                ping = () => Answered(generation);
            }
            else if (!_reported && now - sent >= Threshold)
            {
                _reported = true;
                stuckSince = sent;
                line = $"The UI thread has not answered for {Seconds(now - sent)} s{Describe()}. " +
                       "The window cannot repaint until it does.";
            }
            else if (_reported && !_longReported && now - sent >= LongHang)
            {
                _longReported = true;
                longHang = now - sent;
            }
        }
        if (line is not null)
        {
            if (Clues is { } clues && stuckSince is { } since)
            {
                try
                {
                    if (clues(since) is { Length: > 0 } extra) line += " " + extra;
                }
                catch { }
            }
            Safe(() => _report(line));
        }
        if (longHang is { } stuck && OnLongHang is { } onLong) Safe(() => onLong(stuck));
        if (ping is not null) Safe(() => _post(ping));
    }

    private void Answered(int generation)
    {
        string? line = null;
        lock (_gate)
        {
            if (generation != _generation || _pingSentAt is not { } sent) return;
            if (_reported) line = $"The UI thread answered again after {Seconds(_clock() - sent)} s{Describe()}.";
            _pingSentAt = null;
            _reported = false;
            _longReported = false;
        }
        if (line is not null) Safe(() => _report(line));
    }

    private string Describe() => Context is { Length: > 0 } c ? $" (window {c})" : "";

    private static string Seconds(TimeSpan t) => Math.Max(0, t.TotalSeconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    private static void Safe(Action action)
    {
        // A watchdog that throws on a timer thread would take the process down: the opposite of its job.
        try { action(); }
        catch { }
    }

    public void Dispose() => _timer?.Dispose();
}
