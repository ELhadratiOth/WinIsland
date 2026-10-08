using Microsoft.UI.Dispatching;
using Windows.Storage.Streams;
using WinIsland.Core.Devices;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Media;
using WinIsland.Platform.Windows.Media;

namespace WinIsland.App.Services.Preview;

/// <summary>
/// <c>WinIsland.exe --preview</c>: a scripted tour through every island state with sample data,
/// so the design can be reviewed (and photographed in CI) without real media or sessions.
/// Timeline (seconds): 0 Claude working (compact) · 4 a track starts (expanded notice) ·
/// 7.5 compact media · 9.5 interactive media · 10.6 lyrics · 12 Claude panel · 15 expanded clock · 18 back to compact ·
/// 20 volume keys (OSD) · 22.5 Teams starts using the microphone · 26.5 interactive (privacy) ·
/// 29 controls · 31 Pomodoro timer · 34 back to compact (timer) · 36 a download finishes ·
/// 39 clipboard history · 41.5 file shelf · 44 back to compact · 45 Claude asks for approval ·
/// 47.5 interactive (approve) · 51 a build starts · 53.5 it passes · 58 a meeting in 5 minutes ·
/// 61 compact (meeting countdown) · 66.5 plugged in (charging) · 69 focus mode on (controls) ·
/// 72 focus off · 74 YouTube in a browser (👍/👎) · 78 back to the clock (compact, symmetric) · 82 end. Weather shows next to the clock throughout.
/// </summary>
internal sealed class PreviewScenario
{
    private readonly List<DispatcherQueueTimer> _timers = [];

    public PreviewScenario()
    {
        ClaudeProjectsDirectory = Path.Combine(Path.GetTempPath(), "WinIsland-preview", "projects");
        CreateClaudeSessions();
    }

    public PreviewMediaSource Media { get; } = new();

    public PreviewAudio Speakers { get; } = new(0.42);

    public PreviewAudio Microphone { get; } = new(0.8);

    public PreviewBrightness Brightness { get; } = new();

    public PreviewPower Power { get; } = new();

    public PreviewPrivacy Privacy { get; } = new();

    public PreviewClipboard Clipboard { get; } = new();

    public PreviewDownloads Downloads { get; } = new();

    public PreviewHookServer Hooks { get; } = new();

    public PreviewCi Ci { get; } = new();

    public PreviewWeather Weather { get; } = new();

    public PreviewCalendar Calendar { get; } = new();

    public PreviewLibrary Library { get; } = new();

    public PreviewReactions Reactions { get; } = new();

    public string ClaudeProjectsDirectory { get; }

    public void Run(DispatcherQueue queue, IPreviewTarget target)
    {
        At(queue, 4.0, async () =>
        {
            Media.SetSessions(new MediaSessionInfo("Spotify.exe", "Spotify", true, true), new MediaSessionInfo("MSEdge", "Microsoft Edge", false, false));
            Media.Set(await SongAsync());
        });
        At(queue, 2.0, () => Hooks.Limits(37, 62));
        At(queue, 9.5, target.PreviewActivate);
        At(queue, 10.6, () => target.PreviewShowLyrics(true));
        At(queue, 12.0, () =>
        {
            target.PreviewShowLyrics(false);
            target.PreviewSelect("claude");
        });
        At(queue, 15.0, () => target.PreviewSelect("clock"));
        At(queue, 18.0, target.PreviewDismiss);
        At(queue, 20.0, () => Speakers.PressKeys(0.64));
        At(queue, 22.5, () => Privacy.Set(new SensorUse(SensorKind.Microphone, "Teams")));
        At(queue, 26.5, target.PreviewActivate);
        At(queue, 29.0, () => target.PreviewSelect("controls"));
        At(queue, 31.0, target.PreviewStartTimer);
        At(queue, 34.0, () =>
        {
            Privacy.Set();
            target.PreviewDismiss();
        });
        At(queue, 35.0, () =>
        {
            Clipboard.Emit("git push -u origin feature/island", TimeSpan.FromMinutes(42));
            Clipboard.Emit("Meeting notes:\n- ship v0.2\n- record the demo", TimeSpan.FromMinutes(18));
            Clipboard.Emit("https://github.com/ELhadratiOth/WinIsland/releases", TimeSpan.FromMinutes(6));
            Clipboard.Emit("The island now shows lyrics, timers and your downloads.", TimeSpan.FromSeconds(20));
            target.PreviewShelf(@"C:\Users\you\Desktop\Design review.pdf", @"C:\Users\you\Pictures\island-hero.png", @"C:\Users\you\Downloads\WinIsland-0.2.0-win-x64.zip");
        });
        At(queue, 36.0, () => Downloads.Complete("WinIsland-0.2.0-win-x64.zip", 106_000_000));
        At(queue, 39.0, () =>
        {
            target.PreviewActivate();
            target.PreviewSelect("clipboard");
        });
        At(queue, 41.5, () => target.PreviewSelect("shelf"));
        At(queue, 44.0, target.PreviewDismiss);
        Core.Claude.ApprovalRequest? approval = null;
        At(queue, 45.0, () => approval = Hooks.Request("Bash", "npm run deploy -- --env production", "backend-api"));
        At(queue, 47.5, target.PreviewActivate);
        At(queue, 50.0, () =>
        {
            approval?.Resolve(Core.Claude.ApprovalDecision.Allow);
            target.PreviewDismiss();
        });
        At(queue, 51.0, () => Ci.Set(Runs(Core.GitHub.RunState.Running)));
        At(queue, 53.5, () => Ci.Set(Runs(Core.GitHub.RunState.Succeeded)));
        At(queue, 58.0, Calendar.MeetingSoon);
        At(queue, 61.0, target.PreviewDismiss);
        At(queue, 66.5, Power.PlugIn);
        At(queue, 69.0, () =>
        {
            target.PreviewFocus(true);
            target.PreviewActivate();
            target.PreviewSelect("controls");
        });
        At(queue, 72.0, () =>
        {
            target.PreviewDismiss();
            target.PreviewFocus(false);
        });
        At(queue, 74.0, () =>
        {
            Reactions.Report("Lofi hip hop radio - beats to relax/study to", "Lofi Girl", liked: false);
            Media.Set(YouTube());
        });
        At(queue, 75.0, () =>
        {
            target.PreviewActivate();
            target.PreviewSelect("media");
        });
        At(queue, 77.0, () => Reactions.Report("Lofi hip hop radio - beats to relax/study to", "Lofi Girl", liked: true));
        At(queue, 79.0, () =>
        {
            target.PreviewDismiss();
            Reactions.Clear();
            Media.Set(null);
            Calendar.Clear(); // the meeting is over: the plain clock is back
            target.PreviewStopTimer();
        });
    }

