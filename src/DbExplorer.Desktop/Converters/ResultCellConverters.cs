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

/// <summary>NULL cells are dimmed and italic so they stand apart from the text "NULL".</summary>
public sealed class NullCellStyleConverter : IValueConverter
{
    public static readonly NullCellStyleConverter Opacity = new(isOpacity: true);
    public static readonly NullCellStyleConverter FontStyle = new(isOpacity: false);

    private readonly bool _isOpacity;
    private NullCellStyleConverter(bool isOpacity) => _isOpacity = isOpacity;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null or DBNull;
        return _isOpacity ? isNull ? 0.5 : 1.0 : isNull ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
