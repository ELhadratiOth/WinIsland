using WinIsland.Core.Media;

namespace WinIsland.App.Services.Preview;

/// <summary>Scripted now-playing data for <c>--preview</c>.</summary>
internal sealed class PreviewMediaSource : IMediaSource
{
    public MediaSnapshot? Current { get; private set; }

    public event EventHandler? Changed;

    public void Set(MediaSnapshot? snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task TogglePlayPauseAsync()
    {
        if (Current is { } c)
        {
            Set(c with { IsPlaying = !c.IsPlaying, PositionSampledAt = DateTimeOffset.UtcNow });
        }

        return Task.CompletedTask;
    }

    public Task NextAsync() => Task.CompletedTask;

    public Task PreviousAsync() => Task.CompletedTask;
}
