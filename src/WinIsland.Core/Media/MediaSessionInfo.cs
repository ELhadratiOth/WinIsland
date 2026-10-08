namespace WinIsland.Core.Media;

/// <summary>An app exposing media controls.</summary>
/// <param name="Id">Stable id (the app's AUMID / executable name).</param>
/// <param name="Name">Friendly name, e.g. "Spotify".</param>
/// <param name="IsSelected">True for the session the island currently follows.</param>
public sealed record MediaSessionInfo(string Id, string Name, bool IsPlaying, bool IsSelected);
