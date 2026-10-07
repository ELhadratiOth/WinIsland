using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace WinIsland.App.Animation;

/// <summary>
/// Shapes and animates the island purely on the compositor: the surface's visual is clipped
/// to a rounded rectangle whose size/offset/radius are animated with keyframe animations.
/// No XAML layout runs per frame, so resizing is smooth at 60–165 Hz and costs nothing once
/// the animation ends (the compositor goes idle).
/// </summary>
internal sealed class PillAnimator
{
    private readonly Compositor _compositor;
    private readonly Visual _visual;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionEasingFunction _resizeEasing;
    private readonly CompositionEasingFunction _fadeEasing;
    private CompositionScopedBatch? _batch;
    private Action? _pendingCompletion;

    public PillAnimator(UIElement surface)
    {
        _visual = ElementCompositionPreview.GetElementVisual(surface);
        _compositor = _visual.Compositor;
        _geometry = _compositor.CreateRoundedRectangleGeometry();
        _visual.Clip = _compositor.CreateGeometricClip(_geometry);

        // Fast out, gentle settle: reads as "springy" without the cost of a physics animation.
        _resizeEasing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1f));
        _fadeEasing = _compositor.CreateLinearEasingFunction();
    }

    public Compositor Compositor => _compositor;

    /// <summary>Moves the clip instantly (used to re-anchor before the window is resized).</summary>
    public void Snap(Vector2 offset, Vector2 size, float cornerRadius)
    {
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.Offset));
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.Size));
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.CornerRadius));
        _geometry.Offset = offset;
        _geometry.Size = size;
        _geometry.CornerRadius = new Vector2(cornerRadius);
    }

    /// <summary>Re-anchors the current (possibly mid-animation) shape at a new horizontal offset.</summary>
    public void Reanchor(Vector2 offset)
    {
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.Offset));
        _geometry.Offset = offset;
    }

    public void Animate(Vector2 offset, Vector2 size, float cornerRadius, TimeSpan duration, Action completed)
    {
        if (_batch is not null)
        {
            // A superseded transition must not run its completion (window shrink) later.
            _batch.Completed -= OnBatchCompleted;
        }

        _pendingCompletion = completed;
        _batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Start(nameof(CompositionRoundedRectangleGeometry.Offset), offset, duration);
        Start(nameof(CompositionRoundedRectangleGeometry.Size), size, duration);
        Start(nameof(CompositionRoundedRectangleGeometry.CornerRadius), new Vector2(cornerRadius), duration);
        _batch.End();
        _batch.Completed += OnBatchCompleted;
    }

    public void FadeIn(TimeSpan duration)
    {
        ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, _fadeEasing);
        fade.Duration = duration;
        _visual.StartAnimation(nameof(Visual.Opacity), fade);
    }

    /// <summary>Opacity fade used as an implicit show/hide animation for content layers.</summary>
    public ICompositionAnimationBase CreateFade(float from, float to, TimeSpan duration, TimeSpan delay)
    {
        ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.Target = nameof(Visual.Opacity);
        fade.InsertKeyFrame(0f, from);
        fade.InsertKeyFrame(1f, to, _fadeEasing);
        fade.Duration = duration;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        return fade;
    }

    private void Start(string property, Vector2 to, TimeSpan duration)
    {
        // No keyframe at 0: the animation starts from the current (possibly animating) value,
        // so interrupting a transition never makes the island jump.
        Vector2KeyFrameAnimation animation = _compositor.CreateVector2KeyFrameAnimation();
        animation.InsertKeyFrame(1f, to, _resizeEasing);
        animation.Duration = duration;
        _geometry.StartAnimation(property, animation);
    }

    private void OnBatchCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, _batch))
        {
            return;
        }

        _batch = null;
        Action? completion = _pendingCompletion;
        _pendingCompletion = null;
        completion?.Invoke();
    }
}
