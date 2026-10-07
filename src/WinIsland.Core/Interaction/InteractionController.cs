using WinIsland.Core.Threading;

namespace WinIsland.Core.Interaction;

/// <summary>
/// Decides when the island switches between passive (click-through) and interactive mode.
/// All members must be called on the UI thread; timers marshal back to it.
/// </summary>
/// <remarks>
/// Interactive mode is entered only through explicit intent:
/// <list type="bullet">
/// <item>the pointer rests on the island for <see cref="InteractionOptions.HoverDwell"/>, or</item>
/// <item><see cref="Activate"/> (hotkey / tray menu), which "pins" the island until dismissed.</item>
/// </list>
/// It ends when the pointer has been away for <see cref="InteractionOptions.LeaveGrace"/> (unless a
/// text field holds keyboard focus), on <see cref="Dismiss"/> (Escape), or when the user starts
/// working somewhere else (<see cref="OnOutsideActivity"/>).
/// </remarks>
public sealed class InteractionController : IDisposable
{
    private readonly OneShotTimer _dwellTimer;
    private readonly OneShotTimer _leaveTimer;
    private bool _pinned;
    private bool _suppressUntilPointerLeaves;

    public InteractionController(TimeProvider time, IUiDispatcher dispatcher, InteractionOptions? options = null)
    {
        Options = options ?? new InteractionOptions();
        _dwellTimer = new OneShotTimer(time, dispatcher, Enter);
        _leaveTimer = new OneShotTimer(time, dispatcher, Leave);
    }

    public event EventHandler<InteractionMode>? ModeChanged;

    public InteractionOptions Options { get; set; }

    public InteractionMode Mode { get; private set; } = InteractionMode.Passive;

    public bool IsPointerInside { get; private set; }

    public bool HasKeyboardFocus { get; private set; }

    public void PointerEntered()
    {
        IsPointerInside = true;
        _leaveTimer.Cancel();

        // Once the pointer is on the island, ordinary hover rules apply even after a hotkey activation.
        _pinned = false;

        if (Mode == InteractionMode.Passive && Options.HoverToInteract && !_suppressUntilPointerLeaves)
        {
            if (Options.HoverDwell <= TimeSpan.Zero)
            {
                Enter();
            }
            else
            {
                _dwellTimer.Start(Options.HoverDwell);
            }
        }
    }

    public void PointerExited()
    {
        IsPointerInside = false;
        _suppressUntilPointerLeaves = false;
        _dwellTimer.Cancel();
        ScheduleLeaveIfIdle();
    }

    /// <summary>Explicit activation (hotkey, tray). Stays interactive until dismissed or the user moves on.</summary>
    public void Activate()
    {
        _dwellTimer.Cancel();
        _leaveTimer.Cancel();
        _suppressUntilPointerLeaves = false;
        _pinned = !IsPointerInside;
        SetMode(InteractionMode.Interactive);
    }

    public void Toggle()
    {
        if (Mode == InteractionMode.Interactive)
        {
            Dismiss();
        }
        else
        {
            Activate();
        }
    }

    public void KeyboardFocusChanged(bool hasFocus)
    {
        HasKeyboardFocus = hasFocus;
        if (hasFocus)
        {
            _leaveTimer.Cancel();
        }
        else
        {
            ScheduleLeaveIfIdle();
        }
    }

    /// <summary>The user switched to another window or clicked elsewhere.</summary>
    public void OnOutsideActivity()
    {
        if (Mode == InteractionMode.Interactive && !IsPointerInside)
        {
            Dismiss();
        }
    }

    /// <summary>Ends interaction immediately (Escape, island hidden, …).</summary>
    public void Dismiss()
    {
        _dwellTimer.Cancel();
        _leaveTimer.Cancel();
        _pinned = false;
        HasKeyboardFocus = false;

        // Don't immediately re-enter because the pointer is still resting on the island.
        _suppressUntilPointerLeaves = IsPointerInside;
        SetMode(InteractionMode.Passive);
    }

    public void Dispose()
    {
        _dwellTimer.Dispose();
        _leaveTimer.Dispose();
    }

    private void ScheduleLeaveIfIdle()
    {
        if (Mode == InteractionMode.Interactive && !IsPointerInside && !HasKeyboardFocus && !_pinned)
        {
            _leaveTimer.Start(Options.LeaveGrace);
        }
    }

    private void Enter()
    {
        if (IsPointerInside || _pinned)
        {
            SetMode(InteractionMode.Interactive);
        }
    }

    private void Leave()
    {
        if (!IsPointerInside && !HasKeyboardFocus && !_pinned)
        {
            SetMode(InteractionMode.Passive);
        }
    }

    private void SetMode(InteractionMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        ModeChanged?.Invoke(this, mode);
    }
}
