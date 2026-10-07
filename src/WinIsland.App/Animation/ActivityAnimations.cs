using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace WinIsland.App.Animation;

/// <summary>
/// Looping "alive" animations (equalizer bars, a turning glyph). They run on the compositor
/// and are started only while the element is on screen and the thing it represents is
/// active; otherwise they are stopped so the GPU stays idle.
/// </summary>
internal sealed class ActivityAnimations
{
    // Per-bar loop shapes (relative heights) and periods: unequal so the bars never sync up.
    private static readonly float[][] BarPatterns =
    [
        [0.35f, 0.95f, 0.5f, 0.8f, 0.35f],
        [0.6f, 0.3f, 1.0f, 0.45f, 0.6f],
        [0.4f, 0.75f, 0.35f, 0.9f, 0.4f],
        [0.8f, 0.45f, 0.7f, 0.3f, 0.8f],
        [0.5f, 0.9f, 0.4f, 0.65f, 0.5f],
    ];

    private static readonly int[] BarPeriodsMs = [820, 690, 910, 760, 870];

    private readonly Compositor _compositor;
    private readonly List<(Visual Visual, int Index, float Height)> _bars = [];
    private readonly List<Visual> _spinners = [];
    private bool _barsRunning;
    private bool _spinnersRunning;

    public ActivityAnimations(Compositor compositor)
    {
        _compositor = compositor;
    }

    /// <summary>Registers equalizer bars (their scale pivots on the bottom edge).</summary>
    public void AddBars(IEnumerable<FrameworkElement> bars)
    {
        int index = 0;
        foreach (FrameworkElement bar in bars)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(bar);
            float height = (float)bar.Height;
            visual.CenterPoint = new Vector3((float)bar.Width / 2, height, 0);
            int pattern = index++ % BarPatterns.Length;
            visual.Scale = new Vector3(1, RestingScale(pattern), 1);
            _bars.Add((visual, pattern, height));
        }
    }

    /// <summary>Registers glyphs that turn slowly while something is working.</summary>
    public void AddSpinner(FrameworkElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3((float)element.Width / 2, (float)element.Height / 2, 0);
        _spinners.Add(visual);
    }

    public void SetBarsRunning(bool running)
    {
        if (running == _barsRunning)
        {
            return;
        }

        _barsRunning = running;
        foreach ((Visual visual, int index, _) in _bars)
        {
            if (running)
            {
                ScalarKeyFrameAnimation loop = _compositor.CreateScalarKeyFrameAnimation();
                float[] pattern = BarPatterns[index];
                for (int i = 0; i < pattern.Length; i++)
                {
                    loop.InsertKeyFrame(i / (float)(pattern.Length - 1), pattern[i]);
                }

                loop.Duration = TimeSpan.FromMilliseconds(BarPeriodsMs[index]);
                loop.IterationBehavior = AnimationIterationBehavior.Forever;
                visual.StartAnimation("Scale.Y", loop);
            }
            else
            {
                visual.StopAnimation("Scale.Y");
                visual.Scale = new Vector3(1, RestingScale(index), 1);
            }
        }
    }

    /// <summary>A still waveform still reads as a waveform (used when paused or with animations off).</summary>
    private static float RestingScale(int pattern) => BarPatterns[pattern][1] * 0.8f;

    public void SetSpinnersRunning(bool running)
    {
        if (running == _spinnersRunning)
        {
            return;
        }

        _spinnersRunning = running;
        foreach (Visual visual in _spinners)
        {
            if (running)
            {
                ScalarKeyFrameAnimation turn = _compositor.CreateScalarKeyFrameAnimation();
                turn.InsertKeyFrame(0f, 0f);
                turn.InsertKeyFrame(1f, 360f, _compositor.CreateLinearEasingFunction());
                turn.Duration = TimeSpan.FromSeconds(6);
                turn.IterationBehavior = AnimationIterationBehavior.Forever;
                visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), turn);
            }
            else
            {
                visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
                visual.RotationAngleInDegrees = 0;
            }
        }
    }
}
