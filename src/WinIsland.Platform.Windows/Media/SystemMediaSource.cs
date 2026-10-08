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
    private string? _pinnedId;
    private IReadOnlyList<MediaSessionInfo> _sessions = [];
    private MediaArtwork? _artwork;
    private int _propsEpoch;
    private int _propsReadEpoch;
    private CancellationTokenSource? _settleCts;
    private ITimer? _resync;
    private string? _trackKey;
    private DateTimeOffset _trackChangedAt;
    private DateTimeOffset? _staleTimelineUntil;

    // Players (Spotify above all) announce a new track before the cover and timeline are ready,
    // so a track change is re-read a few times while things settle.
    private static readonly TimeSpan[] SettleDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1300), TimeSpan.FromMilliseconds(3000)];

    // Safety net for players that forget to raise timeline events; only runs while something plays.
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(12);

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

    public IReadOnlyList<MediaSessionInfo> Sessions => Volatile.Read(ref _sessions);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSessionManager manager =
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _manager = manager;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            manager.SessionsChanged += OnSessionsChanged;
        }

        Attach(ResolveSession(manager));
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager = null;
            }

            DetachSession();
            _version++;
            _settleCts?.Cancel();
            _resync?.Dispose();
            _resync = null;
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

    public Task SeekAsync(TimeSpan position) => Invoke(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

    public Task SelectSessionAsync(string? sessionId)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate)
        {
            _pinnedId = string.IsNullOrEmpty(sessionId) ? null : sessionId;
            manager = _manager;
        }

        if (manager is not null)
        {
            Attach(ResolveSession(manager));
        }

        return Task.CompletedTask;
    }

    /// <summary>The pinned app while it's still around, else whatever Windows considers current.</summary>
    private GlobalSystemMediaTransportControlsSession? ResolveSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        string? pinned;
        lock (_gate)
        {
            pinned = _pinnedId;
        }

        if (pinned is not null)
        {
            foreach (GlobalSystemMediaTransportControlsSession candidate in manager.GetSessions())
            {
                if (candidate.SourceAppUserModelId == pinned)
                {
                    return candidate;
                }
            }

            lock (_gate)
            {
                _pinnedId = null;
            }
        }

        return manager.GetCurrentSession();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        // A pinned app may have closed; also refresh the picker list.
        Attach(ResolveSession(sender));
    }

    private void UpdateSessionList()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        GlobalSystemMediaTransportControlsSession? selected;
        lock (_gate)
        {
            manager = _manager;
            selected = _session;
        }

        if (manager is null)
        {
            Volatile.Write(ref _sessions, []);
            return;
        }

        var list = new List<MediaSessionInfo>();
        foreach (GlobalSystemMediaTransportControlsSession session in manager.GetSessions())
        {
            string id = session.SourceAppUserModelId ?? string.Empty;
            bool playing;
            try
            {
                playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch (Exception)
            {
                playing = false;
            }

            list.Add(new MediaSessionInfo(id, MediaSourceNames.Friendly(id), playing, id == selected?.SourceAppUserModelId));
        }

        Volatile.Write(ref _sessions, list);
    }

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
        Attach(ResolveSession(sender));

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (_gate)
        {
            DetachSession();
            _session = session;
            _propsEpoch++;
            if (session is not null)
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackChanged;
                session.TimelinePropertiesChanged += OnPlaybackChanged;
            }
        }

        _ = RefreshAsync(includeProperties: true);
        ScheduleSettle();
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

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        lock (_gate)
        {
            _propsEpoch++;
        }

        _ = RefreshAsync(includeProperties: true);
        ScheduleSettle();
    }

    private void ScheduleSettle()
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _settleCts?.Cancel();
            _settleCts = cts;
        }

        _ = SettleAsync(cts.Token);
    }

    private async Task SettleAsync(CancellationToken token)
    {
        try
        {
            foreach (TimeSpan delay in SettleDelays)
            {
                await Task.Delay(delay, _time, token).ConfigureAwait(false);
                await RefreshAsync(includeProperties: true, forceArtwork: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer track change took over.
        }
    }

    // Playback/timeline changes don't need the (async, heavier) media properties call.
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _ = RefreshAsync(includeProperties: false);

    private async Task RefreshAsync(bool includeProperties, bool forceArtwork = false)
    {
        GlobalSystemMediaTransportControlsSession? session;
        int version;
        int epoch;
        lock (_gate)
        {
            session = _session;
            version = ++_version;
            epoch = _propsEpoch;

            // A property change whose refresh was superseded by a newer one must not be lost:
            // whoever runs next re-reads the title and cover.
            includeProperties |= _propsReadEpoch != _propsEpoch;
        }

        if (session is null)
        {
            UpdateSessionList();
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
                artwork = await GetArtworkAsync(session.SourceAppUserModelId, title, artist, properties, forceArtwork).ConfigureAwait(false);
            }

            GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();

            DateTimeOffset sampled = timeline.LastUpdatedTime;
            DateTimeOffset now = _time.GetUtcNow();
            if (sampled == default || sampled > now)
            {
                sampled = now;
            }

            TimeSpan position = timeline.Position;
            string appId = session.SourceAppUserModelId ?? string.Empty;
            string trackKey = $"{appId}|{title}|{artist}";
            if (trackKey != _trackKey)
            {
                bool sameApp = previous is not null && previous.SourceAppId == appId;
                _trackKey = trackKey;
                _trackChangedAt = now;

                // The player announced the next song but hasn't refreshed its timeline yet: the old
                // position would show the new song as nearly over. Start from zero until it reports.
                if (sameApp && sampled < now - TimeSpan.FromSeconds(1) && previous!.Title != title)
                {
                    _staleTimelineUntil = sampled;
                }
            }

            if (_staleTimelineUntil is { } stale && sampled <= stale)
            {
                position = TimeSpan.Zero;
                sampled = _trackChangedAt;
            }
            else
            {
                _staleTimelineUntil = null;
            }

            var snapshot = new MediaSnapshot(
                title,
                artist,
                appId,
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                playback.Controls.IsNextEnabled,
                playback.Controls.IsPreviousEnabled,
                position,
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
                },
                playback.Controls.IsPlaybackPositionEnabled);

            lock (_gate)
            {
                // A newer refresh started meanwhile; drop this stale result.
                if (version != _version)
                {
                    return;
                }

                if (includeProperties)
                {
                    _propsReadEpoch = Math.Max(_propsReadEpoch, epoch);
                }
            }

            UpdateSessionList();
            Publish(snapshot);
        }
        catch (Exception ex)
        {
            // Players disappear mid-call all the time; just wait for the next event.
            AppLog.Warn(nameof(SystemMediaSource), "Reading media session failed", ex);
        }
    }

    /// <summary>Decodes cover art only when the track changes; the result is reused for timeline/playback updates.</summary>
    private async Task<MediaArtwork?> GetArtworkAsync(string? appId, string title, string artist, GlobalSystemMediaTransportControlsSessionMediaProperties? properties, bool force)
    {
        string key = $"{appId}|{title}|{artist}";
        bool sameTrack = key == _artworkKey;
        if (sameTrack && !force)
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

        if (sameTrack && _artwork is { } known)
        {
            // Re-reads of the same track: keep what we had when nothing better turned up or nothing changed.
            if (artwork is null || known.Image.AsSpan().SequenceEqual(artwork.Image))
            {
                return known;
            }
        }

        _artworkKey = key;
        _artwork = artwork;
        return artwork;
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        ArmResync(snapshot is { IsPlaying: true });
        if (Interlocked.Exchange(ref _current, snapshot) != snapshot)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ArmResync(bool playing)
    {
        lock (_gate)
        {
            _resync?.Dispose();
            _resync = playing && _manager is not null
                ? _time.CreateTimer(_ => _ = RefreshAsync(includeProperties: false), null, ResyncInterval, Timeout.InfiniteTimeSpan)
                : null;
        }
    }
}
