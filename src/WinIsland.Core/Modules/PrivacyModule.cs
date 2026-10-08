using WinIsland.Core.Devices;
using WinIsland.Core.Geometry;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

/// <summary>
/// Shows which apps are using the microphone or camera, like the coloured dot on iPhone:
/// green for the camera, orange for the microphone. Takes the compact pill while in use and
/// offers a one-click microphone mute.
/// </summary>
public sealed class PrivacyModule : IslandModule
{
    public const string ModuleId = "privacy";

    private readonly IPrivacySource _source;
    private readonly IAudioEndpoint _microphone;
    private readonly IUiDispatcher _dispatcher;
    private IReadOnlyList<SensorUse> _uses = [];
    private bool _isMicMuted;

    public PrivacyModule(IPrivacySource source, IAudioEndpoint microphone, IUiDispatcher dispatcher)
        : base(ModuleId, "In use", "\uE720")
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ToggleMicCommand = new AsyncRelayCommand(_ => _microphone.SetMutedAsync(!IsMicMuted), () => UsesMicrophone && _microphone.IsAvailable);

        _source.Changed += OnSourceChanged;
        _microphone.Changed += OnMicrophoneChanged;
        Apply(_source.Current, notify: false);
        _isMicMuted = _microphone.IsMuted;
    }

    public override bool AllowedInFocus => true;

    public bool UsesCamera => _uses.Any(u => u.Kind == SensorKind.Camera);

    public bool UsesMicrophone => _uses.Any(u => u.Kind == SensorKind.Microphone);

    /// <summary>"Teams, Discord".</summary>
    public string MicrophoneApps => Apps(SensorKind.Microphone);

    public string CameraApps => Apps(SensorKind.Camera);

    public bool IsMicMuted
    {
        get => _isMicMuted;
        private set
        {
            if (SetProperty(ref _isMicMuted, value))
            {
                OnPropertyChanged(nameof(MicButtonGlyph));
                OnPropertyChanged(nameof(MicButtonLabel));
            }
        }
    }

    public string MicButtonGlyph => _isMicMuted ? "\uEC54" : "\uE720";

    public string MicButtonLabel => _isMicMuted ? "Unmute" : "Mute";

    public AsyncRelayCommand ToggleMicCommand { get; }

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Compact => IslandMetrics.CompactWide,
        IslandSize.Expanded => new DipSize(400, UsesCamera && UsesMicrophone ? 112 : 84),
        _ => base.GetSize(size),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Changed -= OnSourceChanged;
            _microphone.Changed -= OnMicrophoneChanged;
        }

        base.Dispose(disposing);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        IReadOnlyList<SensorUse> uses = _source.Current;
        _dispatcher.TryEnqueue(() => Apply(uses, notify: true));
    }

    private void OnMicrophoneChanged(object? sender, bool external) =>
        _dispatcher.TryEnqueue(() => IsMicMuted = _microphone.IsMuted);

    private void Apply(IReadOnlyList<SensorUse> uses, bool notify)
    {
        IReadOnlyList<SensorUse> previous = _uses;
        if (previous.SequenceEqual(uses))
        {
            return;
        }

        _uses = uses;
        OnPropertyChanged(nameof(UsesCamera));
        OnPropertyChanged(nameof(UsesMicrophone));
        OnPropertyChanged(nameof(MicrophoneApps));
        OnPropertyChanged(nameof(CameraApps));
        ToggleMicCommand.NotifyCanExecuteChanged();

        // Camera wins the colour: it's the more sensitive of the two.
        AccentArgb = UsesCamera ? Palette.Green : Palette.Orange;
        CompactText = UsesCamera ? CameraApps : MicrophoneApps;
        CompactDetail = UsesCamera && UsesMicrophone ? "Camera · Mic" : UsesCamera ? "Camera" : "Mic";

        IsAvailable = uses.Count > 0;
        CompactPriority = IsAvailable ? ModulePriority.Live : ModulePriority.Unavailable;
        InteractivePriority = IsAvailable ? ModulePriority.Live : ModulePriority.Unavailable;
        NotifyPresentationChanged();

        // Announce each app that newly starts recording.
        if (notify && uses.Any(u => !previous.Contains(u)))
        {
            RequestAttention(TimeSpan.FromSeconds(3), AttentionPriority.Important);
        }
    }

    private string Apps(SensorKind kind) =>
        string.Join(", ", _uses.Where(u => u.Kind == kind).Select(u => u.AppName).Distinct(StringComparer.OrdinalIgnoreCase));
}
