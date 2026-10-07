using System.Runtime.InteropServices;
using WinIsland.Core.Diagnostics;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Windowing;

/// <summary>
/// Subclasses the island HWND to receive system broadcasts (display/DPI/work-area/time
/// changes, resume, hotkeys, tray callbacks) and to refuse mouse activation. This is how the
/// app reacts to monitors being plugged in or resolution changes without polling.
/// </summary>
public sealed unsafe class WindowMessageHook : IDisposable
{
    private const nuint SubclassId = 0x57494E49; // "WINI"

    private readonly nint _hwnd;
    private readonly List<Func<uint, nint, nint, nint?>> _handlers = [];
    private GCHandle _self;

    public WindowMessageHook(nint hwnd)
    {
        _hwnd = hwnd;
        _self = GCHandle.Alloc(this);
        if (SetWindowSubclass(hwnd, &SubclassProc, SubclassId, (nuint)(nint)GCHandle.ToIntPtr(_self)) == 0)
        {
            _self.Free();
            throw new InvalidOperationException("SetWindowSubclass failed.");
        }
    }

    /// <summary>Monitors connected/disconnected or resolution changed.</summary>
    public event EventHandler? DisplayChanged;

    /// <summary>Work area changed (taskbar moved/resized/auto-hide toggled) or other system setting.</summary>
    public event EventHandler<string?>? SettingChanged;

    public event EventHandler? DpiChanged;

    public event EventHandler? TimeChanged;

    public event EventHandler? ResumedFromSleep;

    public event EventHandler<int>? HotkeyPressed;

    /// <summary>When true, clicks never activate the window (passive and non-text interaction).</summary>
    public bool RefuseMouseActivation { get; set; } = true;

    /// <summary>Adds a handler for custom messages (tray callbacks…). Return a value to handle the message.</summary>
    public void AddHandler(Func<uint, nint, nint, nint?> handler) => _handlers.Add(handler);

    public void Dispose()
    {
        if (_self.IsAllocated)
        {
            RemoveWindowSubclass(_hwnd, &SubclassProc, SubclassId);
            _self.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)refData).Target is WindowMessageHook hook &&
                hook.Handle(msg, wParam, lParam) is { } result)
            {
                return result;
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into native code.
            AppLog.Error(nameof(WindowMessageHook), $"Message 0x{msg:X4} handler failed", ex);
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private nint? Handle(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE when RefuseMouseActivation:
                return MA_NOACTIVATE;

            case WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke(this, EventArgs.Empty);
                break;

            case WM_SETTINGCHANGE:
                SettingChanged?.Invoke(this, lParam != 0 ? Marshal.PtrToStringUni(lParam) : (uint)wParam == SPI_SETWORKAREA ? "WorkArea" : null);
                break;

            case WM_DPICHANGED:
            {
                // Let WinUI update its rasterization scale first, then re-place the island.
                nint result = DefSubclassProc(_hwnd, msg, wParam, lParam);
                DpiChanged?.Invoke(this, EventArgs.Empty);
                return result;
            }

            case WM_TIMECHANGE:
                TimeChanged?.Invoke(this, EventArgs.Empty);
                break;

            case WM_POWERBROADCAST when wParam == PBT_APMRESUMEAUTOMATIC:
                ResumedFromSleep?.Invoke(this, EventArgs.Empty);
                break;

            case WM_HOTKEY:
                HotkeyPressed?.Invoke(this, (int)wParam);
                return 0;
        }

        foreach (Func<uint, nint, nint, nint?> handler in _handlers)
        {
            if (handler(msg, wParam, lParam) is { } handled)
            {
                return handled;
            }
        }

        return null;
    }
}
