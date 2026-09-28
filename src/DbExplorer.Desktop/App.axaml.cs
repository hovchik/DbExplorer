using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DbExplorer.Application;
using DbExplorer.Core.Models;
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
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<DiagramViewModel>();
        services.AddSingleton<QueryViewModel>();
        services.AddSingleton<ComparerViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<AppSettingsService>();
        ApplyTheme(settings.Theme);
        settings.ThemeChanged += ApplyTheme;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            provider.GetRequiredService<DialogService>().Owner = window;
            desktop.MainWindow = window;

            var shutdownStarted = false;
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (shutdownStarted) return;
                shutdownStarted = true;
                e.Cancel = true;
                await vm.ShutdownAsync();
                await provider.DisposeAsync();
                desktop.Shutdown();
            };

            _ = vm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(AppThemeMode theme)
    {
        RequestedThemeVariant = theme switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}

