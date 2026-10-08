namespace WinIsland.Core.Devices;

/// <summary>Stand-ins used when a device API is unavailable (and in previews/tests).</summary>
public sealed class NullAudioEndpoint : IAudioEndpoint
{
    public static readonly NullAudioEndpoint Instance = new();

    public bool IsAvailable => false;

    public double Level => 0;

    public bool IsMuted => false;

    public event EventHandler<bool>? Changed
    {
        add { }
        remove { }
    }

    public Task SetLevelAsync(double level) => Task.CompletedTask;

    public Task SetMutedAsync(bool muted) => Task.CompletedTask;
}

public sealed class NullBrightnessControl : IBrightnessControl
{
    public static readonly NullBrightnessControl Instance = new();

    public bool IsSupported => false;

    public int Level => 0;

    public event EventHandler<bool>? Changed
    {
        add { }
        remove { }
    }

    public Task SetLevelAsync(int level) => Task.CompletedTask;
}

public sealed class NullPowerSource : IPowerSource
{
    public static readonly NullPowerSource Instance = new();

    public PowerStatus Current => PowerStatus.None;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

public sealed class NullPrivacySource : IPrivacySource
{
    public static readonly NullPrivacySource Instance = new();

    public IReadOnlyList<SensorUse> Current => [];

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
