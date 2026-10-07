using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Interaction;

namespace WinIsland.Core.Tests;

public class InteractionControllerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly InteractionController _controller;

    public InteractionControllerTests()
    {
        _controller = new InteractionController(_time, new InlineDispatcher(), new InteractionOptions
        {
            HoverDwell = TimeSpan.FromMilliseconds(180),
            LeaveGrace = TimeSpan.FromMilliseconds(400),
        });
    }

    [Fact]
    public void Starts_passive()
    {
        Assert.Equal(InteractionMode.Passive, _controller.Mode);
    }

    [Fact]
    public void Becomes_interactive_after_hover_dwell()
    {
        _controller.PointerEntered();
        _time.Advance(TimeSpan.FromMilliseconds(179));
        Assert.Equal(InteractionMode.Passive, _controller.Mode);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(InteractionMode.Interactive, _controller.Mode);
    }

    [Fact]
    public void Pointer_passing_over_the_island_stays_click_through()
    {
        _controller.PointerEntered();
        _time.Advance(TimeSpan.FromMilliseconds(60));
        _controller.PointerExited();
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(InteractionMode.Passive, _controller.Mode);
    }

    [Fact]
    public void Returns_to_passive_after_leave_grace()
    {
        EnterInteractive();

        _controller.PointerExited();
        _time.Advance(TimeSpan.FromMilliseconds(399));
        Assert.Equal(InteractionMode.Interactive, _controller.Mode);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(InteractionMode.Passive, _controller.Mode);
    }

    [Fact]
    public void Coming_back_within_grace_keeps_interaction()
    {
        EnterInteractive();

        _controller.PointerExited();
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _controller.PointerEntered();
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(InteractionMode.Interactive, _controller.Mode);
    }

    [Fact]
    public void Keyboard_focus_keeps_the_island_interactive_until_released()
    {
        EnterInteractive();
        _controller.KeyboardFocusChanged(true);

        _controller.PointerExited();
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(InteractionMode.Interactive, _controller.Mode);

        _controller.KeyboardFocusChanged(false);
        _time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(InteractionMode.Passive, _controller.Mode);
    }

    [Fact]
    public void Explicit_activation_is_pinned_until_outside_activity()
    {
        _controller.Activate();
        Assert.Equal(InteractionMode.Interactive, _controller.Mode);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(InteractionMode.Interactive, _controller.Mode);

        _controller.OnOutsideActivity();
        Assert.Equal(InteractionMode.Passive, _controller.Mode);
    }

    [Fact]
    public void Pinned_activation_follows_hover_rules_once_the_pointer_visits()
    {
        _controller.Activate();
        _controller.PointerEntered();
        _controller.PointerExited();

        _time.Advance(TimeSpan.FromMilliseconds(400));

        Assert.Equal(InteractionMode.Passive, _controller.Mode);
    }

    [Fact]
    public void Dismiss_does_not_reactivate_until_the_pointer_leaves_and_returns()
    {
        EnterInteractive();
        _controller.Dismiss();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(InteractionMode.Passive, _controller.Mode);

        _controller.PointerEntered();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(InteractionMode.Passive, _controller.Mode);

        _controller.PointerExited();
        _controller.PointerEntered();
        _time.Advance(TimeSpan.FromMilliseconds(180));
        Assert.Equal(InteractionMode.Interactive, _controller.Mode);
    }

    [Fact]
    public void Hover_can_be_disabled()
    {
        _controller.Options = _controller.Options with { HoverToInteract = false };

        _controller.PointerEntered();
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(InteractionMode.Passive, _controller.Mode);
    }

    [Fact]
    public void Raises_mode_changed_once_per_transition()
    {
        var modes = new List<InteractionMode>();
        _controller.ModeChanged += (_, m) => modes.Add(m);

        EnterInteractive();
        _controller.Activate();
        _controller.Dismiss();
        _controller.Dismiss();

        Assert.Equal([InteractionMode.Interactive, InteractionMode.Passive], modes);
    }

    private void EnterInteractive()
    {
        _controller.PointerEntered();
        _time.Advance(TimeSpan.FromMilliseconds(180));
        Assert.Equal(InteractionMode.Interactive, _controller.Mode);
    }
}
