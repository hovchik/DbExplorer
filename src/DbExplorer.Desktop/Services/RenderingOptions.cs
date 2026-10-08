using Avalonia;
using DbExplorer.Application;

namespace DbExplorer.Desktop.Services;

/// <summary>How the window is drawn on Windows, decided once at start-up.</summary>
public static class RenderingOptions
{
    /// <summary>
    /// The GPU path (ANGLE on Direct3D) with the CPU as fallback, which is Avalonia's default; only the CPU when the user
    /// turned on software rendering or DBEXPLORER_RENDERING=software is set. The way out when the GPU path stops
    /// repainting the window, as can happen after the display slept or the driver reset while the window was minimized.
    /// </summary>
    public static IReadOnlyList<Win32RenderingMode> Win32Modes() =>
        WantsSoftware(Environment.GetEnvironmentVariable("DBEXPLORER_RENDERING"), ReadSetting)
            ? [Win32RenderingMode.Software]
            : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software];

    /// <summary>The environment variable wins over the setting, so a user can get a window back without opening it.</summary>
    public static bool WantsSoftware(string? environment, Func<bool> setting) =>
        environment?.Trim().ToLowerInvariant() switch
        {
            "software" or "cpu" => true,
            "gpu" or "angle" => false,
            _ => setting()
        };

    private static bool ReadSetting()
    {
        try { return new AppSettingsService(new AppPaths()).SoftwareRendering; }
        catch { return false; }
    }
}
