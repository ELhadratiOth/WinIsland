using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Claude;
using WinIsland.Core.Layout;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public class PlanLimitTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    private const string StatusJson = """
        {"model":{"id":"claude-opus-5-5","display_name":"Opus"},
         "rate_limits":{"five_hour":{"used_percentage":82.4,"resets_at":1791460800},"seven_day":{"used_percentage":41.2,"resets_at":1791720000}}}
        """;

    public PlanLimitTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    [Fact]
    public void Status_line_input_yields_limits_and_a_compact_line()
    {
        ClaudeRateLimits? limits = ClaudeStatusLine.ParseLimits(StatusJson);

        Assert.NotNull(limits);
        Assert.Equal(82.4, limits.FiveHourPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791460800), limits.FiveHourResets);
        Assert.Equal("Opus · 5h 82% · week 41%", ClaudeStatusLine.Render(StatusJson));

        // API-key sessions have no rate_limits: just the model.
        Assert.Null(ClaudeStatusLine.ParseLimits("""{"model":{"display_name":"Sonnet"}}"""));
        Assert.Equal("Sonnet", ClaudeStatusLine.Render("""{"model":{"display_name":"Sonnet"}}"""));
    }

    [Fact]
    public void Claude_panel_shows_limits_and_warns_once_per_window()
    {
        var monitor = new ClaudeSessionMonitor(new ClaudeSessionMonitorOptions { ProjectsDirectory = Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid()) }, _time);
        using var claude = new ClaudeModule(monitor, new NullMessenger(), new InlineDispatcher(), _time);
        var attention = new List<AttentionRequest>();
        claude.AttentionRequested += (_, r) => attention.Add(r);

        DateTimeOffset resets = _time.GetUtcNow().AddHours(2);
        claude.ApplyLimits(new ClaudeRateLimits(82.4, resets, 41.2, _time.GetUtcNow().AddDays(3)));

        Assert.True(claude.HasLimits);
        Assert.Equal($"82% · resets {resets.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}", claude.FiveHourLimitText);
        Assert.Equal(0.824, claude.FiveHourFraction, 3);
        Assert.Equal(Palette.Yellow, claude.LimitAccent);
        Assert.Single(attention);
        Assert.StartsWith("82% of your 5-hour limit used", claude.AttentionText, StringComparison.Ordinal);

        claude.ApplyLimits(new ClaudeRateLimits(85, resets, 42, _time.GetUtcNow().AddDays(3)));
        Assert.Single(attention);

        claude.ApplyLimits(new ClaudeRateLimits(96, resets, 42, _time.GetUtcNow().AddDays(3)));
        Assert.Equal(2, attention.Count);
        Assert.Equal(Palette.Red, claude.LimitAccent);

        // The 5-hour window resets: it disappears, the week stays.
        _time.Advance(TimeSpan.FromHours(2.01));
        Assert.False(claude.HasFiveHourLimit);
        Assert.True(claude.HasWeekLimit);
        Assert.Equal(IslandSize.Large, claude.InteractiveSize);
    }

    [Fact]
    public void Installer_adds_a_status_line_only_when_there_is_none()
    {
        JsonNode fresh = JsonNode.Parse(ClaudeHooksInstaller.Apply(null, @"C:\Apps\WinIsland.exe", install: true))!;
        Assert.Equal("C:/Apps/WinIsland.exe --claude-statusline", fresh["statusLine"]!["command"]!.GetValue<string>());

        string custom = """{"statusLine":{"type":"command","command":"~/my-line.sh"}}""";
        JsonNode kept = JsonNode.Parse(ClaudeHooksInstaller.Apply(custom, @"C:\Apps\WinIsland.exe", install: true))!;
        Assert.Equal("~/my-line.sh", kept["statusLine"]!["command"]!.GetValue<string>());

        JsonNode removed = JsonNode.Parse(ClaudeHooksInstaller.Apply(fresh.ToJsonString(), "x", install: false))!;
        Assert.Null(removed["statusLine"]);
        Assert.Equal("\"C:/Program Files/WinIsland/WinIsland.exe\" --claude-statusline", ClaudeHooksInstaller.StatusLineCommand(@"C:\Program Files\WinIsland\WinIsland.exe"));
    }

    private sealed class NullMessenger : IClaudeMessenger
    {
        public bool IsAvailable => false;

        public Task SendAsync(ClaudeSessionInfo session, string message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
