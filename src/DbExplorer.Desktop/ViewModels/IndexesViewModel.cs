using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

public partial class IndexesViewModel : ViewModelBase, ISessionAware
{
    private DatabaseSession? _session;
    private IReadOnlyList<DbIndex> _all = [];

    [ObservableProperty] private bool _includeFragmentation;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private IReadOnlyList<DbIndex> _indexes = [];
    [ObservableProperty] private string _status = "Press Load. Fragmentation uses LIMITED mode (SQL Server) and can take a while on large databases.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private bool _isLoading;

    public void Attach(DatabaseSession? session)
    {
        _session = session;
        _all = [];
        Indexes = [];
        // A load of the previous session may still be running; its result is dropped and must not block this one.
        IsLoading = false;
        LoadCommand.NotifyCanExecuteChanged();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private bool CanLoad => _session is not null && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanLoad), AllowConcurrentExecutions = true)]
    private async Task LoadAsync()
    {
        if (_session is not { } session) return;
        IsLoading = true;
        Status = "Loading…";
        try
        {
            var all = await session.Provider.GetIndexesAsync(IncludeFragmentation);
            if (!ReferenceEquals(session, _session)) return;
            _all = all;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(session, _session)) Status = "Error: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(session, _session)) IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var f = FilterText.Trim();
        Indexes = f.Length == 0
            ? _all
            : _all.Where(i =>
                    $"{i.Schema}.{i.Table}".Contains(f, StringComparison.OrdinalIgnoreCase)
                    || i.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || (i.Columns?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        Status = $"{Indexes.Count:N0} of {_all.Count:N0} indexes";
    }
}
