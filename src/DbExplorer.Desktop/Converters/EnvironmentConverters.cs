using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DbExplorer.Core.Connections;

namespace DbExplorer.Desktop.Converters;

/// <summary>Banner color per environment: green dev, blue test, orange staging, red production.</summary>
public sealed class EnvironmentToBrushConverter : IValueConverter
{
    public static readonly EnvironmentToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionEnvironment.Development => new SolidColorBrush(Color.Parse("#2E7D32")),
        ConnectionEnvironment.Test => new SolidColorBrush(Color.Parse("#1565C0")),
        ConnectionEnvironment.Staging => new SolidColorBrush(Color.Parse("#E65100")),
        ConnectionEnvironment.Production => new SolidColorBrush(Color.Parse("#C62828")),
        _ => Brushes.Transparent
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
