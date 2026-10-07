using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Foreground;

/// <summary>
/// Event-driven tracking of the foreground window: activation changes, minimise/restore,
/// move/size loops (Snap Layouts) and position changes of the foreground window only.
/// The location hook is scoped to the foreground window's thread and re-created on every
/// activation change, so we never receive the system-wide flood of LOCATIONCHANGE events.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly List<WinEventHook> _hooks = [];
    private WinEventHook? _locationHook;
    private uint _locationThread;
    private nint _foreground;

    /// <summary>The foreground window changed to another (non-island) window.</summary>
    public event EventHandler? ForegroundSwitched;

    /// <summary>Anything that may change the visibility/placement decision. May fire in bursts; debounce it.</summary>
    public event EventHandler? Changed;

    public nint Foreground => _foreground;

    /// <summary>A window is being dragged or resized by the user.</summary>
    public bool IsMoveSizeActive { get; private set; }

    public void Start()
    {
        if (_hooks.Count > 0)
        {
            return;
        }

        _hooks.Add(new WinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, OnEvent));
        _hooks.Add(new WinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, OnEvent));
        _hooks.Add(new WinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, OnEvent));
        TrackForeground(GetForegroundWindow());
    }

    public void Dispose()
    {
        foreach (WinEventHook hook in _hooks)
        {
            hook.Dispose();
        }

        _hooks.Clear();
        _locationHook?.Dispose();
        _locationHook = null;
    }

    private unsafe void TrackForeground(nint hwnd)
    {
        _foreground = hwnd;
        uint thread = hwnd == 0 ? 0 : GetWindowThreadProcessId(hwnd, null);
        if (thread == _locationThread && _locationHook is not null)
        {
            return;
        }

        _locationHook?.Dispose();
        _locationHook = null;
        _locationThread = thread;
        if (thread != 0)
        {
            uint pid = GetProcessId(hwnd);
            _locationHook = new WinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, OnEvent, pid, thread);
        }
    }

    private void OnEvent(uint eventType, nint hwnd, int idObject, int idChild)
    {
        switch (eventType)
        {
            case EVENT_SYSTEM_FOREGROUND when hwnd != 0:
                TrackForeground(hwnd);
                ForegroundSwitched?.Invoke(this, EventArgs.Empty);
                break;

            case EVENT_SYSTEM_MOVESIZESTART:
                IsMoveSizeActive = true;
                break;

            case EVENT_SYSTEM_MOVESIZEEND:
                IsMoveSizeActive = false;
                break;

            case EVENT_OBJECT_LOCATIONCHANGE:
                // Only the foreground window itself (not carets, cursors or child controls).
                if (hwnd != _foreground || idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
                {
                    return;
                }

                break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
