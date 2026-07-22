using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Threading;
using PlatformNativeWindow = AfterglowReader.SystemIntegration.NativeWindow;

namespace AfterglowReader;

/// <summary>
/// Application bootstrap with a small local crash log for early P0 diagnostics.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\AfterglowReader.SingleInstance";
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AfterglowReader",
        "startup.log");
    private Mutex? _instanceMutex;

    public App()
    {
        AssemblyLoadContext.Default.Resolving += ResolveCompositionDependency;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static Assembly? ResolveCompositionDependency(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is not ("Microsoft.Windows.SDK.NET" or "WinRT.Runtime"))
        {
            return null;
        }

        var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            PlatformNativeWindow.NotifyExistingInstance();
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
        LogMessage("Startup", "MainWindow.Show completed");
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
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

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log("TaskScheduler", e.Exception);
        e.SetObserved();
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
