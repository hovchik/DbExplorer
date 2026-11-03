using Avalonia.Platform;
using Avalonia.Styling;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Tests;

public sealed class AppThemesTests
{
    [Fact]
    public void High_contrast_is_dark_and_follows_windows_with_the_system_theme()
    {
        Assert.True(AppThemes.IsDark(AppThemes.HighContrast));
        Assert.True(AppThemes.IsDark(ThemeVariant.Dark));
        Assert.False(AppThemes.IsDark(ThemeVariant.Light));

        var contrast = new PlatformColorValues { ContrastPreference = ColorContrastPreference.High };
        Assert.Equal(AppThemes.HighContrast, AppThemes.For(AppThemeMode.System, contrast));
        Assert.Equal(ThemeVariant.Default, AppThemes.For(AppThemeMode.System, new PlatformColorValues()));
        Assert.Equal(ThemeVariant.Light, AppThemes.For(AppThemeMode.Light, contrast));
        Assert.Equal(AppThemes.HighContrast, AppThemes.For(AppThemeMode.HighContrast, null));
    }
}
