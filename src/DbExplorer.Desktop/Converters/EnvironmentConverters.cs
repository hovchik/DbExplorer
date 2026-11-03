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

/// <summary>
/// The toolbar picker's dot for an open connection: [connection id, the window's OpenMarks] → the connection's colour
/// mark (<see cref="ViewModels.ConnectionMarks"/>), or nothing for a closed one. <see cref="IsOpen"/> gives visibility.
/// </summary>
public sealed class OpenMarkConverter : IMultiValueConverter
{
    public static readonly OpenMarkConverter Brush = new(false);
    public static readonly OpenMarkConverter IsOpen = new(true);

    private readonly bool _visibility;

    private OpenMarkConverter(bool visibility) => _visibility = visibility;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var mark = values.Count == 2 && values[0] is Guid id && values[1] is IReadOnlyDictionary<Guid, Avalonia.Media.IBrush> marks &&
                   marks.TryGetValue(id, out var brush)
            ? brush
            : null;
        return _visibility ? mark is not null : mark;
    }
}
