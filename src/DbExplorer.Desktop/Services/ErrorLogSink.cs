using System.Text;
using Avalonia.Logging;

namespace DbExplorer.Desktop.Services;

/// <summary>
/// Copies Avalonia's own errors and warnings (render loop, compositor, Win32 platform, layout) into
/// <see cref="ErrorLog"/>, still passing everything to the previous sink. Without it a render failure goes only to the
/// debugger's trace output, and a window that stops repainting leaves nothing for the user to send.
/// Binding and property warnings are left out: they are noise from templates, not failures. Each message is kept at
/// most once a minute so a failing render loop cannot fill the log.
/// </summary>
public sealed class ErrorLogSink(ILogSink? inner, Action<string, string>? write = null, Func<DateTimeOffset>? clock = null) : ILogSink
{
    private static readonly TimeSpan Repeat = TimeSpan.FromMinutes(1);
    private readonly Action<string, string> _write = write ?? ErrorLog.Note;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private readonly Dictionary<string, DateTimeOffset> _lastWritten = new();

    public static bool Keeps(LogEventLevel level, string area) =>
        level >= LogEventLevel.Error ||
        (level == LogEventLevel.Warning && area != LogArea.Binding && area != LogArea.Property);

    public bool IsEnabled(LogEventLevel level, string area) => Keeps(level, area) || inner?.IsEnabled(level, area) == true;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (inner?.IsEnabled(level, area) == true) inner.Log(level, area, source, messageTemplate, propertyValues);
        if (!Keeps(level, area)) return;

        var message = Format(messageTemplate, propertyValues);
        var key = area + "|" + messageTemplate;
        var now = _clock();
        lock (_lastWritten)
        {
            if (_lastWritten.TryGetValue(key, out var last) && now - last < Repeat) return;
            _lastWritten[key] = now;
        }
        var origin = source is null ? "" : $" ({source.GetType().Name})";
        _write($"avalonia {level.ToString().ToLowerInvariant()} {area}", message + origin);
    }

    /// <summary>Fills the <c>{Name}</c> holes of a message template in order, as the trace sink does.</summary>
    public static string Format(string template, IReadOnlyList<object?> values)
    {
        var text = new StringBuilder();
        var next = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var close = template[i] == '{' ? template.IndexOf('}', i + 1) : -1;
            if (close < 0)
            {
                text.Append(template[i]);
                continue;
            }
            text.Append(next < values.Count ? values[next]?.ToString() ?? "(null)" : template[i..(close + 1)]);
            next++;
            i = close;
        }
        // An exception passed as the last value is not always named in the template; it is the most useful part.
        if (values.Count > next && values[^1] is Exception ex) text.AppendLine().Append(ex);
        return text.ToString();
    }
}
