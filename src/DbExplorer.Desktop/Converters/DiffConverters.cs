using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.Converters;

public sealed class DiffKindToBrushConverter : IValueConverter
{
    public static readonly DiffKindToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffLineKind.Added => new SolidColorBrush(Color.FromArgb(60, 46, 160, 67)),
        DiffLineKind.Removed => new SolidColorBrush(Color.FromArgb(60, 219, 61, 61)),
        _ => Brushes.Transparent
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DataRowStatusToBrushConverter : IValueConverter
{
    public static readonly DataRowStatusToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DataRowStatus.Different => new SolidColorBrush(Color.FromArgb(60, 219, 61, 61)),
        DataRowStatus.OnlyLeft => new SolidColorBrush(Color.FromArgb(60, 219, 150, 61)),
        DataRowStatus.OnlyRight => new SolidColorBrush(Color.FromArgb(60, 61, 130, 219)),
        _ => Brushes.Transparent
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
