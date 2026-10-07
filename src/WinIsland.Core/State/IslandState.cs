using WinIsland.Core.Geometry;
using WinIsland.Core.Interaction;
using WinIsland.Core.Layout;
using WinIsland.Core.Visibility;

namespace WinIsland.Core.State;

/// <summary>
/// Everything the UI needs to render the island. Immutable; a new instance is published only
/// when something actually changed, so the UI never re-renders for nothing.
/// </summary>
public sealed record IslandState(
    HiddenReason HiddenReason,
    string ModuleId,
    IslandSize Size,
    DipSize SizeDip,
    InteractionMode Mode,
    bool IsAttention,
    bool HasSwitcher = false)
{
    public bool IsVisible => HiddenReason == HiddenReason.None;
}
