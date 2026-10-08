using WinIsland.Core.Geometry;

namespace WinIsland.Core.Layout;

/// <summary>Default sizes and timings. Sizes are in DIPs so they scale with display DPI.</summary>
public static class IslandMetrics
{
    public static readonly DipSize Compact = new(172, 36);
    public static readonly DipSize Expanded = new(380, 84);
    public static readonly DipSize Large = new(460, 300);

    /// <summary>Compact width when the pill carries leading and trailing content (art + waveform…).</summary>
    public static readonly DipSize CompactWide = new(236, 36);

    /// <summary>Extra height for the module switcher strip shown while interacting.</summary>
    public const double SwitcherHeight = 28;

    /// <summary>Width of one switcher chip including its spacing (30 + 6).</summary>
    public const double SwitcherChipWidth = 36;

    /// <summary>Narrowest island that fits a switcher with <paramref name="chips"/> chips and side margins.</summary>
    public static double SwitcherMinWidth(int chips) => (chips * SwitcherChipWidth) + 24;

    /// <summary>Extra room (DIPs) around the shape during a transition so a spring overshoot is never clipped.</summary>
    public const double OvershootMargin = 14;

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
        kind == IslandSize.Compact ? size.Height / 2 : Math.Min(32, size.Height / 2);
}
