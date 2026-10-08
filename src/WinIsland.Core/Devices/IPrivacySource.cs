namespace WinIsland.Core.Devices;

public enum SensorKind
{
    Microphone,
    Camera,
}

/// <summary>An app currently using the microphone or camera.</summary>
public sealed record SensorUse(SensorKind Kind, string AppName);

/// <summary>Which apps are using the microphone or camera right now (the Windows privacy indicator).</summary>
public interface IPrivacySource
{
    IReadOnlyList<SensorUse> Current { get; }

    /// <summary>Raised on any thread.</summary>
    event EventHandler? Changed;
}
