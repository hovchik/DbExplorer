using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DbExplorer.Desktop.Converters;

public sealed class BoolToHighlightBrushConverter : IValueConverter
{
    public static readonly BoolToHighlightBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brushes.Goldenrod : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
