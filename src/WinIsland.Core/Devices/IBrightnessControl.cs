namespace WinIsland.Core.Devices;

/// <summary>Built-in display brightness (laptops/tablets; external monitors are not supported).</summary>
public interface IBrightnessControl
{
    bool IsSupported { get; }

    /// <summary>0–100.</summary>
    int Level { get; }

    /// <summary>Raised on any thread; true when the change came from outside WinIsland.</summary>
    event EventHandler<bool>? Changed;

    Task SetLevelAsync(int level);
}
