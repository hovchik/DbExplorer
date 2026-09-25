using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;

namespace DbExplorer.Desktop.ViewModels;

public partial class MetadataSearchViewModel(MetadataSearchService service) : ViewModelBase, ISessionAware
{
    private const int MaxResults = 5000;

    private DatabaseSession? _session;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _searchObjects = true;
    [ObservableProperty] private bool _searchColumns = true;
    [ObservableProperty] private bool _searchDefinitions = true;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private IReadOnlyList<MetadataSearchResult> _results = [];
    [ObservableProperty] private string _status = "Searches names and source code in the local metadata snapshot — no load on the server.";

    public void Attach(DatabaseSession? session)
    {
        _cts?.Cancel();
        _session = session;
        Results = [];
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_session is null || string.IsNullOrWhiteSpace(Query)) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var scope = MetadataSearchScope.None;
        if (SearchObjects) scope |= MetadataSearchScope.ObjectNames;
        if (SearchColumns) scope |= MetadataSearchScope.ColumnNames;
        if (SearchDefinitions) scope |= MetadataSearchScope.Definitions;

        var query = new MetadataSearchQuery(Query, scope, MatchCase, WholeWord, UseRegex, MaxResults);
        var snapshot = _session.Snapshot;
        var sw = Stopwatch.StartNew();
        Status = "Searching…";

        try
        {
            var results = await Task.Run(() => service.Search(snapshot, query, ct), ct);
            if (ct.IsCancellationRequested) return;
            Results = results;
            Status = $"{results.Count:N0} matches in {sw.ElapsedMilliseconds:N0} ms" +
                     (results.Count >= MaxResults ? " (limit reached — refine the search)" : "");
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArgumentException ex)
        {
            Status = "Invalid pattern: " + ex.Message;
        }
    }
}
