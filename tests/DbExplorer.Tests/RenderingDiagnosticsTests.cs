using Avalonia.Logging;
using DbExplorer.Application;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Tests;

/// <summary>Software rendering switch and the copy of Avalonia's render / platform warnings into errors.log.</summary>
public class RenderingDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-rendering-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly List<(string Source, string Message)> _written = [];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ErrorLogSink Sink(ILogSink? inner = null) => new(inner, (s, m) => _written.Add((s, m)), () => _now);

    [Fact]
    public void Software_rendering_is_off_by_default_and_survives_a_restart()
    {
        Assert.False(new AppSettingsService(new AppPaths(_root)).SoftwareRendering);
        new AppSettingsService(new AppPaths(_root)).SetSoftwareRendering(true);
        Assert.True(new AppSettingsService(new AppPaths(_root)).SoftwareRendering);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData("software", false, true)]
    [InlineData(" CPU ", false, true)]
    [InlineData("gpu", true, false)]
    [InlineData("something else", true, true)]
    public void The_environment_variable_wins_over_the_setting(string? environment, bool setting, bool expected) =>
        Assert.Equal(expected, RenderingOptions.WantsSoftware(environment, () => setting));

    [Fact]
    public void Render_and_platform_warnings_are_logged_but_binding_noise_is_not()
    {
        var sink = Sink();
        sink.Log(LogEventLevel.Warning, LogArea.Visual, null, "Exception in render loop: {Error}", "device lost");
        sink.Log(LogEventLevel.Warning, LogArea.Binding, null, "Could not convert {Value}", "x");
        sink.Log(LogEventLevel.Information, LogArea.Win32Platform, null, "Window state {State}", "Minimized");
        sink.Log(LogEventLevel.Error, LogArea.Binding, null, "Broken {Path}", "Foo");

        Assert.Equal(2, _written.Count);
        Assert.Equal("Exception in render loop: device lost", _written[0].Message);
        Assert.Contains("warning", _written[0].Source);
        Assert.Equal("Broken Foo", _written[1].Message);
    }

    [Fact]
    public void A_repeating_message_is_logged_at_most_once_a_minute()
    {
        var sink = Sink();
        for (var i = 0; i < 100; i++)
        {
            sink.Log(LogEventLevel.Error, LogArea.Visual, null, "Render failed {N}", i);
            _now += TimeSpan.FromSeconds(1);
        }
        Assert.Equal(2, _written.Count);
    }

    [Fact]
    public void Everything_still_reaches_the_previous_sink()
    {
        var inner = new RecordingSink();
        var sink = Sink(inner);
        sink.Log(LogEventLevel.Warning, LogArea.Binding, null, "Could not convert {Value}", "x");
        Assert.Single(inner.Messages);
        Assert.Empty(_written);
    }

    [Fact]
    public void An_exception_value_not_named_in_the_template_is_kept()
    {
        var text = ErrorLogSink.Format("Render loop stopped", [new InvalidOperationException("EGL_CONTEXT_LOST")]);
        Assert.Contains("EGL_CONTEXT_LOST", text);
        Assert.Equal("a 1 {b}", ErrorLogSink.Format("a {x} {b}", [1]));
    }

    private sealed class RecordingSink : ILogSink
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => true;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Messages.Add(messageTemplate);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Messages.Add(messageTemplate);
    }
}
