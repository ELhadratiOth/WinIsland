using System.Windows.Input;
using WinIsland.Core.Diagnostics;
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

    public MediaModule(IMediaSource source, TimeProvider time, IUiDispatcher dispatcher)
        : base(ModuleId, "Media", "")
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _progressTimer = new OneShotTimer(time, dispatcher, TickProgress);

        TogglePlayPauseCommand = new AsyncRelayCommand(_ => _source.TogglePlayPauseAsync(), onError: LogError);
        NextCommand = new AsyncRelayCommand(_ => _source.NextAsync(), () => _snapshot?.CanGoNext ?? false, LogError);
        PreviousCommand = new AsyncRelayCommand(_ => _source.PreviousAsync(), () => _snapshot?.CanGoPrevious ?? false, LogError);

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

    /// <summary>Segoe Fluent Icons Pause / Play.</summary>
    public string PlayPauseGlyph => IsPlaying ? "" : "";

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

        IsAvailable = hasMedia;
        CompactPriority = IsPlaying ? ModulePriority.Media : ModulePriority.Unavailable;
        InteractivePriority = hasMedia ? ModulePriority.Media : ModulePriority.Unavailable;

        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        UpdateProgress();

        bool trackChanged = hasMedia && snapshot!.IsPlaying &&
            (previous is null || previous.Title != snapshot.Title || previous.Artist != snapshot.Artist);
        if (trackChanged)
        {
            RequestAttention(TrackChangeAttention);
        }
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
