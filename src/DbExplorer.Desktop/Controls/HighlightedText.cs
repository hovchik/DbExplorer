using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// A one-line TextBlock built from <see cref="HighlightSegment"/>s: the runs a search found get the theme's
/// AppSearchHitBrush behind them and semi-bold text. It trims with an ellipsis like any TextBlock.
/// </summary>
public sealed class HighlightedText : TextBlock
{
    public static readonly StyledProperty<IEnumerable?> SegmentsProperty =
        AvaloniaProperty.Register<HighlightedText, IEnumerable?>(nameof(Segments));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public HighlightedText()
    {
        TextTrimming = TextTrimming.CharacterEllipsis;
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    public IEnumerable? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty) Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    private void Rebuild()
    {
        var inlines = Inlines ??= [];
        inlines.Clear();
        if (Segments is null) return;
        var hit = this.TryFindResource("AppSearchHitBrush", ActualThemeVariant, out var found) && found is IBrush b
            ? b : new SolidColorBrush(Color.FromArgb(0x66, 0xF2, 0xC0, 0x3C));
        foreach (var segment in Segments.OfType<HighlightSegment>())
        {
            // Tabs in code lines would push the text far right; one space reads the same.
            var run = new Run(segment.Text.Replace('\t', ' '));
            if (segment.IsMatch)
            {
                run.Background = hit;
                run.FontWeight = FontWeight.SemiBold;
            }
            inlines.Add(run);
        }
    }
}
