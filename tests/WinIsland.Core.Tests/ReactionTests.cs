using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Media;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public sealed class ReactionTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
    private readonly InlineDispatcher _dispatcher = new();
    private readonly FakeMediaSource _source = new();

    private MediaSnapshot Playing(string app, string title = "Midnight City") =>
        new(title, "M83", app, true, true, true, TimeSpan.Zero, TimeSpan.FromSeconds(240), _time.GetUtcNow());

    [Fact]
    public void Heart_shows_for_spotify_even_before_it_is_connected_and_asks_to_connect()
    {
        var media = new MediaModule(_source, _time, _dispatcher);
        int asked = 0;
        media.SpotifyConnectRequested = () => asked++;

        _source.Set(Playing("Spotify.exe"));

        Assert.True(media.ShowHeart);
        Assert.False(media.ShowThumbs);
        Assert.Equal("Connect Spotify to like songs", media.LikeToolTip);
        media.ToggleLikeCommand.Execute(null);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void Heart_and_thumbs_are_hidden_for_other_players()
    {
        var media = new MediaModule(_source, _time, _dispatcher);

        _source.Set(Playing("VLC.exe"));

        Assert.False(media.ShowHeart);
        Assert.False(media.ShowThumbs);
    }

    [Fact]
    public void Thumbs_show_for_a_browser_once_the_extension_reports_that_video()
    {
        var reactions = new FakeReactions();
        var media = new MediaModule(_source, _time, _dispatcher, reactions: reactions);

        _source.Set(Playing("Chrome", "Lofi hip hop radio"));
        Assert.False(media.ShowThumbs);

        reactions.Report("Lofi hip hop radio", liked: true);

        Assert.True(media.ShowThumbs);
        Assert.False(media.ShowHeart);
        Assert.True(media.IsThumbUp);
        Assert.False(media.IsThumbDown);
    }

    [Fact]
    public void A_report_about_another_video_does_not_count()
    {
        var reactions = new FakeReactions();
        var media = new MediaModule(_source, _time, _dispatcher, reactions: reactions);
        _source.Set(Playing("Chrome", "Lofi hip hop radio"));

        reactions.Report("Some other video", liked: true);

        Assert.False(media.ShowThumbs);
        Assert.False(media.IsThumbUp);
    }

    [Fact]
    public void Thumbs_show_when_a_browser_window_looks_like_youtube()
    {
        var reactions = new FakeReactions { LooksLikeYouTube = true };
        var media = new MediaModule(_source, _time, _dispatcher, reactions: reactions);

        _source.Set(Playing("MSEdge", "Lofi hip hop radio"));

        Assert.True(media.ShowThumbs);
    }

    [Fact]
    public async Task Pressing_a_thumb_sends_it_and_explains_when_the_extension_is_missing()
    {
        var reactions = new FakeReactions { Connected = false, LooksLikeYouTube = true };
        var media = new MediaModule(_source, _time, _dispatcher, reactions: reactions);
        _source.Set(Playing("Chrome", "Lofi hip hop radio"));

        media.ThumbDownCommand.Execute(null);
        await Task.Yield();

        Assert.Equal([BrowserReaction.Dislike], reactions.Sent);
        Assert.Contains("extension", media.ThumbDownToolTip, StringComparison.Ordinal);

        reactions.Connected = true;
        media.ThumbUpCommand.Execute(null);
        await Task.Yield();

        Assert.Equal("Like", media.ThumbUpToolTip);
    }

    [Theory]
    [InlineData("Lofi hip hop radio - beats to relax/study to", "Lofi hip hop radio beats to relax/study to - YouTube - Google Chrome", true)]
    [InlineData("Midnight City", "midnight  city", true)]
    [InlineData("Midnight City", "Outro", false)]
    [InlineData("", "anything", false)]
    public void Titles_match_loosely(string a, string b, bool expected) =>
        Assert.Equal(expected, BrowserReactionProtocol.TitlesMatch(a, b));

    [Fact]
    public void Extension_reports_are_parsed_and_foreign_sites_are_ignored()
    {
        BrowserReactionState? state = BrowserReactionProtocol.ParseState(
            """{"site":"ytmusic","title":"Song","channel":"Band","liked":true,"disliked":false,"playing":true}""", _time.GetUtcNow());

        Assert.NotNull(state);
        Assert.Equal("ytmusic", state.Site);
        Assert.True(state.Liked);
        Assert.False(state.Disliked);
        Assert.Null(BrowserReactionProtocol.ParseState("""{"site":"vimeo","title":"x"}""", _time.GetUtcNow()));
        Assert.Null(BrowserReactionProtocol.ParseState("garbage", _time.GetUtcNow()));
    }

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("MSEdge", true)]
    [InlineData("firefox.exe", true)]
    [InlineData("Spotify.exe", false)]
    [InlineData("Microsoft.Windows.Search_cw5n1h2txyewy", false)]
    public void Browsers_are_recognised(string appId, bool expected) =>
        Assert.Equal(expected, MediaSourceNames.IsBrowser(appId));

    private sealed class FakeReactions : IBrowserReactions
    {
        private BrowserReactionState? _state;

        public bool Connected { get; set; } = true;

        public bool LooksLikeYouTube { get; set; }

        public List<BrowserReaction> Sent { get; } = [];

        public BrowserReactionState? Current => _state;

        public bool IsExtensionConnected => Connected;

        public event EventHandler? Changed;

        public void Report(string title, bool liked)
        {
            _state = new BrowserReactionState("youtube", title, "Channel", liked, false, true, DateTimeOffset.UtcNow);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<bool> SendAsync(BrowserReaction reaction, CancellationToken cancellationToken)
        {
            Sent.Add(reaction);
            return Task.FromResult(Connected);
        }

        public Task<bool> LooksLikeYouTubeAsync(string title, CancellationToken cancellationToken) => Task.FromResult(LooksLikeYouTube);
    }
}
