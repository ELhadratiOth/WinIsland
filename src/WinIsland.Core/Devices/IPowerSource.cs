namespace WinIsland.Core.Devices;

public sealed record PowerStatus(bool HasBattery, int Percent, bool IsPluggedIn, bool IsCharging, bool EnergySaver)
{
    public static readonly PowerStatus None = new(false, 100, true, false, false);
}

public interface IPowerSource
{
    PowerStatus Current { get; }

    /// <summary>Raised on any thread.</summary>
    event EventHandler? Changed;
}
