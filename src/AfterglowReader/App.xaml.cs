using System.IO;
using System.Windows.Threading;

namespace AfterglowReader;

/// <summary>
/// Application bootstrap with a small local crash log for early P0 diagnostics.
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AfterglowReader",
        "startup.log");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
        LogMessage("Startup", "MainWindow.Show completed");
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log("AppDomain", exception);
        }
    }

    private static void Log(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {source}: {exception}\n");
        }
        catch
        {
            // Logging must never make startup fail again.
        }
    }

    private static void LogMessage(string source, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {source}: {message}\n");
        }
        catch
        {
            // Logging must never make startup fail again.
        }
    }

    internal static void LogDiagnostic(string source, string message)
        => LogMessage(source, message);
}
