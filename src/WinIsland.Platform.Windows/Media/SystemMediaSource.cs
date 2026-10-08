using Windows.Media;
using Windows.Media.Control;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;
using WinIsland.Core.Media;

namespace WinIsland.Platform.Windows.Media;

/// <summary>
/// Now playing via Windows.Media.Control (the same source as the Windows volume flyout).
/// Purely event-driven: session, media-property, playback and timeline change events.
/// </summary>
public sealed class SystemMediaSource : IMediaSource, IIntegration
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaSnapshot? _current;
    private int _version;
    private string? _artworkKey;
    private MediaArtwork? _artwork;

    private readonly Func<bool> _onlineLookupEnabled;
    private readonly OnlineArtworkLookup _onlineLookup = new();

    public SystemMediaSource(TimeProvider time, Func<bool>? onlineLookupEnabled = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _onlineLookupEnabled = onlineLookupEnabled ?? (() => false);
    }

    public event EventHandler? Changed;

    public string Name => "Media";

    public bool RequiresNetwork => false;

    public MediaSnapshot? Current => Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSessionManager manager =
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _manager = manager;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
        }

        Attach(manager.GetCurrentSession());
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager = null;
            }

            DetachSession();
            _version++;
        }

        Publish(null);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public Task TogglePlayPauseAsync() => Invoke(s => s.TryTogglePlayPauseAsync().AsTask());

    public Task NextAsync() => Invoke(s => s.TrySkipNextAsync().AsTask());

    public Task PreviousAsync() => Invoke(s => s.TrySkipPreviousAsync().AsTask());

    public Task SetShuffleAsync(bool active) => Invoke(s => s.TryChangeShuffleActiveAsync(active).AsTask());

    public Task SetRepeatModeAsync(MediaRepeatMode mode) => Invoke(s => s.TryChangeAutoRepeatModeAsync(mode switch
    {
        MediaRepeatMode.Track => MediaPlaybackAutoRepeatMode.Track,
        MediaRepeatMode.List => MediaPlaybackAutoRepeatMode.List,
        _ => MediaPlaybackAutoRepeatMode.None,
    }).AsTask());

    private Task Invoke(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate)
        {
            session = _session;
        }

        return session is null ? Task.CompletedTask : action(session);
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        Attach(sender.GetCurrentSession());

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (_gate)
        {
            DetachSession();
            _session = session;
            if (session is not null)
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackChanged;
                session.TimelinePropertiesChanged += OnPlaybackChanged;
            }
        }

        _ = RefreshAsync(includeProperties: true);
    }

    // Caller holds _gate.
    private void DetachSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackChanged;
        _session.TimelinePropertiesChanged -= OnPlaybackChanged;
        _session = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ = RefreshAsync(includeProperties: true);

    // Playback/timeline changes don't need the (async, heavier) media properties call.
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _ = RefreshAsync(includeProperties: false);

    private async Task RefreshAsync(bool includeProperties)
    {
        GlobalSystemMediaTransportControlsSession? session;
        int version;
        lock (_gate)
        {
            session = _session;
            version = ++_version;
        }

        if (session is null)
        {
            Publish(null);
            return;
        }

        try
        {
            MediaSnapshot? previous = Current;
            string title = previous?.Title ?? string.Empty;
            string artist = previous?.Artist ?? string.Empty;
            MediaArtwork? artwork = previous?.Artwork;
            string? album = previous?.Album;
            if (includeProperties || previous is null)
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties properties =
                    await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
                title = properties?.Title ?? string.Empty;
                artist = properties?.Artist ?? string.Empty;
                album = string.IsNullOrWhiteSpace(properties?.AlbumTitle) ? null : properties.AlbumTitle;
                artwork = await GetArtworkAsync(session.SourceAppUserModelId, title, artist, properties).ConfigureAwait(false);
            }

            GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();

            DateTimeOffset sampled = timeline.LastUpdatedTime;
            DateTimeOffset now = _time.GetUtcNow();
            if (sampled == default || sampled > now)
            {
                sampled = now;
            }

            var snapshot = new MediaSnapshot(
                title,
                artist,
                session.SourceAppUserModelId ?? string.Empty,
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                playback.Controls.IsNextEnabled,
                playback.Controls.IsPreviousEnabled,
                timeline.Position,
                timeline.EndTime - timeline.StartTime,
                sampled,
                artwork,
                album,
                playback.Controls.IsShuffleEnabled,
                playback.IsShuffleActive ?? false,
                playback.Controls.IsRepeatEnabled,
                playback.AutoRepeatMode switch
                {
                    MediaPlaybackAutoRepeatMode.Track => MediaRepeatMode.Track,
                    MediaPlaybackAutoRepeatMode.List => MediaRepeatMode.List,
                    _ => MediaRepeatMode.None,
                });

            lock (_gate)
            {
                // A newer refresh started meanwhile; drop this stale result.
                if (version != _version)
                {
                    return;
                }
            }

            Publish(snapshot);
        }
        catch (Exception ex)
        {
            // Players disappear mid-call all the time; just wait for the next event.
            AppLog.Warn(nameof(SystemMediaSource), "Reading media session failed", ex);
        }
    }

    /// <summary>Decodes cover art only when the track changes; the result is reused for timeline/playback updates.</summary>
    private async Task<MediaArtwork?> GetArtworkAsync(string? appId, string title, string artist, GlobalSystemMediaTransportControlsSessionMediaProperties? properties)
    {
        string key = $"{appId}|{title}|{artist}";
        if (key == _artworkKey)
        {
            return _artwork;
        }

        MediaArtwork? artwork = null;
        try
        {
            artwork = await ArtworkLoader.LoadAsync(properties?.Thumbnail).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(SystemMediaSource), "Could not load cover art", ex);
        }

        // The player gave no cover (common for browser tabs): look up the real one online.
        if (artwork is null && _onlineLookupEnabled())
        {
            artwork = await _onlineLookup.FindAsync(artist, title).ConfigureAwait(false);
        }

        _artworkKey = key;
        _artwork = artwork;
        return artwork;
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        if (Interlocked.Exchange(ref _current, snapshot) != snapshot)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
