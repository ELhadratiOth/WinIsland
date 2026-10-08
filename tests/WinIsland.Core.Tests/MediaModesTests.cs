using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Media;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public class MediaModesTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeMediaSource _source = new();

    private MediaSnapshot Song(bool canShuffle = true, bool shuffle = false, bool canRepeat = true, MediaRepeatMode repeat = MediaRepeatMode.None) =>
        new("Song", "Artist", "Spotify.exe", true, true, true, TimeSpan.Zero, TimeSpan.FromMinutes(3), _time.GetUtcNow(),
            CanShuffle: canShuffle, IsShuffleActive: shuffle, CanRepeat: canRepeat, RepeatMode: repeat);

    [Fact]
    public void Reflects_the_players_shuffle_and_repeat_state()
    {
        using var media = new MediaModule(_source, _time, new InlineDispatcher());

        _source.Set(Song(shuffle: true, repeat: MediaRepeatMode.Track));

        Assert.True(media.IsShuffleActive);
        Assert.True(media.IsRepeatActive);
        Assert.Equal(MediaRepeatMode.Track, media.RepeatMode);
        Assert.Equal("\uE8ED", media.RepeatGlyph);
    }

    [Fact]
    public void Shuffle_toggles_immediately_and_asks_the_player()
    {
        using var media = new MediaModule(_source, _time, new InlineDispatcher());
        _source.Set(Song());

        media.ToggleShuffleCommand.Execute(null);

        Assert.True(media.IsShuffleActive);
        Assert.Equal([true], _source.ShuffleRequests);
    }

    [Fact]
    public void Repeat_cycles_off_all_one_off()
    {
        using var media = new MediaModule(_source, _time, new InlineDispatcher());
        _source.Set(Song());

        media.CycleRepeatCommand.Execute(null);
        Assert.Equal(MediaRepeatMode.List, media.RepeatMode);
        Assert.Equal("\uE8EE", media.RepeatGlyph);
        media.CycleRepeatCommand.Execute(null);
        media.CycleRepeatCommand.Execute(null);

        Assert.Equal(MediaRepeatMode.None, media.RepeatMode);
        Assert.False(media.IsRepeatActive);
        Assert.Equal([MediaRepeatMode.List, MediaRepeatMode.Track, MediaRepeatMode.None], _source.RepeatRequests);
    }

    [Fact]
    public void Buttons_are_disabled_when_the_player_does_not_support_them()
    {
        using var media = new MediaModule(_source, _time, new InlineDispatcher());
        _source.Set(Song(canShuffle: false, canRepeat: false));

        Assert.False(media.ToggleShuffleCommand.CanExecute(null));
        Assert.False(media.CycleRepeatCommand.CanExecute(null));

        media.ToggleShuffleCommand.Execute(null);
        Assert.Empty(_source.ShuffleRequests);
    }
}
