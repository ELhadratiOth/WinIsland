using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.App.Services;
using WinIsland.Core.Diagnostics;

namespace WinIsland.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private IslandHost? _host;

    public App()
    {
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
            _singleInstance.Dispose();
            _singleInstance = null;
            Exit();
            return;
        }

        _host = new IslandHost(DispatcherQueue.GetForCurrentThread());
        _host.ExitRequested += async (_, _) =>
        {
            await _host.DisposeAsync();
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
            Exit();
        };

        _host.Start();
    }
}
