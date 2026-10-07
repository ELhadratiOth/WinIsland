namespace WinIsland.Core.Layout;

/// <summary>The presentation sizes of the island, from least to most intrusive.</summary>
public enum IslandSize
{
    /// <summary>Small pill: a glyph and a short label (e.g. the clock).</summary>
    Compact,

    /// <summary>Medium card: a module's primary content and quick controls (e.g. media).</summary>
    Expanded,

    /// <summary>Interaction panel for modules that need lists or text input (e.g. Claude Code).</summary>
    Large,
}
