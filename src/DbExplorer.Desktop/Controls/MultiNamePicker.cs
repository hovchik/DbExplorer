using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// A drop-down to tick any number of names (schemas, tables). The button shows a summary; its list has a filter box
/// and an "all" entry at the top that clears every tick, so nothing ticked means everything. Items are
/// <see cref="PickChoice"/>s, whose IsSelected the ticks change directly.
/// </summary>
public class MultiNamePicker : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<MultiNamePicker, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string?> SummaryProperty =
        AvaloniaProperty.Register<MultiNamePicker, string?>(nameof(Summary));

    public static readonly StyledProperty<string> AllTextProperty =
        AvaloniaProperty.Register<MultiNamePicker, string>(nameof(AllText), "All");

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<MultiNamePicker, string>(nameof(Watermark), "Type to filter…");

    public static readonly StyledProperty<string> EmptyTextProperty =
        AvaloniaProperty.Register<MultiNamePicker, string>(nameof(EmptyText), "Nothing to pick from.");

    private readonly TextBlock _label;
    private readonly TextBox _filterBox;
    private readonly CheckBox _all;
    private readonly ItemsControl _list;
    private readonly TextBlock _empty;
    private readonly Flyout _flyout;
    private readonly Button _button;
    private INotifyCollectionChanged? _observed;
    private readonly List<PickChoice> _watched = [];

    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    /// <summary>Raised just before the list opens, so its items can be brought up to date.</summary>
    public event Action? Opening;

    /// <summary>What the button shows, e.g. "All schemas", "public" or "3 schemas".</summary>
    public string? Summary { get => GetValue(SummaryProperty); set => SetValue(SummaryProperty, value); }

    /// <summary>The top entry that clears every tick, e.g. "All schemas".</summary>
    public string AllText { get => GetValue(AllTextProperty); set => SetValue(AllTextProperty, value); }

    public string Watermark { get => GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }

    public string EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<MultiNamePicker, string>(nameof(Caption), "Pick");

    /// <summary>What a screen reader calls the button before its summary, e.g. "Schemas".</summary>
    public string Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    public MultiNamePicker()
    {
        _label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var arrow = new StrokeIcon { Data = Icons.ChevronDown, Width = 12, Height = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 1);

        _filterBox = new TextBox { Watermark = Watermark };
        AutomationProperties.SetName(_filterBox, "Filter");
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        _filterBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _flyout!.Hide();
            e.Handled = true;
        };

        _all = new CheckBox { FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 0) };
        _all.IsCheckedChanged += (_, _) =>
        {
            if (_all.IsChecked != true) { UpdateAll(); return; } // "all" can't be unticked by itself: tick a name instead
            foreach (var c in Choices()) c.IsSelected = false;
        };

        _list = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<PickChoice>((_, _) =>
            {
                // A TextBlock, not a string: a string's "_" would be taken as an access key and hidden (order_items).
                var text = new TextBlock();
                text.Bind(TextBlock.TextProperty, new Binding(nameof(PickChoice.Name)));
                var box = new CheckBox { Margin = new Thickness(2, 0, 0, 0), Content = text };
                box.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(PickChoice.IsSelected)) { Mode = BindingMode.TwoWay });
                box.Bind(AutomationProperties.NameProperty, new Binding(nameof(PickChoice.Name)));
                return box;
            })
        };
        _empty = new TextBlock { Opacity = 0.6, Margin = new Thickness(4, 2), TextWrapping = TextWrapping.Wrap };

        var content = new StackPanel
        {
            Spacing = 6,
            Width = 300,
            Children =
            {
                _filterBox,
                _all,
                new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)) },
                new ScrollViewer { MaxHeight = 340, Content = _list },
                _empty
            }
        };
        _flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = content };
        _flyout.Opening += (_, _) => Opening?.Invoke();
        _flyout.Opened += (_, _) =>
        {
            _filterBox.Text = "";
            ApplyFilter();
            Dispatcher.UIThread.Post(() => _filterBox.Focus(), DispatcherPriority.Input);
        };

        Content = _button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _label, arrow } },
            Flyout = _flyout,
        };
        UpdateTexts();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
            _observed = ItemsSource as INotifyCollectionChanged;
            if (_observed is not null) _observed.CollectionChanged += OnItemsChanged;
            Rewatch();
            ApplyFilter();
        }
        else if (change.Property == SummaryProperty || change.Property == AllTextProperty
                 || change.Property == WatermarkProperty || change.Property == EmptyTextProperty
                 || change.Property == CaptionProperty)
            UpdateTexts();
    }

    private IEnumerable<PickChoice> Choices() => ItemsSource?.OfType<PickChoice>() ?? [];

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rewatch();
        ApplyFilter();
    }

    /// <summary>Follows each choice's tick so the "all" entry stays right.</summary>
    private void Rewatch()
    {
        foreach (var c in _watched) c.PropertyChanged -= OnChoiceChanged;
        _watched.Clear();
        _watched.AddRange(Choices());
        foreach (var c in _watched) c.PropertyChanged += OnChoiceChanged;
        UpdateAll();
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PickChoice.IsSelected)) UpdateAll();
    }

    private void UpdateAll() => _all.IsChecked = !_watched.Any(c => c.IsSelected);

    private void ApplyFilter()
    {
        var text = _filterBox.Text?.Trim() ?? "";
        var visible = Choices().Where(c => text.Length == 0 || c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = visible;
        _empty.IsVisible = visible.Count == 0;
        _empty.Text = _watched.Count == 0 ? EmptyText : "No name contains that text.";
    }

    private void UpdateTexts()
    {
        _label.Text = string.IsNullOrEmpty(Summary) ? AllText : Summary;
        _all.Content = new TextBlock { Text = AllText };
        _filterBox.Watermark = Watermark;
        AutomationProperties.SetName(_button, $"{Caption}: {_label.Text}");
    }
}
