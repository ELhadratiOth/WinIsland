using System.Management;
using WinIsland.Core.Devices;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;

namespace WinIsland.Platform.Windows.Devices;

/// <summary>
/// Built-in display brightness through WMI (WmiMonitorBrightness*). Event-driven via
/// WmiMonitorBrightnessEvent. Desktops with external monitors report "not supported".
/// </summary>
public sealed class WmiBrightness : IBrightnessControl, IIntegration
{
    private static readonly TimeSpan OwnChangeWindow = TimeSpan.FromSeconds(2);

    private readonly ManagementScope _scope = new(@"root\WMI");
    private ManagementEventWatcher? _watcher;
    private volatile bool _supported;
    private int _level;
    private int _requested = -1;
    private int _applying;
    private int _lastOwnLevel = -1;
    private long _lastOwnTicks;

    public event EventHandler<bool>? Changed;

    public string Name => "Brightness";

    public bool RequiresNetwork => false;

    public bool IsSupported => _supported;

    public int Level => Volatile.Read(ref _level);

    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(_scope, new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE"));
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    Volatile.Write(ref _level, Convert.ToInt32(item["CurrentBrightness"], System.Globalization.CultureInfo.InvariantCulture));
                    _supported = true;
                }
            }
        }
        catch (ManagementException ex)
        {
            // Typical for desktops: no WMI brightness provider for external monitors.
            AppLog.Info(nameof(WmiBrightness), $"Brightness not available: {ex.ErrorCode}");
            _supported = false;
        }

        if (_supported)
        {
            _watcher = new ManagementEventWatcher(_scope, new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += OnEventArrived;
            _watcher.Start();
        }

        Changed?.Invoke(this, false);
    }, cancellationToken);

    public Task StopAsync()
    {
        if (_watcher is not null)
        {
            _watcher.EventArrived -= OnEventArrived;
            try
            {
                _watcher.Stop();
            }
            catch (ManagementException)
            {
            }

            _watcher.Dispose();
            _watcher = null;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Coalesces slider drags: only the latest requested level is applied.</summary>
    public Task SetLevelAsync(int level)
    {
        if (!_supported)
        {
            return Task.CompletedTask;
        }

        Volatile.Write(ref _requested, Math.Clamp(level, 0, 100));
        if (Interlocked.Exchange(ref _applying, 1) == 1)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                int next;
                while ((next = Interlocked.Exchange(ref _requested, -1)) >= 0)
                {
                    Volatile.Write(ref _lastOwnLevel, next);
                    Volatile.Write(ref _lastOwnTicks, Environment.TickCount64);
                    Apply(next);
                }
            }
            finally
            {
                Volatile.Write(ref _applying, 0);
            }
        });
    }

    private void Apply(int level)
    {
        using var methods = new ManagementClass(_scope, new ManagementPath("WmiMonitorBrightnessMethods"), null);
        foreach (ManagementObject instance in methods.GetInstances().Cast<ManagementObject>())
        {
            using (instance)
            {
                instance.InvokeMethod("WmiSetBrightness", [1u, (byte)level]);
            }
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        int level = Convert.ToInt32(e.NewEvent["Brightness"], System.Globalization.CultureInfo.InvariantCulture);
        if (Interlocked.Exchange(ref _level, level) == level)
        {
            return;
        }

        bool own = level == Volatile.Read(ref _lastOwnLevel) &&
            Environment.TickCount64 - Volatile.Read(ref _lastOwnTicks) < OwnChangeWindow.TotalMilliseconds;
        Changed?.Invoke(this, !own);
    }
}
