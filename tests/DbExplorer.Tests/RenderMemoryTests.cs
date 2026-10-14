using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using DbExplorer.Desktop.Controls;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;

namespace DbExplorer.Tests;

/// <summary>
/// Redrawing and refreshing must not grow memory while the app sits with auto refresh on. A mutable <see cref="Pen"/> made in Render with a long-lived brush stays registered
/// with that brush in the compositor, so every redraw of the toolbar icons (on each refresh, hover or busy change) kept
/// about 140 bytes for good. And Avalonia's DataGrid leaks part of every row when its ItemsSource is replaced, so grids
/// refill one list instead (<see cref="GridRows"/>).
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
        // A long warm-up: what earlier tests left in the compositor settles over the first frames.
        for (var i = 0; i < 5000; i++) Frame(icon);

        var before = RetainedBytes();
        const int frames = 10_000;
        for (var i = 0; i < frames; i++) Frame(icon);
        var grown = RetainedBytes() - before;
        window.Close();

        // The leak kept ~140 bytes a frame (1.4 MB here); a few KB of noise is allowed.
        Assert.True(grown < 200_000, $"Memory grew by {grown:N0} bytes over {frames:N0} redraws.");
    }

    [AvaloniaFact]
    public void Refreshing_a_result_grid_does_not_grow_memory()
    {
        // What Query auto refresh does: a new result set into the same grid on every tick.
        static ResultSetView Result() =>
            new("t", ["id", "name"], Enumerable.Range(0, 20).Select(r => new ResultRow([r, "name " + r])).ToList());
        var view = new ResultGridView { ResultSet = Result() };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        for (var i = 0; i < 20; i++) Refresh();

        var before = RetainedBytes();
        const int refreshes = 300;
        for (var i = 0; i < refreshes; i++) Refresh();
        var grown = RetainedBytes() - before;
        window.Close();

        // Replacing the grid's ItemsSource kept ~11 KB a refresh (3.3 MB here).
        Assert.True(grown < 500_000, $"Memory grew by {grown:N0} bytes over {refreshes:N0} refreshes.");

        void Refresh()
        {
            view.ResultSet = Result();
            for (var k = 0; k < 3; k++) Frame(view);
        }
    }

    [AvaloniaFact]
    public void Grid_rows_reuse_the_grid_items_list()
    {
        var grid = new DataGrid();
        GridRows.SetSource(grid, new[] { "a", "b" });
        var list = grid.ItemsSource;
        Assert.IsType<ReloadableList<string>>(list);

        GridRows.SetSource(grid, new List<string> { "c", "d", "e" });
        Assert.Same(list, grid.ItemsSource);
        Assert.Equal(["c", "d", "e"], grid.ItemsSource!.Cast<string>());

        GridRows.SetSource(grid, null);
        Assert.Same(list, grid.ItemsSource);
        Assert.Empty(grid.ItemsSource!.Cast<string>());
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
