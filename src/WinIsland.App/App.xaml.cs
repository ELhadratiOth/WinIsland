using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.App.Services;
using WinIsland.Core.Diagnostics;
using WinIsland.Platform.Windows.Shell;

namespace WinIsland.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Lives for the whole process; the host and mutex are released in the exit handler.")]
public partial class App : Application
{
    private Mutex? _singleInstance;
    private IslandHost? _host;

    public App()
    {
        AppLog.EnableFile(AppLog.DefaultPath);
        AppLog.Info(nameof(App), $"WinIsland {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error(nameof(App), "Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // A background utility must not take the desktop down with it over a non-fatal error.
            AppLog.Error(nameof(App), "Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\WinIsland.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            AppLog.Info(nameof(App), "Another instance is already running; exiting");
            _singleInstance.Dispose();
            _singleInstance = null;
            Exit();
            return;
        }

        try
        {
            _host = new IslandHost(DispatcherQueue.GetForCurrentThread());
            _host.ExitRequested += async (_, _) =>
            {
                AppLog.Info(nameof(App), "Exit requested");
                await _host.DisposeAsync();
                _singleInstance?.ReleaseMutex();
                _singleInstance?.Dispose();
                Exit();
            };

            _host.Start();
            AppLog.Info(nameof(App), "Startup complete");
        }
        catch (Exception ex)
        {
            Fatal("WinIsland could not start", ex);
            Exit();
        }
    }

    private static void Fatal(string message, Exception? ex)
    {
        AppLog.Error(nameof(App), message, ex);
        NativeDialog.ShowError(
            "WinIsland",
            $"{message}.\n\n{ex?.GetType().Name}: {ex?.Message}\n\nDetails were written to:\n{AppLog.FilePath ?? AppLog.DefaultPath}");
    }
}
