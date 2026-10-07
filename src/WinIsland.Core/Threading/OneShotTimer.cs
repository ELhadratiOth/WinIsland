namespace WinIsland.Core.Threading;

/// <summary>
/// A re-armable one-shot timer whose callback runs on the UI dispatcher. Used for debouncing,
/// hover dwell, attention timeouts etc. Nothing in the app runs a periodic polling timer;
/// everything schedules exactly the next wake-up it needs.
/// </summary>
public sealed class OneShotTimer : IDisposable
{
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _callback;
    private readonly object _gate = new();
    private ITimer? _timer;
    private long _generation;
    private bool _disposed;

    public OneShotTimer(TimeProvider time, IUiDispatcher dispatcher, Action callback)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _timer is not null;
            }
        }
    }

    /// <summary>(Re)starts the timer. A pending callback from an earlier start is discarded.</summary>
    public void Start(TimeSpan dueTime)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer?.Dispose();
            long generation = ++_generation;
            _timer = _time.CreateTimer(_ => Fire(generation), null, dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime, Timeout.InfiniteTimeSpan);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _generation++;
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _generation++;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Fire(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _timer?.Dispose();
            _timer = null;
        }

        _dispatcher.TryEnqueue(() =>
        {
            // Re-check: a Cancel() may have run on the UI thread between Fire and this callback.
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }
            }

            _callback();
        });
    }
}
