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
/// 39 clipboard history · 41.5 file shelf · 44 back to compact · 46 end.
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

    public string ClaudeProjectsDirectory { get; }

    public void Run(DispatcherQueue queue, IPreviewTarget target)
    {
        At(queue, 4.0, async () =>
        {
            Media.SetSessions(new MediaSessionInfo("Spotify.exe", "Spotify", true, true), new MediaSessionInfo("MSEdge", "Microsoft Edge", false, false));
            Media.Set(await SongAsync());
        });
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
            CanShuffle: true, IsShuffleActive: true, CanRepeat: true, RepeatMode: MediaRepeatMode.None);
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

    private void Session(string project, string id, TimeSpan age)
    {
        string dir = Path.Combine(ClaudeProjectsDirectory, $"C--work-{project}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{id}.jsonl");
        File.WriteAllText(file, $$"""{"type":"user","cwd":"C:\\work\\{{project}}"}""" + "\n");
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

    void PreviewShowLyrics(bool show);

    void PreviewShelf(params string[] paths);
}
