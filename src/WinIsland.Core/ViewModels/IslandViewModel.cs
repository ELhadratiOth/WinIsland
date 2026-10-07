using System.ComponentModel;
using WinIsland.Core.Interaction;
using WinIsland.Core.Layout;
using WinIsland.Core.Modules;
using WinIsland.Core.Mvvm;
using WinIsland.Core.State;

namespace WinIsland.Core.ViewModels;

/// <summary>
/// Projection of <see cref="IslandState"/> and the modules into bindable properties.
/// Properties only raise change notifications when their value changes, so XAML does no
/// work while the island sits idle.
/// </summary>
public sealed class IslandViewModel : ObservableObject, IDisposable
{
    private readonly IslandStateManager _stateManager;
    private IslandState _state;
    private IslandModule _activeModule;

    public IslandViewModel(IslandStateManager stateManager, ClockModule clock, MediaModule media, ClaudeModule claude)
    {
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        Clock = clock;
        Media = media;
        Claude = claude;

        _state = stateManager.State;
        _activeModule = FindModule(_state.ModuleId);
        _activeModule.PropertyChanged += OnActiveModulePropertyChanged;
        Claude.PropertyChanged += OnClaudePropertyChanged;
        foreach (IslandModule module in _stateManager.Modules)
        {
            module.PresentationChanged += OnModulePresentationChanged;
        }

        UpdateSwitcher();
        UpdateViewActivity();
    }

    public ClockModule Clock { get; }

    public MediaModule Media { get; }

    public ClaudeModule Claude { get; }

    public IslandState State => _state;

    public bool IsCompact => _state.Size == IslandSize.Compact;

    public bool IsInteractive => _state.Mode == InteractionMode.Interactive;

    public string CompactGlyph => _activeModule.Glyph;

    public string CompactText => _activeModule.CompactText;

    /// <summary>Small status dot in the compact pill when Claude is working but another module is shown.</summary>
    public bool ShowActivityDot => IsCompact && Claude.HasActiveSessions && _activeModule != Claude;

    public bool ShowClockExpanded => Is(ClockModule.ModuleId, IslandSize.Expanded);

    public bool ShowMediaExpanded => Is(MediaModule.ModuleId, IslandSize.Expanded);

    public bool ShowClaudeExpanded => Is(ClaudeModule.ModuleId, IslandSize.Expanded);

    public bool ShowClaudeLarge => Is(ClaudeModule.ModuleId, IslandSize.Large);

    /// <summary>Module switcher strip, shown only while interacting and when there's more than one choice.</summary>
    public bool ShowSwitcher => _state.IsVisible && _state.HasSwitcher;

    public IReadOnlyList<IslandModule> SwitcherModules { get; private set; } = [];

    public void SelectModule(string moduleId) => _stateManager.SelectModule(moduleId);

    public void Dispose()
    {
        _activeModule.PropertyChanged -= OnActiveModulePropertyChanged;
        Claude.PropertyChanged -= OnClaudePropertyChanged;
        foreach (IslandModule module in _stateManager.Modules)
        {
            module.PresentationChanged -= OnModulePresentationChanged;
        }
    }

    /// <summary>Called by the host after the state manager publishes a new state.</summary>
    public void Apply(IslandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state == _state)
        {
            return;
        }

        bool modeChanged = state.Mode != _state.Mode;
        _state = state;

        IslandModule module = FindModule(state.ModuleId);
        if (module != _activeModule)
        {
            _activeModule.PropertyChanged -= OnActiveModulePropertyChanged;
            _activeModule = module;
            _activeModule.PropertyChanged += OnActiveModulePropertyChanged;
            OnPropertyChanged(nameof(CompactGlyph));
            OnPropertyChanged(nameof(CompactText));
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(ShowActivityDot));
        OnPropertyChanged(nameof(ShowClockExpanded));
        OnPropertyChanged(nameof(ShowMediaExpanded));
        OnPropertyChanged(nameof(ShowClaudeExpanded));
        OnPropertyChanged(nameof(ShowClaudeLarge));
        OnPropertyChanged(nameof(ShowSwitcher));
        if (modeChanged)
        {
            OnPropertyChanged(nameof(IsInteractive));
        }

        UpdateViewActivity();
    }

    private bool Is(string moduleId, IslandSize size) =>
        _state.IsVisible && _state.ModuleId == moduleId && _state.Size == size;

    private IslandModule FindModule(string id) =>
        _stateManager.Modules.FirstOrDefault(m => m.Id == id) ?? _stateManager.Modules[0];

    private void UpdateViewActivity()
    {
        foreach (IslandModule module in _stateManager.Modules)
        {
            module.IsViewActive = _state.IsVisible && _state.Size != IslandSize.Compact && module == _activeModule;
        }
    }

    private void UpdateSwitcher()
    {
        List<IslandModule> available = _stateManager.Modules.Where(m => m.IsAvailable).ToList();
        if (!available.SequenceEqual(SwitcherModules))
        {
            SwitcherModules = available;
            OnPropertyChanged(nameof(SwitcherModules));
        }
    }

    private void OnModulePresentationChanged(object? sender, EventArgs e) => UpdateSwitcher();

    private void OnActiveModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandModule.CompactText))
        {
            OnPropertyChanged(nameof(CompactText));
        }
    }

    private void OnClaudePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClaudeModule.HasActiveSessions))
        {
            OnPropertyChanged(nameof(ShowActivityDot));
        }
    }
}
