using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Media;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public class ModuleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 41, 30, TimeSpan.Zero));
    private readonly InlineDispatcher _dispatcher = new();

    public ModuleTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Clock_updates_exactly_at_the_minute_boundary()
    {
        using var clock = new ClockModule(_time, _dispatcher);
        string before = clock.TimeText;

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(before, clock.TimeText);

        // The wake-up is scheduled 20 ms past the boundary so it can never render the old minute.
        _time.Advance(TimeSpan.FromMilliseconds(1020));
        Assert.NotEqual(before, clock.TimeText);
        Assert.Equal(clock.TimeText, clock.CompactText);
    }

    [Fact]
    public void Media_becomes_the_compact_module_only_while_playing()
    {
        var source = new FakeMediaSource();
        using var media = new MediaModule(source, _time, _dispatcher);
        Assert.False(media.IsAvailable);

        source.Set(Song("A", playing: true));
        Assert.True(media.IsAvailable);
        Assert.Equal(ModulePriority.Media, media.CompactPriority);

        source.Set(Song("A", playing: false));
        Assert.Equal(ModulePriority.Unavailable, media.CompactPriority);
        Assert.Equal(ModulePriority.Media, media.InteractivePriority);
    }

    [Fact]
    public void Media_requests_attention_on_track_change_but_not_on_pause()
    {
        var source = new FakeMediaSource();
        using var media = new MediaModule(source, _time, _dispatcher);
        int attention = 0;
        media.AttentionRequested += (_, _) => attention++;

        source.Set(Song("A", playing: true));
        source.Set(Song("A", playing: false));
        source.Set(Song("A", playing: true));
        source.Set(Song("B", playing: true));

        Assert.Equal(2, attention);
    }

    [Fact]
    public void Media_progress_only_ticks_while_the_view_is_visible()
    {
        var source = new FakeMediaSource();
        using var media = new MediaModule(source, _time, _dispatcher);
        source.Set(Song("A", playing: true) with { Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromSeconds(100), PositionSampledAt = _time.GetUtcNow() });
        Assert.Equal(10, media.Progress, precision: 3);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(10, media.Progress, precision: 3);

        media.IsViewActive = true;
        Assert.Equal(15, media.Progress, precision: 3);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(16, media.Progress, precision: 3);
        Assert.Equal("0:16", media.PositionText);

        media.IsViewActive = false;
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(16, media.Progress, precision: 3);
    }

    private MediaSnapshot Song(string title, bool playing) =>
        new(title, "Artist", "app", playing, true, true, TimeSpan.Zero, TimeSpan.FromMinutes(3), _time.GetUtcNow());
}
