using Microsoft.UI.Dispatching;
using WinIsland.Core.Claude;
using WinIsland.Core.Devices;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Display;
using WinIsland.Core.Helpers;
using WinIsland.Core.Geometry;
using WinIsland.Core.GitHub;
using WinIsland.Core.Integrations;
using WinIsland.Core.Interaction;
using WinIsland.Core.Layout;
using WinIsland.Core.Media;
using WinIsland.Core.Modules;
using WinIsland.Core.Settings;
using WinIsland.Core.State;
using WinIsland.Core.Threading;
using WinIsland.Core.ViewModels;
using WinIsland.Core.Visibility;
using WinIsland.Platform.Windows.Audio;
using WinIsland.Platform.Windows.Devices;
using WinIsland.Platform.Windows.Display;
using WinIsland.Platform.Windows.Claude;
using WinIsland.Platform.Windows.Foreground;
using WinIsland.Platform.Windows.GitHub;
using WinIsland.Platform.Windows.Security;
using WinIsland.Platform.Windows.Helpers;
using WinIsland.Platform.Windows.Input;
using WinIsland.Platform.Windows.Media;
using WinIsland.Platform.Windows.Networking;
using WinIsland.Platform.Windows.Shell;
using WinIsland.Platform.Windows.Windowing;

namespace WinIsland.App.Services;

/// <summary>
/// Composition root and event router. Everything here runs on the UI thread and only reacts
/// to events (WinEvents, window messages, hook-thread posts, integration callbacks):
///
///   Windows events ─┬─ foreground / display / DPI / hotkey / pointer
///                   ├─ media / Claude Code sessions (background integrations)
///                   ↓
///   visibility + placement policy  →  IslandStateManager  →  IslandViewModel / IslandWindow
///
/// The UI is touched only when the published <see cref="IslandState"/> or the placement
/// actually changes.
/// </summary>
internal sealed class IslandHost : IAsyncDisposable, Preview.IPreviewTarget
{
    private const int HotkeyId = 0x5749;
    private const double SnapLayoutsZoneDip = 120;

    // Coalesces bursts (window drags, display reconfiguration) while still reacting within a frame or three.
    private static readonly TimeSpan ReevaluateDelay = TimeSpan.FromMilliseconds(50);

    private readonly TimeProvider _time = TimeProvider.System;
    private readonly IUiDispatcher _dispatcher;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SettingsStore _settingsStore;
    private readonly Preview.PreviewScenario? _preview;

    private readonly MonitorProvider _monitors = new();
    private readonly ForegroundInspector _inspector = new();
    private readonly ForegroundWatcher _foreground = new();
    private readonly ConnectivityMonitor _connectivity = new();
    private readonly PointerHotZoneWatcher _pointer;
    private readonly IMediaSource _media;
    private readonly ClaudeSessionMonitor _claudeSessions;

    private readonly ClockModule _clockModule;
    private readonly MediaModule _mediaModule;
    private readonly ClaudeModule _claudeModule;
    private readonly ControlsModule _controlsModule;
    private readonly TimerModule _timerModule;
    private readonly PrivacyModule _privacyModule;
    private readonly SpotifyLibrary? _spotify;
    private readonly ClipboardModule _clipboardModule;
    private readonly ShelfModule _shelfModule;
    private readonly DownloadsModule _downloadsModule;
    private readonly MessageWindow? _helperWindow;
    private readonly ClipboardMonitor? _clipboardMonitor;
    private readonly ApprovalsModule _approvalsModule;
    private readonly CiModule _ciModule;
    private readonly GitHubActionsClient? _github;
    private readonly ClaudeUsageTracker _usage;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsViewModel;
    private readonly IslandStateManager _state;
    private readonly InteractionController _interaction;
    private readonly IslandViewModel _viewModel;
    private readonly IntegrationHost _integrations;

    private readonly IslandWindow _window;
    private readonly IslandWindowController _controller;
    private readonly WindowMessageHook _messages;
    private readonly Throttler _reevaluate;

    private IslandSettings _settings;
    private PlacementOptions _placementOptions;
    private Hotkey? _hotkey;
    private MonitorDescriptor? _monitor;
    private ForegroundSnapshot _foregroundSnapshot = ForegroundSnapshot.Empty;
    private PixelRect? _autoHideTaskbar;
    private bool _userHidden;
    private bool _disposed;

