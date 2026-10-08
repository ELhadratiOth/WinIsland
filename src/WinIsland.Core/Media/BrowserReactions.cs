using System.Text.Json;

namespace WinIsland.Core.Media;

public enum BrowserReaction
{
    Like,
    Dislike,
}

/// <summary>What the WinIsland browser extension reports about the YouTube / YouTube Music page that is playing.</summary>
public sealed record BrowserReactionState(string Site, string Title, string Channel, bool Liked, bool Disliked, bool Playing, DateTimeOffset At);

/// <summary>
/// Like / dislike for video sites playing in a browser. Browsers expose no such control to other
/// apps, so a small extension on the YouTube page reports the state and performs the click.
/// </summary>
public interface IBrowserReactions
{
    /// <summary>The latest report, or null when the extension is absent or has gone quiet.</summary>
    BrowserReactionState? Current { get; }

    /// <summary>True while the extension is polling the island.</summary>
    bool IsExtensionConnected { get; }

    /// <summary>Raised on any thread when <see cref="Current"/> or the connection changes.</summary>
    event EventHandler? Changed;

    /// <summary>Asks the page to press like / dislike; false when the extension isn't there to do it.</summary>
    Task<bool> SendAsync(BrowserReaction reaction, CancellationToken cancellationToken);

    /// <summary>Without the extension: does an open browser window show this title on YouTube?</summary>
    Task<bool> LooksLikeYouTubeAsync(string title, CancellationToken cancellationToken);
}

public static class BrowserReactionProtocol
{
    /// <summary>Parses the extension's report ({site,title,channel,liked,disliked,playing}); null if malformed.</summary>
    public static BrowserReactionState? ParseState(string json, DateTimeOffset now)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string Text(string name) => root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            bool Flag(string name) => root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
            string site = Text("site");
            return site is "youtube" or "ytmusic"
                ? new BrowserReactionState(site, Text("title"), Text("channel"), Flag("liked"), Flag("disliked"), Flag("playing"), now)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Titles from different sources differ in case, decoration and whitespace.</summary>
    public static bool TitlesMatch(string? a, string? b)
    {
        string x = Normalize(a);
        string y = Normalize(b);
        return x.Length > 0 && y.Length > 0 && (x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal));
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Math.Min(text.Length, 256)];
        int n = 0;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) && n < buffer.Length)
            {
                buffer[n++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..n]);
    }
}
