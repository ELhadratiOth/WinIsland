using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.App.Services;
using WinIsland.Core.Diagnostics;
using WinIsland.Platform.Windows.Shell;
using WinIsland.Platform.Windows.Windowing;

namespace WinIsland.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Lives for the whole process; everything is released in Shutdown().")]
public partial class App : Application
{
    private const int OpenLogId = 998;
    private const int ExitId = 999;

    private Mutex? _singleInstance;
    private MessageWindow? _trayWindow;
    private TrayIcon? _tray;
    private IslandHost? _host;
    private string? _startupError;

    public App()
    {
        AppLog.EnableFile(AppLog.DefaultPath);
        AppLog.Info(nameof(App), $"WinIsland {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportFatal("Unhandled exception", e.ExceptionObject as Exception);
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

        // The tray icon comes first and lives on its own hidden window, so Exit and the log are
        // always reachable — even if the island itself fails to start.
        try
        {
            _trayWindow = new MessageWindow("WinIsland.Tray");
            _tray = new TrayIcon(_trayWindow, "WinIsland");
            _tray.MenuRequested += OnTrayMenuRequested;
            _tray.Show();
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(App), "Tray icon unavailable", ex);
        }

        try
        {
            _host = new IslandHost(DispatcherQueue.GetForCurrentThread());
            _host.Start();
            AppLog.Info(nameof(App), "Startup complete");
        }
        catch (Exception ex)
        {
            _host = null;
            _startupError = ex.Message;
            ReportFatal("WinIsland could not start", ex);
            if (_tray is null)
            {
                // Nothing left to interact with: don't leave an invisible process behind.
                _ = ShutdownAsync();
            }
        }
    }

    private void OnTrayMenuRequested(object? sender, (int X, int Y) anchor)
    {
        if (_tray is null || _trayWindow is null)
        {
            return;
        }

        var items = new List<TrayMenuItem>();
        if (_host is not null)
        {
            items.AddRange(_host.GetTrayMenuItems());
        }
        else
        {
            items.Add(new TrayMenuItem(0, $"WinIsland failed to start: {_startupError}", IsEnabled: false));
        }

        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(OpenLogId, "Open log file"));
        items.Add(new TrayMenuItem(ExitId, "Exit WinIsland"));

        int command = _tray.ShowMenu(items, anchor.X, anchor.Y);
        switch (command)
        {
            case 0:
                break;
            case OpenLogId:
                OpenLog();
                break;
            case ExitId:
                _ = ShutdownAsync();
                break;
            default:
                _host?.HandleTrayCommand(command);
                break;
        }
    }

    private async Task ShutdownAsync()
    {
        AppLog.Info(nameof(App), "Exiting");
        if (_host is not null)
        {
            try
            {
                await _host.DisposeAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(nameof(App), "Shutdown failed", ex);
            }
        }

        _tray?.Dispose();
        _trayWindow?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        Exit();
    }

    private static void OpenLog()
    {
        string path = AppLog.FilePath ?? AppLog.DefaultPath;
        try
        {
            Process.Start(new ProcessStartInfo(File.Exists(path) ? path : Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(App), "Could not open the log", ex);
        }
    }

    private static void ReportFatal(string message, Exception? ex)
    {
        AppLog.Error(nameof(App), message, ex);
        NativeDialog.ShowError(
            "WinIsland",
            $"{message}.\n\n{ex?.GetType().Name}: {ex?.Message}\n\nDetails were written to:\n{AppLog.FilePath ?? AppLog.DefaultPath}");
    }
}
