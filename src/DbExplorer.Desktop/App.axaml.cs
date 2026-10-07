using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using DbExplorer.Application;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;
using DbExplorer.Providers.MySql;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Desktop;

// Fully qualified: "Application" alone would resolve to the DbExplorer.Application namespace.
public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>The app's services: application layer, the engines and the view models. The render harness
    /// (tools/DbExplorer.UiRender) reuses this and swaps in a temporary <see cref="AppPaths"/> root.</summary>
    public static IServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection()
            .AddDbExplorerApplication()
            .AddSqlServerProvider()
            .AddPostgresProvider()
            .AddMySqlProvider();      // <- new engines are registered here

        services.AddSingleton<ISshHostKeyPrompt, SshHostKeyPrompt>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<ObjectsViewModel>();
        services.AddSingleton<MetadataSearchViewModel>();
        services.AddSingleton<DataSearchViewModel>();
        services.AddSingleton<IndexesViewModel>();
        services.AddSingleton<LocksViewModel>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<DiagramViewModel>();
        services.AddSingleton<TableDesignerViewModel>();
        services.AddSingleton<ErModelViewModel>();
        services.AddSingleton<QueryBuilderViewModel>();
        services.AddSingleton<SecurityViewModel>();
        services.AddTransient<QueryViewModel>();
        services.AddSingleton<Func<QueryViewModel>>(sp => sp.GetRequiredService<QueryViewModel>);
        services.AddSingleton<QueryWorkspaceViewModel>();
        services.AddSingleton<ComparerViewModel>();
        services.AddSingleton<ChangeRecorderViewModel>();
        services.AddSingleton<SchemaHistoryViewModel>();
        services.AddSingleton<RelationshipsViewModel>();
        services.AddSingleton<LabViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<TeamViewModel>();
        return services;
    }

    /// <summary>The main window with its view model, wired as the dialogs' owner.</summary>
    public static MainWindow CreateMainWindow(IServiceProvider provider)
    {
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        vm.AttachTeam(provider.GetRequiredService<TeamViewModel>());
        var window = new MainWindow { DataContext = vm };
        provider.GetRequiredService<DialogService>().Owner = window;
        return window;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Without a desktop lifetime (the render harness) the host builds the services itself.
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var provider = ConfigureServices().BuildServiceProvider();

        var settings = provider.GetRequiredService<AppSettingsService>();
        ApplyTheme(settings.Theme);
        settings.ThemeChanged += ApplyTheme;

        var window = CreateMainWindow(provider);
        var vm = (MainWindowViewModel)window.DataContext!;
        desktop.MainWindow = window;

        // An exception no command caught (a failed button handler, a lost connection mid-refresh) is logged and
        // shown in the status bar instead of closing the window and losing the user's open tabs.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            ErrorLog.Write("ui", e.Exception);
            e.Handled = true;
            vm.StatusText = $"Unexpected error: {e.Exception.GetBaseException().Message} (details in {ErrorLog.FilePath})";
        };

        var shutdownStarted = false;
        desktop.ShutdownRequested += async (_, e) =>
        {
            if (shutdownStarted) return;
            shutdownStarted = true;
            e.Cancel = true;
            try
            {
                // Rolling back open transactions talks to the server; an unreachable one must not keep the window open.
                var cleanup = CleanupAsync();
                if (await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(10))) != cleanup)
                    ErrorLog.Write("shutdown", new TimeoutException("Cleanup did not finish within 10 s; closing anyway."));
                else await cleanup;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("shutdown", ex);
            }
            finally
            {
                desktop.Shutdown();
            }

            async Task CleanupAsync()
            {
                await vm.ShutdownAsync();
                await provider.DisposeAsync();
            }
        };

        _ = vm.InitializeAsync();

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

