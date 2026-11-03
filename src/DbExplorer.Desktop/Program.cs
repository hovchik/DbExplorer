using Avalonia;
using Avalonia.Logging;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write("unhandled", e.ExceptionObject as Exception);
        // A forgotten fire-and-forget task must not take the process down when the finalizer finds it.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write("unobserved task", e.Exception);
            e.SetObserved();
        };

        // Files from Explorer (Open, Open with, a double-click on a .sql file) go to the window already running.
        var files = DbExplorer.Application.Query.FileArguments.Parse(args, Environment.CurrentDirectory);
        var channel = new FileOpenChannel(FileOpenChannel.DefaultName(DbExplorer.Application.AppPaths.DefaultRoot));
        if (!channel.TryBecomePrimary() && files.Count > 0 &&
            channel.TrySendAsync(files, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult())
            return;
        App.StartupFiles = files;
        App.OpenChannel = channel;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("fatal", ex);
            throw;
        }
        finally
        {
            channel.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = RenderingOptions.Win32Modes() })
            .WithInterFont()
            .LogToTrace()
            // After LogToTrace, so the trace output is kept and render or platform failures also reach errors.log.
            .AfterSetup(_ => Logger.Sink = new ErrorLogSink(Logger.Sink));
}
