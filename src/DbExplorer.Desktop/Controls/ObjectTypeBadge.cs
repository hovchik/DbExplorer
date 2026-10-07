using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.Controls;

/// <summary>Colour per object type in the Objects tab: tables orange, views green, procedures blue, functions violet…
/// Each kind has a theme brush in App.axaml, so the colours are tuned for light and dark themes.</summary>
public static class ObjectTypeColors
{
    /// <summary>The resource key of the brush an object type is drawn with; null for "All types" and unknown text.</summary>
    public static string? ResourceKey(object? type)
    {
        DbObjectType? kind = type switch
        {
            DbObjectType t => t,
            string s when Enum.TryParse<DbObjectType>(s, out var parsed) => parsed,
            _ => null
        };
        return kind switch
        {
            null => null,
            DbObjectType.ScalarFunction or DbObjectType.TableFunction => "AppObjectFunctionBrush",
            _ => $"AppObject{kind}Brush"
        };
    }
}

/// <summary>
/// The object type as a small coloured pill (a dot plus the type name on a tint of the same colour), or as just the
/// dot when <see cref="ShowText"/> is false. Takes a <see cref="DbObjectType"/> or its name.
/// </summary>
public sealed class ObjectTypeBadge : Border
{
    public static readonly StyledProperty<object?> TypeProperty =
        AvaloniaProperty.Register<ObjectTypeBadge, object?>(nameof(Type));

    public static readonly StyledProperty<bool> ShowTextProperty =
        AvaloniaProperty.Register<ObjectTypeBadge, bool>(nameof(ShowText), true);

    private readonly Ellipse _dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeight.SemiBold };

    public ObjectTypeBadge()
    {
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(7, 1, 8, 1);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _dot, _text } };
        ActualThemeVariantChanged += (_, _) => Refresh();
        Refresh();
    }

    public object? Type
    {
        get => GetValue(TypeProperty);
        set => SetValue(TypeProperty, value);
    }

    public bool ShowText
    {
        get => GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TypeProperty || change.Property == ShowTextProperty) Refresh();
    }

    private void Refresh()
    {
        var key = ObjectTypeColors.ResourceKey(Type);
        var color = key is not null && this.TryFindResource(key, ActualThemeVariant, out var found) && found is ISolidColorBrush brush
            ? brush.Color
            : (Color?)null;

        _text.Text = Type?.ToString() ?? "";
        _text.IsVisible = ShowText;
        _dot.IsVisible = color is not null;
        Padding = ShowText ? new Thickness(color is null ? 0 : 7, 1, 8, 1) : new Thickness(0);

        if (color is { } c)
        {
            _dot.Fill = new SolidColorBrush(c);
            _text.Foreground = new SolidColorBrush(c);
            Background = ShowText ? new SolidColorBrush(c, 0.14) : null;
        }
        else
        {
            _text.ClearValue(TextBlock.ForegroundProperty);
            Background = null;
        }
    }
}
