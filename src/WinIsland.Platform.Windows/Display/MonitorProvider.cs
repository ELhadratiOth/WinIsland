using System.Runtime.InteropServices;
using WinIsland.Core.Display;
using WinIsland.Core.Geometry;
using WinIsland.Platform.Windows.Interop;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Display;

/// <summary>
/// Enumerates displays with their physical bounds, work areas and effective DPI. The app is
/// per-monitor-DPI-aware (v2), so all coordinates are physical pixels. Results are cached and
/// invalidated by WM_DISPLAYCHANGE / WM_SETTINGCHANGE / WM_DPICHANGED rather than re-queried.
/// </summary>
public sealed unsafe class MonitorProvider
{
    private IReadOnlyList<MonitorDescriptor>? _cache;

    public IReadOnlyList<MonitorDescriptor> GetMonitors() => _cache ??= Enumerate();

    public void Invalidate() => _cache = null;

    public static string? MonitorIdForWindow(nint hwnd)
    {
        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return monitor == 0 ? null : Describe(monitor)?.Id;
    }

    public static string? MonitorIdForPoint(int x, int y)
    {
        nint monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONULL);
        return monitor == 0 ? null : Describe(monitor)?.Id;
    }

    public static MonitorDescriptor? Describe(nint monitor)
    {
        MONITORINFOEXW info = default;
        info.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (GetMonitorInfo(monitor, &info) == 0)
        {
            return null;
        }

        uint dpiX = 96, dpiY = 96;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY) != 0 || dpiX == 0)
        {
            dpiX = 96;
        }

        string id = new string(info.szDevice);
        return new MonitorDescriptor(
            id,
            info.rcMonitor.ToPixelRect(),
            info.rcWork.ToPixelRect(),
            dpiX / 96.0,
            (info.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }

    private static List<MonitorDescriptor> Enumerate()
    {
        var handles = new List<nint>();
        GCHandle gc = GCHandle.Alloc(handles);
        try
        {
            EnumDisplayMonitors(0, 0, &EnumProc, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        var monitors = new List<MonitorDescriptor>(handles.Count);
        foreach (nint handle in handles)
        {
            if (Describe(handle) is { } descriptor)
            {
                monitors.Add(descriptor);
            }
        }

        return monitors;
    }

    [UnmanagedCallersOnly]
    private static int EnumProc(nint monitor, nint hdc, RECT* rect, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is List<nint> list)
        {
            list.Add(monitor);
        }

        return 1;
    }
}
