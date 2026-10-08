using Microsoft.UI.Dispatching;
using WinIsland.Core.Claude;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Display;
using WinIsland.Core.Geometry;
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
using WinIsland.Platform.Windows.Display;
using WinIsland.Platform.Windows.Foreground;
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
        _mediaModule = new MediaModule(_media, _time, _dispatcher);
        _claudeModule = new ClaudeModule(_claudeSessions, new ClaudeCliMessenger(), _dispatcher, _time);
        _state = new IslandStateManager([_clockModule, _mediaModule, _claudeModule], _time, _dispatcher);
        _interaction = new InteractionController(_time, _dispatcher, _settings.ToInteractionOptions());
        _viewModel = new IslandViewModel(_state, _clockModule, _mediaModule, _claudeModule);

        // Offline-capable sources first; network-bound ones (calendar, usage…) slot in here and
        // are paused automatically while offline.
        _integrations = new IntegrationHost(
            systemMedia is null ? [_claudeSessions] : [systemMedia, _claudeSessions],
            _connectivity,
            _time);

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
        AppLog.Info(nameof(IslandHost), $"Monitors: {string.Join("; ", _monitors.GetMonitors().Select(m => $"{m.Id} {m.Bounds} work {m.WorkArea} x{m.Scale}{(m.IsPrimary ? " primary" : string.Empty)}"))}");
        _connectivity.Start();
        _integrations.Start();
        _preview?.Run(_dispatcherQueue, this);
    }

    void Preview.IPreviewTarget.PreviewActivate() => _interaction.Activate();

    void Preview.IPreviewTarget.PreviewSelect(string moduleId) => _state.SelectModule(moduleId);

    void Preview.IPreviewTarget.PreviewDismiss() => _interaction.Dismiss();

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
        _connectivity.Dispose();
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

    private void UpdateSettings(Func<IslandSettings, IslandSettings> change)
    {
        _settings = change(_settings);
        _placementOptions = _placementOptions with { TopMarginDip = _settings.TopMarginDip };
        _interaction.Options = _settings.ToInteractionOptions();
        ApplyGameSettings();
        UpdatePointerWatcher();
        Reevaluate();
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

    public void HandleTrayCommand(int id)
    {
        switch (TrayMenu.Parse(id))
        {
            case TrayMenu.Command.SetVisibility(VisibilityMode mode):
                UpdateSettings(s => s with { VisibilityMode = mode });
                break;
            case TrayMenu.Command.SetMonitor(MonitorPreference preference):
                UpdateSettings(s => s with { MonitorPreference = preference });
                break;
            case TrayMenu.Command.ToggleHover:
                UpdateSettings(s => s with { HoverToInteract = !s.HoverToInteract });
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
