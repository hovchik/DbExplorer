using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// Pens for <c>Render</c> overrides. A mutable <see cref="Pen"/> drawn with a long-lived brush (a theme resource, an
/// inherited foreground) stays registered with that brush in the compositor for as long as the brush lives, so creating
/// one per frame grows memory on every redraw. Immutable pens are not tracked and are collected with the frame.
/// </summary>
public static class RenderPens
{
    public static ImmutablePen Create(IBrush brush, double thickness, IDashStyle? dashStyle = null,
        PenLineCap lineCap = PenLineCap.Flat, PenLineJoin lineJoin = PenLineJoin.Miter) =>
        new(brush.ToImmutable(), thickness, dashStyle switch
        {
            null => null,
            ImmutableDashStyle immutable => immutable,
            _ => new ImmutableDashStyle(dashStyle.Dashes, dashStyle.Offset)
        }, lineCap, lineJoin);
}
