using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;

namespace DbExplorer.Desktop.Converters;

/// <summary>Shows enum members as words: WholeSchema → "Whole schema", KeysOnly → "Keys only".</summary>
public sealed partial class HumanizeConverter : IValueConverter
{
    public static readonly HumanizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;
        var words = WordBoundary().Replace(value.ToString() ?? "", " ");
        return words.Length <= 1 ? words : words[0] + words[1..].ToLowerInvariant();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBoundary();
}
