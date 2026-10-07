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
    private DateTimeOffset _attentionUntil;

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
            // The user is interacting now; a pending transient expansion is superseded.
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
        }

        _attentionTimer.Dispose();
    }

    private IslandState Compute()
    {
        IslandModule? attention = _attentionModule is { IsAvailable: true } a && _time.GetUtcNow() < _attentionUntil ? a : null;

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
            size = IslandSize.Expanded;
        }
        else
        {
            module = Best(m => m.CompactPriority) ?? _fallback;
            size = IslandSize.Compact;
        }

        DipSize sizeDip = module.GetSize(size);
        bool hasSwitcher = _mode == InteractionMode.Interactive && _modules.Count(m => m.IsAvailable) > 1;
        if (hasSwitcher)
        {
            sizeDip = sizeDip with { Height = sizeDip.Height + IslandMetrics.SwitcherHeight };
        }

        return new IslandState(_hiddenReason, module.Id, size, sizeDip, _mode, attention is not null, hasSwitcher);
    }

    private IslandModule? FindAvailable(string? id) =>
        id is null ? null : _modules.FirstOrDefault(m => m.IsAvailable && m.Id == id);

    private IslandModule? Best(Func<IslandModule, int> priority)
    {
        IslandModule? best = null;
        foreach (IslandModule module in _modules)
        {
            if (module.IsAvailable && priority(module) != ModulePriority.Unavailable &&
                (best is null || priority(module) > priority(best)))
            {
                best = module;
            }
        }

        return best;
    }

    private void OnModulePresentationChanged(object? sender, EventArgs e) => Recompute();

    private void OnModuleAttentionRequested(object? sender, TimeSpan duration)
    {
        // Never pop up over a game/fullscreen app, and never interrupt the user mid-interaction.
        if (sender is not IslandModule module || _hiddenReason != HiddenReason.None || _mode == InteractionMode.Interactive)
        {
            return;
        }

        _attentionModule = module;
        _attentionUntil = _time.GetUtcNow() + duration;
        _attentionTimer.Start(duration);
        Recompute();
    }

    private void ClearAttention()
    {
        _attentionModule = null;
        _attentionTimer.Cancel();
    }
}
