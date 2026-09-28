using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DbExplorer.Application.Compare;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.Converters;

public sealed class DiffKindToBrushConverter : IValueConverter
{
    public static readonly DiffKindToBrushConverter Instance = new();

    private static readonly IBrush Added = new SolidColorBrush(Color.FromArgb(140, 46, 160, 67));
    private static readonly IBrush Removed = new SolidColorBrush(Color.FromArgb(140, 219, 61, 61));
    private static readonly IBrush Skipped = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffLineKind.Added => Added,
        DiffLineKind.Removed => Removed,
        DiffLineKind.Skipped => Skipped,
        _ => Brushes.Transparent
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DataRowStatusToBrushConverter : IValueConverter
{
    public static readonly DataRowStatusToBrushConverter Instance = new();

    private static readonly IBrush Different = new SolidColorBrush(Color.FromArgb(60, 219, 61, 61));
    private static readonly IBrush OnlyLeft = new SolidColorBrush(Color.FromArgb(60, 219, 150, 61));
    private static readonly IBrush OnlyRight = new SolidColorBrush(Color.FromArgb(60, 61, 130, 219));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DataRowStatus.Different => Different,
        DataRowStatus.OnlyLeft => OnlyLeft,
        DataRowStatus.OnlyRight => OnlyRight,
        _ => Brushes.Transparent
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Row tint for structure (column/index/foreign key) and whole-database comparisons, using the same
/// palette as data rows: amber changed, orange only-left, blue only-right.</summary>
public sealed class CompareStatusToBrushConverter : IValueConverter
{
    public static readonly CompareStatusToBrushConverter Instance = new();

    private static readonly IBrush Changed = new SolidColorBrush(Color.FromArgb(60, 219, 170, 61));
    private static readonly IBrush OnlyLeft = new SolidColorBrush(Color.FromArgb(60, 219, 110, 61));
    private static readonly IBrush OnlyRight = new SolidColorBrush(Color.FromArgb(60, 61, 130, 219));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StructureChange.Changed or ObjectCompareStatus.Different => Changed,
        StructureChange.OnlyLeft or ObjectCompareStatus.OnlyLeft => OnlyLeft,
        StructureChange.OnlyRight or ObjectCompareStatus.OnlyRight => OnlyRight,
        _ => Brushes.Transparent
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
