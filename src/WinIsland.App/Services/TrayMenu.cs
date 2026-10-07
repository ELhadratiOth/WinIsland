using WinIsland.Core.Display;
using WinIsland.Core.Settings;
using WinIsland.Core.Visibility;
using WinIsland.Platform.Windows.Shell;

namespace WinIsland.App.Services;

/// <summary>The tray menu: the island's settings surface, kept native and tiny.</summary>
internal static class TrayMenu
{
    private const int VisibilityBase = 100;
    private const int MonitorBase = 200;
    private const int HoverId = 300;
    private const int StartupId = 301;
    private const int HiddenId = 302;
    private const int ExitId = 999;

    public static IReadOnlyList<TrayMenuItem> Build(IslandSettings settings, bool startsWithWindows, bool userHidden) =>
    [
        new TrayMenuItem(0, "Visibility")
        {
            Children =
            [
                Visibility(VisibilityMode.AlwaysShow, "Always show (including games)", settings),
                Visibility(VisibilityMode.HideInGames, "Hide in games", settings),
                Visibility(VisibilityMode.HideInFullscreen, "Hide in fullscreen apps", settings),
                Visibility(VisibilityMode.HideWhenMaximized, "Hide when an app is maximized", settings),
            ],
        },
        new TrayMenuItem(0, "Display")
        {
            Children =
            [
                new TrayMenuItem(MonitorBase + (int)MonitorPreference.Primary, "Primary display", settings.MonitorPreference == MonitorPreference.Primary),
                new TrayMenuItem(MonitorBase + (int)MonitorPreference.FollowActiveWindow, "Follow the active window", settings.MonitorPreference == MonitorPreference.FollowActiveWindow),
            ],
        },
        new TrayMenuItem(HoverId, "Expand on hover", settings.HoverToInteract),
        new TrayMenuItem(StartupId, "Start with Windows", startsWithWindows),
        TrayMenuItem.Separator,
        new TrayMenuItem(HiddenId, userHidden ? "Show island" : "Hide island"),
        new TrayMenuItem(ExitId, "Exit WinIsland"),
    ];

    public static Command? Parse(int id) => id switch
    {
        >= VisibilityBase and <= VisibilityBase + (int)VisibilityMode.HideWhenMaximized => new Command.SetVisibility((VisibilityMode)(id - VisibilityBase)),
        >= MonitorBase and <= MonitorBase + (int)MonitorPreference.FollowActiveWindow => new Command.SetMonitor((MonitorPreference)(id - MonitorBase)),
        HoverId => new Command.ToggleHover(),
        StartupId => new Command.ToggleStartup(),
        HiddenId => new Command.ToggleHidden(),
        ExitId => new Command.Exit(),
        _ => null,
    };

    private static TrayMenuItem Visibility(VisibilityMode mode, string text, IslandSettings settings) =>
        new(VisibilityBase + (int)mode, text, settings.VisibilityMode == mode);

    internal abstract record Command
    {
        public sealed record SetVisibility(VisibilityMode Mode) : Command;

        public sealed record SetMonitor(MonitorPreference Preference) : Command;

        public sealed record ToggleHover : Command;

        public sealed record ToggleStartup : Command;

        public sealed record ToggleHidden : Command;

        public sealed record Exit : Command;
    }
}
