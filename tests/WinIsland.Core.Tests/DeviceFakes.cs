using WinIsland.Core.Devices;

namespace WinIsland.Core.Tests;

internal sealed class FakeAudioEndpoint : IAudioEndpoint
{
    public bool IsAvailable { get; set; } = true;

    public double Level { get; set; } = 0.5;

    public bool IsMuted { get; set; }

    public List<double> LevelRequests { get; } = [];

    public event EventHandler<bool>? Changed;

    public void Raise(bool external) => Changed?.Invoke(this, external);

    public Task SetLevelAsync(double level)
    {
        LevelRequests.Add(level);
        Level = level;
        Raise(external: false);
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool muted)
    {
        IsMuted = muted;
        Raise(external: false);
        return Task.CompletedTask;
    }
}

internal sealed class FakeBrightness : IBrightnessControl
{
    public bool IsSupported { get; set; } = true;

    public int Level { get; set; } = 40;

    public event EventHandler<bool>? Changed;

    public void Raise(bool external) => Changed?.Invoke(this, external);

    public Task SetLevelAsync(int level)
    {
        Level = level;
        Raise(external: false);
        return Task.CompletedTask;
    }
}

internal sealed class FakePower : IPowerSource
{
    public PowerStatus Current { get; set; } = new(true, 80, false, false, false);

    public event EventHandler? Changed;

    public void Set(PowerStatus status)
    {
        Current = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakePrivacy : IPrivacySource
{
    public IReadOnlyList<SensorUse> Current { get; set; } = [];

    public event EventHandler? Changed;

    public void Set(params SensorUse[] uses)
    {
        Current = uses;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
