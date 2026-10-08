using System.Globalization;
using WinIsland.Core.Geometry;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

public enum TimerMode
{
    Timer,
    Stopwatch,
    Pomodoro,
}

public enum TimerRunState
{
    Idle,
    Running,
    Paused,
}

/// <summary>
/// Countdown timer, stopwatch and Pomodoro (25 min focus / 5 min break, a 15 min break every
/// fourth round). Ticks once per second only while running, aligned to the displayed second.
/// </summary>
public sealed class TimerModule : IslandModule
{
    public const string ModuleId = "timer";

    public static readonly TimeSpan PomodoroFocus = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan PomodoroBreak = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PomodoroLongBreak = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time;
    private readonly OneShotTimer _tick;
    private TimerMode _mode = TimerMode.Timer;
    private TimerRunState _state;
    private TimeSpan _duration = TimeSpan.FromMinutes(5);
    private TimeSpan _accumulated;
    private DateTimeOffset _startedAt;
    private int _pomodoroRound = 1;
    private bool _pomodoroBreak;
    private string _displayText = string.Empty;
    private double _progress;

    public TimerModule(TimeProvider time, IUiDispatcher dispatcher)
        : base(ModuleId, "Timer", "\uE916")
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _tick = new OneShotTimer(time, dispatcher, Update);

        StartPauseCommand = new RelayCommand(StartPause);
        ResetCommand = new RelayCommand(Reset, () => _state != TimerRunState.Idle);
        PresetCommand = new RelayCommandOf<string>(minutes =>
        {
            if (double.TryParse(minutes, NumberStyles.Float, CultureInfo.InvariantCulture, out double m))
            {
                StartTimer(TimeSpan.FromMinutes(m));
            }
        });
        SetModeCommand = new RelayCommandOf<string>(mode =>
        {
            if (Enum.TryParse(mode, ignoreCase: true, out TimerMode parsed))
            {
                Mode = parsed;
            }
        });
        AddMinuteCommand = new RelayCommand(() => Extend(TimeSpan.FromMinutes(1)), () => _mode != TimerMode.Stopwatch && _state != TimerRunState.Idle);

