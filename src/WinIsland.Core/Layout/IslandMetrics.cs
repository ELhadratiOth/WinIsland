using WinIsland.Core.Geometry;

namespace WinIsland.Core.Layout;

/// <summary>Default sizes and timings. Sizes are in DIPs so they scale with display DPI.</summary>
public static class IslandMetrics
{
    public static readonly DipSize Compact = new(180, 36);
    public static readonly DipSize Expanded = new(380, 84);
    public static readonly DipSize Large = new(440, 300);

    /// <summary>Extra height for the module switcher strip shown while interacting.</summary>
    public const double SwitcherHeight = 28;

    /// <summary>Resize animation duration (spec: 150–250 ms).</summary>
    public static readonly TimeSpan ResizeDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>Content cross-fade and notification fade duration (spec: 150–200 ms).</summary>
    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>How long a transient event (track change, finished session…) keeps the island expanded.</summary>
    public static readonly TimeSpan DefaultAttentionDuration = TimeSpan.FromSeconds(4);

    public static DipSize DefaultSize(IslandSize size) => size switch
    {
        IslandSize.Compact => Compact,
        IslandSize.Expanded => Expanded,
        IslandSize.Large => Large,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null),
    };

    /// <summary>Corner radius in DIPs: a full pill when compact, a rounded card when larger.</summary>
    public static double CornerRadius(DipSize size, IslandSize kind) =>
        kind == IslandSize.Compact ? size.Height / 2 : Math.Min(28, size.Height / 2);
}
