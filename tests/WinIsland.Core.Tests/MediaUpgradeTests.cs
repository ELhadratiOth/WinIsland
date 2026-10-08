using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Layout;
using WinIsland.Core.Media;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public class MediaUpgradeTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
    private readonly InlineDispatcher _dispatcher = new();
    private readonly FakeMediaSource _source = new();

    private MediaSnapshot Song(string title = "Midnight City", TimeSpan? position = null) =>
        new(title, "M83", "Spotify.exe", true, true, true, position ?? TimeSpan.Zero, TimeSpan.FromSeconds(243), _time.GetUtcNow(), CanSeek: true);

    [Fact]
    public void Lrc_lines_are_parsed_and_sorted_including_repeated_stamps()
    {
        IReadOnlyList<LyricLine> lines = LrcParser.Parse("[ar:M83]\n[00:12.50]Waiting in a car\n[00:05.00][01:00]Chorus\nno stamp");

        Assert.Equal(3, lines.Count);
        Assert.Equal(new LyricLine(TimeSpan.FromSeconds(5), "Chorus"), lines[0]);
        Assert.Equal(new LyricLine(TimeSpan.FromSeconds(12.5), "Waiting in a car"), lines[1]);
        Assert.Equal(TimeSpan.FromMinutes(1), lines[2].Time);
    }

    [Fact]
    public void LrcLib_prefers_synced_lyrics()
    {
        Lyrics? lyrics = LrcLib.ParseSearch("""
            [{"plainLyrics":"just text","syncedLyrics":null},{"plainLyrics":"a","syncedLyrics":"[00:01.00]a"}]
            """);

        Assert.NotNull(lyrics);
        Assert.True(lyrics.IsSynced);
        Assert.Null(LrcLib.ParseRecord("""{"instrumental":true,"plainLyrics":null,"syncedLyrics":null}"""));
        Assert.Contains("duration=243", LrcLib.BuildGetUri("M83", "Midnight City", null, TimeSpan.FromSeconds(243.4)).Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Spotify_matches_title_ignoring_remaster_suffixes()
    {
        string json = """
            {"tracks":{"items":[
              {"id":"wrong","name":"Midnight City","artists":[{"name":"Someone Else"}]},
              {"id":"right","name":"Midnight City - Remastered 2011","artists":[{"name":"M83"}]}
            ]}}
            """;

        Assert.Equal("right", SpotifyApi.ParseTrackId(json, "M83", "Midnight City"));
        Assert.Null(SpotifyApi.ParseTrackId(json, "M83", "Outro"));
        Assert.Equal(43, SpotifyApi.Challenge(SpotifyApi.CreateVerifier()).Length);
    }

    [Fact]
    public async Task Lyrics_follow_the_song_and_open_the_large_view()
    {
        var provider = new FakeLyrics(new Lyrics(
            [new(TimeSpan.FromSeconds(2), "one"), new(TimeSpan.FromSeconds(5), "two"), new(TimeSpan.FromSeconds(9), "three")],
            IsSynced: true));
        using var media = new MediaModule(_source, _time, _dispatcher, provider);
        _source.Set(Song());
        await Eventually.TrueAsync(() => media.HasLyrics, "lyrics load");

        media.ShowLyrics = true;
        media.IsViewActive = true;
        Assert.Equal(IslandSize.Large, media.InteractiveSize);
        Assert.Equal("♪", media.LyricCurrent);
        Assert.Equal("one", media.LyricNext);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("one", media.LyricCurrent);
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("two", media.LyricCurrent);
        Assert.Equal("one", media.LyricPrevious);
        Assert.Equal("three", media.LyricNext);
        Assert.Equal(1, provider.Calls);

        // Same song again (a timeline event) doesn't re-fetch; a new song does.
        _source.Set(Song(position: TimeSpan.FromSeconds(5)));
        _source.Set(Song("Outro"));
        await Eventually.TrueAsync(() => provider.Calls == 2, "second lookup");
    }

    [Fact]
    public void Scrubbing_freezes_the_bar_and_seeks_on_release()
    {
        using var media = new MediaModule(_source, _time, _dispatcher);
        _source.Set(Song());
        media.IsViewActive = true;

        media.BeginScrub();
        media.Scrub(50);
        Assert.Equal("2:01", media.PositionText);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("2:01", media.PositionText);

        media.EndScrub(50);
        Assert.Equal([TimeSpan.FromSeconds(121.5)], _source.SeekRequests);
    }

    [Fact]
    public void Lists_players_and_selects_one()
    {
        using var media = new MediaModule(_source, _time, _dispatcher);
        _source.SetSessions(new MediaSessionInfo("Spotify.exe", "Spotify", true, true), new MediaSessionInfo("chrome", "Google Chrome", false, false));

        Assert.True(media.HasMultipleSessions);
        media.SelectSessionCommand.Execute("chrome");
        Assert.Equal(["chrome"], _source.SelectedSessions);
    }

    [Fact]
    public async Task Like_reflects_the_library_and_toggles()
    {
        var library = new FakeLibrary();
        using var media = new MediaModule(_source, _time, _dispatcher, library: library);
        _source.Set(Song());
        await Eventually.TrueAsync(() => media.CanLike, "track found");

        Assert.True(media.IsLiked);
        Assert.Equal("", media.LikeGlyph);
        media.ToggleLikeCommand.Execute(null);
        await Eventually.TrueAsync(() => !library.Saved, "unliked");
        Assert.False(media.IsLiked);
    }

    private sealed class FakeLyrics(Lyrics lyrics) : ILyricsProvider
    {
        public int Calls { get; private set; }

        public Task<Lyrics?> GetAsync(string artist, string title, string? album, TimeSpan duration, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<Lyrics?>(lyrics);
        }
    }

    private sealed class FakeLibrary : IMusicLibrary
    {
        public bool Saved { get; private set; } = true;

        public bool IsConnected => true;

        public event EventHandler? ConnectionChanged
        {
            add { }
            remove { }
        }

        public Task<string?> FindTrackAsync(string artist, string title, CancellationToken cancellationToken) => Task.FromResult<string?>("id1");

        public Task<bool> IsSavedAsync(string trackId, CancellationToken cancellationToken) => Task.FromResult(Saved);

        public Task SetSavedAsync(string trackId, bool saved, CancellationToken cancellationToken)
        {
            Saved = saved;
            return Task.CompletedTask;
        }
    }
}
