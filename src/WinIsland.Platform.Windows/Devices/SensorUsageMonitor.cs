using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinIsland.Core.Devices;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;
using WinIsland.Core.Media;

namespace WinIsland.Platform.Windows.Devices;

/// <summary>
/// Which apps are using the microphone or camera, read from the same registry data that drives
/// the Windows privacy indicator (CapabilityAccessManager\ConsentStore: an app is recording while
/// its LastUsedTimeStop is 0). Event-driven with RegNotifyChangeKeyValue; nothing polls.
/// </summary>
public sealed partial class SensorUsageMonitor : IPrivacySource, IIntegration
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const uint RegNotifyChangeName = 0x1;
    private const uint RegNotifyChangeLastSet = 0x4;
    private const uint RegNotifyThreadAgnostic = 0x10000000;

    private readonly List<Watch> _watches = [];
    private readonly Lock _gate = new();
    private IReadOnlyList<SensorUse> _current = [];

    public event EventHandler? Changed;

    public string Name => "Microphone and camera";

    public bool RequiresNetwork => false;

    public IReadOnlyList<SensorUse> Current => Volatile.Read(ref _current);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Add("microphone", SensorKind.Microphone);
            Add("webcam", SensorKind.Camera);
        }

        Rescan();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            foreach (Watch watch in _watches)
            {
                watch.Dispose();
            }

            _watches.Clear();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(nint key, int watchSubtree, uint filter, nint eventHandle, int asynchronous);

    // Caller holds _gate.
    private void Add(string capability, SensorKind kind)
    {
        RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\{capability}");
        if (key is null)
        {
            return;
        }

        var watch = new Watch(key, kind, this);
        _watches.Add(watch);
        watch.Arm();
    }

    private void Rescan()
    {
        var uses = new List<SensorUse>();
        lock (_gate)
        {
            foreach (Watch watch in _watches)
            {
                Collect(watch.Key, watch.Kind, uses);
            }
        }

        IReadOnlyList<SensorUse> previous = Current;
        if (!previous.SequenceEqual(uses))
        {
            Volatile.Write(ref _current, uses);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void Collect(RegistryKey root, SensorKind kind, List<SensorUse> uses)
    {
        try
        {
            foreach (string name in root.GetSubKeyNames())
            {
                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using RegistryKey? desktop = root.OpenSubKey(name);
                    foreach (string app in desktop?.GetSubKeyNames() ?? [])
                    {
                        using RegistryKey? entry = desktop!.OpenSubKey(app);
                        if (InUse(entry))
                        {
                            // "C:#Program Files#App#app.exe"
                            uses.Add(new SensorUse(kind, MediaSourceNames.Friendly(app.Split('#')[^1])));
                        }
                    }
                }
                else
                {
                    using RegistryKey? entry = root.OpenSubKey(name);
                    if (InUse(entry))
                    {
                        uses.Add(new SensorUse(kind, MediaSourceNames.Friendly(name)));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn(nameof(SensorUsageMonitor), "Reading the privacy store failed", ex);
        }
    }

    private static bool InUse(RegistryKey? entry) =>
        entry?.GetValue("LastUsedTimeStart") is long start && start != 0 &&
        entry.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    private sealed class Watch : IDisposable
    {
        private readonly SensorUsageMonitor _owner;
        private readonly AutoResetEvent _signal = new(false);
        private readonly RegisteredWaitHandle _wait;
        private bool _disposed;

        public Watch(RegistryKey key, SensorKind kind, SensorUsageMonitor owner)
        {
            Key = key;
            Kind = kind;
            _owner = owner;
            _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => OnSignaled(), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        public RegistryKey Key { get; }

        public SensorKind Kind { get; }

        /// <summary>Notifications are one-shot; re-arm before reading so no change is missed.</summary>
        public void Arm()
        {
            if (_disposed)
            {
                return;
            }

            int result = RegNotifyChangeKeyValue(
                Key.Handle.DangerousGetHandle(),
                1,
                RegNotifyChangeName | RegNotifyChangeLastSet | RegNotifyThreadAgnostic,
                _signal.SafeWaitHandle.DangerousGetHandle(),
                1);
            if (result != 0)
            {
                AppLog.Warn(nameof(SensorUsageMonitor), $"RegNotifyChangeKeyValue failed ({result})");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _wait.Unregister(null);
            Key.Dispose();
            _signal.Dispose();
        }

        private void OnSignaled()
        {
            if (_disposed)
            {
                return;
            }

            Arm();
            _owner.Rescan();
        }
    }
}
