using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using DbExplorer.Desktop.Controls;

namespace DbExplorer.Tests;

/// <summary>
/// Redrawing must not grow memory. A mutable <see cref="Pen"/> made in Render with a long-lived brush stays registered
/// with that brush in the compositor, so every redraw of the toolbar icons (on each refresh, hover or busy change) kept
/// about 140 bytes for good.
/// </summary>
[Collection(nameof(RenderMemoryCollection))]
public class RenderMemoryTests
{
    [AvaloniaFact]
    public void Redrawing_a_stroke_icon_does_not_grow_memory()
    {
        // A theme brush outlives every frame, like the inherited foreground of a toolbar button.
        var icon = new StrokeIcon { Data = Icons.Refresh, Foreground = new SolidColorBrush(Colors.Black) };
        var window = new Window { Width = 100, Height = 100, Content = icon };
        window.Show();
        for (var i = 0; i < 200; i++) Frame(icon);

        var before = RetainedBytes();
        const int frames = 10_000;
        for (var i = 0; i < frames; i++) Frame(icon);
        var grown = RetainedBytes() - before;
        window.Close();

        // The leak kept ~140 bytes a frame (1.4 MB here); a few KB of noise is allowed.
        Assert.True(grown < 200_000, $"Memory grew by {grown:N0} bytes over {frames:N0} redraws.");
    }

    [Fact]
    public void Render_code_makes_no_mutable_pens()
    {
        // Static pens are made once and are fine; anything else in a control is drawn per frame.
        var desktop = Path.Combine(MainTabOrderTests.RepoRoot(), "src", "DbExplorer.Desktop");
        var offenders = Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            .Where(x => Regex.IsMatch(x.line, @"\bnew Pen\(") && !x.line.Contains("static readonly"))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        Assert.True(offenders.Count == 0, "Use RenderPens.Create instead of new Pen in: " + string.Join(", ", offenders));
    }

    private static void Frame(Control control)
    {
        control.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static long RetainedBytes()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }
}

/// <summary>Measures the process's managed heap, so nothing else may run alongside it.</summary>
[CollectionDefinition(nameof(RenderMemoryCollection), DisableParallelization = true)]
public sealed class RenderMemoryCollection;
