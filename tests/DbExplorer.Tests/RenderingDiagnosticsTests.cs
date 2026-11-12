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

    private readonly HangClues _clues;

    public RenderingDiagnosticsTests() => _clues = new HangClues(() => _now);

    private ErrorLogSink Sink(ILogSink? inner = null) => new(inner, (s, m) => _written.Add((s, m)), () => _now, _clues);

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

    [Fact]
    public void Input_on_a_window_or_popup_that_just_closed_is_not_logged()
    {
        var sink = Sink();
        sink.Log(LogEventLevel.Warning, LogArea.Control, new object(), ErrorLogSink.InputAfterCloseTemplate);
        Assert.Empty(_written);
    }

    [Fact]
    public void A_compositor_stall_is_logged_in_plain_words_and_kept_as_a_clue()
    {
        var sink = Sink();
        sink.Log(LogEventLevel.Error, LogArea.Visual, new object(),
            "windows::UI::Composition::ICompositor5.RequestCommitAsync timed out, force-triggering next tick");
        var (source, message) = Assert.Single(_written);
        Assert.Equal("render", source);
        Assert.Contains("did not finish a frame within 1 s", message);

        _now += TimeSpan.FromSeconds(5);
        Assert.Contains("compositor had stalled 5 s before", _clues.Describe(_now));
    }

    [Fact]
    public void Clues_name_the_last_input_and_a_recent_compositor_stall_only()
    {
        var clues = new HangClues(() => _now);
        Assert.Null(clues.Describe(_now));

        clues.RenderStall();
        _now += TimeSpan.FromMinutes(5);
        clues.Input("Ctrl+Enter in QueryView");
        _now += TimeSpan.FromSeconds(2);
        // The stall was 5 minutes earlier: unrelated, left out.
        Assert.Equal("Last input before it: Ctrl+Enter in QueryView, 2 s before.", clues.Describe(_now));

        clues.RenderStall();
        Assert.Contains("compositor had stalled just then", clues.Describe(_now));
        Assert.Contains("Software rendering", clues.Describe(_now));
    }

    [Fact]
    public void An_input_long_after_the_hang_began_is_not_blamed_for_it()
    {
        var clues = new HangClues(() => _now);
        var since = _now;
        _now += TimeSpan.FromSeconds(20);
        clues.Input("click on Button in MainWindow");
        Assert.Null(clues.Describe(since));
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.A, Avalonia.Input.KeyModifiers.None, "typing")]
    [InlineData(Avalonia.Input.Key.D7, Avalonia.Input.KeyModifiers.Shift, "typing")]
    [InlineData(Avalonia.Input.Key.Space, Avalonia.Input.KeyModifiers.None, "typing")]
    [InlineData(Avalonia.Input.Key.F5, Avalonia.Input.KeyModifiers.None, "F5")]
    [InlineData(Avalonia.Input.Key.Enter, Avalonia.Input.KeyModifiers.Control, "Ctrl+Return")]
    [InlineData(Avalonia.Input.Key.F, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift, "Ctrl+Shift+F")]
    public void Keys_are_recorded_as_shortcuts_and_never_as_typed_text(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers, string expected) =>
        Assert.Equal(expected, InputClue.Key(key, modifiers));

    [Fact]
    public void Hang_dumps_can_be_turned_off_and_old_ones_are_deleted()
    {
        Assert.True(HangDump.Enabled(null));
        Assert.True(HangDump.Enabled("on"));
        Assert.False(HangDump.Enabled(" OFF "));
        Assert.False(HangDump.Enabled("0"));

        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "hang-20261112-100700.dmp"), "x");
        File.WriteAllText(Path.Combine(_root, "errors.log"), "x");
        HangDump.DeleteOld(_root);
        Assert.Equal(["errors.log"], Directory.GetFiles(_root).Select(Path.GetFileName));
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
