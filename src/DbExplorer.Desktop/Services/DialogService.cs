using Avalonia.Controls;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;

namespace DbExplorer.Desktop.Services;

public interface IDialogService
{
    Task<ConnectionProfile?> EditConnectionAsync(ConnectionProfile draft, string title);

    Task ShowSearchResultAsync(
        DefinitionService definitions, DatabaseSession session,
        MetadataSearchResult result, MetadataSearchQuery query);
}

public sealed class DialogService(ProviderRegistry registry) : IDialogService
{
    public Window? Owner { get; set; }

    public async Task<ConnectionProfile?> EditConnectionAsync(ConnectionProfile draft, string title)
    {
        if (Owner is null) return null;

        var vm = new ConnectionDialogViewModel(registry, draft);
        var dialog = new ConnectionDialog { DataContext = vm, Title = title };
        var ok = await dialog.ShowDialog<bool>(Owner);
        return ok ? vm.ToProfile() : null;
    }

    public async Task ShowSearchResultAsync(
        DefinitionService definitions, DatabaseSession session,
        MetadataSearchResult result, MetadataSearchQuery query)
    {
        var vm = new SearchResultDetailViewModel();
        var window = new SearchResultDetailWindow { DataContext = vm };
        await vm.LoadAsync(definitions, session, result, query);

        if (Owner is not null) window.Show(Owner);
        else window.Show();
    }
}
