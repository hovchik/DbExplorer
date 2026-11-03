using Avalonia.Platform;
using Avalonia.Styling;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.Services;

/// <summary>The app's theme variants: Fluent's Light and Dark, and a high-contrast one built on Dark.</summary>
public static class AppThemes
{
    /// <summary>App.axaml's "HighContrast" theme dictionary; anything it does not define comes from Dark.</summary>
    public static readonly ThemeVariant HighContrast = new("HighContrast", ThemeVariant.Dark);

    /// <summary>True for Dark and for variants built on it (high contrast): editors and charts pick dark colours.</summary>
    public static bool IsDark(ThemeVariant? variant)
    {
        for (var v = variant; v is not null; v = v.InheritVariant)
            if (v == ThemeVariant.Dark) return true;
        return false;
    }

    /// <summary>The variant for a setting; System follows the OS, and its contrast setting picks high contrast.</summary>
    public static ThemeVariant For(AppThemeMode mode, PlatformColorValues? platform) => mode switch
    {
        AppThemeMode.Light => ThemeVariant.Light,
        AppThemeMode.Dark => ThemeVariant.Dark,
        AppThemeMode.HighContrast => HighContrast,
        _ when platform?.ContrastPreference == ColorContrastPreference.High => HighContrast,
        _ => ThemeVariant.Default
    };
}
