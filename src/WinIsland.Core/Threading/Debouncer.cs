namespace WinIsland.Core.Threading;

/// <summary>Coalesces bursts of events (window moves, file writes…) into a single UI-thread callback.</summary>
public sealed class Debouncer : IDisposable
{
    private readonly OneShotTimer _timer;
    private readonly TimeSpan _delay;

    public Debouncer(TimeProvider time, IUiDispatcher dispatcher, TimeSpan delay, Action callback)
    {
        _delay = delay;
        _timer = new OneShotTimer(time, dispatcher, callback);
    }

    public void Trigger() => _timer.Start(_delay);

    public void Cancel() => _timer.Cancel();

    public void Dispose() => _timer.Dispose();
}
