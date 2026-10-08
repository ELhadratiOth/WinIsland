using WinIsland.Core.Display;
using WinIsland.Core.Interaction;
using WinIsland.Core.Visibility;

namespace WinIsland.Core.Settings;

public sealed record IslandSettings
{
    public VisibilityMode VisibilityMode { get; init; } = VisibilityMode.HideInFullscreen;

    public MonitorPreference MonitorPreference { get; init; } = MonitorPreference.Primary;

    public bool HoverToInteract { get; init; } = true;

    public int HoverDwellMilliseconds { get; init; } = 180;

    public int LeaveGraceMilliseconds { get; init; } = 400;

    /// <summary>Global hotkey that toggles interactive mode, e.g. "Win+Alt+I". Empty disables it.</summary>
    public string ActivationHotkey { get; init; } = "Win+Alt+I";

    public double TopMarginDip { get; init; } = 6;

    /// <summary>
    /// When the playing app provides no cover art, look it up by artist and title with Apple's
    /// public iTunes Search API (sends only those two strings).
    /// </summary>
    public bool OnlineArtworkLookup { get; init; } = true;

    /// <summary>Time-synced lyrics from lrclib.net (sends only artist, title, album and duration).</summary>
    public bool OnlineLyrics { get; init; } = true;

    /// <summary>Client ID of the user's own Spotify developer app (for "Liked Songs"). Empty: off.</summary>
    public string SpotifyClientId { get; init; } = string.Empty;

    /// <summary>GitHub repositories ("owner/name") whose Actions runs appear on the island.</summary>
    public IReadOnlyList<string> GitHubRepos { get; init; } = [];

    /// <summary>Focus mode: only the clock, timers, recording indicators and approvals may show.</summary>
    public bool FocusMode { get; init; }

    /// <summary>Module ids switched off by the user (e.g. "media", "timer").</summary>
    public IReadOnlyList<string> DisabledModules { get; init; } = [];

    /// <summary>Executable names (e.g. "eldenring.exe") always treated as games.</summary>
    public IReadOnlyList<string> GameProcesses { get; init; } = [];

    /// <summary>
    /// Fragments of executable paths that identify games installed by common launchers.
    /// A fullscreen window from such a path counts as a game.
    /// </summary>
    public IReadOnlyList<string> GamePathFragments { get; init; } =
    [
        @"\steamapps\common\",
        @"\Epic Games\",
        @"\XboxGames\",
        @"\GOG Galaxy\Games\",
        @"\Riot Games\",
        @"\Ubisoft Game Launcher\games\",
        @"\EA Games\",
        @"\Battle.net\",
    ];

    public InteractionOptions ToInteractionOptions() => new()
    {
        HoverToInteract = HoverToInteract,
        HoverDwell = TimeSpan.FromMilliseconds(Math.Clamp(HoverDwellMilliseconds, 0, 2000)),
        LeaveGrace = TimeSpan.FromMilliseconds(Math.Clamp(LeaveGraceMilliseconds, 0, 5000)),
    };
}
