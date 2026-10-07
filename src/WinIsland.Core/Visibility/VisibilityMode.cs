namespace WinIsland.Core.Visibility;

/// <summary>
/// User-selectable visibility policy. Each value hides in a superset of the situations of the
/// previous one: <c>AlwaysShow</c> ⊂ <c>HideInGames</c> ⊂ <c>HideInFullscreen</c> ⊂ <c>HideWhenMaximized</c>.
/// </summary>
public enum VisibilityMode
{
    /// <summary>Show everywhere, including over games. Must be chosen explicitly by the user.</summary>
    AlwaysShow,

    /// <summary>Hide only when a game is in the foreground.</summary>
    HideInGames,

    /// <summary>Hide whenever any fullscreen application (games, video, slideshows…) is in the foreground. Default.</summary>
    HideInFullscreen,

    /// <summary>Also hide whenever a maximized application is in the foreground.</summary>
    HideWhenMaximized,
}
