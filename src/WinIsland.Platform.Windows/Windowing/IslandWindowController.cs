using WinIsland.Core.Geometry;
using WinIsland.Platform.Windows.Interop;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Windowing;

/// <summary>
/// Applies the overlay window behaviour to the island's HWND:
/// <list type="bullet">
/// <item>never activates on show/click (<c>WS_EX_NOACTIVATE</c>, <c>SW_SHOWNOACTIVATE</c>, <c>SWP_NOACTIVATE</c>);</item>
/// <item>click-through while passive (<c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c>);</item>
/// <item>always above normal windows, hidden from Alt+Tab/taskbar (<c>WS_EX_TOOLWINDOW</c>);</item>
/// <item>takes keyboard focus only on explicit request, and hands it back afterwards.</item>
/// </list>
/// Must be used on the UI thread.
/// </summary>
public sealed unsafe class IslandWindowController
{
    private nint _previousForeground;

    public IslandWindowController(nint hwnd)
    {
        if (hwnd == 0)
        {
            throw new ArgumentException("Window handle required.", nameof(hwnd));
        }

        Handle = hwnd;
    }

    public nint Handle { get; }

    public bool IsClickThrough { get; private set; }

    public bool IsActivatable { get; private set; } = true;

    public bool IsShown { get; private set; }

    public PixelRect Bounds { get; private set; }

    public bool HasKeyboardFocus => GetForegroundWindow() == Handle;

    public void ApplyOverlayStyles()
    {
        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOPMOST;
        ex &= ~WS_EX_APPWINDOW;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, (nint)ex);
        IsActivatable = false;

        // A layered window is invisible until its attributes are set; 255 = fully opaque,
        // per-pixel transparency still comes from the composition content.
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);

        // No Windows 11 rounded frame/border (the island draws its own shape), no DWM
        // show/hide animation (ours are faster), and don't fade it during Aero Peek.
        int corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(int));
        uint border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, &border, sizeof(uint));
        int enabled = 1;
        DwmSetWindowAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, &enabled, sizeof(int));
        DwmSetWindowAttribute(Handle, DWMWA_EXCLUDED_FROM_PEEK, &enabled, sizeof(int));

        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        SetClickThrough(true);
    }

    /// <summary>Passive mode: every click goes to the window underneath.</summary>
    public void SetClickThrough(bool clickThrough)
    {
        if (IsClickThrough == clickThrough)
        {
            return;
        }

        IsClickThrough = clickThrough;
        UpdateExStyle(WS_EX_TRANSPARENT, clickThrough);
    }

    public void SetBounds(PixelRect bounds)
    {
        if (bounds == Bounds)
        {
            return;
        }

        Bounds = bounds;
        SetWindowPos(Handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void Show()
    {
        if (IsShown)
        {
            return;
        }

        IsShown = true;
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        EnsureTopmost();
    }

    public void Hide()
    {
        if (!IsShown)
        {
            return;
        }

        IsShown = false;
        ShowWindow(Handle, SW_HIDE);
    }

    /// <summary>Other topmost windows can end up above us; re-assert without activating.</summary>
    public void EnsureTopmost()
    {
        if (IsShown)
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
    }

    /// <summary>
    /// Makes the island the foreground window so a text field can receive keystrokes.
    /// Only call in direct response to user input (a click in the island or the hotkey) —
    /// that is also what makes Windows permit the foreground change.
    /// </summary>
    public bool TakeKeyboardFocus()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == Handle)
        {
            return true;
        }

        _previousForeground = foreground;
        SetActivatable(true);
        return SetForegroundWindow(Handle) != 0;
    }

    /// <summary>Returns keyboard focus to whatever the user was working in before.</summary>
    public void ReleaseKeyboardFocus()
    {
        nint previous = _previousForeground;
        _previousForeground = 0;
        if (GetForegroundWindow() == Handle && previous != 0 && IsWindow(previous) != 0)
        {
            SetForegroundWindow(previous);
        }

        SetActivatable(false);
    }

    private void SetActivatable(bool activatable)
    {
        if (IsActivatable == activatable)
        {
            return;
        }

        IsActivatable = activatable;
        UpdateExStyle(WS_EX_NOACTIVATE, !activatable);
    }

    private void UpdateExStyle(long flag, bool set)
    {
        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE);
        long updated = set ? ex | flag : ex & ~flag;
        if (updated != ex)
        {
            SetWindowLongPtr(Handle, GWL_EXSTYLE, (nint)updated);
        }
    }
}
