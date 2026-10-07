namespace WinIsland.Core.Interaction;

public sealed record InteractionOptions
{
    /// <summary>When false the island only becomes interactive through the hotkey or tray menu.</summary>
    public bool HoverToInteract { get; init; } = true;

    /// <summary>
    /// How long the pointer must rest on the island before it turns interactive. Prevents the island
    /// from grabbing clicks when the pointer merely passes over it on its way to a title bar.
    /// </summary>
    public TimeSpan HoverDwell { get; init; } = TimeSpan.FromMilliseconds(180);

    /// <summary>How long the pointer may leave the island before interaction ends.</summary>
    public TimeSpan LeaveGrace { get; init; } = TimeSpan.FromMilliseconds(400);
}
