using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WinIsland.Platform.Windows.Audio;

// Core Audio (MMDevice API) through source-generated COM interop. Only the vtable slots up to
// the last method we call are declared; their order must match the Windows SDK headers.

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

    [PreserveSig]
    int GetDevice(nint id, out IMMDevice? device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid iid, uint clsContext, nint activationParams, out nint instance);
}

[GeneratedComInterface]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal unsafe partial interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    [PreserveSig]
    int GetChannelCount(out uint count);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, Guid* eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, Guid* eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, Guid* eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, Guid* eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute(int mute, Guid* eventContext);

    [PreserveSig]
    int GetMute(out int mute);
}

[GeneratedComInterface]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
internal unsafe partial interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(AudioVolumeNotificationData* data);
}

[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged(nint deviceId, uint newState);

    [PreserveSig]
    int OnDeviceAdded(nint deviceId);

    [PreserveSig]
    int OnDeviceRemoved(nint deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged(nint deviceId, PropertyKey key);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioVolumeNotificationData
{
    public Guid EventContext;
    public int Muted;
    public float MasterVolume;
    public uint Channels;

    // Followed by Channels floats.
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

internal static partial class CoreAudioNative
{
    public const int ERender = 0;
    public const int ECapture = 1;
    public const int EConsole = 0;
    public const uint ClsctxAll = 0x17;

    public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint clsContext, in Guid iid, out nint instance);

    /// <summary>Wraps a raw interface pointer (and releases the caller's reference to it).</summary>
    public static unsafe T Wrap<T>(nint pointer)
    {
        try
        {
            return ComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer)
                ?? throw new InvalidOperationException($"{typeof(T).Name} is unavailable");
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}
