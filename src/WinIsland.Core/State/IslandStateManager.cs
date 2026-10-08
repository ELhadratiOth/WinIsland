using WinIsland.Core.Geometry;
using WinIsland.Core.Interaction;
using WinIsland.Core.Layout;
using WinIsland.Core.Modules;
using WinIsland.Core.Threading;
using WinIsland.Core.Visibility;

namespace WinIsland.Core.State;

/// <summary>
/// Context + state manager: combines module availability, transient attention requests,
/// the interaction mode and the visibility decision into a single <see cref="IslandState"/>.
/// Must be used on the UI thread.
/// </summary>
public sealed class IslandStateManager : IDisposable
{
    private readonly IReadOnlyList<IslandModule> _modules;
    private readonly IslandModule _fallback;
    private readonly TimeProvider _time;
    private readonly OneShotTimer _attentionTimer;

    private InteractionMode _mode = InteractionMode.Passive;
    private HiddenReason _hiddenReason = HiddenReason.None;
    private string? _selectedModuleId;
    private IslandModule? _attentionModule;
    private AttentionRequest? _attention;
    private DateTimeOffset _attentionUntil;
    private Func<IslandModule, bool> _filter = static _ => true;

    public IslandStateManager(IReadOnlyList<IslandModule> modules, TimeProvider time, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Count == 0)
        {
            throw new ArgumentException("At least one module is required.", nameof(modules));
        }

        _modules = modules;
        _fallback = modules[0];
        _time = time;
        _attentionTimer = new OneShotTimer(time, dispatcher, Recompute);

        foreach (IslandModule module in _modules)
        {
            module.PresentationChanged += OnModulePresentationChanged;
            module.AttentionRequested += OnModuleAttentionRequested;
            module.AttentionEnded += OnModuleAttentionEnded;
        }

        State = Compute();
    }

    public event EventHandler<IslandState>? StateChanged;

    public IslandState State { get; private set; }

    public IReadOnlyList<IslandModule> Modules => _modules;

    public void SetMode(InteractionMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        if (mode == InteractionMode.Passive)
        {
            _selectedModuleId = null;
        }
        else
        {
            // Pointing at a notice opens that module; the transient expansion itself is over.
            if (CurrentAttention() is { } attention)
            {
                _selectedModuleId = attention.Id;
            }

            ClearAttention();
        }

        Recompute();
    }

    public void SetHiddenReason(HiddenReason reason)
    {
        if (_hiddenReason == reason)
        {
            return;
        }

        _hiddenReason = reason;
        if (reason != HiddenReason.None)
        {
            // Don't replay stale notifications when the island comes back after a game.
            ClearAttention();
        }

        Recompute();
    }

    /// <summary>
    /// Restricts which modules may show at all (focus mode, modules switched off in settings).
    /// The clock (first module) is always allowed as the fallback.
    /// </summary>
    public void SetFilter(Func<IslandModule, bool> filter)
    {
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        if (_attentionModule is { } module && !Allowed(module))
        {
            ClearAttention();
        }

        Recompute();
    }

    public bool Allowed(IslandModule module) => module == _fallback || _filter(module);

    /// <summary>Lets the user pick a module from the switcher while interacting.</summary>
    public void SelectModule(string? moduleId)
    {
        if (_mode != InteractionMode.Interactive || _selectedModuleId == moduleId)
        {
            return;
        }

        _selectedModuleId = moduleId;
        Recompute();
    }

    public void Recompute()
    {
        IslandState next = Compute();
        if (next == State)
        {
            return;
        }

        State = next;
        StateChanged?.Invoke(this, next);
    }

    public void Dispose()
    {
        foreach (IslandModule module in _modules)
        {
            module.PresentationChanged -= OnModulePresentationChanged;
            module.AttentionRequested -= OnModuleAttentionRequested;
            module.AttentionEnded -= OnModuleAttentionEnded;
        }

        _attentionTimer.Dispose();
    }

    private IslandState Compute()
    {
        IslandModule? attention = CurrentAttention();

        IslandModule module;
        IslandSize size;
        if (_mode == InteractionMode.Interactive)
        {
            module = FindAvailable(_selectedModuleId)
                ?? attention
                ?? Best(m => m.InteractivePriority)
                ?? _fallback;
            size = module.InteractiveSize;
            attention = null;
        }
        else if (attention is not null)
        {
            module = attention;
            size = _attention!.Size;
        }
        else
        {
            module = Best(m => m.CompactPriority) ?? _fallback;
            size = IslandSize.Compact;
        }

        DipSize sizeDip = module.GetSize(size);
        int chips = _mode == InteractionMode.Interactive ? SwitcherModules().Count() : 0;
        bool hasSwitcher = chips > 1;
        if (hasSwitcher)
        {
            sizeDip = new DipSize(
                Math.Max(sizeDip.Width, IslandMetrics.SwitcherMinWidth(chips)),
                sizeDip.Height + IslandMetrics.SwitcherHeight);
        }

        return new IslandState(_hiddenReason, module.Id, size, sizeDip, _mode, attention is not null, hasSwitcher);
    }

    /// <summary>Modules offered in the switcher strip while interacting.</summary>
    public IEnumerable<IslandModule> SwitcherModules() =>
        _modules.Where(m => m.IsAvailable && m.ShowInSwitcher && Allowed(m));

    private IslandModule? CurrentAttention() =>
        _attentionModule is { IsAvailable: true } a && Allowed(a) && _time.GetUtcNow() < _attentionUntil ? a : null;

    private IslandModule? FindAvailable(string? id) =>
        id is null ? null : _modules.FirstOrDefault(m => m.IsAvailable && Allowed(m) && m.Id == id);

    private IslandModule? Best(Func<IslandModule, int> priority)
    {
        IslandModule? best = null;
        foreach (IslandModule module in _modules)
        {
            if (module.IsAvailable && Allowed(module) && priority(module) != ModulePriority.Unavailable &&
                (best is null || priority(module) > priority(best)))
            {
                best = module;
            }
        }

        return best;
    }

    private void OnModulePresentationChanged(object? sender, EventArgs e) => Recompute();

    private void OnModuleAttentionRequested(object? sender, AttentionRequest request)
    {
        // Never pop up over a game/fullscreen app, never interrupt the user mid-interaction,
        // and never cut a more important notice short.
        if (sender is not IslandModule module || !Allowed(module) ||
            _hiddenReason != HiddenReason.None || _mode == InteractionMode.Interactive ||
            (CurrentAttention() is { } current && current != module && _attention!.Priority > request.Priority))
        {
            return;
        }

        _attentionModule = module;
        _attention = request;
        _attentionUntil = _time.GetUtcNow() + request.Duration;
        _attentionTimer.Start(request.Duration);
        Recompute();
    }

    private void OnModuleAttentionEnded(object? sender, EventArgs e)
    {
        if (sender is IslandModule module && module == _attentionModule)
        {
            ClearAttention();
            Recompute();
        }
    }

    private void ClearAttention()
    {
        _attentionModule = null;
        _attention = null;
        _attentionTimer.Cancel();
    }
}
