using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Claude;
using WinIsland.Core.Layout;
using WinIsland.Core.Media;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public class DesignModelTests
{
    [Fact]
    public void Accent_follows_the_dominant_vivid_colour()
    {
        // Mostly saturated red with some grey noise.
        byte[] pixels = Image(24 * 24, i => i % 5 == 0 ? (128, 128, 128) : (200, 30, 40));

        uint accent = AccentPicker.Pick(pixels);

        (int r, int g, int b) = Channels(accent);
        Assert.True(r > 200 && g < 120 && b < 120, $"expected red-ish, got #{accent:X8}");
    }

    [Fact]
    public void Accent_is_lifted_so_dark_covers_still_glow_on_black()
    {
        byte[] pixels = Image(24 * 24, _ => (10, 30, 90)); // dark navy

        (int r, int g, int b) = Channels(AccentPicker.Pick(pixels));

        Assert.True(Math.Max(r, Math.Max(g, b)) >= 216, "brightness must be lifted");
        Assert.True(b > r && b > g, "hue must stay blue");
    }

    [Fact]
    public void Monochrome_art_gives_the_neutral_accent()
    {
        Assert.Equal(AccentPicker.Neutral, AccentPicker.Pick(Image(100, _ => (90, 90, 90))));
        Assert.Equal(AccentPicker.Neutral, AccentPicker.Pick([]));
    }

    [Theory]
    [InlineData("backend-api", "BA")]
    [InlineData("frontend", "FR")]
    [InlineData("data_pipeline_v2", "DP")]
    [InlineData("x", "X")]
    public void Session_initials(string name, string expected) =>
        Assert.Equal(expected, ClaudeSessionItem.MakeInitials(name));

    [Fact]
    public void Session_detail_shows_state_and_age()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        ClaudeSessionInfo Info(bool active, TimeSpan ago) => new("id", "api", "/api", "/t", now - ago, active);

        Assert.Equal("Working · now", ClaudeSessionItem.Describe(Info(true, TimeSpan.FromSeconds(10)), now));
        Assert.Equal("Idle · 5m", ClaudeSessionItem.Describe(Info(false, TimeSpan.FromMinutes(5)), now));
        Assert.Equal("Idle · 3h", ClaudeSessionItem.Describe(Info(false, TimeSpan.FromHours(3)), now));
        Assert.Equal("Idle · 2d", ClaudeSessionItem.Describe(Info(false, TimeSpan.FromDays(2)), now));
    }

    [Fact]
    public void Avatar_colour_is_stable_per_project()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new ClaudeSessionItem(new ClaudeSessionInfo("1", "api", "/work/api", "/t", now, true), now);
        var b = new ClaudeSessionItem(new ClaudeSessionInfo("2", "api", "/work/api", "/t2", now, false), now);

        Assert.Equal(a.AvatarColor, b.AvatarColor);
    }

    [Fact]
    public void Media_uses_a_wide_compact_pill_and_exposes_artwork()
    {
        var time = new FakeTimeProvider();
        var source = new FakeMediaSource();
        using var media = new MediaModule(source, time, new InlineDispatcher());

        source.Set(new MediaSnapshot("Song", "Artist", "app", true, true, true, TimeSpan.Zero, TimeSpan.FromMinutes(3), time.GetUtcNow(), new MediaArtwork([1, 2, 3], 0xFF336699)));

        Assert.Equal(IslandMetrics.CompactWide, media.GetSize(IslandSize.Compact));
        Assert.True(media.HasArtwork);
        Assert.Equal(0xFF336699u, media.AccentColor);
    }

    private static byte[] Image(int count, Func<int, (int R, int G, int B)> pixel)
    {
        byte[] bgra = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            (int r, int g, int b) = pixel(i);
            bgra[(i * 4) + 0] = (byte)b;
            bgra[(i * 4) + 1] = (byte)g;
            bgra[(i * 4) + 2] = (byte)r;
            bgra[(i * 4) + 3] = 255;
        }

        return bgra;
    }

    private static (int R, int G, int B) Channels(uint argb) =>
        ((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));
}
