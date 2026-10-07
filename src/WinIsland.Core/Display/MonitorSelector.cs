using WinIsland.Core.Visibility;

namespace WinIsland.Core.Display;

/// <summary>Chooses which display hosts the island. Pure function so it can be re-run on every display change.</summary>
public static class MonitorSelector
{
    public static MonitorDescriptor? Select(
        IReadOnlyList<MonitorDescriptor> monitors,
        MonitorPreference preference,
        ForegroundSnapshot? foreground,
        string? currentMonitorId)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            return null;
        }

        MonitorDescriptor primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        if (preference == MonitorPreference.Primary)
        {
            return primary;
        }

        // Following a fullscreen window would just move the island somewhere it then has to hide,
        // so we only follow ordinary windows and otherwise stay where we are.
        if (foreground is { MonitorId: { } fgMonitor } &&
            foreground.Kind is ForegroundKind.Normal or ForegroundKind.Maximized &&
            !foreground.IsGame &&
            Find(monitors, fgMonitor) is { } followed)
        {
            return followed;
        }

        // The previous display may have been disconnected.
        return Find(monitors, currentMonitorId) ?? primary;
    }

    private static MonitorDescriptor? Find(IReadOnlyList<MonitorDescriptor> monitors, string? id) =>
        id is null ? null : monitors.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
