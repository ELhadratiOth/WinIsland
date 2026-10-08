using System.Globalization;
using WinIsland.Core.Devices;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Geometry;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

public enum OsdKind
{
    None,
    Volume,
    Brightness,
    Microphone,
    Battery,
}

/// <summary>
/// Quick controls (volume, brightness, microphone, battery) plus the slim on-screen display
/// that appears when they change from outside the island (volume keys, plugging in…).
/// </summary>
public sealed class ControlsModule : IslandModule
{
    public const string ModuleId = "controls";

    private static readonly TimeSpan OsdDuration = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan PowerNoticeDuration = TimeSpan.FromSeconds(3.5);

    private readonly IAudioEndpoint _speakers;
    private readonly IAudioEndpoint _microphone;
    private readonly IBrightnessControl _brightness;
    private readonly IPowerSource _power;
    private readonly IUiDispatcher _dispatcher;

    private double _volume;
    private bool _isMuted;
    private bool _hasSpeakers;
    private int _brightnessLevel;
    private bool _hasBrightness;
    private bool _hasMicrophone;
    private bool _isMicMuted;
    private PowerStatus _powerStatus = PowerStatus.None;
    private OsdKind _osd;
    private bool _focusMode;

    public ControlsModule(IAudioEndpoint speakers, IAudioEndpoint microphone, IBrightnessControl brightness, IPowerSource power, IUiDispatcher dispatcher)
        : base(ModuleId, "Controls", "")
    {
        _speakers = speakers ?? throw new ArgumentNullException(nameof(speakers));
        _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
        _brightness = brightness ?? throw new ArgumentNullException(nameof(brightness));
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        ToggleMuteCommand = new AsyncRelayCommand(_ => _speakers.SetMutedAsync(!IsMuted), () => HasSpeakers, LogError);
        ToggleMicCommand = new AsyncRelayCommand(_ => _microphone.SetMutedAsync(!IsMicMuted), () => HasMicrophone, LogError);
        ToggleFocusCommand = new RelayCommand(() => FocusModeToggleRequested?.Invoke(this, EventArgs.Empty));

        IsAvailable = true;
        CompactPriority = ModulePriority.Unavailable;
        InteractivePriority = ModulePriority.Background;

        _speakers.Changed += OnSpeakersChanged;
        _microphone.Changed += OnMicrophoneChanged;
        _brightness.Changed += OnBrightnessChanged;
        _power.Changed += OnPowerChanged;
        ApplySpeakers(external: false);
        ApplyMicrophone(external: false);
        ApplyBrightness(external: false);
        ApplyPower(_power.Current, notify: false);
    }

    /// <summary>The user pressed the focus chip; the host owns the setting.</summary>
    public event EventHandler? FocusModeToggleRequested;

    public override bool AllowedInFocus => true;

    // ---- Volume ----

    public bool HasSpeakers
    {
        get => _hasSpeakers;
        private set => SetProperty(ref _hasSpeakers, value);
    }

