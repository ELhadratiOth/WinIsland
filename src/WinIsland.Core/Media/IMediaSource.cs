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
}
