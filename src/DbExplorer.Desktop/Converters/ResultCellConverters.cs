using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.Converters;

/// <summary>Result-grid cell text: one line (newlines shown as ↵) and capped in length so a large
/// text value can't blow up row height or layout; the value viewer still shows it in full.</summary>
public sealed class ResultCellTextConverter : IValueConverter
{
    public static readonly ResultCellTextConverter Instance = new();
    private const int MaxChars = 500;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is byte[]) return CellValueConverter.Instance.Convert(value, targetType, parameter, culture);
        var text = ResultViewQuery.DisplayText(value);
        if (text is null) return "NULL";
        if (text.Length > MaxChars) text = text[..MaxChars] + "…";
        return text.IndexOfAny(['\r', '\n', '\t']) < 0
            ? text
            : text.Replace("\r\n", " ↵ ").Replace('\n', '↵').Replace('\r', '↵').Replace('\t', ' ');
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Type-based colouring of result values, as in DataGrip / DBeaver: each kind of value
/// has a theme brush (see App.axaml) so numbers, dates, flags, ids, binary and NULL read at a glance.</summary>
public static class CellValueColors
{
    public const string Text = "AppCellTextBrush";
    public const string Number = "AppCellNumberBrush";
    public const string DateTime = "AppCellDateBrush";
    public const string Boolean = "AppCellBooleanBrush";
    public const string Identifier = "AppCellGuidBrush";
    public const string Binary = "AppCellBinaryBrush";
    public const string Null = "AppCellNullBrush";

    /// <summary>The resource key of the brush a value is drawn with.</summary>
    public static string ResourceKey(object? value) => value switch
    {
        null or DBNull => Null,
        string or char => Text,
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Number,
        System.DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => DateTime,
        bool => Boolean,
        Guid => Identifier,
        byte[] => Binary,
        _ => Text
    };
}
