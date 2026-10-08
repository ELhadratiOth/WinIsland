using System.Net;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Media;

namespace WinIsland.Platform.Windows.Media;

/// <summary>Time-synced lyrics from lrclib.net (free, no account). Sends only artist, title, album and duration.</summary>
public sealed class LrcLibClient(Func<bool> enabled) : ILyricsProvider
{
    private static readonly HttpClient Http = CreateClient();

    public async Task<Lyrics?> GetAsync(string artist, string title, string? album, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!enabled() || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        using (HttpResponseMessage exact = await Http.GetAsync(LrcLib.BuildGetUri(artist, title, album, duration), cancellationToken).ConfigureAwait(false))
        {
            if (exact.IsSuccessStatusCode)
            {
                Lyrics? lyrics = LrcLib.ParseRecord(await exact.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (lyrics is not null)
                {
                    return lyrics;
                }
            }
            else if (exact.StatusCode != HttpStatusCode.NotFound)
            {
                AppLog.Info(nameof(LrcLibClient), $"Lookup returned {(int)exact.StatusCode}");
                return null;
            }
        }

        // No exact match (album or duration differ): fall back to a search.
        using HttpResponseMessage search = await Http.GetAsync(LrcLib.BuildSearchUri(artist, title), cancellationToken).ConfigureAwait(false);
        return search.IsSuccessStatusCode
            ? LrcLib.ParseSearch(await search.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
            : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // LRCLIB asks clients to identify themselves.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland/0.2 (https://github.com/ELhadratiOth/WinIsland)");
        return client;
    }
}
