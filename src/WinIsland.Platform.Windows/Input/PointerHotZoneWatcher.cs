using System.Runtime.InteropServices;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Geometry;
using WinIsland.Core.Threading;
using WinIsland.Platform.Windows.Interop;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Input;

/// <summary>
/// Detects the pointer entering/leaving the island while the window itself is click-through
/// (a click-through window receives no mouse messages at all).
/// </summary>
/// <remarks>
/// Uses a low-level mouse hook on a dedicated thread with its own message loop, so UI work
/// (layout, animation) can never delay system-wide mouse input. The callback only compares
/// the point to a rectangle and posts to the UI thread on enter/leave transitions.
/// The hook is installed only while the island is visible and hover activation is enabled —
/// it is removed entirely in fullscreen apps and games.
/// </remarks>
public sealed unsafe class PointerHotZoneWatcher : IDisposable
{
    private const uint WM_RECHECK = WM_APP + 0x10;

    private static PointerHotZoneWatcher? s_active;

    private readonly IUiDispatcher _dispatcher;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private HotZone? _zone;
    private bool _inside; // hook thread only

    public PointerHotZoneWatcher(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Raised on the UI thread with true on enter and false on leave.</summary>
    public event EventHandler<bool>? InsideChanged;

    public bool IsRunning => _thread is not null;

    /// <summary>Sets the rectangle (physical pixels) that counts as "on the island". Null disables detection.</summary>
    public void SetHotZone(PixelRect? zone)
    {
        HotZone? next = zone is { IsEmpty: false } z ? new HotZone(z) : null;
        if (Volatile.Read(ref _zone)?.Rect == next?.Rect)
        {
            return;
        }

        Volatile.Write(ref _zone, next);

        // The island moved/resized under a stationary pointer: re-evaluate without waiting for a move.
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_RECHECK, 0, 0);
        }
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref s_active, this, null) is not null)
        {
            throw new InvalidOperationException("Only one pointer watcher can be active.");
        }

        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "WinIsland pointer watcher",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        Thread? thread = _thread;
        if (thread is null)
        {
            return;
        }

        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        }

        thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _threadId = 0;
        Interlocked.CompareExchange(ref s_active, null, this);
    }

    public void Dispose() => Stop();

    private void Run(ManualResetEventSlim ready)
    {
        MSG msg;

        // Force creation of this thread's message queue before anyone posts to it.
        PeekMessage(&msg, 0, 0, 0, PM_NOREMOVE);
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, &HookProc, GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            AppLog.Warn(nameof(PointerHotZoneWatcher), "SetWindowsHookEx(WH_MOUSE_LL) failed; hover activation disabled");
        }

        ready.Set();
        _inside = false;
        Recheck();

        while (GetMessage(&msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_RECHECK)
            {
                Recheck();
                continue;
            }

            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }

        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }

        if (_inside)
        {
            _inside = false;
            Post(false);
        }
    }

    private void Recheck()
    {
        POINT p;
        if (GetCursorPos(&p) != 0)
        {
            Evaluate(p.X, p.Y);
        }
    }

    private void Evaluate(int x, int y)
    {
        HotZone? zone = Volatile.Read(ref _zone);
        bool inside = zone is not null && zone.Rect.Contains(x, y);
        if (inside != _inside)
        {
            _inside = inside;
            Post(inside);
        }
    }

    private void Post(bool inside) => _dispatcher.TryEnqueue(() => InsideChanged?.Invoke(this, inside));

    [UnmanagedCallersOnly]
    private static nint HookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (uint)wParam == WM_MOUSEMOVE && s_active is { } watcher)
        {
            MSLLHOOKSTRUCT* data = (MSLLHOOKSTRUCT*)lParam;
            watcher.Evaluate(data->pt.X, data->pt.Y);
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private sealed record HotZone(PixelRect Rect);
}
