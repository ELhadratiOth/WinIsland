using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace WinIsland.App.Animation;

/// <summary>
/// Draws and animates the island purely on the compositor.
/// <list type="bullet">
/// <item>One <see cref="CompositionRoundedRectangleGeometry"/> is shared by the black fill, the
/// hairline edge highlight and the content clip, so all three morph together.</item>
/// <item>Size, offset and corner radius move with spring physics (natural overshoot and settle,
/// like the iPhone island) instead of fixed-duration curves.</item>
/// </list>
/// No XAML layout runs per frame and the compositor goes idle when a spring settles.
/// </summary>
internal sealed class PillAnimator
{
    private const float RestingStrokeOpacity = 0.10f;
    private const float HoverStrokeOpacity = 0.30f;

    private readonly Compositor _compositor;
    private readonly Visual _surface;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionColorBrush _strokeBrush;
    private readonly CompositionEasingFunction _fadeEasing;
    private CompositionScopedBatch? _batch;
    private Action? _pendingCompletion;

    /// <param name="surface">Element whose visual is clipped to the pill (holds all content).</param>
    /// <param name="backgroundHost">Element (first child of the surface) that hosts the drawn pill shape.</param>
    public PillAnimator(UIElement surface, UIElement backgroundHost)
    {
        _surface = ElementCompositionPreview.GetElementVisual(surface);
        _compositor = _surface.Compositor;
        _geometry = _compositor.CreateRoundedRectangleGeometry();
        _surface.Clip = _compositor.CreateGeometricClip(_geometry);

        // Pure black body with a faint edge highlight so the island reads on dark wallpapers too.
        // The stroke straddles the clip edge, so a 2px stroke shows as a crisp 1px hairline.
        _strokeBrush = _compositor.CreateColorBrush(Color.FromArgb((byte)(255 * RestingStrokeOpacity), 255, 255, 255));
        CompositionSpriteShape shape = _compositor.CreateSpriteShape(_geometry);
        shape.FillBrush = _compositor.CreateColorBrush(Color.FromArgb(255, 0, 0, 0));
        shape.StrokeBrush = _strokeBrush;
        shape.StrokeThickness = 2f;

        ShapeVisual shapeVisual = _compositor.CreateShapeVisual();
        shapeVisual.Shapes.Add(shape);
        shapeVisual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(backgroundHost, shapeVisual);

        _fadeEasing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
    }

    public Compositor Compositor => _compositor;

    /// <summary>Moves the shape instantly (first show, monitor change, reduced motion).</summary>
    public void Snap(Vector2 offset, Vector2 size, float cornerRadius)
    {
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.Offset));
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.Size));
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.CornerRadius));
        _geometry.Offset = offset;
        _geometry.Size = size;
        _geometry.CornerRadius = new Vector2(cornerRadius);
    }

    /// <summary>Re-anchors the current (possibly mid-animation) shape at a new offset.</summary>
    public void Reanchor(Vector2 offset)
    {
        _geometry.StopAnimation(nameof(CompositionRoundedRectangleGeometry.Offset));
        _geometry.Offset = offset;
    }

    /// <summary>Springs from the current shape to the target; <paramref name="completed"/> runs once it settles.</summary>
    public void Animate(Vector2 offset, Vector2 size, float cornerRadius, bool growing, Action completed)
    {
        if (_batch is not null)
        {
            // A superseded transition must not run its completion (window shrink) later.
            _batch.Completed -= OnBatchCompleted;
        }

        _pendingCompletion = completed;
        _batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        // Growing gets a lively, slightly bouncy spring; collapsing settles more firmly.
        float damping = growing ? 0.68f : 0.86f;
        TimeSpan period = TimeSpan.FromMilliseconds(growing ? 52 : 44);
        Spring(nameof(CompositionRoundedRectangleGeometry.Offset), offset, damping, period);
        Spring(nameof(CompositionRoundedRectangleGeometry.Size), size, damping, period);
        Spring(nameof(CompositionRoundedRectangleGeometry.CornerRadius), new Vector2(cornerRadius), 0.9f, period);

        _batch.End();
        _batch.Completed += OnBatchCompleted;
    }

    /// <summary>Brightens the edge highlight while the pointer rests on the island (a hover affordance).</summary>
    public void SetHover(bool hover)
    {
        ColorKeyFrameAnimation animation = _compositor.CreateColorKeyFrameAnimation();
        float opacity = hover ? HoverStrokeOpacity : RestingStrokeOpacity;
        animation.InsertKeyFrame(1f, Color.FromArgb((byte)(255 * opacity), 255, 255, 255), _fadeEasing);
        animation.Duration = TimeSpan.FromMilliseconds(180);
        _strokeBrush.StartAnimation(nameof(CompositionColorBrush.Color), animation);
    }

    public void FadeIn(TimeSpan duration)
    {
        ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, _fadeEasing);
        fade.Duration = duration;
        _surface.StartAnimation(nameof(Visual.Opacity), fade);
    }

    /// <summary>Content layer entrance: fade in while settling down from slightly above.</summary>
    public ICompositionAnimationBase CreateEnter(TimeSpan delay)
    {
        ScalarKeyFrameAnimation opacity = _compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = nameof(Visual.Opacity);
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, _fadeEasing);
        opacity.Duration = TimeSpan.FromMilliseconds(220);
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        Vector3KeyFrameAnimation slide = _compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0f, new Vector3(0, -6, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, _fadeEasing);
        slide.Duration = TimeSpan.FromMilliseconds(320);
        slide.DelayTime = delay;
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        CompositionAnimationGroup group = _compositor.CreateAnimationGroup();
        group.Add(opacity);
        group.Add(slide);
        return group;
    }

    /// <summary>Content layer exit: a quick fade so the incoming layer owns the stage.</summary>
    public ICompositionAnimationBase CreateExit()
    {
        ScalarKeyFrameAnimation opacity = _compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = nameof(Visual.Opacity);
        opacity.InsertKeyFrame(1f, 0f, _fadeEasing);
        opacity.Duration = TimeSpan.FromMilliseconds(110);
        return opacity;
    }

    private void Spring(string property, Vector2 to, float damping, TimeSpan period)
    {
        // A natural-motion spring starts from the current (possibly animating) value, so
        // interrupting a transition never makes the island jump.
        SpringVector2NaturalMotionAnimation spring = _compositor.CreateSpringVector2Animation();
        spring.FinalValue = to;
        spring.DampingRatio = damping;
        spring.Period = period;
        _geometry.StartAnimation(property, spring);
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
