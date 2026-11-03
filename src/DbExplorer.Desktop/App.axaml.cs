using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using DbExplorer.Application;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Diagnostics;
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
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AccessibleNames.Install(); // screen-reader names for icon buttons and unlabelled inputs
    }

    /// <summary>Watches the UI thread while the desktop app runs (null in tests and the render harness).</summary>
    internal static ResponsivenessWatchdog? Watchdog { get; private set; }

    /// <summary>The files named on the command line, opened in Query tabs once the saved tabs are back.</summary>
    internal static IReadOnlyList<string> StartupFiles { get; set; } = [];

    /// <summary>Receives the files a later start was asked to open (null in tests and the render harness).</summary>
    internal static FileOpenChannel? OpenChannel { get; set; }

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
        services.AddSingleton<NavigatorViewModel>();
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
        Keymap.Current = provider.GetRequiredService<Application.Shortcuts.KeymapService>();
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
            vm.ShowError($"Unexpected error: {e.Exception.GetBaseException().Message} (details in {ErrorLog.FilePath})");
        };

        // Records in errors.log when the UI thread stops answering, and for how long: a hung UI thread and a window
        // that only stopped repainting look the same to the user, and the log tells them apart.
        Watchdog = new ResponsivenessWatchdog(
            action => Dispatcher.UIThread.Post(action, DispatcherPriority.Send),
            line => ErrorLog.Note("ui watchdog", line));
        desktop.Exit += (_, _) => Watchdog?.Dispose();

        var shutdownStarted = false;
        desktop.ShutdownRequested += async (_, e) =>
        {
            if (shutdownStarted) return;
            shutdownStarted = true;
            e.Cancel = true;
            // Quitting from outside the window (e.g. the macOS menu) asks about unsaved work like closing it does.
            if (!vm.QuitConfirmed && !await vm.ConfirmLeaveAsync(LeaveReason.Quit))
            {
                shutdownStarted = false;
                return;
            }
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

        vm.StartupFiles = StartupFiles;
        _ = vm.InitializeAsync();
        OpenChannel?.Listen(paths => Dispatcher.UIThread.Post(async () =>
        {
            window.BringToFront();
            await vm.OpenFilesAsync(paths);
        }));

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

