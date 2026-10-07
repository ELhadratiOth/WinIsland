using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Claude;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public sealed class ClaudeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winisland-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClaudeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Reads_the_latest_cwd_from_the_end_of_a_transcript()
    {
        string file = Write("p", "s.jsonl",
            """{"type":"user","cwd":"/old/place"}""",
            """{"type":"assistant","cwd":"/work/backend-api","message":{"content":"hi"}}""");

        Assert.Equal("/work/backend-api", ClaudeTranscriptReader.ReadWorkingDirectory(file));
    }

    [Fact]
    public void Falls_back_to_the_start_when_the_tail_is_one_huge_line()
    {
        string huge = "{\"type\":\"tool\",\"output\":\"" + new string('x', ClaudeTranscriptReader.ChunkSize * 2) + "\"}";
        string file = Write("p", "s.jsonl", """{"type":"user","cwd":"/work/frontend"}""", huge);

        Assert.Equal("/work/frontend", ClaudeTranscriptReader.ReadWorkingDirectory(file));
    }

    [Fact]
    public void Tolerates_malformed_lines()
    {
        string file = Write("p", "s.jsonl", """{"cwd": broken""", "not json", """{"cwd":"/ok"}""", "{\"cwd\":");

        Assert.Equal("/ok", ClaudeTranscriptReader.ReadWorkingDirectory(file));
    }

    [Fact]
    public async Task Discovers_recent_sessions_and_marks_activity()
    {
        Write("C--work-backend-api", "aaa.jsonl", """{"cwd":"/work/backend-api"}""");
        string old = Write("C--work-data-pipeline", "bbb.jsonl", """{"cwd":"/work/data-pipeline"}""");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-10));

        await using var monitor = new ClaudeSessionMonitor(Options(), _time);
        await monitor.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(monitor.IsInstalled);
        Assert.Equal(["backend-api", "data-pipeline"], monitor.Sessions.Select(s => s.ProjectName));
        Assert.Equal([true, false], monitor.Sessions.Select(s => s.IsActive));
    }

    [Fact]
    public async Task Keeps_a_bounded_number_of_sessions()
    {
        for (int i = 0; i < 6; i++)
        {
            string f = Write($"p{i}", $"s{i}.jsonl", $$"""{"cwd":"/work/p{{i}}"}""");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-i));
        }

        string ancient = Write("old", "old.jsonl", """{"cwd":"/work/old"}""");
        File.SetLastWriteTimeUtc(ancient, DateTime.UtcNow.AddDays(-30));

        await using var monitor = new ClaudeSessionMonitor(Options() with { MaxSessions = 3 }, _time);
        await monitor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["p0", "p1", "p2"], monitor.Sessions.Select(s => s.ProjectName));
    }

    [Fact]
    public async Task Reports_when_a_working_session_goes_idle_without_polling()
    {
        Write("p", "abc.jsonl", """{"cwd":"/work/backend-api"}""");
        await using var monitor = new ClaudeSessionMonitor(Options(), _time);
        var idle = new List<ClaudeSessionInfo>();
        monitor.SessionBecameIdle += (_, s) => idle.Add(s);
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(monitor.Sessions.Single().IsActive);

        // The single expiry timer fires once the activity window has elapsed.
        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.False(monitor.Sessions.Single().IsActive);
        Assert.Equal("backend-api", Assert.Single(idle).ProjectName);
    }

    [Fact]
    public async Task Missing_claude_directory_is_not_an_error()
    {
        await using var monitor = new ClaudeSessionMonitor(new ClaudeSessionMonitorOptions { ProjectsDirectory = Path.Combine(_root, "nope") }, _time);

        await monitor.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(monitor.IsInstalled);
        Assert.Empty(monitor.Sessions);
    }

    [Fact]
    public async Task Module_mirrors_sessions_and_uses_the_large_panel()
    {
        Write("p", "abc.jsonl", """{"cwd":"/work/backend-api"}""");
        await using var monitor = new ClaudeSessionMonitor(Options(), _time);
        using var module = new ClaudeModule(monitor, new ClaudeCliMessenger("/nonexistent/claude"), new InlineDispatcher());
        await monitor.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(module.IsAvailable);
        Assert.Equal(1, module.ActiveCount);
        Assert.Equal("backend-api", module.CompactText);
        Assert.Equal(ModulePriority.Activity, module.CompactPriority);
        Assert.Equal("abc", module.SelectedSession?.SessionId);

        int attention = 0;
        module.AttentionRequested += (_, _) => attention++;
        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(0, module.ActiveCount);
        Assert.Equal(1, attention);
        Assert.Equal("backend-api is waiting for you", module.AttentionText);
    }

    [Fact]
    public async Task Stop_releases_everything()
    {
        Write("p", "abc.jsonl", """{"cwd":"/work/backend-api"}""");
        var monitor = new ClaudeSessionMonitor(Options(), _time);
        await monitor.StartAsync(TestContext.Current.CancellationToken);

        await monitor.StopAsync();

        Assert.Empty(monitor.Sessions);
    }

    private ClaudeSessionMonitorOptions Options() => new()
    {
        ProjectsDirectory = _root,
        ActiveWindow = TimeSpan.FromSeconds(60),
    };

    private string Write(string project, string name, params string[] lines)
    {
        string dir = Path.Combine(_root, project);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }
}
