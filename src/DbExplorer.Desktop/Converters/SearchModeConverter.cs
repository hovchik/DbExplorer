using System.Globalization;
using Avalonia.Data.Converters;
using DbExplorer.Core.Search;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Converters;

/// <summary>Shows a <see cref="SearchMatchMode"/> by its label ("Whole value" for Exact).</summary>
public sealed class SearchModeConverter : IValueConverter
{
    public static readonly SearchModeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SearchMatchMode mode ? DataSearchViewModel.ModeLabel(mode) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Small converters for the Search data view.</summary>
public static class DataSearchConverters
{
    /// <summary>NULL values in the found row are shown in italics.</summary>
    public static readonly IValueConverter ItalicWhenTrue =
        new FuncValueConverter<bool, Avalonia.Media.FontStyle>(b => b ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal);

    /// <summary>NULL values in the found row are dimmed.</summary>
    public static readonly IValueConverter DimWhenTrue = new FuncValueConverter<bool, double>(b => b ? 0.6 : 1.0);
}
