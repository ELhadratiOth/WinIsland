namespace WinIsland.Core.Interaction;

public enum InteractionMode
{
    /// <summary>Click-through overlay: input goes to whatever is behind the island, focus is never taken.</summary>
    Passive,

    /// <summary>The user is deliberately using the island: it accepts mouse input (and keyboard input when a text field is focused).</summary>
    Interactive,
}