    // What is currently on screen, so Render() can skip redundant work.
    private DipSize? _renderedPill;
    private PixelRect? _renderedBounds;
    private MonitorDescriptor? _renderedMonitor;
    private int _transitionGeneration;

    public IslandHost(DispatcherQueue queue, bool preview = false)
    {
        _dispatcher = new DispatcherQueueUiDispatcher(queue);
        _dispatcherQueue = queue;
        _preview = preview ? new Preview.PreviewScenario() : null;

        // The preview never touches the user's real settings.
        _settingsStore = new SettingsStore(preview ? Path.Combine(Path.GetTempPath(), "WinIsland-preview", "settings.json") : SettingsStore.DefaultPath);
        _settings = preview ? new IslandSettings() : _settingsStore.Load();
        _placementOptions = new PlacementOptions { TopMarginDip = _settings.TopMarginDip };
        ApplyGameSettings();

        var systemMedia = preview ? null : new SystemMediaSource(_time, () => _settings.OnlineArtworkLookup);
        _media = (IMediaSource?)_preview?.Media ?? systemMedia!;
        _claudeSessions = new ClaudeSessionMonitor(
            _preview is null ? new ClaudeSessionMonitorOptions() : new ClaudeSessionMonitorOptions { ProjectsDirectory = _preview.ClaudeProjectsDirectory },
            _time);

        // The clock goes first: it is the fallback module when nothing else has content.
        _clockModule = new ClockModule(_time, _dispatcher);
        _spotify = preview ? null : new SpotifyLibrary(() => _settings.SpotifyClientId);
        ILyricsProvider lyrics = preview ? new Preview.PreviewLyrics() : new LrcLibClient(() => _settings.OnlineLyrics);
        _mediaModule = new MediaModule(_media, _time, _dispatcher, lyrics, _spotify);
        _usage = new ClaudeUsageTracker(_preview?.ClaudeProjectsDirectory ?? ClaudeSessionMonitorOptions.DefaultProjectsDirectory(), _time);
        _claudeModule = new ClaudeModule(_claudeSessions, new ClaudeCliMessenger(), _dispatcher, _time, _usage);

        // System devices: Core Audio, WMI brightness, battery, microphone/camera use.
        var integrations = new List<IIntegration>();
        IAudioEndpoint speakers, microphone;
        IBrightnessControl brightness;
        IPowerSource power;
        IPrivacySource privacy;
        if (_preview is not null)
        {
            (speakers, microphone, brightness, power, privacy) = (_preview.Speakers, _preview.Microphone, _preview.Brightness, _preview.Power, _preview.Privacy);
        }
        else
        {
            var speakerEndpoint = CoreAudioEndpoint.Speakers();
            var micEndpoint = CoreAudioEndpoint.Microphone();
            var wmiBrightness = new WmiBrightness();
            var powerSource = new PowerSource();
            var sensors = new SensorUsageMonitor();
            integrations.AddRange([speakerEndpoint, micEndpoint, wmiBrightness, powerSource, sensors]);
            (speakers, microphone, brightness, power, privacy) = (speakerEndpoint, micEndpoint, wmiBrightness, powerSource, sensors);
        }

        _controlsModule = new ControlsModule(speakers, microphone, brightness, power, _dispatcher);
        _timerModule = new TimerModule(_time, _dispatcher);
        _privacyModule = new PrivacyModule(privacy, microphone, _dispatcher);
        _timerModule.Finished += (_, _) => Sounds.Notify();
        _controlsModule.FocusModeToggleRequested += (_, _) => UpdateSettings(s => s with { FocusMode = !s.FocusMode });

        // Helpers: clipboard history, file shelf, downloads.
        IClipboardService clipboard;
        IDownloadSource downloads;
        IShellLauncher shell;
        if (_preview is not null)
        {
            (clipboard, downloads, shell) = (_preview.Clipboard, _preview.Downloads, new Preview.PreviewShell());
        }
        else
        {
            _helperWindow = new MessageWindow("WinIsland.Helpers");
            _clipboardMonitor = new ClipboardMonitor(_helperWindow, () => IsModuleEnabled(ClipboardModule.ModuleId), _time);
            var downloadsWatcher = new DownloadsWatcher(_time);
            integrations.Add(downloadsWatcher);
            (clipboard, downloads, shell) = (_clipboardMonitor, downloadsWatcher, new ShellLauncher());
        }

        // Developer: Claude Code approvals (hook pipe) and GitHub Actions.
        IClaudeHookServer hooks;
        ICiSource ci;
        if (_preview is not null)
        {
            (hooks, ci) = (_preview.Hooks, _preview.Ci);
        }
        else
        {
            var hookServer = new ClaudeHookServer();
            _github = new GitHubActionsClient(() => _settings.GitHubRepos, GitHubToken, _time);
            integrations.AddRange([hookServer, _github]);
            (hooks, ci) = (hookServer, _github);
        }

        integrations.Add(_usage);
        _approvalsModule = new ApprovalsModule(hooks, _time, _dispatcher);
        _ciModule = new CiModule(ci, shell, _time, _dispatcher);

        _clipboardModule = new ClipboardModule(clipboard, _time);
        _shelfModule = new ShelfModule(shell);
        _downloadsModule = new DownloadsModule(downloads, shell, _dispatcher);

        // Order = switcher order; the clock goes first as the fallback.
        _state = new IslandStateManager(
            [_clockModule, _mediaModule, _claudeModule, _approvalsModule, _privacyModule, _timerModule, _ciModule, _downloadsModule, _clipboardModule, _shelfModule, _controlsModule],
            _time,
            _dispatcher);
        _interaction = new InteractionController(_time, _dispatcher, _settings.ToInteractionOptions());
        _viewModel = new IslandViewModel(_state, _clockModule, _mediaModule, _claudeModule);

        // Offline-capable sources first; network-bound ones (calendar, usage…) slot in here and
        // are paused automatically while offline.
        if (systemMedia is not null)
        {
            integrations.Insert(0, systemMedia);
        }

        integrations.Add(_claudeSessions);
        _integrations = new IntegrationHost(integrations, _connectivity, _time);
        ApplyModuleFilter();

        _window = new IslandWindow(_viewModel);
        _controller = new IslandWindowController(_window.Handle);
        _messages = new WindowMessageHook(_window.Handle);
        _pointer = new PointerHotZoneWatcher(_dispatcher);
        _reevaluate = new Throttler(_time, _dispatcher, ReevaluateDelay, Reevaluate);
    }

