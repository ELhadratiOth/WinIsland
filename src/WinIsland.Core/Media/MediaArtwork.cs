namespace WinIsland.Core.Media;

/// <summary>Cover art, already downscaled for display, plus the accent colour derived from it (ARGB).</summary>
public sealed record MediaArtwork(byte[] Image, uint AccentArgb);
