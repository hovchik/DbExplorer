using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>"View side by side": a result goes, as a snapshot, to the Side by side board, next to results of other tabs.</summary>
public partial class QueryViewModel
{
    /// <summary>When the results on screen arrived (local time); the board shows it.</summary>
    private DateTime? _resultsAt;

    /// <summary>Asks the window to put a snapshot of a result on the Side by side board; returns the status line to show.</summary>
    public event Func<ResultSetView, BoardSource, string?>? SideBySideRequested;

    /// <summary>Where <paramref name="view"/> came from: this tab, its connection and database, and when it ran.</summary>
    public BoardSource SourceOf(ResultSetView view)
    {
        var profile = _session?.Profile;
        var database = NullIfEmpty(CurrentDatabase) ?? NullIfEmpty(profile?.Database);
        return new BoardSource(Title, profile?.DisplayName, database, view.PinnedAt ?? _resultsAt ?? DateTime.Now,
            view.Query ?? _resultsQuery);
    }

    [RelayCommand]
    private void ViewSideBySide(ResultSetView? view)
    {
        if (view is null || !ShownResults.Any(r => ReferenceEquals(r, view))) return;
        if (SideBySideRequested?.Invoke(view, SourceOf(view)) is { } status) Status = status;
    }
}
