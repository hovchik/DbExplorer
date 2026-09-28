using Avalonia.Controls;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Query;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;

namespace DbExplorer.Desktop.Services;

public interface IDialogService
{
    Task<ConnectionProfile?> EditConnectionAsync(ConnectionProfile draft, string title);

    Task ShowSearchResultAsync(
        DefinitionService definitions, DatabaseSession session,
        MetadataSearchResult result, MetadataSearchQuery query);

    Task ShowRoutineExecutionAsync(
        QueryExecutionService queryService, DatabaseSession session, DbObject routine);

    Task ShowGetDataAsync(
        QueryExecutionService queryService, DatabaseSession session, DbObject table);

    Task<bool> ConfirmAsync(string message, string confirmText = "Run", string? requiredText = null, string? banner = null);

    void ShowCommandPalette(IReadOnlyList<PaletteItem> items);
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

    public Task ShowRoutineExecutionAsync(
        QueryExecutionService queryService, DatabaseSession session, DbObject routine)
    {
        var vm = new RoutineExecutionViewModel();
        var window = new RoutineExecutionWindow { DataContext = vm };
        if (session.Profile.IsProduction && routine.Type == DbObjectType.Procedure)
        {
            vm.ConfirmBeforeRun = async () =>
            {
                var dialog = new ConfirmWindow(
                    $"Stored procedures can modify data. Run {routine.FullName} on PRODUCTION?",
                    "Run on production", requiredText: "PRODUCTION", banner: $"PRODUCTION · {session.Profile.DisplayName}");
                return await dialog.ShowDialog<bool>(window);
            };
        }
        _ = vm.InitializeAsync(queryService, session, routine);

        if (Owner is not null) window.Show(Owner);
        else window.Show();
        return Task.CompletedTask;
    }

    public async Task<bool> ConfirmAsync(string message, string confirmText = "Run", string? requiredText = null, string? banner = null)
    {
        var window = new ConfirmWindow(message, confirmText, requiredText, banner);
        if (Owner is null) return false;
        return await window.ShowDialog<bool>(Owner);
    }

    public void ShowCommandPalette(IReadOnlyList<PaletteItem> items)
    {
        if (Owner is null) return;
        var window = new CommandPaletteWindow { DataContext = new CommandPaletteViewModel(items) };
        window.Show(Owner);
    }

    public Task ShowGetDataAsync(
        QueryExecutionService queryService, DatabaseSession session, DbObject table)
    {
        var vm = new GetDataViewModel();
        var window = new GetDataWindow { DataContext = vm };
        vm.Initialize(queryService, session, table);

        if (Owner is not null) window.Show(Owner);
        else window.Show();

        return Task.CompletedTask;
    }
}
