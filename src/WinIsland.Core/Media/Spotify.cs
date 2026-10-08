using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinIsland.Core.Media;

/// <summary>"Liked Songs" for the playing track (Spotify Web API on Windows).</summary>
public interface IMusicLibrary
{
    bool IsConnected { get; }

    /// <summary>Raised on any thread when the account is connected or disconnected.</summary>
    event EventHandler? ConnectionChanged;

    /// <summary>The library's id for the track, or null when it can't be found.</summary>
    Task<string?> FindTrackAsync(string artist, string title, CancellationToken cancellationToken);

    Task<bool> IsSavedAsync(string trackId, CancellationToken cancellationToken);

    Task SetSavedAsync(string trackId, bool saved, CancellationToken cancellationToken);
}

/// <summary>Spotify Web API helpers: PKCE sign-in (no client secret) and track matching.</summary>
public static class SpotifyApi
{
    public const int RedirectPort = 43821;
    public const string RedirectUri = "http://127.0.0.1:43821/callback";
    public const string Scopes = "user-library-read user-library-modify";

    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static Uri BuildAuthorizeUri(string clientId, string challenge, string state) =>
        new("https://accounts.spotify.com/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scopes)}" +
            "&code_challenge_method=S256" +
            $"&code_challenge={challenge}" +
            $"&state={Uri.EscapeDataString(state)}");

    public static Uri BuildSearchUri(string artist, string title) =>
        new($"https://api.spotify.com/v1/search?type=track&limit=5&q={Uri.EscapeDataString($"track:\"{title}\" artist:\"{artist}\"")}");

    /// <summary>Best match of a search result: exact title and an artist that matches, else null.</summary>
    public static string? ParseTrackId(string json, string artist, string title)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tracks", out JsonElement tracks) ||
            !tracks.TryGetProperty("items", out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            string name = item.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
            bool artistMatches = item.TryGetProperty("artists", out JsonElement artists) &&
                artists.ValueKind == JsonValueKind.Array &&
                artists.EnumerateArray().Any(a => a.TryGetProperty("name", out JsonElement an) &&
                    artist.Contains(an.GetString() ?? "\0", StringComparison.OrdinalIgnoreCase));
            if (artistMatches && Normalize(name) == Normalize(title) && item.TryGetProperty("id", out JsonElement id))
            {
                return id.GetString();
            }
        }

        return null;
    }

    /// <summary>"Song - Remastered 2011" and "Song (feat. X)" both match "Song".</summary>
    internal static string Normalize(string title)
    {
        string t = title;
        int cut = t.IndexOfAny(['(', '[']);
        if (cut > 0)
        {
            t = t[..cut];
        }

        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            t = t[..dash];
        }

        return t.Trim().ToLowerInvariant();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
