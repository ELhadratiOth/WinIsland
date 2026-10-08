using WinIsland.Core.Media;

namespace WinIsland.App.Services.Preview;

/// <summary>Placeholder synced lyrics for the preview tour (no network, no copyrighted text).</summary>
internal sealed class PreviewLyrics : ILyricsProvider
{
    public Task<Lyrics?> GetAsync(string artist, string title, string? album, TimeSpan duration, CancellationToken cancellationToken)
    {
        string[] lines =
        [
            "City lights are waking up",
            "Neon rivers on the glass",
            "We drive until the morning comes",
            "Every signal turning green",
            "Hold the moment, let it last",
            "Midnight never felt so close",
        ];
        var synced = lines.Select((text, i) => new LyricLine(TimeSpan.FromSeconds(80 + (i * 4)), text)).ToList();
        return Task.FromResult<Lyrics?>(new Lyrics(synced, IsSynced: true));
    }
}
