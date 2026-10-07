using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Tests;

public class TimerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly InlineDispatcher _dispatcher = new();

    [Fact]
    public void Debouncer_fires_once_after_the_burst_ends()
    {
        int calls = 0;
        using var debouncer = new Debouncer(_time, _dispatcher, TimeSpan.FromMilliseconds(50), () => calls++);

        for (int i = 0; i < 10; i++)
        {
            debouncer.Trigger();
            _time.Advance(TimeSpan.FromMilliseconds(20));
        }

        Assert.Equal(0, calls);
        _time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Throttler_keeps_firing_during_a_continuous_stream()
    {
        int calls = 0;
        using var throttler = new Throttler(_time, _dispatcher, TimeSpan.FromMilliseconds(50), () => calls++);

        // 500 ms of events every 10 ms, like a window being dragged.
        for (int i = 0; i < 50; i++)
        {
            throttler.Trigger();
            _time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.InRange(calls, 9, 10);
    }

    [Fact]
    public void Cancelled_timer_never_fires()
    {
        int calls = 0;
        using var timer = new OneShotTimer(_time, _dispatcher, () => calls++);

        timer.Start(TimeSpan.FromMilliseconds(10));
        timer.Cancel();
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, calls);
        Assert.False(timer.IsPending);
    }

    [Fact]
    public void Restarting_discards_the_earlier_schedule()
    {
        int calls = 0;
        using var timer = new OneShotTimer(_time, _dispatcher, () => calls++);

        timer.Start(TimeSpan.FromMilliseconds(10));
        timer.Start(TimeSpan.FromMilliseconds(100));
        _time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(0, calls);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, calls);
    }
}
