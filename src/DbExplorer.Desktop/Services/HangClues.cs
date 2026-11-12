using System.Globalization;

namespace DbExplorer.Desktop.Services;

/// <summary>
/// What happened just before the UI thread stopped answering, for the watchdog's line in errors.log: the last click or
/// shortcut (which control, in which view) and whether the graphics compositor had stalled shortly before. A hang can
/// then be traced to what the user did, or to the graphics driver, without a memory dump.
/// Typed text is never recorded: plain keys show only as "typing".
/// </summary>
public sealed class HangClues(Func<DateTimeOffset>? clock = null)
{
    /// <summary>How long before a hang a compositor stall still counts as related to it.</summary>
    public static readonly TimeSpan RenderStallWindow = TimeSpan.FromSeconds(60);

    public static HangClues Shared { get; } = new();

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private volatile Clue? _input;
    private volatile Clue? _renderStall;

    private sealed record Clue(string Text, DateTimeOffset At);

    /// <summary>Records an input (from the UI thread), e.g. "Ctrl+Enter in QueryView".</summary>
    public void Input(string text) => _input = new Clue(text, _clock());

    /// <summary>Records that the graphics compositor missed a frame and Avalonia restarted it.</summary>
    public void RenderStall() => _renderStall = new Clue("", _clock());

    /// <summary>The clues for a hang whose UI thread last answered at <paramref name="stuckSince"/>, or null.</summary>
    public string? Describe(DateTimeOffset stuckSince)
    {
        var parts = new List<string>();
        if (_input is { } input && input.At <= stuckSince + TimeSpan.FromSeconds(5))
            parts.Add($"Last input before it: {input.Text}, {Ago(stuckSince - input.At)}.");
        if (_renderStall is { } stall && stall.At >= stuckSince - RenderStallWindow)
            parts.Add($"The graphics compositor had stalled {Ago(stuckSince - stall.At)} " +
                      "(display sleep, graphics driver reset or remote desktop); if this repeats, turn on Software rendering in Preferences › Appearance.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string Ago(TimeSpan t)
    {
        var seconds = Math.Round(t.TotalSeconds);
        if (Math.Abs(seconds) < 1) return "just then";
        var n = Math.Abs(seconds).ToString("0", CultureInfo.InvariantCulture);
        return seconds > 0 ? $"{n} s before" : $"{n} s after";
    }
}
