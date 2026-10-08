using System.Windows.Input;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Geometry;
using WinIsland.Core.Layout;
using WinIsland.Core.Media;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

/// <summary>
/// Now playing. Updates only on media events; the progress bar is interpolated locally and
/// only while the expanded view is actually on screen.
/// </summary>
public sealed class MediaModule : IslandModule
{
    public const string ModuleId = "media";

    private static readonly TimeSpan TrackChangeAttention = TimeSpan.FromSeconds(3);

    private readonly ILyricsProvider? _lyricsProvider;
    private readonly IMusicLibrary? _library;
    private readonly IBrowserReactions? _reactions;
    private bool _youtubeHint;
    private bool _isSpotify;
    private bool _isBrowser;
    private string _reactionStatus = string.Empty;
    private readonly OneShotTimer _lyricsTimer;
    private CancellationTokenSource? _trackCts;
    private string _trackKey = string.Empty;
    private Lyrics? _lyrics;
    private bool _lyricsLoading;
    private bool _showLyrics;
    private int _lyricIndex = int.MinValue;
    private string? _trackId;
    private bool _isLiked;
    private bool _scrubbing;
    private IReadOnlyList<MediaSessionInfo> _sessions = [];

    private readonly IMediaSource _source;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly OneShotTimer _progressTimer;
    private MediaSnapshot? _snapshot;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private bool _isPlaying;
    private double _progress;
    private string _positionText = string.Empty;
    private string _durationText = string.Empty;
    private string _album = string.Empty;
    private string _sourceName = string.Empty;
    private byte[]? _artwork;
    private uint _accentColor = AccentPicker.Neutral;
    private bool _canShuffle;
    private bool _isShuffleActive;
    private bool _canRepeat;
    private MediaRepeatMode _repeatMode;