        IsAvailable = true;
        InteractivePriority = ModulePriority.Background;
        AccentArgb = Palette.Orange;
        Update();
    }

    /// <summary>A countdown or Pomodoro phase ended (the host plays a sound).</summary>
    public event EventHandler? Finished;

    public override bool AllowedInFocus => true;

    public TimerMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            ResetCore();
            _mode = value;
            _duration = value switch
            {
                TimerMode.Pomodoro => PomodoroFocus,
                TimerMode.Timer => TimeSpan.FromMinutes(5),
                _ => TimeSpan.Zero,
            };
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTimerMode));
            OnPropertyChanged(nameof(IsStopwatchMode));
            OnPropertyChanged(nameof(IsPomodoroMode));
            Update();
        }
    }

    public bool IsTimerMode => _mode == TimerMode.Timer;

    public bool IsStopwatchMode => _mode == TimerMode.Stopwatch;

    public bool IsPomodoroMode => _mode == TimerMode.Pomodoro;

    public TimerRunState State => _state;

    public bool IsRunning => _state == TimerRunState.Running;

    public bool IsIdle => _state == TimerRunState.Idle;

    /// <summary>Presets are offered for a countdown that hasn't started.</summary>
    public bool ShowPresets => _mode == TimerMode.Timer && _state == TimerRunState.Idle;

    public string StartPauseGlyph => IsRunning ? "\uE769" : "\uE768";

    public string StartPauseLabel => IsRunning ? "Pause" : _state == TimerRunState.Paused ? "Resume" : "Start";

    /// <summary>"24:59", "1:02:03" or stopwatch "0:12.4"-free "0:12".</summary>
    public string DisplayText
    {
        get => _displayText;
        private set => SetProperty(ref _displayText, value);
    }

    /// <summary>0–100: elapsed share of a countdown (0 for the stopwatch).</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>"Focus · round 2 of 4", "Short break", "Timer", "Stopwatch".</summary>
    public string Caption => _mode switch
    {
        TimerMode.Pomodoro when _pomodoroBreak => _pomodoroRound % 4 == 0 ? "Long break" : "Short break",
        TimerMode.Pomodoro => string.Create(CultureInfo.CurrentCulture, $"Focus · round {((_pomodoroRound - 1) % 4) + 1} of 4"),
        TimerMode.Stopwatch => "Stopwatch",
        _ => "Timer",
    };

    public RelayCommand StartPauseCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand AddMinuteCommand { get; }

    /// <summary>Parameter: minutes as text ("5").</summary>
    public RelayCommandOf<string> PresetCommand { get; }

    /// <summary>Parameter: "Timer", "Stopwatch" or "Pomodoro".</summary>
    public RelayCommandOf<string> SetModeCommand { get; }

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Compact => IslandMetrics.CompactWide,
        IslandSize.Expanded => new DipSize(400, 152),
        _ => base.GetSize(size),
    };

    public TimeSpan Elapsed => _state == TimerRunState.Running ? _accumulated + (_time.GetUtcNow() - _startedAt) : _accumulated;

    public TimeSpan Remaining => _duration - Elapsed < TimeSpan.Zero ? TimeSpan.Zero : _duration - Elapsed;

    public void StartTimer(TimeSpan duration)
    {
        ResetCore();
        if (_mode != TimerMode.Timer)
        {
            _mode = TimerMode.Timer;
            OnPropertyChanged(nameof(Mode));
            OnPropertyChanged(nameof(IsTimerMode));
            OnPropertyChanged(nameof(IsStopwatchMode));
            OnPropertyChanged(nameof(IsPomodoroMode));
        }

        _duration = duration;
        Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
        }

        base.Dispose(disposing);
    }

    private void StartPause()
    {
        if (_state == TimerRunState.Running)
        {
            _accumulated = Elapsed;
            SetState(TimerRunState.Paused);
            Update();
        }
        else
        {
            Start();
        }
    }

    private void Start()
    {
        if (_mode != TimerMode.Stopwatch && _duration <= TimeSpan.Zero)
        {
            return;
        }

        _startedAt = _time.GetUtcNow();
        SetState(TimerRunState.Running);
        Update();
    }

    private void Extend(TimeSpan by)
    {
        _duration += by;
        Update();
    }

    private void Reset()
    {
        ResetCore();
        if (_mode == TimerMode.Pomodoro)
        {
            _pomodoroRound = 1;
            _pomodoroBreak = false;
            _duration = PomodoroFocus;
        }

        Update();
    }

    private void ResetCore()
    {
        _accumulated = TimeSpan.Zero;
        SetState(TimerRunState.Idle);
        _tick.Cancel();
    }

    private void SetState(TimerRunState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(ShowPresets));
        OnPropertyChanged(nameof(StartPauseGlyph));
        OnPropertyChanged(nameof(StartPauseLabel));
        ResetCommand.NotifyCanExecuteChanged();
        AddMinuteCommand.NotifyCanExecuteChanged();

        // A running or paused timer owns the compact pill; an idle one stays out of the way.
        CompactPriority = state == TimerRunState.Idle ? ModulePriority.Unavailable : ModulePriority.Live;
        InteractivePriority = state == TimerRunState.Idle ? ModulePriority.Background : ModulePriority.Live;
    }

    private void Update()
    {
        TimeSpan elapsed = Elapsed;
        if (_mode != TimerMode.Stopwatch && _state == TimerRunState.Running && elapsed >= _duration)
        {
            Complete();
            return;
        }

        TimeSpan shown = _mode == TimerMode.Stopwatch ? elapsed : Remaining;
        DisplayText = Format(shown, roundUp: _mode != TimerMode.Stopwatch);
        Progress = _mode == TimerMode.Stopwatch || _duration <= TimeSpan.Zero ? 0 : Math.Clamp(elapsed / _duration * 100, 0, 100);
        CompactText = _mode switch
        {
            TimerMode.Pomodoro => _pomodoroBreak ? "Break" : "Focus",
            TimerMode.Stopwatch => "Stopwatch",
            _ => "Timer",
        };
        CompactDetail = DisplayText;
        OnPropertyChanged(nameof(Caption));

        if (_state == TimerRunState.Running)
        {
            // Wake exactly when the displayed second changes.
            long ticks = _mode == TimerMode.Stopwatch
                ? TimeSpan.TicksPerSecond - (elapsed.Ticks % TimeSpan.TicksPerSecond)
                : Remaining.Ticks % TimeSpan.TicksPerSecond;
            _tick.Start(TimeSpan.FromTicks(ticks == 0 ? TimeSpan.TicksPerSecond : ticks));
        }
    }

    private void Complete()
    {
        _tick.Cancel();
        if (_mode == TimerMode.Pomodoro)
        {
            // Move straight on to the next phase, like a real Pomodoro timer.
            if (_pomodoroBreak)
            {
                _pomodoroBreak = false;
                _pomodoroRound++;
                _duration = PomodoroFocus;
            }
            else
            {
                _pomodoroBreak = true;
                _duration = _pomodoroRound % 4 == 0 ? PomodoroLongBreak : PomodoroBreak;
            }

            _accumulated = TimeSpan.Zero;
            _startedAt = _time.GetUtcNow();
            Update();
        }
        else
        {
            _accumulated = TimeSpan.Zero;
            SetState(TimerRunState.Idle);
            DisplayText = "Done";
            CompactDetail = "Done";
            Progress = 100;
        }

        Finished?.Invoke(this, EventArgs.Empty);
        RequestAttention(TimeSpan.FromSeconds(6), AttentionPriority.Important);
    }

    internal static string Format(TimeSpan t, bool roundUp)
    {
        // A countdown shows 0:01 until it actually reaches zero.
        long seconds = roundUp ? (long)Math.Ceiling(t.TotalSeconds - 1e-9) : (long)Math.Floor(t.TotalSeconds);
        seconds = Math.Max(0, seconds);
        return seconds >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}
