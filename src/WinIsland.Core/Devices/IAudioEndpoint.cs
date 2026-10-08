namespace WinIsland.Core.Devices;

/// <summary>The default speakers or microphone (Core Audio on Windows).</summary>
public interface IAudioEndpoint
{
    /// <summary>False when no such device exists (no microphone plugged in…).</summary>
    bool IsAvailable { get; }

    /// <summary>0–1.</summary>
    double Level { get; }

    bool IsMuted { get; }

    /// <summary>
    /// Raised on any thread when level, mute or the default device changes. The argument is
    /// true when the change came from outside WinIsland (volume keys, another app).
    /// </summary>
    event EventHandler<bool>? Changed;

    Task SetLevelAsync(double level);

    Task SetMutedAsync(bool muted);
}
