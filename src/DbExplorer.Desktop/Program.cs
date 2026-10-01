using Avalonia;
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

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("fatal", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
