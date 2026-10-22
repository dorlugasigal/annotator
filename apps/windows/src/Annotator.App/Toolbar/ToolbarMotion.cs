using System.Diagnostics;
using System.Numerics;
using Annotator.Workspace.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Toolbar;

/// <summary>
/// Plays the toolbar's control motion (<see cref="ToolbarControlMotion"/>) on its elements with composition keyframes
/// through <see cref="UIElement.StartAnimation"/>: it runs on the compositor, and nothing runs once it ends.
/// </summary>
internal static class ToolbarMotion
{
    /// <summary>The Mac's Reduce Motion is Windows' Animation effects setting (WIN-MOT-03).</summary>
    public static unsafe bool ReducesMotion()
    {
        BOOL animations = true;
        return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &animations, 0) && !animations;
    }

    /// <summary>Seconds on the steady clock.</summary>
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Plays <paramref name="samples"/> on a Vector3 property of <paramref name="element"/> (Scale, Translation).</summary>
    public static void Vector(UIElement element, string property, MotionSamples samples, Func<double, Vector3> value)
    {
        // GetElementVisual is refused once an element uses CenterPoint, Scale or Translation; the thread's compositor is the same.
        Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
        LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
        Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = property;
        var duration = Math.Max(samples.Duration, 0.001);
        for (var index = 0; index < samples.Times.Count; index++)
        {
            animation.InsertKeyFrame((float)(samples.Times[index] / duration), value(samples.Values[index]), linear);
        }
        animation.Duration = TimeSpan.FromSeconds(duration);
        element.StartAnimation(animation);
    }

    /// <summary>Plays <paramref name="samples"/> on a scalar property of <paramref name="element"/> (Opacity).</summary>
    public static void Scalar(UIElement element, string property, MotionSamples samples, Func<double, float> value)
    {
        // GetElementVisual is refused once an element uses CenterPoint, Scale or Translation; the thread's compositor is the same.
        Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
        LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
        ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Target = property;
        var duration = Math.Max(samples.Duration, 0.001);
        for (var index = 0; index < samples.Times.Count; index++)
        {
            animation.InsertKeyFrame((float)(samples.Times[index] / duration), value(samples.Values[index]), linear);
        }
        animation.Duration = TimeSpan.FromSeconds(duration);
        element.StartAnimation(animation);
    }
}

/// <summary>
/// One element's opacity or uniform scale as the toolbar's control motion changes it. Each change starts from the value
/// on screen, so an interrupted change never jumps; the element's own property always holds where the last change ends.
/// </summary>
internal sealed class Tween
{
    private readonly UIElement _element;
    private readonly bool _scale;
    private MotionSamples? _track;
    private double _started;
    private double _rest;

    private Tween(UIElement element, bool scale, double value)
    {
        _element = element;
        _scale = scale;
        _rest = value;
        Apply(value);
    }

    public static Tween Opacity(UIElement element, double value) => new(element, scale: false, value);

    public static Tween Scale(UIElement element, double value) => new(element, scale: true, value);

    /// <summary>The value on screen now.</summary>
    public double Current => Playing ? _track!.At(ToolbarMotion.Now - _started) : _rest;

    private bool Playing => _track is { } track && ToolbarMotion.Now - _started < track.Duration;

    /// <summary>Moves to <paramref name="target"/> along <paramref name="progress"/> (0 to 1), or at once without it.</summary>
    public void To(double target, MotionSamples? progress)
    {
        if (progress is null)
        {
            Set(target);
            return;
        }
        var from = Current;
        if (from == target && !Playing)
        {
            return;
        }
        double[] values = [.. progress.Values.Select(share => from + (target - from) * share)];
        Play(new MotionSamples([.. progress.Times], values));
    }

    /// <summary>Plays absolute values, such as the dot's pulse.</summary>
    public void Play(MotionSamples values)
    {
        _rest = values.Values[^1];
        _track = values;
        _started = ToolbarMotion.Now;
        Apply(_rest);
        if (_scale)
        {
            ToolbarMotion.Vector(_element, "Scale", values, value => new Vector3((float)value, (float)value, 1));
        }
        else
        {
            ToolbarMotion.Scalar(_element, "Opacity", values, value => (float)value);
        }
    }

    /// <summary>Shows <paramref name="value"/> at once, stopping a change that still plays.</summary>
    public void Set(double value)
    {
        var playing = Playing;
        if (!playing && _rest == value)
        {
            return;
        }
        if (playing)
        {
            // A one-millisecond step replaces the running animation, whose end would otherwise win.
            Play(new MotionSamples([0, 0.001], [value, value]));
        }
        _track = null;
        _rest = value;
        Apply(value);
    }

    private void Apply(double value)
    {
        if (_scale)
        {
            _element.Scale = new Vector3((float)value, (float)value, 1);
        }
        else
        {
            _element.Opacity = value;
        }
    }
}
