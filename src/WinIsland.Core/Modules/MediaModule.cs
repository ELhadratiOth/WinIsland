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

    public MediaModule(IMediaSource source, TimeProvider time, IUiDispatcher dispatcher)
        : base(ModuleId, "Media", "\uEC4F")
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _progressTimer = new OneShotTimer(time, dispatcher, TickProgress);

        TogglePlayPauseCommand = new AsyncRelayCommand(_ => _source.TogglePlayPauseAsync(), onError: LogError);
        NextCommand = new AsyncRelayCommand(_ => _source.NextAsync(), () => _snapshot?.CanGoNext ?? false, LogError);
        PreviousCommand = new AsyncRelayCommand(_ => _source.PreviousAsync(), () => _snapshot?.CanGoPrevious ?? false, LogError);
        ToggleShuffleCommand = new AsyncRelayCommand(_ => ToggleShuffleAsync(), () => CanShuffle, LogError);
        CycleRepeatCommand = new AsyncRelayCommand(_ => CycleRepeatAsync(), () => CanRepeat, LogError);

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
        IslandSize.Compact => IslandMetrics.CompactWide,
        IslandSize.Expanded => new DipSize(460, 196),
        _ => base.GetSize(size),
    };

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

    protected override void OnViewActiveChanged(bool active) => UpdateProgress();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Changed -= OnSourceChanged;
            _progressTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        MediaSnapshot? snapshot = _source.Current;
        _dispatcher.TryEnqueue(() => Apply(snapshot));
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

        CanShuffle = hasMedia && snapshot!.CanShuffle;
        IsShuffleActive = hasMedia && snapshot!.IsShuffleActive;
        CanRepeat = hasMedia && snapshot!.CanRepeat;
        RepeatMode = hasMedia ? snapshot!.RepeatMode : MediaRepeatMode.None;

        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        ToggleShuffleCommand.NotifyCanExecuteChanged();
        CycleRepeatCommand.NotifyCanExecuteChanged();
        UpdateProgress();

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
        Progress = Math.Clamp(position / snapshot.Duration * 100, 0, 100);
        PositionText = Format(position);
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
