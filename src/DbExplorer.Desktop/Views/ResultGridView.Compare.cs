using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.Views;

/// <summary>"Compare ▾": asks the host (the Query tab) to show another result next to this one, and lets two grids
/// scroll together.</summary>
public partial class ResultGridView
{
    /// <summary>Show "Compare ▾" (button, menu item, key). Off by default: only hosts that handle
    /// <see cref="CompareRequestedEvent"/> turn it on.</summary>
    public static readonly StyledProperty<bool> CanCompareProperty =
        AvaloniaProperty.Register<ResultGridView, bool>(nameof(CanCompare));

    public bool CanCompare
    {
        get => GetValue(CanCompareProperty);
        set => SetValue(CanCompareProperty, value);
    }

    /// <summary>Raised (bubbling) when the user asks to compare this result with another; the source is this grid, and
    /// <see cref="CompareAnchor"/> is where the host opens its list of results.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CompareRequestedEvent =
        RoutedEvent.Register<ResultGridView, RoutedEventArgs>(nameof(CompareRequested), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs>? CompareRequested
    {
        add => AddHandler(CompareRequestedEvent, value);
        remove => RemoveHandler(CompareRequestedEvent, value);
    }

    /// <summary>The Compare ▾ button, to open the list of results under it.</summary>
    public Control CompareAnchor => CompareButton;

    private MenuItem? _compareItem;
    private readonly Separator _compareSeparator = new();

    private void InitCompare()
    {
        _compareItem = Item("Compare with another result…", RequestCompare);
        Keymap.SetCommand(_compareItem, ShortcutCommands.GridCompare);
        if (Grid.ContextMenu is { } menu)
        {
            menu.Items.Add(_compareSeparator);
            menu.Items.Add(_compareItem);
        }
        CompareButton.Click += (_, _) => RequestCompare();
        UpdateCompare();
    }

    private void UpdateCompare()
    {
        var on = CanCompare;
        CompareButton.IsVisible = on;
        _compareSeparator.IsVisible = on;
        if (_compareItem is not null) _compareItem.IsVisible = on;
    }

    /// <summary>Asks the host for its list of results to compare with; nothing when the host does not offer it.</summary>
    public void RequestCompare()
    {
        if (!CanCompare) return;
        RaiseEvent(new RoutedEventArgs(CompareRequestedEvent, this));
    }

    // ---------------------------------------------------------------- scrolling several grids together

    /// <summary>Raised when the user scrolls this grid (wheel, keys, scroll bars), with the new offsets.</summary>
    public event Action<ResultGridView, double, double>? Scrolled;

    private ScrollBar? _vScroll;
    private ScrollBar? _hScroll;
    private bool _applyingScroll;

    /// <summary>The grid's scroll bars, found once its template is applied.</summary>
    private bool FindScrollBars()
    {
        if (_vScroll is not null && _hScroll is not null) return true;
        foreach (var bar in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Grid).OfType<ScrollBar>())
        {
            if (bar.Name == "PART_VerticalScrollbar" && _vScroll is null)
            {
                _vScroll = bar;
                bar.PropertyChanged += OnScrollBarChanged;
            }
            else if (bar.Name == "PART_HorizontalScrollbar" && _hScroll is null)
            {
                _hScroll = bar;
                bar.PropertyChanged += OnScrollBarChanged;
            }
        }
        return _vScroll is not null && _hScroll is not null;
    }

    /// <summary>Starts reporting scrolls through <see cref="Scrolled"/> (the board calls this for each of its grids).</summary>
    public void WatchScrolling()
    {
        if (FindScrollBars()) return;
        Grid.TemplateApplied += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => FindScrollBars(),
            Avalonia.Threading.DispatcherPriority.Loaded);
        Grid.LayoutUpdated += OnFirstLayout;
    }

    private void OnFirstLayout(object? sender, EventArgs e)
    {
        if (FindScrollBars()) Grid.LayoutUpdated -= OnFirstLayout;
    }

    private void OnScrollBarChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty || _applyingScroll) return;
        Scrolled?.Invoke(this, _hScroll?.Value ?? 0, _vScroll?.Value ?? 0);
    }

    /// <summary>The current offsets: sideways (pixels) and down (pixels, rows are all the same height).</summary>
    public (double Horizontal, double Vertical) ScrollOffset => (_hScroll?.Value ?? 0, _vScroll?.Value ?? 0);

    /// <summary>Scrolls to the given offsets (clamped to what this grid can scroll), without reporting it back.</summary>
    public void ScrollTo(double horizontal, double vertical)
    {
        if (!FindScrollBars()) return;
        _applyingScroll = true;
        try
        {
            Apply(_vScroll!, vertical);
            Apply(_hScroll!, horizontal);
        }
        finally
        {
            _applyingScroll = false;
        }
    }

    /// <summary>The DataGrid scrolls when its scroll bar reports a scroll (it does not watch the bar's value), and that
    /// handler is internal: set the bar's value, then let the grid process it the way it does a dragged thumb. Avalonia's
    /// DataGrid has no public offset API (checked by ResultsBoardViewTests, so an upgrade that renames it shows up).</summary>
    private void Apply(ScrollBar bar, double value)
    {
        var target = Math.Clamp(value, bar.Minimum, bar.Maximum);
        if (Math.Abs(bar.Value - target) < 0.5) return;
        bar.Value = target;
        var process = ReferenceEquals(bar, _vScroll) ? ProcessVertical : ProcessHorizontal;
        process?.Invoke(Grid, [ScrollEventType.ThumbTrack]);
    }

    private const System.Reflection.BindingFlags Internal =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

    /// <summary>False if an Avalonia upgrade removed the DataGrid's internal scroll handlers: "Scroll together" then does nothing.</summary>
    public static bool CanScrollTo => ProcessVertical is not null && ProcessHorizontal is not null;

    private static readonly System.Reflection.MethodInfo? ProcessVertical =
        typeof(DataGrid).GetMethod("ProcessVerticalScroll", Internal, [typeof(ScrollEventType)]);

    private static readonly System.Reflection.MethodInfo? ProcessHorizontal =
        typeof(DataGrid).GetMethod("ProcessHorizontalScroll", Internal, [typeof(ScrollEventType)]);
}
