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

    [Fact]
    public void A_lower_priority_notice_never_cuts_a_more_important_one_short()
    {
        _media.Set(available: true, compact: ModulePriority.Background, interactive: ModulePriority.Media);
        _panel.Set(available: true, compact: ModulePriority.Unavailable, interactive: ModulePriority.Background);

        _panel.Attention(TimeSpan.FromSeconds(30), AttentionPriority.Blocking);
        _media.Attention(TimeSpan.FromSeconds(3), AttentionPriority.Feedback);
        Assert.Equal("panel", _manager.State.ModuleId);

        _panel.End();
        Assert.False(_manager.State.IsAttention);
        _media.Attention(TimeSpan.FromSeconds(3), AttentionPriority.Feedback, IslandSize.Compact);
        Assert.Equal("media", _manager.State.ModuleId);
        Assert.Equal(IslandSize.Compact, _manager.State.Size);
        Assert.True(_manager.State.IsAttention);
    }

    [Fact]
    public void Hovering_a_notice_opens_that_module()
    {
        _media.Set(available: true, compact: ModulePriority.Media, interactive: ModulePriority.Media);
        _panel.Set(available: true, compact: ModulePriority.Unavailable, interactive: ModulePriority.Background);

        _panel.Attention(TimeSpan.FromSeconds(5));
        _manager.SetMode(InteractionMode.Interactive);

        Assert.Equal("panel", _manager.State.ModuleId);
        Assert.Equal(IslandSize.Large, _manager.State.Size);
        Assert.False(_manager.State.IsAttention);
    }

    [Fact]
    public void Filtered_modules_never_show_but_the_clock_always_can()
    {
        _media.Set(available: true, compact: ModulePriority.Media, interactive: ModulePriority.Media);
        _manager.SetFilter(m => m.Id != "media");

        Assert.Equal(ClockModule.ModuleId, _manager.State.ModuleId);
        _media.Attention(TimeSpan.FromSeconds(3));
        Assert.False(_manager.State.IsAttention);
        Assert.DoesNotContain(_media, _manager.SwitcherModules());

        _manager.SetFilter(_ => false);
        Assert.Contains(_clock, _manager.SwitcherModules());
    }

    [Fact]
    public void The_island_is_at_least_as_wide_as_its_switcher()
    {
        var many = Enumerable.Range(0, 12).Select(i => new TestModule($"m{i}", IslandSize.Expanded)).ToList();
        foreach (TestModule m in many)
        {
            m.Set(available: true, compact: ModulePriority.Unavailable, interactive: ModulePriority.Background);
        }

        using var manager = new IslandStateManager([_clock, .. many], _time, _dispatcher);
        manager.SetMode(InteractionMode.Interactive);

        Assert.Equal(ClockModule.ModuleId, manager.State.ModuleId);
        Assert.Equal(IslandMetrics.SwitcherMinWidth(13), manager.State.SizeDip.Width);
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

        public void Attention(TimeSpan duration, int priority = AttentionPriority.Normal, IslandSize size = IslandSize.Expanded) =>
            RequestAttention(duration, priority, size);

        public void End() => EndAttention();
    }
}
