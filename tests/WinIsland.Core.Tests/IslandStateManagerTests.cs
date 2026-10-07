using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Interaction;
using WinIsland.Core.Layout;
using WinIsland.Core.Modules;
using WinIsland.Core.State;
using WinIsland.Core.Visibility;

namespace WinIsland.Core.Tests;

public class IslandStateManagerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 41, 30, TimeSpan.Zero));
    private readonly InlineDispatcher _dispatcher = new();
    private readonly ClockModule _clock;
    private readonly TestModule _media = new("media", IslandSize.Expanded);
    private readonly TestModule _panel = new("panel", IslandSize.Large);
    private readonly IslandStateManager _manager;

    public IslandStateManagerTests()
    {
        _clock = new ClockModule(_time, _dispatcher);
        _manager = new IslandStateManager([_clock, _media, _panel], _time, _dispatcher);
    }

    [Fact]
    public void Defaults_to_the_compact_clock()
    {
        Assert.Equal(ClockModule.ModuleId, _manager.State.ModuleId);
        Assert.Equal(IslandSize.Compact, _manager.State.Size);
        Assert.True(_manager.State.IsVisible);
    }

    [Fact]
    public void Compact_shows_the_highest_priority_available_module()
    {
        _media.Set(available: true, compact: ModulePriority.Media, interactive: ModulePriority.Media);

        Assert.Equal("media", _manager.State.ModuleId);
        Assert.Equal(IslandSize.Compact, _manager.State.Size);
    }

    [Fact]
    public void Attention_expands_temporarily_then_collapses()
    {
        _media.Set(available: true, compact: ModulePriority.Background, interactive: ModulePriority.Media);

        _media.Attention(TimeSpan.FromSeconds(3));
        Assert.Equal("media", _manager.State.ModuleId);
        Assert.Equal(IslandSize.Expanded, _manager.State.Size);
        Assert.True(_manager.State.IsAttention);

        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ClockModule.ModuleId, _manager.State.ModuleId);
        Assert.Equal(IslandSize.Compact, _manager.State.Size);
    }

    [Fact]
    public void Attention_is_ignored_while_hidden_for_fullscreen()
    {
        _media.Set(available: true, compact: ModulePriority.Background, interactive: ModulePriority.Media);
        _manager.SetHiddenReason(HiddenReason.Game);

        _media.Attention(TimeSpan.FromSeconds(3));
        _manager.SetHiddenReason(HiddenReason.None);

        Assert.False(_manager.State.IsAttention);
        Assert.Equal(IslandSize.Compact, _manager.State.Size);
    }

    [Fact]
    public void Interaction_uses_the_module_interactive_size()
    {
        _panel.Set(available: true, compact: ModulePriority.Unavailable, interactive: ModulePriority.Activity);

        _manager.SetMode(InteractionMode.Interactive);

        Assert.Equal("panel", _manager.State.ModuleId);
        Assert.Equal(IslandSize.Large, _manager.State.Size);

        // Clock + panel are both available, so the switcher strip is added below the content.
        Assert.True(_manager.State.HasSwitcher);
        Assert.Equal(IslandMetrics.Large with { Height = IslandMetrics.Large.Height + IslandMetrics.SwitcherHeight }, _manager.State.SizeDip);
    }

    [Fact]
    public void No_switcher_when_only_one_module_is_available()
    {
        _manager.SetMode(InteractionMode.Interactive);

        Assert.False(_manager.State.HasSwitcher);
        Assert.Equal(_clock.GetSize(IslandSize.Expanded), _manager.State.SizeDip);
    }

    [Fact]
    public void User_selection_applies_only_while_interacting()
    {
        _media.Set(available: true, compact: ModulePriority.Media, interactive: ModulePriority.Media);
        _panel.Set(available: true, compact: ModulePriority.Unavailable, interactive: ModulePriority.Background);

        _manager.SelectModule("panel");
        Assert.Equal("media", _manager.State.ModuleId);

        _manager.SetMode(InteractionMode.Interactive);
        Assert.Equal("media", _manager.State.ModuleId);

        _manager.SelectModule("panel");
        Assert.Equal("panel", _manager.State.ModuleId);

        _manager.SetMode(InteractionMode.Passive);
        _manager.SetMode(InteractionMode.Interactive);
        Assert.Equal("media", _manager.State.ModuleId);
    }

    [Fact]
    public void Publishes_only_real_changes()
    {
        int changes = 0;
        _manager.StateChanged += (_, _) => changes++;

        _manager.SetHiddenReason(HiddenReason.None);
        _manager.SetMode(InteractionMode.Passive);
        _manager.Recompute();
        Assert.Equal(0, changes);

        _manager.SetHiddenReason(HiddenReason.Fullscreen);
        Assert.Equal(1, changes);
        Assert.False(_manager.State.IsVisible);
    }

    private sealed class TestModule(string id, IslandSize interactiveSize) : IslandModule(id, id, "x")
    {
        public override IslandSize InteractiveSize => interactiveSize;

        public void Set(bool available, int compact, int interactive)
        {
            IsAvailable = available;
            CompactPriority = compact;
            InteractivePriority = interactive;
        }

        public void Attention(TimeSpan duration) => RequestAttention(duration);
    }
}
