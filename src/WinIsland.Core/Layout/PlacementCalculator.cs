using WinIsland.Core.Display;
using WinIsland.Core.Geometry;

namespace WinIsland.Core.Layout;

public sealed record PlacementOptions
{
    /// <summary>Gap between the top of the usable area and the island.</summary>
    public double TopMarginDip { get; init; } = 6;

    /// <summary>Minimum gap kept between the island and the monitor edges.</summary>
    public double EdgePaddingDip { get; init; } = 8;

    /// <summary>
    /// How far the island may be pushed down to dodge an overlay before we hide it instead.
    /// Beyond this it would sit in the middle of the user's content, which is worse than disappearing.
    /// </summary>
    public double MaxObstacleShiftDip { get; init; } = 120;
}

public enum PlacementAdjustment
{
    None,
    ClampedToMonitor,
    ShiftedForObstacle,
    HiddenForObstacle,
}

public readonly record struct PlacementResult(PixelRect Bounds, bool Visible, PlacementAdjustment Adjustment);

/// <summary>
/// Computes where the island goes: horizontally centred on the monitor, just below the top of
/// the work area (so a top-docked taskbar is respected), scaled for the monitor's DPI, never
/// extending past the monitor, and nudged away from (or hidden behind) important shell overlays.
/// </summary>
public static class PlacementCalculator
{
    private const int MaxObstaclePasses = 4;

    public static PlacementResult Place(
        MonitorDescriptor monitor,
        DipSize size,
        PlacementOptions options,
        IReadOnlyList<PixelRect>? obstacles = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(options);

        double scale = monitor.Scale > 0 ? monitor.Scale : 1.0;
        PixelRect area = monitor.WorkArea.IsEmpty ? monitor.Bounds : monitor.WorkArea;
        int padding = Px(options.EdgePaddingDip, scale);

        int maxWidth = Math.Max(1, area.Width - (2 * padding));
        int maxHeight = Math.Max(1, area.Height - (2 * padding));
        int width = Px(size.Width, scale);
        int height = Px(size.Height, scale);

        var adjustment = PlacementAdjustment.None;
        if (width > maxWidth || height > maxHeight)
        {
            width = Math.Min(width, maxWidth);
            height = Math.Min(height, maxHeight);
            adjustment = PlacementAdjustment.ClampedToMonitor;
        }

        // Centre on the physical screen (like a notch), not on the work area: a side-docked
        // taskbar should not drag the island off-centre unless it would actually overlap.
        int x = monitor.Bounds.X + ((monitor.Bounds.Width - width) / 2);
        int y = area.Y + Px(options.TopMarginDip, scale);
        PixelRect rect = new PixelRect(x, y, width, height).ClampInside(area);

        if (obstacles is { Count: > 0 })
        {
            int initialY = rect.Y;
            int maxShift = Px(options.MaxObstacleShiftDip, scale);
            for (int pass = 0; pass < MaxObstaclePasses; pass++)
            {
                int blockingBottom = int.MinValue;
                foreach (PixelRect obstacle in obstacles)
                {
                    if (obstacle.IntersectsWith(rect))
                    {
                        blockingBottom = Math.Max(blockingBottom, obstacle.Bottom);
                    }
                }

                if (blockingBottom == int.MinValue)
                {
                    break;
                }

                int newY = blockingBottom + padding;
                if (newY - initialY > maxShift || newY + rect.Height > area.Bottom || pass == MaxObstaclePasses - 1)
                {
                    return new PlacementResult(rect, Visible: false, PlacementAdjustment.HiddenForObstacle);
                }

                rect = rect.WithY(newY);
                adjustment = PlacementAdjustment.ShiftedForObstacle;
            }
        }

        return new PlacementResult(rect, Visible: true, adjustment);
    }

    private static int Px(double dip, double scale) => (int)Math.Ceiling(dip * scale);
}
