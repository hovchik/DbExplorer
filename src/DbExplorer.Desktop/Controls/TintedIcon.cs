using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// A <see cref="StrokeIcon"/> drawn in a theme colour named by resource key (an <c>AppObject*Brush</c>, or a
/// colour such as <c>SystemAccentColor</c>), looked up again when the theme changes. Without a key it keeps the text
/// colour. The database navigator uses it so tables are orange, views green and so on, as in the Objects tab.
/// </summary>
public sealed class TintedIcon : Decorator
{
    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<TintedIcon, string?>(nameof(Data));

    public static readonly StyledProperty<string?> ColorKeyProperty =
        AvaloniaProperty.Register<TintedIcon, string?>(nameof(ColorKey));

    private readonly StrokeIcon _icon = new();

    public TintedIcon()
    {
        Child = _icon;
        ActualThemeVariantChanged += (_, _) => Refresh();
    }

    public string? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public string? ColorKey
    {
        get => GetValue(ColorKeyProperty);
        set => SetValue(ColorKeyProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty) _icon.Data = Data;
        else if (change.Property == ColorKeyProperty) Refresh();
        else if (change.Property == WidthProperty) _icon.Width = Width;
        else if (change.Property == HeightProperty) _icon.Height = Height;
    }

    private void Refresh()
    {
        if (ColorKey is { } key && this.TryFindResource(key, ActualThemeVariant, out var found))
        {
            IBrush? brush = found switch
            {
                IBrush b => b,
                Color c => new SolidColorBrush(c),
                _ => null
            };
            if (brush is not null)
            {
                _icon.Foreground = brush;
                return;
            }
        }
        _icon.ClearValue(StrokeIcon.ForegroundProperty);
    }
}

/// <summary>Bool-to-style converters for the database navigator rows.</summary>
public static class NavigatorConverters
{
    /// <summary>True → SemiBold (the connected connection, the connection's database).</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter Weight =
        new Avalonia.Data.Converters.FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>True → faded (other saved connections while one is connected, messages).</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter Dim =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(b => b ? 0.55 : 1.0);
}
