using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Claude;
using WinIsland.Core.GitHub;
using WinIsland.Core.Helpers;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public class DeveloperModuleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 14, 20, 0, TimeSpan.Zero));
    private readonly InlineDispatcher _dispatcher = new();

    [Fact]
    public void Usage_lines_are_parsed_and_priced()
    {
        UsageEntry? entry = ClaudeUsageTracker.Parse("""
            {"type":"assistant","timestamp":"2026-10-08T12:02:44.543Z","requestId":"req_1","message":{"id":"msg_1","model":"claude-sonnet-4-5","usage":{"input_tokens":1000,"output_tokens":2000,"cache_creation_input_tokens":4000,"cache_read_input_tokens":100000}}}
            """);

        Assert.NotNull(entry);
        Assert.Equal("msg_1", entry.MessageId);
        Assert.Equal(107_000, entry.TotalTokens);
        // (1000 + 4000×1.25 + 100000×0.1)×$3 + 2000×$15, per million.
        Assert.Equal(0.078m, entry.Cost);
        Assert.Null(ClaudeUsageTracker.Parse("""{"type":"user","message":{"content":"hi"}}"""));
        Assert.Null(ClaudePricing.PricesFor("some-future-model"));
        Assert.Equal("1.2M", UsageText.Tokens(1_234_567));
    }

    [Fact]
    public void Usage_summary_counts_today_and_the_current_five_hour_window()
    {
        UsageEntry At(string id, int hour, int minute, long tokens) =>
            new(id, new DateTimeOffset(2026, 10, 8, hour, minute, 0, TimeSpan.Zero), "claude-opus-4-5", tokens, 0, 0, 0);

        UsageEntry[] entries =
        [
            new("old", new DateTimeOffset(2026, 10, 7, 23, 0, 0, TimeSpan.Zero), "claude-opus-4-5", 999, 0, 0, 0),
            At("a", 6, 10, 100),
            At("b", 9, 0, 200),
            At("c", 12, 40, 300),
            At("d", 14, 5, 400),
        ];

        UsageSummary summary = ClaudeUsageTracker.Summarize(entries, _time.GetUtcNow(), TimeZoneInfo.Utc);

        Assert.Equal(1_000, summary.TodayTokens);
        // 06:00–11:00 window held a and b; c (12:40) opened 12:00–17:00 which also holds d.
        Assert.Equal(700, summary.WindowTokens);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 17, 0, 0, TimeSpan.Zero), summary.WindowResetsAt);
    }

    [Fact]
    public async Task Usage_tracker_reads_appended_lines_once()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winisland-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "proj"));
        string file = Path.Combine(dir, "proj", "s.jsonl");
        string Line(string id, int tokens) =>
            "{\"type\":\"assistant\",\"timestamp\":\"" + _time.GetUtcNow().ToString("O") + "\",\"message\":{\"id\":\"" + id +
            "\",\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":" + tokens + ",\"output_tokens\":0}}}";
        try
        {
            await File.WriteAllTextAsync(file, Line("m1", 10) + "\n" + Line("m1", 10) + "\n", TestContext.Current.CancellationToken);
            await using var tracker = new ClaudeUsageTracker(dir, _time);
            await tracker.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(10, tracker.Summary.TodayTokens);

            await File.AppendAllTextAsync(file, Line("m2", 5) + "\n", TestContext.Current.CancellationToken);
            await Eventually.TrueAsync(() => tracker.Summary.TodayTokens == 15, "appended line counted");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Permission_payloads_become_readable_requests()
    {
        ApprovalRequest? request = ClaudeHookPayload.ParsePermission("""
            {"session_id":"abc","cwd":"C:\\work\\backend-api","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"rm -rf node_modules","description":"Remove node_modules"}}
            """);

        Assert.NotNull(request);
        Assert.Equal("Run a command", request.Action);
        Assert.Equal("rm -rf node_modules", request.Summary);
        Assert.Equal("backend-api", request.Project);
        Assert.Equal("github · create_issue", new ApprovalRequest("mcp__github__create_issue", "", "", null).ToolLabel);

        Assert.Equal(string.Empty, ClaudeHookPayload.BuildResponse(ApprovalDecision.Ask));
        JsonNode allow = JsonNode.Parse(ClaudeHookPayload.BuildResponse(ApprovalDecision.Allow))!;
        Assert.Equal("allow", allow["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>());
        Assert.Equal("PermissionRequest", allow["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>());
        JsonNode deny = JsonNode.Parse(ClaudeHookPayload.BuildResponse(ApprovalDecision.Deny))!;
        Assert.Equal("deny", deny["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>());
    }

    [Fact]
    public void Approvals_hold_the_island_until_answered()
    {
        var server = new FakeHookServer();
        using var module = new ApprovalsModule(server, _time, _dispatcher);
        var attention = new List<AttentionRequest>();
        int ended = 0;
        module.AttentionRequested += (_, r) => attention.Add(r);
        module.AttentionEnded += (_, _) => ended++;

        var first = new ApprovalRequest("Bash", "npm test", "web", null);
        var second = new ApprovalRequest("Edit", @"C:\web\a.ts", "web", null);
        server.Request(first);
        server.Request(second);

        Assert.Equal(ModulePriority.Blocking, module.CompactPriority);
        Assert.Equal(AttentionPriority.Blocking, attention[0].Priority);
        Assert.Equal("npm test", module.Summary);
        Assert.Equal("+1 more waiting", module.MoreText);

        module.AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, first.Decision.Result);
        Assert.Equal("Edit a file", module.Title);

        // Answered in the terminal instead: Claude Code drops the hook call.
        second.Resolve(ApprovalDecision.Ask);
        Assert.False(module.HasRequest);
        Assert.Equal(1, ended);
        Assert.False(module.IsAvailable);
    }

    [Fact]
    public void Notices_show_briefly_then_go_away()
    {
        var server = new FakeHookServer();
        using var module = new ApprovalsModule(server, _time, _dispatcher);
        server.Notice(new ClaudeNotice("Claude is waiting for your input", "web"));
        Assert.True(module.HasNoticeOnly);

        _time.Advance(TimeSpan.FromSeconds(7));
        Assert.False(module.IsAvailable);
    }

    [Fact]
    public void Installer_adds_and_removes_only_its_own_hooks()
    {
        string existing = """
            {
              // user comment
              "model": "opus",
              "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "lint.sh" } ] } ] },
            }
            """;

        string installed = ClaudeHooksInstaller.Apply(existing, @"C:\Apps\WinIsland\WinIsland.exe", install: true);
        string twice = ClaudeHooksInstaller.Apply(installed, @"C:\Apps\WinIsland\WinIsland.exe", install: true);
        Assert.Equal(installed, twice);

        JsonNode root = JsonNode.Parse(installed)!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal("lint.sh", root["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("C:/Apps/WinIsland/WinIsland.exe --claude-hook permission", root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("idle_prompt", root["hooks"]!["Notification"]![0]!["matcher"]!.GetValue<string>());

        JsonNode spaced = JsonNode.Parse(ClaudeHooksInstaller.Apply(null, @"C:\Program Files\WinIsland\WinIsland.exe", install: true))!;
        JsonNode handler = spaced["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!;
        Assert.Equal(@"C:\Program Files\WinIsland\WinIsland.exe", handler["command"]!.GetValue<string>());
        Assert.Equal("--claude-hook", handler["args"]![0]!.GetValue<string>());

        JsonNode removed = JsonNode.Parse(ClaudeHooksInstaller.Apply(installed, "x", install: false))!;
        Assert.Null(removed["hooks"]!["PermissionRequest"]);
        Assert.Null(removed["hooks"]!["Notification"]);
        Assert.NotNull(removed["hooks"]!["PreToolUse"]);
    }

    [Fact]
    public void Ci_announces_a_finished_build_but_not_old_ones()
    {
        var source = new FakeCi();
        source.Set(Run(1, RunState.Succeeded, 10));
        using var module = new CiModule(source, new NullShell(), _time, _dispatcher);
        var attention = new List<AttentionRequest>();
        module.AttentionRequested += (_, r) => attention.Add(r);

        source.Set(Run(2, RunState.Running, 1), Run(1, RunState.Succeeded, 10));
        Assert.Equal(ModulePriority.Activity, module.CompactPriority);
        Assert.Equal("Building island", module.CompactText);
        Assert.Empty(attention);

        source.Set(Run(2, RunState.Failed, 0), Run(1, RunState.Succeeded, 10));
        Assert.Equal("Build failed · island", module.Headline);
        Assert.Equal(Palette.Red, module.AccentArgb);
        Assert.Equal(AttentionPriority.Important, Assert.Single(attention).Priority);
        Assert.Single(module.Runs);
    }

    [Fact]
    public void Workflow_runs_are_parsed()
    {
        IReadOnlyList<WorkflowRun> runs = WorkflowRunsParser.Parse("me/island", """
            {"workflow_runs":[{"id":7,"name":"CI","head_branch":"main","display_title":"Fix","status":"completed","conclusion":"timed_out","html_url":"https://x","updated_at":"2026-10-08T09:00:00Z"}]}
            """);

        WorkflowRun run = Assert.Single(runs);
        Assert.Equal(RunState.Failed, run.State);
        Assert.Equal("Fix", run.Title);
    }

    private WorkflowRun Run(long id, RunState state, int minutesAgo) =>
        new(id, "me/island", "CI", "main", $"run {id}", state, "https://github.com", _time.GetUtcNow().AddMinutes(-minutesAgo));

    private sealed class FakeHookServer : IClaudeHookServer
    {
        public event EventHandler<ApprovalRequest>? PermissionRequested;

        public event EventHandler<ClaudeNotice>? NoticeReceived;

        public void Request(ApprovalRequest request) => PermissionRequested?.Invoke(this, request);

        public void Notice(ClaudeNotice notice) => NoticeReceived?.Invoke(this, notice);
    }

    private sealed class FakeCi : ICiSource
    {
        public IReadOnlyList<WorkflowRun> Runs { get; private set; } = [];

        public event EventHandler? Changed;

        public void Set(params WorkflowRun[] runs)
        {
            Runs = runs;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class NullShell : IShellLauncher
    {
        public void Open(string path)
        {
        }

        public void Reveal(string path)
        {
        }
    }
}
