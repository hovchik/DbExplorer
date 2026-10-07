using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// The drop-down every database is chosen from: a button showing the picked name that opens a
/// <see cref="NameFilterList"/>, so a database can be found by typing any part of its name.
/// Bound like a ComboBox: <see cref="ItemsSource"/>, <see cref="SelectedItem"/> (two-way) and <see cref="PlaceholderText"/>.
/// </summary>
public class DatabasePicker : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<DatabasePicker, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string?> SelectedItemProperty =
        AvaloniaProperty.Register<DatabasePicker, string?>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<DatabasePicker, string?>(nameof(PlaceholderText), "(database)");

    private readonly Button _button;
    private readonly TextBlock _label;
    private readonly Flyout _flyout;
    private readonly NameFilterList _list;

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>The picked database; null shows <see cref="PlaceholderText"/>.</summary>
    public string? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public DatabasePicker()
    {
        _label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var arrow = new TextBlock { Text = "▾", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 1);

        _list = new NameFilterList();
        _list.Picked += name =>
        {
            SelectedItem = name;
            _flyout!.Hide();
        };
        _list.Dismissed += () => _flyout!.Hide();

        _flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = _list };
        _flyout.Opened += (_, _) =>
        {
            _list.Width = Math.Max(300, Bounds.Width);
            _list.Reset(SelectedItem);
        };

        _button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _label, arrow } },
            Flyout = _flyout,
        };
        Content = _button;
        UpdateLabel();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty) _list.ItemsSource = ItemsSource;
        else if (change.Property == SelectedItemProperty || change.Property == PlaceholderTextProperty) UpdateLabel();
    }

    private void UpdateLabel()
    {
        var picked = !string.IsNullOrEmpty(SelectedItem);
        _label.Text = picked ? SelectedItem : PlaceholderText;
        _label.Opacity = picked ? 1 : 0.6;
    }
}
