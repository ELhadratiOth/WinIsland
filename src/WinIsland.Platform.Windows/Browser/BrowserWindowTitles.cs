using System.Runtime.InteropServices;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Browser;

/// <summary>Titles of visible browser windows (they show the active tab's title, e.g. "Song - YouTube - Google Chrome").</summary>
internal static unsafe class BrowserWindowTitles
{
    public static IReadOnlyList<string> Read()
    {
        var titles = new List<string>();
        GCHandle handle = GCHandle.Alloc(titles);
        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return titles;
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint hwnd, nint lParam)
    {
        if (IsWindowVisible(hwnd) == 0)
        {
            return 1;
        }

        char* className = stackalloc char[64];
        int classLength = GetClassName(hwnd, className, 64);
        string cls = new(className, 0, Math.Max(classLength, 0));

        // Chromium browsers (Chrome, Edge, Brave, Opera, Vivaldi) and Firefox.
        if (cls != "Chrome_WidgetWin_1" && cls != "MozillaWindowClass")
        {
            return 1;
        }

        char* text = stackalloc char[512];
        int length = GetWindowText(hwnd, text, 512);
        if (length > 0 && GCHandle.FromIntPtr(lParam).Target is List<string> list)
        {
            list.Add(new string(text, 0, length));
        }

        return 1;
    }
}
