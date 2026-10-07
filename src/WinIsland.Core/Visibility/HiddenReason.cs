namespace WinIsland.Core.Visibility;

public enum HiddenReason
{
    None,
    Fullscreen,
    Game,
    Maximized,

    /// <summary>A window is being dragged near the top of the screen, where Snap Layouts appears.</summary>
    SnapLayouts,

    /// <summary>A shell overlay occupies the island's area and it could not be moved out of the way.</summary>
    Obstacle,

    NoMonitor,

    /// <summary>The user hid the island from the tray menu.</summary>
    User,
}
