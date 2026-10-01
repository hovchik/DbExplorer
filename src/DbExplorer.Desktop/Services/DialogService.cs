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

    /// <param name="filter">Optional WHERE condition (e.g. one record's primary key); the window can drop it.</param>
    Task ShowGetDataAsync(
        QueryExecutionService queryService, DatabaseSession session, DbObject table,
        string? filter = null, string? filterDescription = null);

    /// <summary>Diagram of the tables a searched value was found in, and their found rows combined along foreign keys.</summary>
    void ShowDataRelations(
        QueryExecutionService queryService, DatabaseSession session, string term, IReadOnlyList<DataMatch> matches,
        Action<string, string?> openSql, Action<DbObject> openObject);

    Task<bool> ConfirmAsync(string message, string confirmText = "Run", string? requiredText = null, string? banner = null);

    void ShowCommandPalette(IReadOnlyList<PaletteItem> items);

    /// <summary>Asks for a value per query parameter; null when cancelled.</summary>
    Task<IReadOnlyDictionary<string, string>?> PromptParametersAsync(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> defaults);

    /// <summary>Shows a (possibly long) cell value, pretty-printed when it is JSON or XML.</summary>
    void ShowValue(string title, string value);

    /// <summary>Asks for one line of text; null when cancelled.</summary>
    Task<string?> PromptTextAsync(string title, string message, string label, string initial = "", string? watermark = null);

    /// <summary>Puts text on the clipboard.</summary>
    Task CopyTextAsync(string text);
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

    public async Task<IReadOnlyDictionary<string, string>?> PromptParametersAsync(
        IReadOnlyList<string> names, IReadOnlyDictionary<string, string> defaults)
    {
        if (Owner is null) return null;
        var window = new ParametersWindow(names, defaults);
        return await window.ShowDialog<IReadOnlyDictionary<string, string>?>(Owner);
    }

    public async Task<string?> PromptTextAsync(string title, string message, string label, string initial = "", string? watermark = null)
    {
        if (Owner is null) return null;
        return await new TextPromptWindow(title, message, label, initial, watermark).ShowDialog<string?>(Owner);
    }

    public async Task CopyTextAsync(string text)
    {
        if (Owner?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public void ShowValue(string title, string value)
    {
        var window = new ValueViewerWindow(title, value);
        if (Owner is not null) window.Show(Owner);
        else window.Show();
    }

    public void ShowCommandPalette(IReadOnlyList<PaletteItem> items)
    {
        if (Owner is null) return;
        var window = new CommandPaletteWindow { DataContext = new CommandPaletteViewModel(items) };
        window.Show(Owner);
    }

    public Task ShowGetDataAsync(
        QueryExecutionService queryService, DatabaseSession session, DbObject table,
        string? filter = null, string? filterDescription = null)
    {
        var vm = new GetDataViewModel();
        var window = new GetDataWindow { DataContext = vm };
        vm.Initialize(queryService, session, table, filter, filterDescription, this);

        if (Owner is not null) window.Show(Owner);
        else window.Show();

        return Task.CompletedTask;
    }

    public void ShowDataRelations(
        QueryExecutionService queryService, DatabaseSession session, string term, IReadOnlyList<DataMatch> matches,
        Action<string, string?> openSql, Action<DbObject> openObject)
    {
        var vm = new DataRelationsViewModel(queryService, session, term, matches) { OpenSql = openSql, OpenObject = openObject };
        var window = new DataRelationsWindow { DataContext = vm };
        window.Closed += (_, _) => vm.Cancel();
        _ = vm.LoadAsync();

        if (Owner is not null) window.Show(Owner);
        else window.Show();
    }
}
