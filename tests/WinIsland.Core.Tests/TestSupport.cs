using WinIsland.Core.Display;
using WinIsland.Core.Geometry;
using WinIsland.Core.Media;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Tests;

/// <summary>Runs dispatched work inline, standing in for the UI thread.</summary>
internal sealed class InlineDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public bool TryEnqueue(Action action)
    {
        action();
        return true;
    }
}

internal static class Monitors
{
    public static MonitorDescriptor Primary1080p(double scale = 1.0) =>
        new("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), scale, IsPrimary: true);

    public static MonitorDescriptor SecondaryLeft4k() =>
        new("\\\\.\\DISPLAY2", new PixelRect(-3840, -200, 3840, 2160), new PixelRect(-3840, -200, 3840, 2112), 2.0, IsPrimary: false);
}

internal sealed class FakeMediaSource : IMediaSource
{
    public MediaSnapshot? Current { get; private set; }

    public event EventHandler? Changed;

    public int ToggleCount { get; private set; }

    public void Set(MediaSnapshot? snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task TogglePlayPauseAsync()
    {
        ToggleCount++;
        return Task.CompletedTask;
    }

    public Task NextAsync() => Task.CompletedTask;

    public Task PreviousAsync() => Task.CompletedTask;

    public List<bool> ShuffleRequests { get; } = [];

    public List<MediaRepeatMode> RepeatRequests { get; } = [];

    public Task SetShuffleAsync(bool active)
    {
        ShuffleRequests.Add(active);
        return Task.CompletedTask;
    }

    public Task SetRepeatModeAsync(MediaRepeatMode mode)
    {
        RepeatRequests.Add(mode);
        return Task.CompletedTask;
    }
}

internal static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting: {because}");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
