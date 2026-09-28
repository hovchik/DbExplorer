using System.Globalization;
using Avalonia.Data.Converters;

namespace DbExplorer.Desktop.Converters;

/// <summary>Grid cell display: NULL marker, binary as hex (instead of "System.Byte[]"), everything else as-is.</summary>
public sealed class CellValueConverter : IValueConverter
{
    public static readonly CellValueConverter Instance = new();
    private const int MaxBytesShown = 64;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => "0x" + System.Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, MaxBytesShown))) +
                        (bytes.Length > MaxBytesShown ? $"… ({bytes.Length:N0} bytes)" : ""),
        _ => value
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
