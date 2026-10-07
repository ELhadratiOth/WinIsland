using System.Runtime.InteropServices;
using WinIsland.Core.Diagnostics;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Foreground;

/// <summary>
/// Out-of-context WinEvent hook. Callbacks arrive as queued messages on the thread that created
/// the hook (the UI thread), so handlers must stay cheap — they only record and schedule work.
/// </summary>
internal sealed unsafe class WinEventHook : IDisposable
{
    private static readonly Dictionary<nint, WinEventHook> s_hooks = [];
    private static readonly Lock s_gate = new();

    private readonly Action<uint, nint, int, int> _handler;
    private nint _handle;

    public WinEventHook(uint eventMin, uint eventMax, Action<uint, nint, int, int> handler, uint processId = 0, uint threadId = 0)
    {
        _handler = handler;
        _handle = SetWinEventHook(eventMin, eventMax, 0, &Callback, processId, threadId, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_handle == 0)
        {
            AppLog.Warn(nameof(WinEventHook), $"SetWinEventHook 0x{eventMin:X}-0x{eventMax:X} failed");
            return;
        }

        lock (s_gate)
        {
            s_hooks[_handle] = this;
        }
    }

    public bool IsActive => _handle != 0;

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        UnhookWinEvent(_handle);
        lock (s_gate)
        {
            s_hooks.Remove(_handle);
        }

        _handle = 0;
    }

    [UnmanagedCallersOnly]
    private static void Callback(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        WinEventHook? target;
        lock (s_gate)
        {
            s_hooks.TryGetValue(hook, out target);
        }

        if (target is null)
        {
            return;
        }

        try
        {
            target._handler(eventType, hwnd, idObject, idChild);
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(WinEventHook), "WinEvent handler failed", ex);
        }
    }
}