    /// <summary>0–100. Setting it (slider) changes the system volume.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (SetVolume(value))
            {
                _ = Run(_speakers.SetLevelAsync(value / 100));
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(VolumeGlyph));
                RefreshOsd();
            }
        }
    }

    /// <summary>Segoe Fluent Icons Mute / Volume0–3.</summary>
    public string VolumeGlyph => VolumeIcon(_volume, _isMuted);

    public AsyncRelayCommand ToggleMuteCommand { get; }

    // ---- Brightness ----

    public bool HasBrightness
    {
        get => _hasBrightness;
        private set => SetProperty(ref _hasBrightness, value);
    }

    /// <summary>0–100. Setting it (slider) changes the display brightness.</summary>
    public double Brightness
    {
        get => _brightnessLevel;
        set
        {
            int level = (int)Math.Clamp(Math.Round(value), 0, 100);
            if (level != _brightnessLevel)
            {
                _brightnessLevel = level;
                OnPropertyChanged();
                RefreshOsd();
                _ = Run(_brightness.SetLevelAsync(level));
            }
        }
    }

    // ---- Microphone ----

    public bool HasMicrophone
    {
        get => _hasMicrophone;
        private set => SetProperty(ref _hasMicrophone, value);
    }

    public bool IsMicMuted
    {
        get => _isMicMuted;
        private set
        {
            if (SetProperty(ref _isMicMuted, value))
            {
                OnPropertyChanged(nameof(MicGlyph));
                OnPropertyChanged(nameof(MicLabel));
                RefreshOsd();
            }
        }
    }

    public string MicGlyph => _isMicMuted ? "" : "";

    public string MicLabel => _isMicMuted ? "Mic off" : "Mic on";

    public AsyncRelayCommand ToggleMicCommand { get; }

    // ---- Focus ----

    public bool IsFocusMode
    {
        get => _focusMode;
        set
        {
            if (SetProperty(ref _focusMode, value))
            {
                OnPropertyChanged(nameof(FocusLabel));
            }
        }
    }

    public string FocusLabel => _focusMode ? "Focus on" : "Focus";

    public RelayCommand ToggleFocusCommand { get; }

    // ---- Battery ----

    public bool HasBattery => _powerStatus.HasBattery;

    public int BatteryPercent => _powerStatus.Percent;

    public bool IsCharging => _powerStatus.IsPluggedIn;

    /// <summary>"76%", or "76% · Charging".</summary>
    public string BatteryText => !_powerStatus.HasBattery
        ? string.Empty
        : _powerStatus.IsPluggedIn
            ? string.Create(CultureInfo.CurrentCulture, $"{_powerStatus.Percent}% · {(_powerStatus.Percent >= 100 ? "Charged" : "Charging")}")
            : string.Create(CultureInfo.CurrentCulture, $"{_powerStatus.Percent}%");

    public uint BatteryAccent => BatteryColor(_powerStatus);

    /// <summary>0–1, for the drawn battery icon.</summary>
    public double BatteryFraction => _powerStatus.Percent / 100.0;

    // ---- On-screen display (compact pill shown when something changes) ----

    public OsdKind Osd
    {
        get => _osd;
        private set
        {
            if (SetProperty(ref _osd, value))
            {
                RefreshOsd();
            }
        }
    }

    public string OsdGlyph { get; private set; } = string.Empty;

    public string OsdLabel { get; private set; } = string.Empty;

    /// <summary>0–100, the bar's fill.</summary>
    public double OsdValue { get; private set; }

    /// <summary>0–1, the bar's fill as a scale factor.</summary>
    public double OsdFraction => OsdValue / 100.0;

    public string OsdText { get; private set; } = string.Empty;

    public bool OsdShowsBar { get; private set; } = true;

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Compact => new DipSize(280, 36),
        IslandSize.Expanded => new DipSize(400, HasBrightness ? 152 : 128),
        _ => base.GetSize(size),
    };

    internal static string VolumeIcon(double volume, bool muted) => muted || volume <= 0
        ? ""
        : volume < 34 ? "" : volume < 67 ? "" : "";

    internal static uint BatteryColor(PowerStatus status) =>
        status.IsPluggedIn ? Palette.Green
        : status.Percent <= 10 ? Palette.Red
        : status.Percent <= 20 || status.EnergySaver ? Palette.Yellow
        : Palette.White;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _speakers.Changed -= OnSpeakersChanged;
            _microphone.Changed -= OnMicrophoneChanged;
            _brightness.Changed -= OnBrightnessChanged;
            _power.Changed -= OnPowerChanged;
        }

        base.Dispose(disposing);
    }

    private bool SetVolume(double value)
    {
        if (!SetProperty(ref _volume, value, nameof(Volume)))
        {
            return false;
        }

        OnPropertyChanged(nameof(VolumeGlyph));
        RefreshOsd();
        return true;
    }

    private void OnSpeakersChanged(object? sender, bool external) => _dispatcher.TryEnqueue(() => ApplySpeakers(external));

    private void OnMicrophoneChanged(object? sender, bool external) => _dispatcher.TryEnqueue(() => ApplyMicrophone(external));

    private void OnBrightnessChanged(object? sender, bool external) => _dispatcher.TryEnqueue(() => ApplyBrightness(external));

    private void OnPowerChanged(object? sender, EventArgs e)
    {
        PowerStatus status = _power.Current;
        _dispatcher.TryEnqueue(() => ApplyPower(status, notify: true));
    }

    private void ApplySpeakers(bool external)
    {
        HasSpeakers = _speakers.IsAvailable;
        bool changed = SetVolume(Math.Round(_speakers.Level * 100));
        bool muteChanged = _isMuted != _speakers.IsMuted;
        IsMuted = _speakers.IsMuted;
        ToggleMuteCommand.NotifyCanExecuteChanged();
        if (external && (changed || muteChanged))
        {
            ShowOsd(OsdKind.Volume);
        }
    }

    private void ApplyMicrophone(bool external)
    {
        HasMicrophone = _microphone.IsAvailable;
        bool changed = _isMicMuted != _microphone.IsMuted;
        IsMicMuted = _microphone.IsMuted;
        ToggleMicCommand.NotifyCanExecuteChanged();
        if (external && changed)
        {
            ShowOsd(OsdKind.Microphone);
        }
    }

    private void ApplyBrightness(bool external)
    {
        bool supportChanged = _hasBrightness != _brightness.IsSupported;
        HasBrightness = _brightness.IsSupported;
        if (_brightnessLevel != _brightness.Level)
        {
            _brightnessLevel = _brightness.Level;
            OnPropertyChanged(nameof(Brightness));
            RefreshOsd();
            if (external)
            {
                ShowOsd(OsdKind.Brightness);
            }
        }

        if (supportChanged)
        {
            NotifyPresentationChanged();
        }
    }

    private void ApplyPower(PowerStatus status, bool notify)
    {
        PowerStatus previous = _powerStatus;
        _powerStatus = status;
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(BatteryPercent));
        OnPropertyChanged(nameof(IsCharging));
        OnPropertyChanged(nameof(BatteryText));
        OnPropertyChanged(nameof(BatteryAccent));
        OnPropertyChanged(nameof(BatteryFraction));
        RefreshOsd();

        if (!notify || !status.HasBattery)
        {
            return;
        }

        if (status.IsPluggedIn && !previous.IsPluggedIn)
        {
            ShowOsd(OsdKind.Battery, PowerNoticeDuration, AttentionPriority.Normal);
        }
        else if (!status.IsPluggedIn && (Crossed(previous, status, 20) || Crossed(previous, status, 10)))
        {
            ShowOsd(OsdKind.Battery, PowerNoticeDuration * 2, AttentionPriority.Important);
        }
    }

    private static bool Crossed(PowerStatus previous, PowerStatus current, int threshold) =>
        previous.Percent > threshold && current.Percent <= threshold;

    private void ShowOsd(OsdKind kind, TimeSpan? duration = null, int priority = AttentionPriority.Feedback)
    {
        Osd = kind;
        RefreshOsd();
        RequestAttention(duration ?? OsdDuration, priority, IslandSize.Compact);
    }

    private void RefreshOsd()
    {
        (string glyph, string label, double value, string text, bool bar, uint accent) = _osd switch
        {
            OsdKind.Brightness => ("", "Brightness", (double)_brightnessLevel, Percent(_brightnessLevel), true, Palette.White),
            OsdKind.Microphone => (MicGlyph, _isMicMuted ? "Microphone muted" : "Microphone on", 0d, string.Empty, false, _isMicMuted ? Palette.Red : Palette.White),
            OsdKind.Battery => (_powerStatus.IsPluggedIn ? "" : "", _powerStatus.IsPluggedIn ? "Charging" : "Low battery", _powerStatus.Percent, Percent(_powerStatus.Percent), true, BatteryColor(_powerStatus)),
            _ => (VolumeGlyph, "Volume", _isMuted ? 0 : _volume, _isMuted ? "Muted" : Percent(_volume), true, Palette.White),
        };

        OsdGlyph = glyph;
        OsdLabel = label;
        OsdValue = value;
        OsdText = text;
        OsdShowsBar = bar;
        AccentArgb = accent;
        OnPropertyChanged(nameof(OsdGlyph));
        OnPropertyChanged(nameof(OsdLabel));
        OnPropertyChanged(nameof(OsdValue));
        OnPropertyChanged(nameof(OsdFraction));
        OnPropertyChanged(nameof(OsdText));
        OnPropertyChanged(nameof(OsdShowsBar));
    }

    private static string Percent(double value) => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(value)}");

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

    private static void LogError(Exception ex) => AppLog.Warn(nameof(ControlsModule), "Control change failed", ex);
}
