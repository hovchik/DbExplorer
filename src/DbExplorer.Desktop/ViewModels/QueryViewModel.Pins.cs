using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Pinned results: a result tab the user pins stays, read-only, when the query runs again.</summary>
public partial class QueryViewModel
{
    public ResultPins Pins { get; } = new();

    /// <summary>The result tabs on screen: pinned results first, then the latest run's (<see cref="ResultSets"/>).</summary>
    public IReadOnlyList<ResultSetView> ShownResults => Pins.Arrange(ResultSets);

    [ObservableProperty] private ResultSetView? _selectedResult;

    public bool HasPinnedResults => Pins.Count > 0;

    /// <summary>The SQL behind the results on screen, remembered on pinned results.</summary>
    private string? _resultsQuery;

    /// <summary>The tab to select the next time the shown results change.</summary>
    private ResultSetView? _selectNext;

    partial void OnResultSetsChanged(IReadOnlyList<ResultSetView> oldValue, IReadOnlyList<ResultSetView> newValue) =>
        RefreshShownResults(oldValue, newValue, keepPosition: _isRefreshRun || _isFetchMoreRun);

    /// <param name="keepPosition">Keep the selected tab's position among the run's results (auto refresh, pin changes).</param>
    private void RefreshShownResults(IReadOnlyList<ResultSetView> oldRunResults, IReadOnlyList<ResultSetView> newRunResults, bool keepPosition)
    {
        var select = Pins.ChooseSelection(_selectNext, SelectedResult, oldRunResults, newRunResults, keepPosition);
        _selectNext = null;
        OnPropertyChanged(nameof(ShownResults));
        OnPropertyChanged(nameof(HasPinnedResults));
        SelectedResult = select;
        CloseAllPinnedResultsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Pins a result of the latest run: it moves to the pinned tabs, where the next run leaves it alone.</summary>
    [RelayCommand]
    private void PinResult(ResultSetView? view)
    {
        if (view is null || !ResultSets.Any(r => ReferenceEquals(r, view))) return;
        if (Pins.WhyNot(view) is { } why)
        {
            Status = why;
            return;
        }
        var pinned = Pins.Pin(view, _resultsQuery, DateTime.Now);
        _selectNext = pinned;
        ResultSets = ResultSets.Where(r => !ReferenceEquals(r, view)).ToList();
    }

    [RelayCommand]
    private void ClosePinnedResult(ResultSetView? view)
    {
        if (view is null || !Pins.Remove(view)) return;
        RefreshShownResults(ResultSets, ResultSets, keepPosition: true);
    }

    [RelayCommand(CanExecute = nameof(HasPinnedResults))]
    private void CloseAllPinnedResults()
    {
        if (Pins.Count == 0) return;
        Pins.Clear();
        RefreshShownResults(ResultSets, ResultSets, keepPosition: true);
    }

    [RelayCommand]
    private async Task RenamePinnedResultAsync(ResultSetView? view)
    {
        if (view is null || !Pins.Contains(view)) return;
        var title = await dialogs.PromptTextAsync("Rename pinned result", "A name for this pinned result.", "Name", view.Title, "Before the fix");
        if (string.IsNullOrWhiteSpace(title) || Pins.Rename(view, title) is not { } renamed) return;
        _selectNext = renamed;
        RefreshShownResults(ResultSets, ResultSets, keepPosition: true);
    }
}
