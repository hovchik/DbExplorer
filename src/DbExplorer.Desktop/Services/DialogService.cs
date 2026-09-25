using Avalonia.Controls;
using DbExplorer.Application.Providers;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;

namespace DbExplorer.Desktop.Services;

public interface IDialogService
{
    Task<ConnectionProfile?> EditConnectionAsync(ConnectionProfile draft, string title);
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
}
