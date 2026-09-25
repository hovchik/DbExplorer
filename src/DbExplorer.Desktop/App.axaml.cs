using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DbExplorer.Application;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Desktop;

// Fully qualified: "Application" alone would resolve to the DbExplorer.Application namespace.
public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection()
            .AddDbExplorerApplication()
            .AddSqlServerProvider()
            .AddPostgresProvider();   // <- new engines are registered here

        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<ObjectsViewModel>();
        services.AddSingleton<MetadataSearchViewModel>();
        services.AddSingleton<DataSearchViewModel>();
        services.AddSingleton<IndexesViewModel>();
        services.AddSingleton<LocksViewModel>();
        services.AddSingleton<QueryViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        var provider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            provider.GetRequiredService<DialogService>().Owner = window;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => vm.Shutdown();
            _ = vm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
