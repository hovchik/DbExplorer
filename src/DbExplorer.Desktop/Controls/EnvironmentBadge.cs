using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DbExplorer.Core.Connections;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// The connection's environment as a small tinted pill ("DEV", "TEST", "STAGING", "PROD"), drawn like
/// <see cref="ObjectTypeBadge"/> with the same theme brushes: development green, test blue, staging orange and
/// production red. Hidden when the connection has no environment.
/// </summary>
public sealed class EnvironmentBadge : Border
{
    public static readonly StyledProperty<ConnectionEnvironment> EnvironmentProperty =
        AvaloniaProperty.Register<EnvironmentBadge, ConnectionEnvironment>(nameof(Environment));

    private readonly TextBlock _text = new()
    {
        VerticalAlignment = VerticalAlignment.Center, FontSize = 10.5, FontWeight = FontWeight.Bold, LetterSpacing = 0.4
    };

    public EnvironmentBadge()
    {
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(6, 1, 6, 1);
        VerticalAlignment = VerticalAlignment.Center;
        Child = _text;
        ActualThemeVariantChanged += (_, _) => Refresh();
        Refresh();
    }

    public ConnectionEnvironment Environment
    {
        get => GetValue(EnvironmentProperty);
        set => SetValue(EnvironmentProperty, value);
    }

    /// <summary>The theme brush for an environment, shared with the Objects tab's type colours.</summary>
    public static string? ResourceKey(ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Development => "AppObjectViewBrush",
        ConnectionEnvironment.Test => "AppObjectProcedureBrush",
        ConnectionEnvironment.Staging => "AppObjectTableBrush",
        ConnectionEnvironment.Production => "AppObjectTriggerBrush",
        _ => null
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EnvironmentProperty) Refresh();
    }

    private void Refresh()
    {
        var key = ResourceKey(Environment);
        IsVisible = key is not null;
        if (key is null) return;
        _text.Text = Environment.ShortTag();
        if (this.TryFindResource(key, ActualThemeVariant, out var found) && found is ISolidColorBrush brush)
        {
            _text.Foreground = new SolidColorBrush(brush.Color);
            Background = new SolidColorBrush(brush.Color, 0.16);
        }
    }
}
