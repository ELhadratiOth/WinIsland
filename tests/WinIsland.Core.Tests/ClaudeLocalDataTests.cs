using WinIsland.Core.Claude;

namespace WinIsland.Core.Tests;

public sealed class ClaudeLocalDataTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "WinIsland-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public void Default_roots_cover_both_claude_locations()
    {
        IReadOnlyList<string> roots = ClaudePaths.ConfigRoots(null, _home);

        Assert.Equal([Path.Combine(_home, ".claude"), Path.Combine(_home, ".config", "claude")], roots);
    }

    [Fact]
    public void Config_dir_variable_replaces_the_defaults_and_may_list_several_roots()
    {
        string a = Path.Combine(_home, "a");
        string b = Path.Combine(_home, "b");

        Assert.Equal([a, b], ClaudePaths.ConfigRoots($"{a}, {b},{a}", _home));
    }

    [Fact]
    public void Only_existing_projects_directories_are_returned()
    {
        string present = Path.Combine(_home, ".config", "claude", "projects");
        Directory.CreateDirectory(present);

        Assert.Equal([present], ClaudePaths.ProjectsDirectories(ClaudePaths.ConfigRoots(null, _home), null));
    }

    [Fact]
    public void Credentials_are_read_from_the_oauth_block()
    {
        ClaudeCredentials? c = ClaudeCredentials.Parse("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc","refreshToken":"r","expiresAt":1893456000000}}""");

        Assert.NotNull(c);
        Assert.Equal("sk-ant-oat01-abc", c.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1893456000000), c.ExpiresAt);
        Assert.False(c.IsExpired(DateTimeOffset.FromUnixTimeMilliseconds(1893455000000)));
        Assert.True(c.IsExpired(DateTimeOffset.FromUnixTimeMilliseconds(1893457000000)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"claudeAiOauth":{}}""")]
    [InlineData("""{"mcpOAuth":{}}""")]
    public void Credentials_without_a_token_are_ignored(string json) => Assert.Null(ClaudeCredentials.Parse(json));

    [Fact]
    public void Plan_usage_reads_both_windows()
    {
        ClaudeRateLimits? limits = ClaudePlanUsage.Parse("""
            {"five_hour":{"utilization":37.5,"resets_at":"2026-10-08T14:59:59.943648+00:00"},
             "seven_day":{"utilization":12.0,"resets_at":"2026-10-12T03:59:59+00:00"},
             "seven_day_opus":null,"extra_usage":{"is_enabled":false}}
            """);

        Assert.NotNull(limits);
        Assert.Equal(37.5, limits.FiveHourPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 14, 59, 59, TimeSpan.Zero).AddTicks(9436480), limits.FiveHourResets);
        Assert.Equal(12.0, limits.WeekPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 3, 59, 59, TimeSpan.Zero), limits.WeekResets);
    }

    [Fact]
    public void Plan_usage_tolerates_a_missing_window_and_null_reset()
    {
        ClaudeRateLimits? limits = ClaudePlanUsage.Parse("""{"five_hour":{"utilization":3,"resets_at":null},"seven_day":null}""");

        Assert.NotNull(limits);
        Assert.Equal(3, limits.FiveHourPercent);
        Assert.Null(limits.FiveHourResets);
        Assert.Null(limits.WeekPercent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("nonsense")]
    [InlineData("""{"error":{"type":"rate_limit_error"}}""")]
    public void Plan_usage_without_windows_is_null(string json) => Assert.Null(ClaudePlanUsage.Parse(json));

    [Fact]
    public async Task Usage_tracker_reads_transcripts_from_every_directory()
    {
        string a = Path.Combine(_home, "one");
        string b = Path.Combine(_home, "two");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        string Line(string id, int output) =>
            "{\"type\":\"assistant\",\"timestamp\":\"" + now.ToString("O") + "\",\"message\":{\"id\":\"" + id +
            "\",\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":10,\"output_tokens\":" + output + "}}}";
        File.WriteAllText(Path.Combine(a, "s1.jsonl"), Line("m1", 100) + "\n");
        File.WriteAllText(Path.Combine(b, "s2.jsonl"), Line("m2", 200) + "\n");

        var tracker = new ClaudeUsageTracker([a, b, Path.Combine(_home, "missing")], new FixedTime(now));
        await tracker.StartAsync(CancellationToken.None);
        await tracker.StopAsync();

        Assert.Equal(10 + 100 + 10 + 200, tracker.Summary.WindowTokens);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
