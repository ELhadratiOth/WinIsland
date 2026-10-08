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

    public Task SetShuffleAsync(bool active)
    {
        if (Current is { } c)
        {
            Set(c with { IsShuffleActive = active });
        }

        return Task.CompletedTask;
    }

    public Task SetRepeatModeAsync(MediaRepeatMode mode)
    {
        if (Current is { } c)
        {
            Set(c with { RepeatMode = mode });
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<MediaSessionInfo> Sessions { get; private set; } = [];

    public void SetSessions(params MediaSessionInfo[] sessions)
    {
        Sessions = sessions;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task SeekAsync(TimeSpan position)
    {
        if (Current is { } c)
        {
            Set(c with { Position = position, PositionSampledAt = DateTimeOffset.UtcNow });
        }

        return Task.CompletedTask;
    }

    public Task SelectSessionAsync(string? sessionId)
    {
        SetSessions([.. Sessions.Select(s => s with { IsSelected = s.Id == sessionId })]);
        return Task.CompletedTask;
    }
}
