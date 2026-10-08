using System.Net.Http;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Media;

namespace WinIsland.Platform.Windows.Media;

/// <summary>
/// Finds cover art for songs whose player provides none, via Apple's public iTunes Search API.
/// Only artist and title are sent; failures (offline, no match) simply leave the placeholder.
/// </summary>
public sealed class OnlineArtworkLookup
{
    private const int MaxImageBytes = 2 * 1024 * 1024;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "WinIsland" } },
    };

    public async Task<MediaArtwork?> FindAsync(string artist, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            string json = await Http.GetStringAsync(ItunesArtwork.BuildSearchUri(artist, title), cancellationToken).ConfigureAwait(false);
            if (ItunesArtwork.ParseArtworkUrl(json, artist, title) is not { } imageUrl)
            {
                return null;
            }

            using HttpResponseMessage response = await Http.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }

            byte[] image = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return image.Length is > 0 and <= MaxImageBytes
                ? await ArtworkLoader.LoadAsync(image, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppLog.Info(nameof(OnlineArtworkLookup), $"No online artwork: {ex.GetType().Name}");
            return null;
        }
    }
}
