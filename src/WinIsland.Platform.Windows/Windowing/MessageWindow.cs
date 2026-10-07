using System.Runtime.InteropServices;
using WinIsland.Core.Diagnostics;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Windowing;

/// <summary>
/// A plain, never-shown Win32 window owned by the UI thread. Hosts the tray icon independently
/// of the island window, and (being top-level, unlike HWND_MESSAGE windows) still receives
/// broadcasts such as "TaskbarCreated" when Explorer restarts.
/// </summary>
public sealed unsafe class MessageWindow : IWindowMessageSource, IDisposable
{
    private static readonly Dictionary<nint, MessageWindow> s_windows = [];
    private readonly List<Func<uint, nint, nint, nint?>> _handlers = [];

    public MessageWindow(string className)
    {
        ArgumentException.ThrowIfNullOrEmpty(className);
        nint instance = GetModuleHandle(null);
        fixed (char* name = className)
        {
            var wc = new Interop.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Interop.WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = name,
            };

            // Returns 0 if the class already exists (e.g. after a restart within the process); that's fine.
            RegisterClassEx(&wc);
            Handle = CreateWindowEx((uint)WS_EX_TOOLWINDOW, name, name, WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        }

        if (Handle == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        }

        s_windows[Handle] = this;
    }

    public nint Handle { get; private set; }

    public void AddHandler(Func<uint, nint, nint, nint?> handler) => _handlers.Add(handler);

    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        s_windows.Remove(Handle);
        DestroyWindow(Handle);
        Handle = 0;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (s_windows.TryGetValue(hwnd, out MessageWindow? window))
            {
                foreach (Func<uint, nint, nint, nint?> handler in window._handlers)
                {
                    if (handler(msg, wParam, lParam) is { } result)
                    {
                        return result;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(MessageWindow), $"Message 0x{msg:X4} handler failed", ex);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
