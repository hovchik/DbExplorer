using System.Globalization;
using Avalonia.Data.Converters;

namespace DbExplorer.Desktop.Converters;

/// <summary>Binds an enum-valued property to a SelectedIndex (enum member order = index).</summary>
public sealed class EnumToIndexConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Enum e ? System.Convert.ToInt32(e, CultureInfo.InvariantCulture) : 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && i >= 0 && Enum.IsDefined(targetType, i) ? Enum.ToObject(targetType, i) : Avalonia.Data.BindingOperations.DoNothing;
}
