using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// Outline icons on a 24×24 grid, drawn by <see cref="StrokeIcon"/> with round caps and joins. Path data uses
/// Avalonia's path mini-language; every figure is stroked, never filled.
/// </summary>
public static class Icons
{
    public const string Plus = "M12 5v14 M5 12h14";
    public const string Pencil = "M4 20h4L19 9a2.83 2.83 0 0 0-4-4L4 16z M13.5 6.5l4 4";
    public const string Trash = "M4 7h16 M10 3h4 M6 7l1 12.2a2 2 0 0 0 2 1.8h6a2 2 0 0 0 2-1.8L18 7 M10 11v6 M14 11v6";
    public const string ArrowsUpDown = "M7 20V4 M3 8l4-4 4 4 M17 4v16 M13 16l4 4 4-4";
    public const string Users = "M9 11a4 4 0 1 0 0-8a4 4 0 1 0 0 8z M2 21v-1a6 6 0 0 1 6-6h2a6 6 0 0 1 6 6v1 M16 3.3a4 4 0 0 1 0 7.4 M19 14.3a6 6 0 0 1 3 5.2V21";
    public const string Search = "M11 18a7 7 0 1 0 0-14a7 7 0 1 0 0 14z M20 20l-4-4";
    public const string Refresh = "M20 12a8 8 0 0 0-14.3-4.9 M4 3v5h5 M4 12a8 8 0 0 0 14.3 4.9 M20 21v-5h-5";
    public const string Sun = "M12 16a4 4 0 1 0 0-8a4 4 0 1 0 0 8z M12 2.5v2 M12 19.5v2 M5.3 5.3l1.4 1.4 M17.3 17.3l1.4 1.4 M2.5 12h2 M19.5 12h2 M5.3 18.7l1.4-1.4 M17.3 6.7l1.4-1.4";
    public const string Moon = "M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z";
    public const string Monitor = "M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M8 20h8 M12 16v4";
    public const string Plug = "M9 3v5 M15 3v5 M6 8h12v3a6 6 0 0 1-12 0z M12 17v4";
    public const string Power = "M12 3v8 M6.3 6.8a8 8 0 1 0 11.4 0";
    public const string Close = "M6 6l12 12 M18 6L6 18";
    public const string Check = "M5 12.5l4.5 4.5L19 7.5";
    public const string Lock = "M7 11V8a5 5 0 0 1 10 0v3 M6 11h12a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1v-8a1 1 0 0 1 1-1z";
    public const string ChevronDown = "M7 10l5 5 5-5";
}

/// <summary>
/// An outline icon (see <see cref="Icons"/>) scaled from its 24×24 grid to the control's size and stroked with the
/// inherited text foreground, so it follows the button it sits in: hover, pressed, disabled and the theme.
/// </summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, string?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    /// <summary>Line width on the 24×24 grid: 2 draws about 1.3 px at the default 16 px size.</summary>
    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeWidth), 2.0);

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, ForegroundProperty, StrokeWidthProperty);
        WidthProperty.OverrideDefaultValue<StrokeIcon>(16);
        HeightProperty.OverrideDefaultValue<StrokeIcon>(16);
    }

    public string? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Data) || Foreground is not { } brush) return;
        if (!Cache.TryGetValue(Data, out var geometry)) Cache[Data] = geometry = Geometry.Parse(Data);

        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        if (scale <= 0) return;
        var dx = (Bounds.Width - 24 * scale) / 2;
        var dy = (Bounds.Height - 24 * scale) / 2;
        var pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(null, pen, geometry);
    }
}
