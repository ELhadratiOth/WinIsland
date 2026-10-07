using WinIsland.Core.Geometry;

namespace WinIsland.Core.Visibility;

public enum ForegroundKind
{
    /// <summary>No meaningful foreground window (e.g. during a desktop switch).</summary>
    None,

    /// <summary>The desktop, taskbar, Start, notification centre or another shell surface.</summary>
    Shell,

    Normal,
    Maximized,

    /// <summary>Covers its entire monitor (borderless fullscreen or exclusive fullscreen).</summary>
    Fullscreen,
}

/// <summary>Classification of the current foreground window, produced by the platform layer.</summary>
public sealed record ForegroundSnapshot(
    ForegroundKind Kind,
    string? MonitorId,
    PixelRect Bounds,
    bool IsGame = false,
    string? ProcessName = null,
    bool IsShellOverlay = false)
{
    public static readonly ForegroundSnapshot Empty = new(ForegroundKind.None, null, default);
}
