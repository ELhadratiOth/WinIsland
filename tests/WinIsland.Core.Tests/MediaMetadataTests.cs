using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Media;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public class MediaMetadataTests
{
    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("MSEdge", "Microsoft Edge")]
    [InlineData("Chrome", "Google Chrome")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player")]
    [InlineData("foobar2000.exe", "foobar2000")]
    [InlineData("Contoso.Player_abc123!App", "App")]
    [InlineData("", "")]
    public void Friendly_source_names(string appId, string expected) =>
        Assert.Equal(expected, MediaSourceNames.Friendly(appId));

    [Fact]
    public void Itunes_picks_the_matching_result_and_upsizes_the_art()
    {
        const string json = """
            {"resultCount":2,"results":[
              {"artistName":"Some Cover Band","trackName":"Midnight City (Cover)","artworkUrl100":"https://is1.mzstatic.com/a/100x100bb.jpg"},
              {"artistName":"M83","trackName":"Midnight City","artworkUrl100":"https://is1.mzstatic.com/b/100x100bb.jpg"}
            ]}
            """;

        Uri? url = ItunesArtwork.ParseArtworkUrl(json, "M83", "Midnight City");

        Assert.Equal("https://is1.mzstatic.com/b/600x600bb.jpg", url?.ToString());
    }

    [Fact]
    public void Itunes_rejects_unrelated_results()
    {
        const string json = """{"results":[{"artistName":"Someone Else","trackName":"Other Song","artworkUrl100":"https://x/100x100bb.jpg"}]}""";

        Assert.Null(ItunesArtwork.ParseArtworkUrl(json, "M83", "Midnight City"));
        Assert.Null(ItunesArtwork.ParseArtworkUrl("""{"results":[]}""", "M83", "Midnight City"));
    }

    [Fact]
    public void Itunes_search_uri_escapes_the_query()
    {
        Uri uri = ItunesArtwork.BuildSearchUri("AC/DC", "Back In Black & Co");

        Assert.Equal("itunes.apple.com", uri.Host);
        Assert.Contains("term=AC%2FDC%20Back%20In%20Black%20%26%20Co", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_module_exposes_album_and_source()
    {
        var time = new FakeTimeProvider();
        var source = new FakeMediaSource();
        using var media = new MediaModule(source, time, new InlineDispatcher());

        source.Set(new MediaSnapshot("Song", "Artist", "Spotify.exe", true, true, true, TimeSpan.Zero, TimeSpan.FromMinutes(3), time.GetUtcNow(), Album: "Album"));

        Assert.Equal("Album", media.Album);
        Assert.True(media.HasAlbum);
        Assert.Equal("PLAYING ON SPOTIFY", media.SourceCaption);
    }
}
