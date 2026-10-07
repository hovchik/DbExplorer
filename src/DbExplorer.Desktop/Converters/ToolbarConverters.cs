using System.Globalization;
using Avalonia.Data.Converters;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Controls;

namespace DbExplorer.Desktop.Converters;

/// <summary>The theme button's icon: a monitor for System, a sun for Light, a moon for Dark.</summary>
public sealed class ThemeModeToIconConverter : IValueConverter
{
    public static readonly ThemeModeToIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AppThemeMode.Light => Icons.Sun,
        AppThemeMode.Dark => Icons.Moon,
        _ => Icons.Monitor
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when an enum value's name equals the converter parameter (a menu's check mark).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Enum e && string.Equals(e.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"Folder / " before a connection's name in the picker, or nothing outside folders.</summary>
public sealed class FolderPrefixConverter : IValueConverter
{
    public static readonly FolderPrefixConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? $"{s.Trim()} / " : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
