using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>How the board lays its results out.</summary>
public enum BoardLayout
{
    /// <summary>Next to each other, left to right.</summary>
    Columns,

    /// <summary>One above the other, for wide results.</summary>
    Rows
}

/// <summary>
/// The Side by side main tab: results from any Query tab (any connection or database) shown next to each other, so they
/// can be compared by eye. Read-only snapshots; nothing is run, merged or diffed here.
/// </summary>
public partial class ResultsBoardViewModel : ViewModelBase
{
    private readonly ResultsBoard _board = new();

    /// <summary>The results in display order.</summary>
    public ObservableCollection<BoardResult> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsColumns))]
    [NotifyPropertyChangedFor(nameof(IsRows))]
    private BoardLayout _layout = BoardLayout.Columns;

    /// <summary>Scrolling one grid scrolls the others to the same row (and the same distance sideways).</summary>
    [ObservableProperty] private bool _syncScroll = true;

    public bool IsColumns
    {
        get => Layout == BoardLayout.Columns;
        set { if (value) Layout = BoardLayout.Columns; }
    }

    public bool IsRows
    {
        get => Layout == BoardLayout.Rows;
        set { if (value) Layout = BoardLayout.Rows; }
    }

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public int Count => Items.Count;

    /// <summary>The main tab's header: the count tells there is something to look at.</summary>
    public string Header => Items.Count == 0 ? "Side by side" : $"Side by side ({Items.Count})";

    public string Summary => Items.Count switch
    {
        0 => "",
        1 => "1 result. Add another from any Query tab to see them next to each other.",
        _ => $"{Items.Count} results"
    };

    /// <summary>The board changed (added, removed, moved, cleared); the view lays the grids out again.</summary>
    public event Action? Changed;

    /// <summary>Puts a snapshot of <paramref name="view"/> on the board. Returns the status line to show: what happened,
    /// or why it was not added.</summary>
    public string Add(ResultSetView view, BoardSource source)
    {
        if (_board.WhyNot(view) is { } why) return why;
        var item = _board.Add(view, source)!;
        Items.Add(item);
        OnChanged();
        return Items.Count == 1
            ? $"\"{item.Title}\" is on the Side by side board. Add another result from any Query tab to see them next to each other."
            : $"\"{item.Title}\" added. {Items.Count} results side by side.";
    }

    [RelayCommand]
    private void Remove(BoardResult? item)
    {
        if (item is null || !_board.Remove(item)) return;
        Items.Remove(item);
        OnChanged();
    }

    [RelayCommand]
    private void MoveLeft(BoardResult? item) => Move(item, -1);

    [RelayCommand]
    private void MoveRight(BoardResult? item) => Move(item, +1);

    private void Move(BoardResult? item, int delta)
    {
        if (item is null || !_board.Move(item, delta)) return;
        Items.Move(Items.IndexOf(item), _board.IndexOf(item));
        OnChanged();
    }

    [RelayCommand(CanExecute = nameof(HasItems))]
    private void Clear()
    {
        if (Items.Count == 0) return;
        _board.Clear();
        Items.Clear();
        OnChanged();
    }

    partial void OnLayoutChanged(BoardLayout value) => Changed?.Invoke();

    private void OnChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Summary));
        ClearCommand.NotifyCanExecuteChanged();
        Changed?.Invoke();
    }
}
