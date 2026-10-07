using WinIsland.Core.Display;
using WinIsland.Core.Geometry;
using WinIsland.Core.Layout;

namespace WinIsland.Core.Tests;

public class PlacementCalculatorTests
{
    private static readonly PlacementOptions Options = new() { TopMarginDip = 6, EdgePaddingDip = 8, MaxObstacleShiftDip = 120 };

    [Fact]
    public void Centers_horizontally_at_the_top_of_the_work_area()
    {
        PlacementResult result = PlacementCalculator.Place(Monitors.Primary1080p(), new DipSize(200, 36), Options);

        Assert.True(result.Visible);
        Assert.Equal(new PixelRect(860, 6, 200, 36), result.Bounds);
    }

    [Fact]
    public void Scales_size_and_margin_by_monitor_dpi()
    {
        PlacementResult result = PlacementCalculator.Place(Monitors.Primary1080p(scale: 1.5), new DipSize(200, 36), Options);

        Assert.Equal(300, result.Bounds.Width);
        Assert.Equal(54, result.Bounds.Height);
        Assert.Equal(9, result.Bounds.Y);
        Assert.Equal((1920 - 300) / 2, result.Bounds.X);
    }

    [Fact]
    public void Sits_below_a_top_docked_taskbar()
    {
        var monitor = new MonitorDescriptor("M", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 48, 1920, 1032), 1.0, true);

        PlacementResult result = PlacementCalculator.Place(monitor, new DipSize(200, 36), Options);

        Assert.Equal(48 + 6, result.Bounds.Y);
    }

    [Fact]
    public void Works_on_monitors_with_negative_coordinates()
    {
        MonitorDescriptor monitor = Monitors.SecondaryLeft4k();

        PlacementResult result = PlacementCalculator.Place(monitor, new DipSize(200, 36), Options);

        Assert.True(monitor.Bounds.Contains(result.Bounds));
        Assert.Equal(-3840 + ((3840 - 400) / 2), result.Bounds.X);
        Assert.Equal(-200 + 12, result.Bounds.Y);
    }

    [Fact]
    public void Never_extends_outside_a_small_monitor()
    {
        var tiny = new MonitorDescriptor("M", new PixelRect(100, 100, 300, 200), new PixelRect(100, 100, 300, 200), 1.0, true);

        PlacementResult result = PlacementCalculator.Place(tiny, IslandMetrics.Large, Options);

        Assert.Equal(PlacementAdjustment.ClampedToMonitor, result.Adjustment);
        Assert.True(tiny.Bounds.Contains(result.Bounds), result.Bounds.ToString());
    }

    [Fact]
    public void Shifts_down_to_avoid_an_overlay()
    {
        var overlay = new PixelRect(700, 0, 520, 60);

        PlacementResult result = PlacementCalculator.Place(Monitors.Primary1080p(), new DipSize(200, 36), Options, [overlay]);

        Assert.True(result.Visible);
        Assert.Equal(PlacementAdjustment.ShiftedForObstacle, result.Adjustment);
        Assert.False(result.Bounds.IntersectsWith(overlay));
        Assert.Equal(60 + 8, result.Bounds.Y);
    }

    [Fact]
    public void Hides_when_an_overlay_would_push_it_too_far()
    {
        var overlay = new PixelRect(0, 0, 1920, 600);

        PlacementResult result = PlacementCalculator.Place(Monitors.Primary1080p(), new DipSize(200, 36), Options, [overlay]);

        Assert.False(result.Visible);
        Assert.Equal(PlacementAdjustment.HiddenForObstacle, result.Adjustment);
    }

    [Fact]
    public void Ignores_overlays_that_do_not_intersect()
    {
        var toast = new PixelRect(1500, 900, 400, 120);

        PlacementResult result = PlacementCalculator.Place(Monitors.Primary1080p(), new DipSize(200, 36), Options, [toast]);

        Assert.Equal(PlacementAdjustment.None, result.Adjustment);
        Assert.Equal(6, result.Bounds.Y);
    }
}