    private void At(DispatcherQueue queue, double seconds, Action action)
    {
        DispatcherQueueTimer timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            AppLog.Info("Preview", $"t={seconds}s");
            action();
        };
        timer.Start();
        _timers.Add(timer);
    }

    private static async Task<MediaSnapshot> SongAsync()
    {
        MediaArtwork? artwork = null;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "PreviewArt.png"));
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }

            artwork = await ArtworkLoader.LoadAsync(RandomAccessStreamReference.CreateFromStream(stream));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Preview", "Preview artwork unavailable", ex);
        }

        return new MediaSnapshot(
            "Midnight City", "M83", "Spotify.exe", IsPlaying: true, CanGoNext: true, CanGoPrevious: true,
            Position: TimeSpan.FromSeconds(83), Duration: TimeSpan.FromSeconds(243), PositionSampledAt: DateTimeOffset.UtcNow,
            Artwork: artwork, Album: "Hurry Up, We're Dreaming",
            CanShuffle: true, IsShuffleActive: true, CanRepeat: true, RepeatMode: MediaRepeatMode.None, CanSeek: true);
    }

    private static MediaSnapshot YouTube() => new(
        "Lofi hip hop radio - beats to relax/study to", "Lofi Girl", "Chrome", IsPlaying: true, CanGoNext: false, CanGoPrevious: false,
        Position: TimeSpan.FromSeconds(1210), Duration: TimeSpan.Zero, PositionSampledAt: DateTimeOffset.UtcNow);

    private static Core.GitHub.WorkflowRun[] Runs(Core.GitHub.RunState latest)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return
        [
            new(3, "ELhadratiOth/WinIsland", "CI", "main", "Island: approvals and builds", latest, "https://github.com/ELhadratiOth/WinIsland/actions", now),
            new(2, "ELhadratiOth/WinIsland", "Release", "main", "Release v0.2.0", Core.GitHub.RunState.Succeeded, "https://github.com/ELhadratiOth/WinIsland/actions", now.AddMinutes(-35)),
            new(1, "ELhadratiOth/website", "Deploy", "main", "Update landing page", Core.GitHub.RunState.Failed, "https://github.com/ELhadratiOth/website/actions", now.AddHours(-3)),
        ];
    }

    private void CreateClaudeSessions()
    {
        if (Directory.Exists(ClaudeProjectsDirectory))
        {
            Directory.Delete(ClaudeProjectsDirectory, recursive: true);
        }

        Session("backend-api", "a1", TimeSpan.Zero);
        Session("frontend", "b2", TimeSpan.Zero);
        Session("data-pipeline", "c3", TimeSpan.FromMinutes(14));
    }

    /// <summary>An assistant line with token usage, so the Claude panel shows today's usage.</summary>
    private static string UsageLine(string id, TimeSpan age)
    {
        string timestamp = (DateTimeOffset.UtcNow - age).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        long cacheRead = id == "a1" ? 1_250_000 : 640_000;
        return "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"message\":{\"id\":\"msg_" + id +
            "\",\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":5200,\"output_tokens\":18400,\"cache_creation_input_tokens\":42000,\"cache_read_input_tokens\":" +
            cacheRead.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}\n";
    }

    private void Session(string project, string id, TimeSpan age)
    {
        string dir = Path.Combine(ClaudeProjectsDirectory, $"C--work-{project}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{id}.jsonl");
        File.WriteAllText(file, $$"""{"type":"user","cwd":"C:\\work\\{{project}}"}""" + "\n" + UsageLine(id, age));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
    }
}

/// <summary>Hooks the preview scenario needs into the host.</summary>
internal interface IPreviewTarget
{
    void PreviewActivate();

    void PreviewSelect(string moduleId);

    void PreviewDismiss();

    void PreviewStartTimer();

    void PreviewStopTimer();

    void PreviewShowLyrics(bool show);

    void PreviewShelf(params string[] paths);

    void PreviewFocus(bool on);
}
