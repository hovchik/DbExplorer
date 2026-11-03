using System.Globalization;
using Avalonia.Data.Converters;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Converters;

/// <summary>A result tab's rows as aligned plain text (Ctrl+T in the Query tab), see <see cref="ResultText"/>.</summary>
public sealed class ResultTextConverter : IValueConverter
{
    public static readonly ResultTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ResultSetView { IsGrid: true } view
            ? ResultText.Format(view.Columns, view.Rows.Where(r => !r.IsDeleted).Select(r => r.Values).ToList(),
                totalRows: view.IsTruncated ? view.TotalRowCount : null, totalIsExact: view.TotalRowCountIsExact)
            : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