    public MediaModule(IMediaSource source, TimeProvider time, IUiDispatcher dispatcher, ILyricsProvider? lyrics = null, IMusicLibrary? library = null, IBrowserReactions? reactions = null)
        : base(ModuleId, "Media", "\uEC4F")
    {
        _lyricsProvider = lyrics;
        _library = library;
        _reactions = reactions;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _progressTimer = new OneShotTimer(time, dispatcher, TickProgress);

        TogglePlayPauseCommand = new AsyncRelayCommand(_ => _source.TogglePlayPauseAsync(), onError: LogError);
        NextCommand = new AsyncRelayCommand(_ => _source.NextAsync(), () => _snapshot?.CanGoNext ?? false, LogError);
        PreviousCommand = new AsyncRelayCommand(_ => _source.PreviousAsync(), () => _snapshot?.CanGoPrevious ?? false, LogError);
        ToggleShuffleCommand = new AsyncRelayCommand(_ => ToggleShuffleAsync(), () => CanShuffle, LogError);
        CycleRepeatCommand = new AsyncRelayCommand(_ => CycleRepeatAsync(), () => CanRepeat, LogError);
        _lyricsTimer = new OneShotTimer(time, dispatcher, UpdateLyricLine);
        ToggleLyricsCommand = new RelayCommand(() => ShowLyrics = !ShowLyrics, () => _lyricsProvider is not null);
        ToggleLikeCommand = new AsyncRelayCommand(_ => ToggleLikeAsync(), () => ShowHeart, LogError);
        ThumbUpCommand = new AsyncRelayCommand(_ => ReactAsync(BrowserReaction.Like), () => ShowThumbs, LogError);
        ThumbDownCommand = new AsyncRelayCommand(_ => ReactAsync(BrowserReaction.Dislike), () => ShowThumbs, LogError);
        SelectSessionCommand = new RelayCommandOf<string>(id => _ = Run(_source.SelectSessionAsync(id)));
        if (_library is not null)
        {
            _library.ConnectionChanged += OnLibraryConnectionChanged;
        }

        if (_reactions is not null)
        {
            _reactions.Changed += OnReactionsChanged;
        }

        _source.Changed += OnSourceChanged;
        Apply(_source.Current);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Artist
    {
        get => _artist;
        private set => SetProperty(ref _artist, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseGlyph));
            }
        }
    }

    public string Album
    {
        get => _album;
        private set
        {
            if (SetProperty(ref _album, value))
            {
                OnPropertyChanged(nameof(HasAlbum));
            }
        }
    }

    public bool HasAlbum => _album.Length > 0;

    /// <summary>Friendly name of the playing app, e.g. "Spotify".</summary>
    public string SourceName
    {
        get => _sourceName;
        private set
        {
            if (SetProperty(ref _sourceName, value))
            {
                OnPropertyChanged(nameof(SourceCaption));
            }
        }
    }

    /// <summary>"PLAYING ON SPOTIFY" (or "NOW PLAYING" when the app is unknown).</summary>
    public string SourceCaption => _sourceName.Length > 0 ? $"PLAYING ON {_sourceName.ToUpperInvariant()}" : "NOW PLAYING";

    /// <summary>Downscaled cover art (PNG/JPEG bytes), or null when the player provides none.</summary>
    public byte[]? Artwork
    {
        get => _artwork;
        private set
        {
            if (SetProperty(ref _artwork, value))
            {
                OnPropertyChanged(nameof(HasArtwork));
            }
        }
    }

    public bool HasArtwork => _artwork is not null;

    /// <summary>ARGB accent derived from the cover art; tints the waveform and progress bar.</summary>
    public uint AccentColor
    {
        get => _accentColor;
        private set => SetProperty(ref _accentColor, value);
    }

    /// <summary>True when the player accepts shuffle changes (many browser tabs don't).</summary>
    public bool CanShuffle
    {
        get => _canShuffle;
        private set => SetProperty(ref _canShuffle, value);
    }

    public bool IsShuffleActive
    {
        get => _isShuffleActive;
        private set => SetProperty(ref _isShuffleActive, value);
    }

    public bool CanRepeat
    {
        get => _canRepeat;
        private set => SetProperty(ref _canRepeat, value);
    }

    public MediaRepeatMode RepeatMode
    {
        get => _repeatMode;
        private set
        {
            if (SetProperty(ref _repeatMode, value))
            {
                OnPropertyChanged(nameof(IsRepeatActive));
                OnPropertyChanged(nameof(RepeatGlyph));
            }
        }
    }

    public bool IsRepeatActive => _repeatMode != MediaRepeatMode.None;

    /// <summary>Segoe Fluent Icons RepeatOne for a single track, RepeatAll otherwise.</summary>
    public string RepeatGlyph => _repeatMode == MediaRepeatMode.Track ? "\uE8ED" : "\uE8EE";

    public override DipSize GetSize(IslandSize size) => size switch
    {
        // Room for the time (and temperature) between the cover and the waveform.
        IslandSize.Compact => IslandMetrics.CompactWide with { Width = Clock?.HasWeather == true ? 276 : 244 },
        IslandSize.Expanded => new DipSize(460, 206),
        IslandSize.Large => new DipSize(460, 300),
        _ => base.GetSize(size),
    };

    /// <summary>The clock whose time and weather share the compact pill while music plays.</summary>
    public ClockModule? Clock { get; set; }

    /// <summary>The lyrics view is the media module's large size.</summary>
    public override IslandSize InteractiveSize => _showLyrics ? IslandSize.Large : IslandSize.Expanded;

    // ---- Seeking ----

    public bool CanSeek => _snapshot?.CanSeek ?? false;

    /// <summary>The user grabbed the progress bar: stop moving it under their finger.</summary>
    public void BeginScrub() => _scrubbing = true;

    /// <summary>Live position label while dragging (0–100).</summary>
    public void Scrub(double percent)
    {
        if (_scrubbing && _snapshot is { Duration: var d } && d > TimeSpan.Zero)
        {
            PositionText = Format(d * Math.Clamp(percent / 100, 0, 1));
        }
    }

    public void EndScrub(double percent)
    {
        if (!_scrubbing)
        {
            return;
        }

        _scrubbing = false;
        if (CanSeek && _snapshot is { Duration: var d } && d > TimeSpan.Zero)
        {
            TimeSpan target = d * Math.Clamp(percent / 100, 0, 1);
            Progress = Math.Clamp(percent, 0, 100);
            PositionText = Format(target);
            _ = Run(_source.SeekAsync(target));
        }
        else
        {
            UpdateProgress();
        }
    }

    // ---- Player picker ----

    public IReadOnlyList<MediaSessionInfo> Sessions
    {
        get => _sessions;
        private set
        {
            if (!_sessions.SequenceEqual(value))
            {
                _sessions = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasMultipleSessions));
            }
        }
    }

    public bool HasMultipleSessions => _sessions.Count > 1;

    /// <summary>Parameter: a session id, or an empty string for automatic.</summary>
    public RelayCommandOf<string> SelectSessionCommand { get; }

    // ---- Lyrics ----

    public bool ShowLyrics
    {
        get => _showLyrics;
        set
        {
            if (SetProperty(ref _showLyrics, value))
            {
                NotifyPresentationChanged();
                UpdateLyricLine();
            }
        }
    }

    public RelayCommand ToggleLyricsCommand { get; }

    public bool HasLyrics => _lyrics is not null;

    public bool HasSyncedLyrics => _lyrics is { IsSynced: true };

    public bool HasPlainLyrics => _lyrics is { IsSynced: false };

    /// <summary>"Looking for lyrics…" / "No lyrics for this song".</summary>
    public string LyricsStatus => _lyricsLoading ? "Looking for lyrics…" : _lyrics is null ? "No lyrics found for this song" : string.Empty;

    public bool ShowLyricsStatus => LyricsStatus.Length > 0;

    public string LyricPrevious { get; private set; } = string.Empty;

    public string LyricCurrent { get; private set; } = string.Empty;

    public string LyricNext { get; private set; } = string.Empty;

    public string LyricNext2 { get; private set; } = string.Empty;

    /// <summary>Full text for lyrics without timestamps.</summary>
    public string LyricsPlainText => _lyrics is { IsSynced: false } l ? string.Join("\n", l.Lines.Select(x => x.Text)) : string.Empty;

    // ---- Like ----

    /// <summary>Asked (on the UI thread) when the heart is pressed before Spotify is connected.</summary>
    public Action? SpotifyConnectRequested { get; set; }

    /// <summary>The heart shows whenever Spotify is the player; it saves to Liked Songs once connected.</summary>
    public bool ShowHeart => _isSpotify;

    public bool CanLike => _trackId is not null && (_library?.IsConnected ?? false);

    public bool IsLiked
    {
        get => _isLiked;
        private set
        {
            if (SetProperty(ref _isLiked, value))
            {
                OnPropertyChanged(nameof(LikeGlyph));
            }
        }
    }

    /// <summary>Segoe Fluent Icons HeartFill / Heart.</summary>
    public string LikeGlyph => _isLiked ? "\uEB52" : "\uEB51";

    public string LikeToolTip => _library?.IsConnected == true
        ? (_isLiked ? "Remove from Liked Songs" : "Add to Liked Songs")
        : "Connect Spotify to like songs";

    public AsyncRelayCommand ToggleLikeCommand { get; }

    // ---- Like / dislike for YouTube in a browser ----

    /// <summary>👍/👎 show for a browser playing YouTube (reported by the extension, or spotted in a window title).</summary>
    public bool ShowThumbs => _isBrowser && (_youtubeHint || ReactionState is not null);

    private BrowserReactionState? ReactionState =>
        _reactions?.Current is { } s && BrowserReactionProtocol.TitlesMatch(s.Title, _snapshot?.Title) ? s : null;

    public bool IsThumbUp => ReactionState?.Liked ?? false;

    public bool IsThumbDown => ReactionState?.Disliked ?? false;

    /// <summary>What happened to the last press, e.g. how to get the extension.</summary>
    public string ReactionStatus
    {
        get => _reactionStatus;
        private set
        {
            if (SetProperty(ref _reactionStatus, value))
            {
                OnPropertyChanged(nameof(ThumbUpToolTip));
                OnPropertyChanged(nameof(ThumbDownToolTip));
            }
        }
    }

    public string ThumbUpToolTip => _reactionStatus.Length > 0 ? _reactionStatus : "Like";

    public string ThumbDownToolTip => _reactionStatus.Length > 0 ? _reactionStatus : "Dislike";

    public AsyncRelayCommand ThumbUpCommand { get; }

    public AsyncRelayCommand ThumbDownCommand { get; }

    /// <summary>Segoe Fluent Icons Pause / Play.</summary>
    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";

    /// <summary>0–100.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public ICommand TogglePlayPauseCommand { get; }

    public AsyncRelayCommand NextCommand { get; }

    public AsyncRelayCommand PreviousCommand { get; }

    public AsyncRelayCommand ToggleShuffleCommand { get; }

    /// <summary>Off → repeat all → repeat one → off, like Spotify and Apple Music.</summary>
    public AsyncRelayCommand CycleRepeatCommand { get; }

    public static MediaRepeatMode NextRepeatMode(MediaRepeatMode mode) => mode switch
    {
        MediaRepeatMode.None => MediaRepeatMode.List,
        MediaRepeatMode.List => MediaRepeatMode.Track,
        _ => MediaRepeatMode.None,
    };

    protected override void OnViewActiveChanged(bool active)
    {
        UpdateProgress();
        UpdateLyricLine();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Changed -= OnSourceChanged;
            _progressTimer.Dispose();
            _lyricsTimer.Dispose();
            _trackCts?.Cancel();
            _trackCts?.Dispose();
            if (_library is not null)
            {
                _library.ConnectionChanged -= OnLibraryConnectionChanged;
            }

            if (_reactions is not null)
            {
                _reactions.Changed -= OnReactionsChanged;
            }
        }

        base.Dispose(disposing);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        MediaSnapshot? snapshot = _source.Current;
        IReadOnlyList<MediaSessionInfo> sessions = _source.Sessions;
        _dispatcher.TryEnqueue(() =>
        {
            Sessions = sessions;
            Apply(snapshot);
        });
    }

    private void Apply(MediaSnapshot? snapshot)
    {
        MediaSnapshot? previous = _snapshot;
        _snapshot = snapshot;

        bool hasMedia = snapshot is not null && (snapshot.Title.Length > 0 || snapshot.Artist.Length > 0);
        Title = snapshot?.Title ?? string.Empty;
        Artist = snapshot?.Artist ?? string.Empty;
        IsPlaying = hasMedia && snapshot!.IsPlaying;
        CompactText = Title;
        Album = snapshot?.Album ?? string.Empty;
        SourceName = MediaSourceNames.Friendly(snapshot?.SourceAppId);
        Artwork = snapshot?.Artwork?.Image;
        AccentColor = snapshot?.Artwork?.AccentArgb ?? AccentPicker.Neutral;

        IsAvailable = hasMedia;
        CompactPriority = IsPlaying ? ModulePriority.Media : ModulePriority.Unavailable;
        InteractivePriority = hasMedia ? ModulePriority.Media : ModulePriority.Unavailable;

        _isSpotify = hasMedia && MediaSourceNames.IsSpotify(snapshot!.SourceAppId);
        _isBrowser = hasMedia && MediaSourceNames.IsBrowser(snapshot!.SourceAppId);
        NotifyReactionsChanged();

        CanShuffle = hasMedia && snapshot!.CanShuffle;
        IsShuffleActive = hasMedia && snapshot!.IsShuffleActive;
        CanRepeat = hasMedia && snapshot!.CanRepeat;
        RepeatMode = hasMedia ? snapshot!.RepeatMode : MediaRepeatMode.None;

        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        ToggleShuffleCommand.NotifyCanExecuteChanged();
        CycleRepeatCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSeek));
        UpdateProgress();

        string key = hasMedia ? $"{snapshot!.SourceAppId}|{snapshot.Title}|{snapshot.Artist}" : string.Empty;
        if (key != _trackKey)
        {
            _trackKey = key;
            OnTrackChanged(hasMedia ? snapshot : null);
        }
        else
        {
            // Position or play state changed (seek, pause): re-align the lyric line.
            UpdateLyricLine();
        }

        bool trackChanged = hasMedia && snapshot!.IsPlaying &&
            (previous is null || previous.Title != snapshot.Title || previous.Artist != snapshot.Artist);
        if (trackChanged)
        {
            RequestAttention(TrackChangeAttention);
        }
    }

    // The new state shows immediately; the player's own change event confirms (or reverts) it.
    private Task ToggleShuffleAsync()
    {
        IsShuffleActive = !IsShuffleActive;
        return _source.SetShuffleAsync(IsShuffleActive);
    }

    private Task CycleRepeatAsync()
    {
        RepeatMode = NextRepeatMode(RepeatMode);
        return _source.SetRepeatModeAsync(RepeatMode);
    }

    private void TickProgress() => UpdateProgress();

    /// <summary>New song: fetch its lyrics and like state in the background (each at most once).</summary>
    private void OnTrackChanged(MediaSnapshot? snapshot)
    {
        _trackCts?.Cancel();
        _trackCts?.Dispose();
        _trackCts = null;
        SetLyrics(null, loading: false);
        SetTrackId(null);
        IsLiked = false;
        _youtubeHint = false;
        ReactionStatus = string.Empty;
        NotifyReactionsChanged();
        if (snapshot is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _trackCts = cts;
        if (_isBrowser && _reactions is not null)
        {
            _ = ProbeYouTubeAsync(snapshot.Title, cts.Token);
        }

        if (_lyricsProvider is not null)
        {
            SetLyrics(null, loading: true);
            _ = LoadLyricsAsync(snapshot, cts.Token);
        }

        if (_library is { IsConnected: true })
        {
            _ = LoadLikeAsync(snapshot, cts.Token);
        }
    }

    private async Task LoadLyricsAsync(MediaSnapshot snapshot, CancellationToken token)
    {
        Lyrics? lyrics = null;
        try
        {
            lyrics = await _lyricsProvider!.GetAsync(snapshot.Artist, snapshot.Title, snapshot.Album, snapshot.Duration, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(MediaModule), "Lyrics lookup failed", ex);
        }

        _dispatcher.TryEnqueue(() =>
        {
            if (!token.IsCancellationRequested)
            {
                SetLyrics(lyrics, loading: false);
            }
        });
    }

    private async Task LoadLikeAsync(MediaSnapshot snapshot, CancellationToken token)
    {
        try
        {
            string? id = await _library!.FindTrackAsync(snapshot.Artist, snapshot.Title, token).ConfigureAwait(false);
            bool saved = id is not null && await _library.IsSavedAsync(id, token).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    SetTrackId(id);
                    IsLiked = saved;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(MediaModule), "Spotify lookup failed", ex);
        }
    }

    private async Task ToggleLikeAsync()
    {
        if (_library is null || !_library.IsConnected)
        {
            SpotifyConnectRequested?.Invoke();
            return;
        }

        if (_trackId is not { } id)
        {
            return;
        }

        bool target = !IsLiked;
        IsLiked = target;
        try
        {
            await _library.SetSavedAsync(id, target, CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            IsLiked = !target;
            throw;
        }
    }

    private void OnLibraryConnectionChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        // Connecting mid-song: look the current track up right away.
        OnPropertyChanged(nameof(CanLike));
        OnPropertyChanged(nameof(LikeToolTip));
        ToggleLikeCommand.NotifyCanExecuteChanged();
        if (_library is { IsConnected: true } && _snapshot is { } snapshot && _trackId is null && _trackCts is { } cts)
        {
            _ = LoadLikeAsync(snapshot, cts.Token);
        }
    });

    private void OnReactionsChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(NotifyReactionsChanged);

    private void NotifyReactionsChanged()
    {
        OnPropertyChanged(nameof(ShowHeart));
        OnPropertyChanged(nameof(LikeToolTip));
        OnPropertyChanged(nameof(ShowThumbs));
        OnPropertyChanged(nameof(IsThumbUp));
        OnPropertyChanged(nameof(IsThumbDown));
        ToggleLikeCommand.NotifyCanExecuteChanged();
        ThumbUpCommand.NotifyCanExecuteChanged();
        ThumbDownCommand.NotifyCanExecuteChanged();
    }

    private async Task ProbeYouTubeAsync(string title, CancellationToken token)
    {
        try
        {
            bool found = await _reactions!.LooksLikeYouTubeAsync(title, token).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && found != _youtubeHint)
                {
                    _youtubeHint = found;
                    NotifyReactionsChanged();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(MediaModule), "Looking for YouTube failed", ex);
        }
    }

    private async Task ReactAsync(BrowserReaction reaction)
    {
        if (_reactions is null)
        {
            return;
        }

        bool sent = await _reactions.SendAsync(reaction, CancellationToken.None).ConfigureAwait(true);
        ReactionStatus = sent
            ? string.Empty
            : "Install the WinIsland browser extension (the extension folder next to WinIsland.exe) to like and dislike from the island.";
    }

    private void SetTrackId(string? id)
    {
        _trackId = id;
        OnPropertyChanged(nameof(CanLike));
        ToggleLikeCommand.NotifyCanExecuteChanged();
    }

    private void SetLyrics(Lyrics? lyrics, bool loading)
    {
        _lyrics = lyrics;
        _lyricsLoading = loading;
        _lyricIndex = int.MinValue;
        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(HasSyncedLyrics));
        OnPropertyChanged(nameof(HasPlainLyrics));
        OnPropertyChanged(nameof(LyricsStatus));
        OnPropertyChanged(nameof(ShowLyricsStatus));
        OnPropertyChanged(nameof(LyricsPlainText));
        UpdateLyricLine();
    }

    /// <summary>Shows the line being sung; wakes exactly at the next line while the lyrics are on screen.</summary>
    private void UpdateLyricLine()
    {
        _lyricsTimer.Cancel();
        if (_lyrics is not { IsSynced: true } lyrics || _snapshot is not { } snapshot)
        {
            SetLyricLines(int.MinValue, []);
            return;
        }

        TimeSpan position = snapshot.PositionAt(_time.GetUtcNow());
        int index = lyrics.IndexAt(position);
        SetLyricLines(index, lyrics.Lines);

        if (IsViewActive && _showLyrics && snapshot.IsPlaying && index + 1 < lyrics.Lines.Count)
        {
            _lyricsTimer.Start(lyrics.Lines[index + 1].Time - position);
        }
    }

    private void SetLyricLines(int index, IReadOnlyList<LyricLine> lines)
    {
        if (index == _lyricIndex)
        {
            return;
        }

        _lyricIndex = index;
        string Line(int i) => i >= 0 && i < lines.Count ? lines[i].Text : string.Empty;
        LyricPrevious = Line(index - 1);
        LyricCurrent = index < 0 && lines.Count > 0 ? "♪" : Line(index);
        LyricNext = Line(index + 1);
        LyricNext2 = Line(index + 2);
        OnPropertyChanged(nameof(LyricPrevious));
        OnPropertyChanged(nameof(LyricCurrent));
        OnPropertyChanged(nameof(LyricNext));
        OnPropertyChanged(nameof(LyricNext2));
    }

    private static async Task Run(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private void UpdateProgress()
    {
        MediaSnapshot? snapshot = _snapshot;
        if (snapshot is null || snapshot.Duration <= TimeSpan.Zero)
        {
            Progress = 0;
            PositionText = DurationText = string.Empty;
            _progressTimer.Cancel();
            return;
        }

        TimeSpan position = snapshot.PositionAt(_time.GetUtcNow());
        if (!_scrubbing)
        {
            Progress = Math.Clamp(position / snapshot.Duration * 100, 0, 100);
            PositionText = Format(position);
        }

        DurationText = Format(snapshot.Duration);

        // Only tick while someone can see the progress bar.
        if (IsViewActive && snapshot.IsPlaying)
        {
            TimeSpan untilNextSecond = TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(position.Ticks % TimeSpan.TicksPerSecond);
            _progressTimer.Start(untilNextSecond);
        }
        else
        {
            _progressTimer.Cancel();
        }
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", null) : t.ToString(@"m\:ss", null);

    private static void LogError(Exception ex) => AppLog.Warn(nameof(MediaModule), "Media command failed", ex);
}
