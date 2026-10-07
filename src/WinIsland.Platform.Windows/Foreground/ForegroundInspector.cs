using WinIsland.Core.Geometry;
using WinIsland.Core.Visibility;
using WinIsland.Platform.Windows.Display;
using WinIsland.Platform.Windows.Interop;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Foreground;

/// <summary>
/// Classifies a foreground window: shell surface, normal, maximized, fullscreen, game.
/// Cheap enough to call on every (debounced) foreground event.
/// </summary>
public sealed class ForegroundInspector
{
    private static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    // Start, Search, notification centre, quick settings, Task View / Alt+Tab, tray overflow.
    private static readonly HashSet<string> ShellOverlayClasses = new(StringComparer.Ordinal)
    {
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame",
        "TaskListThumbnailWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "ForegroundStaging", "Shell_Flyout",
    };

    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "SearchApp.exe", "ShellHost.exe",
    };

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly Dictionary<uint, string?> _processPaths = [];

    public IReadOnlyCollection<string> GameProcesses { get; set; } = [];

    public IReadOnlyCollection<string> GamePathFragments { get; set; } = [];

    /// <summary>Returns null for the island's own windows (keep the previous classification).</summary>
    public ForegroundSnapshot? Inspect(nint hwnd)
    {
        if (hwnd == 0 || IsWindow(hwnd) == 0)
        {
            return ForegroundSnapshot.Empty;
        }

        uint pid = GetProcessId(hwnd);
        if (pid == _ownProcessId)
        {
            return null;
        }

        nint root = GetAncestor(hwnd, GA_ROOT);
        if (root != 0)
        {
            hwnd = root;
        }

        string className = GetClassName(hwnd);
        if (DesktopClasses.Contains(className))
        {
            return new ForegroundSnapshot(ForegroundKind.Shell, MonitorProvider.MonitorIdForWindow(hwnd), default);
        }

        if (IsCloaked(hwnd) || IsIconic(hwnd) != 0 || !TryGetWindowRect(hwnd, out RECT rect))
        {
            return ForegroundSnapshot.Empty;
        }

        PixelRect bounds = rect.ToPixelRect();
        MonitorInfoLite? monitor = MonitorOf(hwnd);
        string? path = ProcessPath(pid);
        string processName = path is null ? string.Empty : Path.GetFileName(path);

        if (ShellOverlayClasses.Contains(className) && ShellProcesses.Contains(processName))
        {
            return new ForegroundSnapshot(ForegroundKind.Shell, monitor?.Id, bounds, ProcessName: processName, IsShellOverlay: true);
        }

        bool exclusiveFullscreen = QueryUserNotificationState() == QUNS_RUNNING_D3D_FULL_SCREEN;
        bool coversMonitor = monitor is not null &&
            bounds.X <= monitor.Bounds.X && bounds.Y <= monitor.Bounds.Y &&
            bounds.Right >= monitor.Bounds.Right && bounds.Bottom >= monitor.Bounds.Bottom;

        ForegroundKind kind = exclusiveFullscreen || coversMonitor
            ? ForegroundKind.Fullscreen
            : IsZoomed(hwnd) != 0 ? ForegroundKind.Maximized : ForegroundKind.Normal;

        bool isGame = exclusiveFullscreen ||
            (kind == ForegroundKind.Fullscreen && IsKnownGame(processName, path));

        return new ForegroundSnapshot(kind, monitor?.Id, bounds, isGame, processName);
    }

    private bool IsKnownGame(string processName, string? path)
    {
        foreach (string game in GameProcesses)
        {
            if (string.Equals(game, processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (path is not null)
        {
            foreach (string fragment in GamePathFragments)
            {
                if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string? ProcessPath(uint pid)
    {
        if (!_processPaths.TryGetValue(pid, out string? path))
        {
            // Bounded cache: a long-running session sees many short-lived processes.
            if (_processPaths.Count > 256)
            {
                _processPaths.Clear();
            }

            path = GetProcessPath(pid);
            _processPaths[pid] = path;
        }

        return path;
    }

    private static MonitorInfoLite? MonitorOf(nint hwnd)
    {
        nint handle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return handle == 0 || MonitorProvider.Describe(handle) is not { } d ? null : new MonitorInfoLite(d.Id, d.Bounds);
    }

    private sealed record MonitorInfoLite(string Id, PixelRect Bounds);
}
