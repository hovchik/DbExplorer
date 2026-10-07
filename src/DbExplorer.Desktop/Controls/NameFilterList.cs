using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Application.Search;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// A filter box over a list of names: typing narrows the list to the names that contain the text (ignoring case),
/// Up/Down move through the matches while the caret stays in the box, Enter or a click picks one and Esc dismisses.
/// The body of <see cref="DatabasePicker"/>'s drop-down, and of other lists a database is chosen from.
/// </summary>
public class NameFilterList : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<NameFilterList, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<NameFilterList, string>(nameof(Watermark), "Type to filter databases…");

    public static readonly StyledProperty<double> MaxListHeightProperty =
        AvaloniaProperty.Register<NameFilterList, double>(nameof(MaxListHeight), 380);

    private readonly TextBox _filterBox;
    private readonly ListBox _list;
    private INotifyCollectionChanged? _observed;

    /// <summary>The names to choose from.</summary>
    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public double MaxListHeight
    {
        get => GetValue(MaxListHeightProperty);
        set => SetValue(MaxListHeightProperty, value);
    }

    /// <summary>A name was picked with Enter or a click.</summary>
    public event Action<string>? Picked;

    /// <summary>Esc was pressed in the filter box.</summary>
    public event Action? Dismissed;

    public NameFilterList()
    {
        _filterBox = new TextBox { Watermark = Watermark };
        _list = new ListBox { MaxHeight = MaxListHeight };
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        _filterBox.KeyDown += OnFilterKeyDown;
        _list.Tapped += OnListTapped;
        _list.KeyDown += OnListKeyDown;
        Content = new StackPanel { Spacing = 6, Children = { _filterBox, _list } };
    }

    /// <summary>Clears the filter, selects <paramref name="current"/> and puts the caret in the filter box; called each time the list is shown.</summary>
    public void Reset(string? current)
    {
        _filterBox.Text = "";
        ApplyFilter();
        _list.SelectedItem = current;
        if (current is not null) _list.ScrollIntoView(current);
        Dispatcher.UIThread.Post(() => _filterBox.Focus(), DispatcherPriority.Input);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
            _observed = ItemsSource as INotifyCollectionChanged;
            if (_observed is not null) _observed.CollectionChanged += OnItemsChanged;
            ApplyFilter();
        }
        else if (change.Property == WatermarkProperty) _filterBox.Watermark = Watermark;
        else if (change.Property == MaxListHeightProperty) _list.MaxHeight = MaxListHeight;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var names = ItemsSource?.OfType<string>() ?? [];
        _list.ItemsSource = NameFilter.Apply(names, _filterBox.Text);
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        var count = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Down or Key.Up when count > 0:
                var index = _list.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                _list.SelectedIndex = Math.Clamp(index, 0, count - 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                Pick(_list.SelectedItem as string ?? (count > 0 ? _list.Items[0] as string : null));
                e.Handled = true;
                break;
            case Key.Escape:
                Dismissed?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        // Only a tap on a name picks it, not one on the scroll bar.
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: string name }) Pick(name);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Pick(_list.SelectedItem as string);
        e.Handled = true;
    }

    private void Pick(string? name)
    {
        if (name is not null) Picked?.Invoke(name);
    }
}