    /// <summary>
    /// Startup order: lightweight core → island visible (clock) → integrations in the background.
    /// Nothing here waits on I/O beyond reading the small settings file.
    /// </summary>
    public void Start()
    {
        AppLog.Info(nameof(IslandHost), $"Window 0x{_window.Handle:X}; settings: {_settings.VisibilityMode}, {_settings.MonitorPreference}, hover={_settings.HoverToInteract}; experiments: {Experiments.Describe()}");
        _messages.RemoveNonClientArea = !Experiments.Has("keepnc");
        _controller.ApplyOverlayStyles(layered: !Experiments.Has("nolayered"), stripFrame: !Experiments.Has("keepframe"));
        _controller.RefreshFrame();
        if (Experiments.Has("activate"))
        {
            _controller.ActivateWithoutKeepingFocus(_window.Activate);
        }

        Subscribe();

        _foreground.Start();
        RefreshTaskbar();
        Reevaluate();

        RegisterHotkey();
        _clipboardMonitor?.Start();
        AppLog.Info(nameof(IslandHost), $"Monitors: {string.Join("; ", _monitors.GetMonitors().Select(m => $"{m.Id} {m.Bounds} work {m.WorkArea} x{m.Scale}{(m.IsPrimary ? " primary" : string.Empty)}"))}");
        _connectivity.Start();
        _integrations.Start();
        _preview?.Run(_dispatcherQueue, this);
    }

    void Preview.IPreviewTarget.PreviewActivate() => _interaction.Activate();

    void Preview.IPreviewTarget.PreviewSelect(string moduleId) => _state.SelectModule(moduleId);

    void Preview.IPreviewTarget.PreviewDismiss() => _interaction.Dismiss();

    void Preview.IPreviewTarget.PreviewShowLyrics(bool show) => _mediaModule.ShowLyrics = show;

    void Preview.IPreviewTarget.PreviewShelf(params string[] paths) => _shelfModule.Add(paths);

