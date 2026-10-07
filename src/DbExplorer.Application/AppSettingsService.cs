using System.Text.Json;
using DbExplorer.Core.Models;

namespace DbExplorer.Application;

/// <summary>Small app-wide preferences persisted as JSON: the UI theme and the SQL editor's zoom and word wrap.</summary>
public sealed class AppSettingsService
{
    public const double DefaultEditorFontSize = 13;
    public const double MinEditorFontSize = 8;
    public const double MaxEditorFontSize = 36;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;

    public AppSettingsService(AppPaths paths)
    {
        _file = Path.Combine(paths.Root, "settings.json");
        var stored = Load();
        Theme = stored.Theme;
        EditorFontSize = ClampFontSize(stored.EditorFontSize ?? DefaultEditorFontSize);
        EditorWordWrap = stored.EditorWordWrap;
    }

    public AppThemeMode Theme { get; private set; }

    /// <summary>Font size of the SQL editors (zoomed with Ctrl+wheel, Ctrl+= and Ctrl+-), the same in every query tab.</summary>
    public double EditorFontSize { get; private set; }

    /// <summary>Whether the SQL editors wrap long lines (Alt+Z).</summary>
    public bool EditorWordWrap { get; private set; }

    public event Action<AppThemeMode>? ThemeChanged;

    /// <summary>Raised when the editor font size or word wrap changed, so every open editor follows.</summary>
    public event Action? EditorChanged;

    public void SetTheme(AppThemeMode theme)
    {
        if (Theme == theme) return;
        Theme = theme;
        ThemeChanged?.Invoke(theme);
        Save();
    }

    public void SetEditorFontSize(double size)
    {
        size = ClampFontSize(size);
        if (EditorFontSize.Equals(size)) return;
        EditorFontSize = size;
        EditorChanged?.Invoke();
        Save();
    }

    public void SetEditorWordWrap(bool wrap)
    {
        if (EditorWordWrap == wrap) return;
        EditorWordWrap = wrap;
        EditorChanged?.Invoke();
        Save();
    }

    public static double ClampFontSize(double size) =>
        double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinEditorFontSize, MaxEditorFontSize) : DefaultEditorFontSize;

    private StoredSettings Load()
    {
        try
        {
            if (!File.Exists(_file)) return new StoredSettings();
            return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_file), Json) ?? new StoredSettings();
        }
        catch
        {
            return new StoredSettings();
        }
    }

    private void Save()
    {
        try
        {
            var stored = new StoredSettings { Theme = Theme, EditorFontSize = EditorFontSize, EditorWordWrap = EditorWordWrap };
            File.WriteAllText(_file, JsonSerializer.Serialize(stored, Json));
        }
        catch
        {
            // Best-effort; a failed save just means the preference resets to default next launch.
        }
    }

    private sealed class StoredSettings
    {
        public AppThemeMode Theme { get; set; }
        public double? EditorFontSize { get; set; }
        public bool EditorWordWrap { get; set; }
    }
}
