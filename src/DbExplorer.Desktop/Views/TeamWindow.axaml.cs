using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class TeamWindow : Window
{
    public TeamWindow() => InitializeComponent();

    private TeamViewModel? Vm => DataContext as TeamViewModel;

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick the folder your team shares",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        if (folders[0].TryGetLocalPath() is not { } path)
        {
            vm.Status = "Pick a folder on this computer or a mapped network drive.";
            return;
        }
        vm.SetFolder(path);
    }

    private void OnShareConnections(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var profiles = vm.GetProfiles();
        var menu = new MenuFlyout();
        if (profiles.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No saved connections yet", IsEnabled = false });
        else
        {
            var all = new MenuItem { Header = $"All {profiles.Count} connections" };
            all.Click += async (_, _) => await vm.ShareConnectionsAsync(profiles);
            menu.Items.Add(all);
            menu.Items.Add(new Separator());
        }
        foreach (var profile in profiles)
        {
            var item = new MenuItem { Header = profile.ToString() };
            item.Click += async (_, _) => await vm.ShareConnectionsAsync([profile]);
            menu.Items.Add(item);
        }
        menu.ShowAt(ShareConnectionsButton);
    }

    private void OnShareSnippet(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var menu = new MenuFlyout();
        if (vm.MySnippets.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No snippets yet: save one from a query tab's Snippets menu", IsEnabled = false });
        foreach (var snippet in vm.MySnippets)
        {
            var item = new MenuItem { Header = snippet.Name };
            item.Click += async (_, _) => await vm.ShareSnippetAsync(snippet);
            menu.Items.Add(item);
        }
        menu.ShowAt(ShareSnippetButton);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
