using WinIsland.Core.Geometry;

namespace WinIsland.Core.Display;

/// <summary>
/// Snapshot of one display. <see cref="Scale"/> is the effective DPI divided by 96, so a
/// 150% display has a scale of 1.5. All rectangles are in physical pixels.
/// </summary>
public sealed record MonitorDescriptor(
    string Id,
    PixelRect Bounds,
    PixelRect WorkArea,
    double Scale,
    bool IsPrimary);
