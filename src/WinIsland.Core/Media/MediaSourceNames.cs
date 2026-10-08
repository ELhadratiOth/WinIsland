namespace WinIsland.Core.Media;

/// <summary>Turns a media session's app id ("Spotify.exe", "SpotifyAB.SpotifyMusic_…!Spotify", "MSEdge"…) into a display name.</summary>
public static class MediaSourceNames
{
    private static readonly (string Fragment, string Name)[] Known =
    [
        ("spotify", "Spotify"),
        ("msedge", "Microsoft Edge"),
        ("chrome", "Google Chrome"),
        ("firefox", "Firefox"),
        ("opera", "Opera"),
        ("brave", "Brave"),
        ("zunemusic", "Media Player"),
        ("microsoft.media.player", "Media Player"),
        ("applemusic", "Apple Music"),
        ("itunes", "iTunes"),
        ("vlc", "VLC"),
        ("deezer", "Deezer"),
        ("tidal", "TIDAL"),
        ("amazonmusic", "Amazon Music"),
        ("youtube", "YouTube Music"),
    ];

    public static string Friendly(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return string.Empty;
        }

        string lower = appId.ToLowerInvariant();
        foreach ((string fragment, string name) in Known)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
            {
                return name;
            }
        }

        // "Something.exe" or "Publisher.App_hash!App": keep the most readable part.
        string name2 = appId.Split('!')[^1];
        name2 = Path.GetFileNameWithoutExtension(name2.Split('_')[0]);
        int dot = name2.LastIndexOf('.');
        return dot >= 0 && dot < name2.Length - 1 ? name2[(dot + 1)..] : name2;
    }
}
