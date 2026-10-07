namespace WinIsland.Core.Threading;

/// <summary>
/// Runs a UI-thread callback at most once per interval while triggers keep arriving
/// (trailing edge). Unlike <see cref="Debouncer"/>, a continuous stream of events — such as
/// dragging a window — still produces regular callbacks instead of starving them.
/// </summary>
public sealed class Throttler : IDisposable
{
    private readonly OneShotTimer _timer;
    private readonly TimeSpan _interval;

    public Throttler(TimeProvider time, IUiDispatcher dispatcher, TimeSpan interval, Action callback)
    {
        _interval = interval;
        _timer = new OneShotTimer(time, dispatcher, callback);
    }

    public void Trigger()
    {
        if (!_timer.IsPending)
        {
            _timer.Start(_interval);
        }
    }

    public void Cancel() => _timer.Cancel();

    public void Dispose() => _timer.Dispose();
}
