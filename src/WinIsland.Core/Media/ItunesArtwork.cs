using System.Text.Json;

namespace WinIsland.Core.Media;

/// <summary>
/// Helpers for Apple's public iTunes Search API, used to find cover art when the playing
/// app doesn't provide any (e.g. some browser tabs). No API key; only artist + title are sent.
/// </summary>
public static class ItunesArtwork
{
    public static Uri BuildSearchUri(string artist, string title) =>
        new($"https://itunes.apple.com/search?media=music&entity=song&limit=5&term={Uri.EscapeDataString($"{artist} {title}".Trim())}");

    /// <summary>Picks the best matching result's artwork URL, upgraded to <paramref name="size"/> px.</summary>
    public static Uri? ParseArtworkUrl(string json, string artist, string title, int size = 600)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        int bestScore = -1;
        foreach (JsonElement result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("artworkUrl100", out JsonElement art) || art.GetString() is not { Length: > 0 } url)
            {
                continue;
            }

            int score = Score(result, "artistName", artist) * 2 + Score(result, "trackName", title);
            if (score > bestScore)
            {
                bestScore = score;
                best = url;
            }
        }

        // Nothing resembling the song: better no cover than a wrong one.
        if (best is null || bestScore <= 0)
        {
            return null;
        }

        // Apple serves any square size by rewriting the "100x100bb" segment.
        return new Uri(best.Replace("100x100bb", $"{size}x{size}bb", StringComparison.Ordinal));
    }

    private static int Score(JsonElement result, string property, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || !result.TryGetProperty(property, out JsonElement value) || value.GetString() is not { } actual)
        {
            return 0;
        }

        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return actual.Contains(expected, StringComparison.OrdinalIgnoreCase) || expected.Contains(actual, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }
}
