using WinIsland.Core.Devices;

namespace WinIsland.App.Services.Preview;

/// <summary>Scriptable devices for <c>--preview</c>.</summary>
internal sealed class PreviewAudio(double level) : IAudioEndpoint
{
    public bool IsAvailable => true;

    public double Level { get; private set; } = level;

    public bool IsMuted { get; private set; }

    public event EventHandler<bool>? Changed;

    /// <summary>Simulates the volume keys.</summary>
    public void PressKeys(double level)
    {
        Level = level;
        Changed?.Invoke(this, true);
    }

    public Task SetLevelAsync(double level)
    {
        Level = level;
        Changed?.Invoke(this, false);
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool muted)
    {
        IsMuted = muted;
        Changed?.Invoke(this, false);
        return Task.CompletedTask;
    }
}

internal sealed class PreviewBrightness : IBrightnessControl
{
    public bool IsSupported => true;

    public int Level { get; private set; } = 60;

    public event EventHandler<bool>? Changed;

    public Task SetLevelAsync(int level)
    {
        Level = level;
        Changed?.Invoke(this, false);
        return Task.CompletedTask;
    }
}

internal sealed class PreviewPower : IPowerSource
{
    public PowerStatus Current => new(true, 76, false, false, false);

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

internal sealed class PreviewPrivacy : IPrivacySource
{
    public IReadOnlyList<SensorUse> Current { get; private set; } = [];

    public event EventHandler? Changed;

    public void Set(params SensorUse[] uses)
    {
        Current = uses;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