    void Preview.IPreviewTarget.PreviewStartTimer()
    {
        _timerModule.Mode = TimerMode.Pomodoro;
        _timerModule.StartPauseCommand.Execute(null);
        _state.SelectModule(TimerModule.ModuleId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unsubscribe();
        _reevaluate.Dispose();
        _pointer.Dispose();
        _foreground.Dispose();
        _hotkey?.Dispose();

        await _integrations.DisposeAsync();

        _viewModel.Dispose();
        _state.Dispose();
        _interaction.Dispose();
        _clockModule.Dispose();
        _mediaModule.Dispose();
        _claudeModule.Dispose();
        _controlsModule.Dispose();
        _timerModule.Dispose();
        _privacyModule.Dispose();
        _clipboardMonitor?.Dispose();
        _helperWindow?.Dispose();
        _clipboardModule.Dispose();
        _shelfModule.Dispose();
        _downloadsModule.Dispose();
        _approvalsModule.Dispose();
        _ciModule.Dispose();
        _connectivity.Dispose();
        _spotify?.Dispose();
        _settingsWindow?.Close();
        _messages.Dispose();
        _settingsStore.Dispose();
        _window.CloseForExit();
    }

    private void Subscribe()
    {
        _state.StateChanged += OnStateChanged;
        _interaction.ModeChanged += OnInteractionModeChanged;
        _pointer.InsideChanged += OnPointerInsideChanged;
        _foreground.Changed += OnForegroundChanged;
        _foreground.ForegroundSwitched += OnForegroundSwitched;
        _messages.DisplayChanged += OnDisplayConfigurationChanged;
        _messages.DpiChanged += OnDisplayConfigurationChanged;
        _messages.SettingChanged += OnSystemSettingChanged;
        _messages.TimeChanged += OnTimeChanged;
        _messages.ResumedFromSleep += OnResumedFromSleep;
        _messages.HotkeyPressed += OnHotkeyPressed;
        _window.EscapePressed += OnEscapePressed;
        _window.TextInputFocusChanged += OnTextInputFocusChanged;
        _window.Deactivated += OnWindowDeactivated;
        _window.FileDragChanged += OnFileDragChanged;
        _integrations.StatusChanged += OnIntegrationStatusChanged;
    }

    private void Unsubscribe()
    {
        _state.StateChanged -= OnStateChanged;
        _interaction.ModeChanged -= OnInteractionModeChanged;
        _pointer.InsideChanged -= OnPointerInsideChanged;
        _foreground.Changed -= OnForegroundChanged;
        _foreground.ForegroundSwitched -= OnForegroundSwitched;
        _messages.DisplayChanged -= OnDisplayConfigurationChanged;
        _messages.DpiChanged -= OnDisplayConfigurationChanged;
        _messages.SettingChanged -= OnSystemSettingChanged;
        _messages.TimeChanged -= OnTimeChanged;
        _messages.ResumedFromSleep -= OnResumedFromSleep;
        _messages.HotkeyPressed -= OnHotkeyPressed;
        _window.EscapePressed -= OnEscapePressed;
        _window.TextInputFocusChanged -= OnTextInputFocusChanged;
        _window.Deactivated -= OnWindowDeactivated;
        _window.FileDragChanged -= OnFileDragChanged;
        _integrations.StatusChanged -= OnIntegrationStatusChanged;
    }

    // ---- Policy: which monitor, visible or not ------------------------------------------

    private void Reevaluate()
    {
        if (_disposed)
        {
            return;
        }

        if (_inspector.Inspect(_foreground.Foreground) is { } snapshot)
        {
            _foregroundSnapshot = snapshot;
        }

        _monitor = MonitorSelector.Select(_monitors.GetMonitors(), _settings.MonitorPreference, _foregroundSnapshot, _monitor?.Id);

        HiddenReason reason = _userHidden
            ? HiddenReason.User
            : VisibilityEvaluator.Evaluate(_settings.VisibilityMode, _foregroundSnapshot, _monitor?.Id, IsSnapLayoutsLikely());

        if (reason == HiddenReason.None && _monitor is not null && !Place(_monitor, _state.State.SizeDip).Visible)
        {
            reason = HiddenReason.Obstacle;
        }

        // Publishes a new state (→ OnStateChanged → Render) only if something changed…
        _state.SetHiddenReason(reason);

        // …but the position may have changed regardless (monitor, DPI, taskbar, overlay).
        Render();
    }

    private bool IsSnapLayoutsLikely()
    {
        // Windows 11 shows the Snap Layouts bar at the top centre while a window is dragged
        // towards the top edge — exactly where the island lives.
        return _foreground.IsMoveSizeActive &&
            _monitor is { } monitor &&
            CursorPosition.TryGet(out int x, out int y) &&
            monitor.Bounds.Contains(x, y) &&
            y < monitor.Bounds.Y + (int)(SnapLayoutsZoneDip * monitor.Scale);
    }

    private PlacementResult Place(MonitorDescriptor monitor, DipSize size) =>
        PlacementCalculator.Place(monitor, size, _placementOptions, Obstacles(monitor));

    private List<PixelRect> Obstacles(MonitorDescriptor monitor)
    {
        var obstacles = new List<PixelRect>(2);
        if (_foregroundSnapshot is { IsShellOverlay: true, Bounds.IsEmpty: false } overlay &&
            string.Equals(overlay.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase))
        {
            obstacles.Add(overlay.Bounds);
        }

        if (_autoHideTaskbar is { } taskbar && taskbar.IntersectsWith(monitor.Bounds))
        {
            obstacles.Add(taskbar);
        }

        return obstacles;
    }

    // ---- Rendering --------------------------------------------------------------------

    private void OnStateChanged(object? sender, IslandState e)
    {
        IslandState state = _state.State;
        if (!state.IsVisible && _interaction.Mode == InteractionMode.Interactive)
        {
            // Hiding ends any interaction; that publishes another state, rendered re-entrantly.
            _interaction.Dismiss();
            return;
        }

        _viewModel.Apply(state);
        Render();
    }

    private void Render()
    {
        IslandState state = _state.State;
        if (!state.IsVisible || _monitor is not { } monitor)
        {
            HideIsland();
            return;
        }

        PlacementResult placement = Place(monitor, state.SizeDip);
        PixelRect pill = placement.Bounds;

        // Clamping on a small display shrinks the island; render what actually fits.
        var target = new DipSize(pill.Width / monitor.Scale, pill.Height / monitor.Scale);
        _pointer.SetHotZone(pill);

        bool firstShow = !_controller.IsShown;
        bool monitorChanged = _renderedMonitor != monitor;
        bool sizeChanged = _renderedPill != target;
        if (!firstShow && !monitorChanged && !sizeChanged && _renderedBounds == pill)
        {
            return;
        }

        bool animate = sizeChanged && !firstShow && !monitorChanged && _renderedPill is not null && _window.CanAnimate;

        // While animating, the window covers both shapes plus room for the spring's overshoot.
        DipSize windowSize = target;
        if (animate)
        {
            DipSize union = DipSize.Max(_renderedPill!.Value, target);
            windowSize = new DipSize(union.Width + (2 * IslandMetrics.OvershootMargin), union.Height + IslandMetrics.OvershootMargin);
        }
        PixelRect windowRect = animate ? WindowRectAround(pill, windowSize, monitor) : pill;
        int generation = ++_transitionGeneration;
        _renderedPill = target;
        _renderedBounds = pill;
        _renderedMonitor = monitor;

        // Grow first (window = union of old and new shape), animate the shape on the
        // compositor, then shrink the window to the final shape so no invisible area is left
        // capturing clicks.
        // Size the window first: if the transition completes synchronously (no animation) its
        // completion must have the last word on the bounds.
        _controller.SetBounds(windowRect);
        _window.TransitionTo(windowSize, target, state.Size, animate, () =>
        {
            if (generation == _transitionGeneration && windowRect != pill)
            {
                _window.TransitionTo(target, target, state.Size, animate: false, completed: null);
                _controller.SetBounds(pill);
            }
        });

        if (firstShow)
        {
            ShowIsland();
        }
    }

    private static PixelRect WindowRectAround(PixelRect pill, DipSize windowSize, MonitorDescriptor monitor)
    {
        int width = Math.Max(pill.Width, (int)Math.Ceiling(windowSize.Width * monitor.Scale));
        int height = Math.Max(pill.Height, (int)Math.Ceiling(windowSize.Height * monitor.Scale));
        int x = pill.X + (pill.Width / 2) - (width / 2);
        return new PixelRect(x, pill.Y, width, height).ClampInside(monitor.Bounds);
    }

    private void ShowIsland()
    {
        AppLog.Info(nameof(IslandHost), $"Showing island at {_controller.Bounds} on {_monitor?.Id} ({_state.State.ModuleId}, {_state.State.Size})");
        _controller.Show();
        AppLog.Info(nameof(IslandHost), $"Frame: {_controller.DescribeFrame()}");
        _window.PlayShowAnimation();
        UpdatePointerWatcher();
    }

    private void HideIsland()
    {
        if (!_controller.IsShown)
        {
            return;
        }

        // Hide instantly: when a game or fullscreen video takes over, nothing should linger.
        AppLog.Info(nameof(IslandHost), $"Hiding island ({_state.State.HiddenReason}; foreground {_foregroundSnapshot.ProcessName} {_foregroundSnapshot.Kind})");
        _controller.Hide();
        _renderedPill = null;
        _renderedBounds = null;
        _renderedMonitor = null;
        _transitionGeneration++;
        UpdatePointerWatcher();
    }

    private void UpdatePointerWatcher()
    {
        // The low-level hook only exists while it can be useful; none in fullscreen/games.
        if (_controller.IsShown && _settings.HoverToInteract && !_disposed)
        {
            _pointer.Start();
        }
        else
        {
            _pointer.Stop();
        }
    }

    // ---- Interaction ------------------------------------------------------------------

    private void OnInteractionModeChanged(object? sender, InteractionMode mode)
    {
        bool interactive = mode == InteractionMode.Interactive;

        // Accept clicks before the island grows; go back to click-through before it shrinks.
        _controller.SetClickThrough(!interactive);
        if (!interactive)
        {
            _controller.ReleaseKeyboardFocus();
        }

        _state.SetMode(mode);
    }

    private void OnPointerInsideChanged(object? sender, bool inside)
    {
        _window.SetHover(inside);
        if (inside)
        {
            _interaction.PointerEntered();
        }
        else
        {
            _interaction.PointerExited();
        }
    }

    private void OnTextInputFocusChanged(object? sender, bool focused)
    {
        // Keyboard focus moves to the island only because the user clicked into a text field.
        if (focused && _interaction.Mode == InteractionMode.Interactive)
        {
            _interaction.KeyboardFocusChanged(true);
            _controller.TakeKeyboardFocus();
        }
        else if (!focused)
        {
            _interaction.KeyboardFocusChanged(false);
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _interaction.KeyboardFocusChanged(false);
        _interaction.OnOutsideActivity();
    }

    private void OnEscapePressed(object? sender, EventArgs e) => _interaction.Dismiss();

    private void OnFileDragChanged(object? sender, bool dragging)
    {
        _shelfModule.IsDropTarget = dragging;
        if (dragging)
        {
            _state.SelectModule(ShelfModule.ModuleId);
        }
    }

    /// <summary>A token saved in settings (DPAPI), else GITHUB_TOKEN / GH_TOKEN; public repos work without one.</summary>
    private static string? GitHubToken() =>
        SecretStore.Read("github") ??
        Environment.GetEnvironmentVariable("GITHUB_TOKEN") ??
        Environment.GetEnvironmentVariable("GH_TOKEN");

    private bool IsModuleEnabled(string id) => !_settings.DisabledModules.Contains(id, StringComparer.OrdinalIgnoreCase);

    private void OnHotkeyPressed(object? sender, int id)
    {
        if (id != HotkeyId || !_state.State.IsVisible)
        {
            return;
        }

        _interaction.Toggle();
        if (_interaction.Mode == InteractionMode.Interactive)
        {
            // A hotkey is explicit intent, and Windows lets the hotkey's owner take the foreground.
            _controller.TakeKeyboardFocus();
            _window.FocusContent();
        }
    }

    // ---- System events ----------------------------------------------------------------

    private void OnForegroundChanged(object? sender, EventArgs e) => _reevaluate.Trigger();

    private void OnForegroundSwitched(object? sender, EventArgs e)
    {
        _controller.EnsureTopmost();
        _interaction.OnOutsideActivity();
    }

    private void OnDisplayConfigurationChanged(object? sender, EventArgs e)
    {
        _monitors.Invalidate();
        RefreshTaskbar();
        _reevaluate.Trigger();
    }

    private void OnSystemSettingChanged(object? sender, string? area)
    {
        _monitors.Invalidate();
        RefreshTaskbar();
        if (area == "intl")
        {
            _clockModule.Refresh();
        }

        _reevaluate.Trigger();
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData();
        _clockModule.Refresh();
    }

    private void OnResumedFromSleep(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData();
        _clockModule.Refresh();
        _monitors.Invalidate();
        _ = Task.Run(_claudeSessions.Rescan);
        _reevaluate.Trigger();
    }

    private void OnIntegrationStatusChanged(object? sender, IntegrationStatusChange change) =>
        AppLog.Info(nameof(IntegrationHost), $"{change.Name}: {change.Status}{(change.Error is null ? string.Empty : $" ({change.Error})")}");

    private void RefreshTaskbar() => _autoHideTaskbar = TaskbarInfo.AutoHideTopTaskbar();

    private void RegisterHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = Hotkey.TryRegister(_window.Handle, HotkeyId, _settings.ActivationHotkey);
        if (_hotkey is null && !string.IsNullOrWhiteSpace(_settings.ActivationHotkey))
        {
            AppLog.Warn(nameof(IslandHost), $"Hotkey '{_settings.ActivationHotkey}' is invalid or already in use");
        }
    }

    // ---- Settings / tray --------------------------------------------------------------

    private void ApplyGameSettings()
    {
        _inspector.GameProcesses = _settings.GameProcesses.ToArray();
        _inspector.GamePathFragments = _settings.GamePathFragments.ToArray();
    }

    /// <summary>Focus mode and modules switched off in settings.</summary>
    private void ApplyModuleFilter()
    {
        IslandSettings settings = _settings;
        _controlsModule.IsFocusMode = settings.FocusMode;
        _state.SetFilter(m =>
            !settings.DisabledModules.Contains(m.Id, StringComparer.OrdinalIgnoreCase) &&
            (!settings.FocusMode || m.AllowedInFocus));
        _viewModel.RefreshSwitcher();
    }

    private void UpdateSettings(Func<IslandSettings, IslandSettings> change)
    {
        _settings = change(_settings);
        _placementOptions = _placementOptions with { TopMarginDip = _settings.TopMarginDip };
        _interaction.Options = _settings.ToInteractionOptions();
        ApplyGameSettings();
        ApplyModuleFilter();
        UpdatePointerWatcher();
        Reevaluate();
        _settingsViewModel?.Refresh();
        _github?.Refresh();
        _ = SaveSettingsAsync(_settings);
    }

    private async Task SaveSettingsAsync(IslandSettings settings)
    {
        try
        {
            await _settingsStore.SaveAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(IslandHost), "Saving settings failed", ex);
        }
    }

