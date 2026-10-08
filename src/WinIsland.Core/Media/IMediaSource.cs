namespace WinIsland.Core.Media;

/// <summary>Event-driven now-playing source (Windows.Media.Control on Windows).</summary>
public interface IMediaSource
{
    /// <summary>The current session, or null when nothing is playing/paused.</summary>
    MediaSnapshot? Current { get; }

    /// <summary>Raised on any thread when <see cref="Current"/> changes.</summary>
    event EventHandler? Changed;

    Task TogglePlayPauseAsync();

    Task NextAsync();

    Task PreviousAsync();

    Task SetShuffleAsync(bool active);

    Task SetRepeatModeAsync(MediaRepeatMode mode);

    Task SeekAsync(TimeSpan position);

    /// <summary>Every app currently exposing media controls (Spotify, a browser tab…).</summary>
    IReadOnlyList<MediaSessionInfo> Sessions { get; }

    /// <summary>
    /// Follow a specific app instead of the one Windows considers current; null returns to
    /// automatic. A pinned app that closes falls back to automatic.
    /// </summary>
    Task SelectSessionAsync(string? sessionId);
}
