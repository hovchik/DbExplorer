using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;

namespace DbExplorer.Desktop.Converters;

/// <summary>Collapses whitespace/newlines so multi-line SQL fits a single grid row.</summary>
public sealed partial class SingleLineConverter : IValueConverter
{
    public static readonly SingleLineConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? Whitespace().Replace(s, " ").Trim() : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
