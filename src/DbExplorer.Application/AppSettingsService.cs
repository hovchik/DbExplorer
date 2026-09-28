using System.Text.Json;
using DbExplorer.Core.Models;

namespace DbExplorer.Application;

/// <summary>Small app-wide preferences persisted as JSON, currently just the UI theme.</summary>
public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;

    public AppSettingsService(AppPaths paths)
    {
        _file = Path.Combine(paths.Root, "settings.json");
        Theme = Load();
    }

    public AppThemeMode Theme { get; private set; }

    public event Action<AppThemeMode>? ThemeChanged;

    public void SetTheme(AppThemeMode theme)
    {
        if (Theme == theme) return;
        Theme = theme;
        ThemeChanged?.Invoke(theme);
        Save();
    }

    private AppThemeMode Load()
    {
        try
        {
            if (!File.Exists(_file)) return AppThemeMode.System;
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_file), Json);
            return stored?.Theme ?? AppThemeMode.System;
        }
        catch
        {
            return AppThemeMode.System;
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(new StoredSettings { Theme = Theme }, Json));
        }
        catch
        {
            // Best-effort; a failed save just means the preference resets to default next launch.
        }
    }

    private sealed class StoredSettings
    {
        public AppThemeMode Theme { get; set; }
    }
}
