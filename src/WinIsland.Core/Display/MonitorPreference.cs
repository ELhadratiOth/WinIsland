namespace WinIsland.Core.Display;

public enum MonitorPreference
{
    /// <summary>Always show the island on the primary display.</summary>
    Primary,

    /// <summary>Show the island on the display that contains the active (foreground) window.</summary>
    FollowActiveWindow,
}