    /// <summary>The island's own tray menu entries (the app adds log/exit around them).</summary>
    public IReadOnlyList<TrayMenuItem> GetTrayMenuItems() =>
        TrayMenu.Build(_settings, StartupRegistration.IsEnabled(), _userHidden);

    /// <summary>Opens (or brings back) the settings window.</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsViewModel = new SettingsViewModel(() => _settings, UpdateSettings, _state.Modules, _spotify, () => _github?.Refresh());
            _settingsWindow = new SettingsWindow(_settingsViewModel);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _settingsViewModel = null;
            };
        }

        _settingsWindow.Activate();
    }

    public void HandleTrayCommand(int id)
    {
        switch (TrayMenu.Parse(id))
        {
            case TrayMenu.Command.OpenSettings:
                OpenSettings();
                break;
            case TrayMenu.Command.SetVisibility(VisibilityMode mode):
                UpdateSettings(s => s with { VisibilityMode = mode });
                break;
            case TrayMenu.Command.SetMonitor(MonitorPreference preference):
                UpdateSettings(s => s with { MonitorPreference = preference });
                break;
            case TrayMenu.Command.ToggleHover:
                UpdateSettings(s => s with { HoverToInteract = !s.HoverToInteract });
                break;
            case TrayMenu.Command.ToggleFocus:
                UpdateSettings(s => s with { FocusMode = !s.FocusMode });
                break;
            case TrayMenu.Command.ToggleOnlineArtwork:
                UpdateSettings(s => s with { OnlineArtworkLookup = !s.OnlineArtworkLookup });
                break;
            case TrayMenu.Command.ToggleStartup:
                StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
                break;
            case TrayMenu.Command.ToggleHidden:
                _userHidden = !_userHidden;
                Reevaluate();
                break;
        }
    }
}
