using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using WinIsland.Core.Devices;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;

namespace WinIsland.Platform.Windows.Audio;

/// <summary>
/// Volume and mute of the default speakers or microphone, via Core Audio. Purely
/// event-driven: endpoint volume callbacks plus default-device notifications. Changes made by
/// WinIsland carry an event context, so they aren't reported as external (no OSD for our own slider).
/// </summary>
public sealed partial class CoreAudioEndpoint : IAudioEndpoint, IIntegration
{
    private static readonly Guid OwnContext = new("6A1D2C83-5F0B-4E1F-9D5C-57494E49534C");

    private readonly int _flow;
    private readonly Lock _gate = new();
    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceCallback _deviceCallback;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private volatile bool _available;
    private double _level;
    private volatile bool _muted;

    private CoreAudioEndpoint(int flow, string name)
    {
        _flow = flow;
        Name = name;
        _volumeCallback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
    }

    public event EventHandler<bool>? Changed;

    public static CoreAudioEndpoint Speakers() => new(CoreAudioNative.ERender, "Speakers");

    public static CoreAudioEndpoint Microphone() => new(CoreAudioNative.ECapture, "Microphone");

    public string Name { get; }

    public bool RequiresNetwork => false;

    public bool IsAvailable => _available;

    public double Level => Volatile.Read(ref _level);

    public bool IsMuted => _muted;

    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        Guid iid = typeof(IMMDeviceEnumerator).GUID;
        Marshal.ThrowExceptionForHR(CoreAudioNative.CoCreateInstance(CoreAudioNative.MMDeviceEnumeratorClsid, 0, CoreAudioNative.ClsctxAll, iid, out nint pointer));
        IMMDeviceEnumerator enumerator = CoreAudioNative.Wrap<IMMDeviceEnumerator>(pointer);
        lock (_gate)
        {
            _enumerator = enumerator;
        }

        Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(_deviceCallback));
        AttachDefault();
    }, cancellationToken);

    public Task StopAsync()
    {
        lock (_gate)
        {
            DetachVolume();
            if (_enumerator is not null)
            {
                _ = _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
                _enumerator = null;
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public Task SetLevelAsync(double level) => Task.Run(() => WithVolume(v =>
    {
        unsafe
        {
            Guid context = OwnContext;
            return v.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), &context);
        }
    }));

    public Task SetMutedAsync(bool muted) => Task.Run(() => WithVolume(v =>
    {
        unsafe
        {
            Guid context = OwnContext;
            return v.SetMute(muted ? 1 : 0, &context);
        }
    }));

    private void WithVolume(Func<IAudioEndpointVolume, int> action)
    {
        IAudioEndpointVolume? volume;
        lock (_gate)
        {
            volume = _volume;
        }

        if (volume is not null)
        {
            Marshal.ThrowExceptionForHR(action(volume));
        }
    }

    private void AttachDefault()
    {
        lock (_gate)
        {
            DetachVolume();
            if (_enumerator is null)
            {
                return;
            }

            // E_NOTFOUND when there's no such device (no microphone): simply unavailable.
            if (_enumerator.GetDefaultAudioEndpoint(_flow, CoreAudioNative.EConsole, out IMMDevice? device) < 0 || device is null)
            {
                _available = false;
            }
            else
            {
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(iid, CoreAudioNative.ClsctxAll, 0, out nint pointer));
                IAudioEndpointVolume volume = CoreAudioNative.Wrap<IAudioEndpointVolume>(pointer);
                Marshal.ThrowExceptionForHR(volume.RegisterControlChangeNotify(_volumeCallback));
                _volume = volume;
                _ = volume.GetMasterVolumeLevelScalar(out float level);
                _ = volume.GetMute(out int muted);
                Volatile.Write(ref _level, level);
                _muted = muted != 0;
                _available = true;
            }
        }

        Changed?.Invoke(this, false);
    }

    // Caller holds _gate.
    private void DetachVolume()
    {
        if (_volume is not null)
        {
            _ = _volume.UnregisterControlChangeNotify(_volumeCallback);
            _volume = null;
        }

        _available = false;
    }

    private void OnVolumeNotification(Guid context, bool muted, float level)
    {
        Volatile.Write(ref _level, level);
        _muted = muted;
        Changed?.Invoke(this, context != OwnContext);
    }

    private void OnDefaultDeviceChanged(int flow, int role)
    {
        if (flow != _flow || role != CoreAudioNative.EConsole)
        {
            return;
        }

        // Never re-enter Core Audio from inside its own callback.
        _ = Task.Run(() =>
        {
            try
            {
                AttachDefault();
            }
            catch (Exception ex)
            {
                AppLog.Warn(nameof(CoreAudioEndpoint), $"{Name}: switching device failed", ex);
            }
        });
    }

    [GeneratedComClass]
    internal sealed unsafe partial class VolumeCallback(CoreAudioEndpoint owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(AudioVolumeNotificationData* data)
        {
            if (data is not null)
            {
                owner.OnVolumeNotification(data->EventContext, data->Muted != 0, data->MasterVolume);
            }

            return 0;
        }
    }

    [GeneratedComClass]
    internal sealed partial class DeviceCallback(CoreAudioEndpoint owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(nint deviceId, uint newState) => 0;

        public int OnDeviceAdded(nint deviceId) => 0;

        public int OnDeviceRemoved(nint deviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId)
        {
            owner.OnDefaultDeviceChanged(flow, role);
            return 0;
        }

        public int OnPropertyValueChanged(nint deviceId, PropertyKey key) => 0;
    }
}
