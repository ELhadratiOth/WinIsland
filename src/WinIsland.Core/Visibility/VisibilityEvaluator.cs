namespace WinIsland.Core.Visibility;

/// <summary>Decides whether the island may be shown given the foreground window and the user's policy.</summary>
public static class VisibilityEvaluator
{
    public static HiddenReason Evaluate(
        VisibilityMode mode,
        ForegroundSnapshot? foreground,
        string? islandMonitorId,
        bool snapLayoutsActive = false)
    {
        if (islandMonitorId is null)
        {
            return HiddenReason.NoMonitor;
        }

        if (snapLayoutsActive)
        {
            return HiddenReason.SnapLayouts;
        }

        if (foreground is null || foreground.Kind is ForegroundKind.None or ForegroundKind.Shell)
        {
            return HiddenReason.None;
        }

        // A fullscreen video on a second display must not hide the island on this one.
        if (!string.Equals(foreground.MonitorId, islandMonitorId, StringComparison.OrdinalIgnoreCase))
        {
            return HiddenReason.None;
        }

        if (foreground.IsGame && mode >= VisibilityMode.HideInGames)
        {
            return HiddenReason.Game;
        }

        if (foreground.Kind == ForegroundKind.Fullscreen && mode >= VisibilityMode.HideInFullscreen)
        {
            return HiddenReason.Fullscreen;
        }

        if (foreground.Kind == ForegroundKind.Maximized && mode >= VisibilityMode.HideWhenMaximized)
        {
            return HiddenReason.Maximized;
        }

        return HiddenReason.None;
    }
}
