using WinIsland.Core.Display;
using WinIsland.Core.Geometry;
using WinIsland.Core.Visibility;

namespace WinIsland.Core.Tests;

public class VisibilityTests
{
    private const string Island = "\\\\.\\DISPLAY1";

    private static ForegroundSnapshot Fg(ForegroundKind kind, bool game = false, string monitor = Island) =>
        new(kind, monitor, new PixelRect(0, 0, 1920, 1080), game);

    [Theory]
    [InlineData(VisibilityMode.AlwaysShow, ForegroundKind.Fullscreen, true, HiddenReason.None)]
    [InlineData(VisibilityMode.HideInGames, ForegroundKind.Fullscreen, true, HiddenReason.Game)]
    [InlineData(VisibilityMode.HideInGames, ForegroundKind.Fullscreen, false, HiddenReason.None)]
    [InlineData(VisibilityMode.HideInFullscreen, ForegroundKind.Fullscreen, false, HiddenReason.Fullscreen)]
    [InlineData(VisibilityMode.HideInFullscreen, ForegroundKind.Fullscreen, true, HiddenReason.Game)]
    [InlineData(VisibilityMode.HideInFullscreen, ForegroundKind.Maximized, false, HiddenReason.None)]
    [InlineData(VisibilityMode.HideWhenMaximized, ForegroundKind.Maximized, false, HiddenReason.Maximized)]
    [InlineData(VisibilityMode.HideWhenMaximized, ForegroundKind.Normal, false, HiddenReason.None)]
    [InlineData(VisibilityMode.HideWhenMaximized, ForegroundKind.Shell, false, HiddenReason.None)]
    public void Applies_the_user_policy(VisibilityMode mode, ForegroundKind kind, bool game, HiddenReason expected) =>
        Assert.Equal(expected, VisibilityEvaluator.Evaluate(mode, Fg(kind, game), Island));

    [Fact]
    public void Fullscreen_on_another_monitor_does_not_hide_the_island() =>
        Assert.Equal(HiddenReason.None, VisibilityEvaluator.Evaluate(VisibilityMode.HideInFullscreen, Fg(ForegroundKind.Fullscreen, game: true, monitor: "\\\\.\\DISPLAY2"), Island));

    [Fact]
    public void Snap_layouts_hides_temporarily() =>
        Assert.Equal(HiddenReason.SnapLayouts, VisibilityEvaluator.Evaluate(VisibilityMode.AlwaysShow, Fg(ForegroundKind.Normal), Island, snapLayoutsActive: true));

    [Fact]
    public void No_monitor_hides() =>
        Assert.Equal(HiddenReason.NoMonitor, VisibilityEvaluator.Evaluate(VisibilityMode.AlwaysShow, null, null));

    [Fact]
    public void Primary_preference_always_uses_primary()
    {
        MonitorDescriptor[] monitors = [Monitors.SecondaryLeft4k(), Monitors.Primary1080p()];

        MonitorDescriptor? selected = MonitorSelector.Select(monitors, MonitorPreference.Primary, Fg(ForegroundKind.Normal, monitor: "\\\\.\\DISPLAY2"), null);

        Assert.Equal(Island, selected?.Id);
    }

    [Fact]
    public void Follow_preference_moves_to_the_active_window_monitor()
    {
        MonitorDescriptor[] monitors = [Monitors.SecondaryLeft4k(), Monitors.Primary1080p()];

        MonitorDescriptor? selected = MonitorSelector.Select(monitors, MonitorPreference.FollowActiveWindow, Fg(ForegroundKind.Normal, monitor: "\\\\.\\DISPLAY2"), Island);

        Assert.Equal("\\\\.\\DISPLAY2", selected?.Id);
    }

    [Fact]
    public void Follow_preference_does_not_chase_fullscreen_windows()
    {
        MonitorDescriptor[] monitors = [Monitors.SecondaryLeft4k(), Monitors.Primary1080p()];

        MonitorDescriptor? selected = MonitorSelector.Select(monitors, MonitorPreference.FollowActiveWindow, Fg(ForegroundKind.Fullscreen, monitor: "\\\\.\\DISPLAY2"), Island);

        Assert.Equal(Island, selected?.Id);
    }

    [Fact]
    public void Falls_back_to_primary_when_the_current_monitor_is_disconnected()
    {
        MonitorDescriptor[] monitors = [Monitors.Primary1080p()];

        MonitorDescriptor? selected = MonitorSelector.Select(monitors, MonitorPreference.FollowActiveWindow, null, "\\\\.\\DISPLAY2");

        Assert.Equal(Island, selected?.Id);
    }
}
