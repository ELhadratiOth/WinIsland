using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;
using WinIsland.App.Animation;
using WinIsland.Core.Geometry;
using WinIsland.Core.Layout;
using WinIsland.Core.Modules;
using WinIsland.Core.ViewModels;
using WinRT.Interop;
using WinUIEx;

namespace WinIsland.App;

/// <summary>
/// The island surface. Knows how to draw and animate itself; all decisions about where it
/// goes, when it is visible and whether it accepts input are made by <see cref="Services.IslandHost"/>.
/// </summary>
public sealed partial class IslandWindow : Window
{
    private readonly PillAnimator _pill;
    private readonly ActivityAnimations _activity;
    private readonly UISettings _uiSettings = new();
    private DipSize _pillSize;
    private double _previousArea;
    private bool _allowClose;

    public IslandWindow(IslandViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // x:Bind on a Window initialises lazily (on activation); the island is shown without
        // ever being activated, so initialise the bindings explicitly.
        Bindings.Update();

        Handle = WindowNative.GetWindowHandle(this);

        // Fully transparent window; the pill's shape comes from a composition clip.
        if (!Services.Experiments.Has("nobackdrop"))
        {
            SystemBackdrop = new TransparentTintBackdrop();
        }

        // A context-menu presenter is WinUI's own borderless, non-resizable popup flavour.
        OverlappedPresenter presenter = Services.Experiments.Has("plainpresenter") ? OverlappedPresenter.Create() : OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        if (Services.Experiments.Has("plainpresenter"))
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "WinIsland";

        // Alt+F4 on the focused island must not leave a headless process behind; exit is via the tray.
        AppWindow.Closing += (_, args) => args.Cancel = !_allowClose;

        _pill = new PillAnimator(PillSurface, BackgroundHost);
        _activity = new ActivityAnimations(_pill.Compositor);
        _activity.AddBars(CompactBars.Children.OfType<FrameworkElement>());
        _activity.AddBars(ExpandedBars.Children.OfType<FrameworkElement>());
        _activity.AddSpinner(CompactClaudeGlyph);
        _activity.AddSpinner(PanelClaudeGlyph);
        ConfigureContentTransitions();

        AccentBrush.Color = Converters.ToColor(ViewModel.Media.AccentColor);
        ViewModel.PropertyChanged += (_, _) => UpdateActivity();
        ViewModel.Media.PropertyChanged += OnMediaPropertyChanged;
        ViewModel.Claude.PropertyChanged += (_, _) => UpdateActivity();

        // Clicking the text box of a not-yet-focused island: TextBox marks pointer presses as
        // handled, so listen for handled events too.
        MessageInput.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTextInputPointerPressed), handledEventsToo: true);
        Activated += OnActivated;

        // Diagnostic: confirms XAML content actually loaded and rendered.
        RootGrid.Loaded += (_, _) => Core.Diagnostics.AppLog.Info(nameof(IslandWindow), $"Content loaded (scale {RootGrid.XamlRoot?.RasterizationScale})");
    }

    public IslandViewModel ViewModel { get; }

    /// <summary>Tint taken from the current cover art (waveform, progress, glow).</summary>
    public SolidColorBrush AccentBrush { get; } = new(Microsoft.UI.Colors.White);

    public nint Handle { get; }

    public event EventHandler? EscapePressed;

    /// <summary>A text field gained (true) or lost (false) focus.</summary>
    public event EventHandler<bool>? TextInputFocusChanged;

    /// <summary>The window lost activation (the user clicked or switched elsewhere).</summary>
    public event EventHandler? Deactivated;

    private bool AnimationsEnabled => _uiSettings.AnimationsEnabled;

    /// <summary>
    /// Moves the visible pill to <paramref name="pillSize"/> inside a window currently sized
    /// <paramref name="windowSize"/> (both DIPs). The pill is horizontally centred and top-aligned.
    /// </summary>
    public void TransitionTo(DipSize windowSize, DipSize pillSize, IslandSize kind, bool animate, Action? completed)
    {
        ContentHost.Width = pillSize.Width;
        ContentHost.Height = pillSize.Height;

        var offset = new Vector2((float)((windowSize.Width - pillSize.Width) / 2), 0);
        var size = new Vector2((float)pillSize.Width, (float)pillSize.Height);
        float radius = (float)IslandMetrics.CornerRadius(pillSize, kind);

        if (!animate || !AnimationsEnabled)
        {
            _pill.Snap(offset, size, radius);
            _pillSize = pillSize;
            _previousArea = pillSize.Width * pillSize.Height;
            completed?.Invoke();
            return;
        }

        // The window may just have grown around the current shape: keep that shape centred,
        // then animate from it.
        _pill.Reanchor(new Vector2((float)((windowSize.Width - _pillSize.Width) / 2), 0));
        _pillSize = pillSize;
        bool growing = pillSize.Width * pillSize.Height >= _previousArea;
        _previousArea = pillSize.Width * pillSize.Height;
        _pill.Animate(offset, size, radius, growing, () => completed?.Invoke());
    }

    public void PlayShowAnimation()
    {
        if (AnimationsEnabled)
        {
            _pill.FadeIn(IslandMetrics.FadeDuration);
        }
    }

    /// <summary>Puts keyboard focus on the most useful element after a hotkey activation.</summary>
    public void FocusContent()
    {
        if (ViewModel.ShowClaudeLarge)
        {
            MessageInput.Focus(FocusState.Programmatic);
        }
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>Brightens the island's edge while the pointer rests on it (before it turns interactive).</summary>
    public void SetHover(bool hover) => _pill.SetHover(hover);

    private void ConfigureContentTransitions()
    {
        if (!AnimationsEnabled)
        {
            return;
        }

        // Layers fade/slide on the compositor when their Visibility flips; XAML keeps a hiding
        // layer rendered until its exit completes. The incoming layer waits a beat for the
        // shape to start growing so content never spills outside it. Each element gets its own
        // animation objects.
        UIElement[] layers =
        [
            CompactClockLayer, CompactMediaLayer, CompactClaudeLayer, ClockLayer, MediaLayer,
            ClaudeNoticeLayer, ClaudePanelLayer, Switcher, AmbientGlow,
        ];
        foreach (UIElement layer in layers)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(layer, true);
            ElementCompositionPreview.SetImplicitShowAnimation(layer, _pill.CreateEnter(TimeSpan.FromMilliseconds(70)));
            ElementCompositionPreview.SetImplicitHideAnimation(layer, _pill.CreateExit());
        }
    }

    private void OnMediaPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Core.Modules.MediaModule.AccentColor))
        {
            AccentBrush.Color = Converters.ToColor(ViewModel.Media.AccentColor);
        }

        UpdateActivity();
    }

    /// <summary>Loops run only while visible and meaningful, so an idle island costs no GPU time.</summary>
    private void UpdateActivity()
    {
        bool visible = ViewModel.State.IsVisible && AnimationsEnabled;
        _activity.SetBarsRunning(visible && ViewModel.Media.IsPlaying && (ViewModel.ShowMediaCompact || ViewModel.ShowMediaExpanded));
        _activity.SetSpinnersRunning(visible && ViewModel.Claude.HasActiveSessions && (ViewModel.ShowClaudeCompact || ViewModel.ShowClaudeLarge));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        EscapePressed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTextInputPointerPressed(object sender, PointerRoutedEventArgs e) =>
        TextInputFocusChanged?.Invoke(this, true);

    private void OnTextInputGotFocus(object sender, RoutedEventArgs e) =>
        TextInputFocusChanged?.Invoke(this, true);

    private void OnTextInputLostFocus(object sender, RoutedEventArgs e) =>
        TextInputFocusChanged?.Invoke(this, false);

    private void OnMessageInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.Claude.SendCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.Claude.SendCommand.Execute(null);
        }
    }

    private void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Claude.SelectedSession = SessionList.SelectedItem as ClaudeSessionItem;

    private void OnModuleButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string moduleId })
        {
            ViewModel.SelectModule(moduleId);
        }
    }
}
