using Avalonia;
using dbclient.Services;

namespace dbclient;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Fatal("Unhandled exception (AppDomain)", e.ExceptionObject as Exception
                ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        AppLogger.LogStartupBanner();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLogger.Fatal("Unhandled exception", ex);
            throw;
        }
        finally
        {
            AppLogger.Info("dbclient exited");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
