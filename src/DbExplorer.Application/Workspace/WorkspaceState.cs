using System.Text.Json;

namespace DbExplorer.Application.Workspace;

/// <summary>Where the window was and what the user was looking at when DbExplorer last closed, so the next start
/// opens the same way. The query tabs' text lives in query-tabs.json; this file only says which one was active.</summary>
public sealed class WorkspaceState
{
    /// <summary>The window's normal (not maximized) bounds and whether it was maximized; null before the first close.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>The main tab that was open (an <c>AppTab</c> name, kept as text so a renamed or removed tab is ignored).</summary>
    public string? MainTab { get; set; }

    /// <summary>Position of the active Query tab in the tab strip.</summary>
    public int? QueryTabIndex { get; set; }

    /// <summary>The connection picked in the toolbar.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>Whether that connection was open when the app closed (it is then opened again at start).</summary>
    public bool WasConnected { get; set; }
}

/// <summary>Window bounds: position in physical screen pixels (as the OS reports it), size in device-independent
/// pixels (as the window's Width and Height), so a monitor with another scale factor still gets the same window.</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>A monitor's working area (without the taskbar) in physical pixels, with its scale factor.</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height, double Scaling, bool IsPrimary = false)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>Reads and writes workspace.json. Best-effort both ways: a missing or damaged file starts with defaults.</summary>
public sealed class WorkspaceStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string FilePath => Path.Combine(paths.Root, "workspace.json");

    public WorkspaceState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new WorkspaceState();
            return JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(FilePath), Json) ?? new WorkspaceState();
        }
        catch
        {
            return new WorkspaceState();
        }
    }

    public void Save(WorkspaceState state)
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(state, Json));
        }
        catch
        {
            // Losing the layout only means the next start opens with defaults.
        }
    }
}

/// <summary>Fits saved window bounds onto the monitors that exist now: a monitor may have been unplugged, moved or
/// changed resolution since, and a window restored off-screen looks like an app that does not start.</summary>
public static class WindowPlacementFitter
{
    public const double MinWidth = 640;
    public const double MinHeight = 420;

    /// <summary>The bounds to open with, or null to keep the default (centred on the primary screen).</summary>
    public static WindowPlacement? Fit(WindowPlacement? saved, IReadOnlyList<ScreenArea> screens)
    {
        if (saved is null || screens.Count == 0) return null;
        if (!double.IsFinite(saved.Width) || !double.IsFinite(saved.Height) || saved.Width <= 0 || saved.Height <= 0) return null;

        // The monitor showing most of the window; none at all (it was on a monitor that is gone) → default placement,
        // but a maximized window still opens maximized.
        var screen = screens.MaxBy(s => Overlap(saved, s));
        if (Overlap(saved, screen) <= 0)
        {
            if (!saved.Maximized) return null;
            screen = screens.FirstOrDefault(s => s.IsPrimary);
            if (screen.Width == 0) screen = screens[0];
            return CenterOn(screen, saved with { Maximized = true });
        }

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var maxWidth = screen.Width / scaling;
        var maxHeight = screen.Height / scaling;
        var width = Math.Round(Math.Clamp(saved.Width, Math.Min(MinWidth, maxWidth), maxWidth));
        var height = Math.Round(Math.Clamp(saved.Height, Math.Min(MinHeight, maxHeight), maxHeight));
        var pxWidth = (int)Math.Round(width * scaling);
        var pxHeight = (int)Math.Round(height * scaling);

        // Slide it fully onto that monitor (the title bar above all, or it cannot be dragged back).
        var x = Math.Clamp(saved.X, screen.X, Math.Max(screen.X, screen.Right - pxWidth));
        var y = Math.Clamp(saved.Y, screen.Y, Math.Max(screen.Y, screen.Bottom - pxHeight));
        return new WindowPlacement(x, y, width, height, saved.Maximized);
    }

    private static WindowPlacement CenterOn(ScreenArea screen, WindowPlacement saved)
    {
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var width = Math.Round(Math.Clamp(saved.Width, Math.Min(MinWidth, screen.Width / scaling), screen.Width / scaling));
        var height = Math.Round(Math.Clamp(saved.Height, Math.Min(MinHeight, screen.Height / scaling), screen.Height / scaling));
        var x = screen.X + (screen.Width - (int)Math.Round(width * scaling)) / 2;
        var y = screen.Y + (screen.Height - (int)Math.Round(height * scaling)) / 2;
        return new WindowPlacement(x, y, width, height, saved.Maximized);
    }

    /// <summary>Visible area of the window on <paramref name="screen"/>, in square pixels (sized with that screen's scale).</summary>
    private static long Overlap(WindowPlacement w, ScreenArea screen)
    {
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var right = w.X + (long)Math.Round(w.Width * scaling);
        var bottom = w.Y + (long)Math.Round(w.Height * scaling);
        var dx = Math.Min(right, screen.Right) - Math.Max(w.X, screen.X);
        var dy = Math.Min(bottom, screen.Bottom) - Math.Max(w.Y, screen.Y);
        return dx > 0 && dy > 0 ? dx * dy : 0;
    }
}
