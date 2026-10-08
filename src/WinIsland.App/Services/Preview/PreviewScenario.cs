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
/// 7.5 compact media · 9.5 interactive media · 12 Claude panel · 15 expanded clock · 18 back to compact ·
/// 20 volume keys (OSD) · 22.5 Teams starts using the microphone · 26 interactive (privacy) ·
/// 28.5 controls · 31 Pomodoro timer · 34 back to compact (timer) · 38 end.
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

    public string ClaudeProjectsDirectory { get; }

    public void Run(DispatcherQueue queue, IPreviewTarget target)
    {
        At(queue, 4.0, async () => Media.Set(await SongAsync()));
        At(queue, 9.5, target.PreviewActivate);
        At(queue, 12.0, () => target.PreviewSelect("claude"));
        At(queue, 15.0, () => target.PreviewSelect("clock"));
        At(queue, 18.0, target.PreviewDismiss);
        At(queue, 20.0, () => Speakers.PressKeys(0.64));
        At(queue, 22.5, () => Privacy.Set(new SensorUse(SensorKind.Microphone, "Teams")));
        At(queue, 26.0, target.PreviewActivate);
        At(queue, 28.5, () => target.PreviewSelect("controls"));
        At(queue, 31.0, target.PreviewStartTimer);
        At(queue, 34.0, () =>
        {
            Privacy.Set();
            target.PreviewDismiss();
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
}
