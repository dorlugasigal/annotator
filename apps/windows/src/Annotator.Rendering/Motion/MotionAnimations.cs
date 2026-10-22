using System.Numerics;
using Annotator.Workspace.Motion;
using Windows.UI.Composition;
using Annotator.Core.Motion;

namespace Annotator.Rendering.Motion;

/// <summary>
/// Builds Windows.UI.Composition keyframe animations from the Mac's motion tracks, so the compositor plays them at the
/// display's refresh rate with no work on the overlay thread (WIN-MOT-04). Core Animation eases a whole timeline and
/// interpolates linearly between key times; <see cref="KeyframeSchedule"/> turns that into per-keyframe cubic Bezier
/// easings, which are shared by every animation with the same curve. Use it on the compositor's thread.
/// </summary>
public sealed class MotionAnimations(Compositor compositor)
{
    private readonly Compositor _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
    private readonly Dictionary<CubicBezier, CompositionEasingFunction> _easings = [];
    private LinearEasingFunction? _linear;

    /// <summary>Animations created so far, for measurements.</summary>
    public int Created { get; private set; }

    /// <summary>Keyframes inserted so far, for measurements.</summary>
    public int Keyframes { get; private set; }

    /// <summary>An animation of a scalar track mapped through <paramref name="map"/>.</summary>
    public ScalarKeyFrameAnimation Scalar(ScalarTrack track, Func<double, float> map, double delay, double duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        ScalarKeyFrameAnimation animation = _compositor.CreateScalarKeyFrameAnimation();
        foreach ((float time, double eased, CompositionEasingFunction? easing) in Schedule(track.KeyTimes, track.Curve))
        {
            var value = map(track.AtEased(eased));
            if (easing is null)
            {
                animation.InsertKeyFrame(time, value);
            }
            else
            {
                animation.InsertKeyFrame(time, value, easing);
            }
            Keyframes++;
        }
        return Finish(animation, delay, duration);
    }

    /// <summary>An animation of a transform track mapped to a vector, such as a visual's scale or offset.</summary>
    public Vector3KeyFrameAnimation Vector(TransformTrack track, Func<MotionTransform, Vector3> map, double delay, double duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        Vector3KeyFrameAnimation animation = _compositor.CreateVector3KeyFrameAnimation();
        foreach ((float time, double eased, CompositionEasingFunction? easing) in Schedule(track.KeyTimes, track.Curve))
        {
            var value = map(track.AtEased(eased));
            if (easing is null)
            {
                animation.InsertKeyFrame(time, value);
            }
            else
            {
                animation.InsertKeyFrame(time, value, easing);
            }
            Keyframes++;
        }
        return Finish(animation, delay, duration);
    }

    /// <summary>An animation of a transform track mapped to a scalar, such as a visual's rotation.</summary>
    public ScalarKeyFrameAnimation Angle(TransformTrack track, Func<MotionTransform, float> map, double delay, double duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        ScalarKeyFrameAnimation animation = _compositor.CreateScalarKeyFrameAnimation();
        foreach ((float time, double eased, CompositionEasingFunction? easing) in Schedule(track.KeyTimes, track.Curve))
        {
            var value = map(track.AtEased(eased));
            if (easing is null)
            {
                animation.InsertKeyFrame(time, value);
            }
            else
            {
                animation.InsertKeyFrame(time, value, easing);
            }
            Keyframes++;
        }
        return Finish(animation, delay, duration);
    }

    /// <summary>Linear keyframes at even times: values already sampled in time, such as the Mac's pose and poof keyframes.</summary>
    public ScalarKeyFrameAnimation Sampled(ReadOnlySpan<double> values, double delay, double duration)
    {
        ScalarKeyFrameAnimation animation = _compositor.CreateScalarKeyFrameAnimation();
        var linear = _linear ??= _compositor.CreateLinearEasingFunction();
        for (var index = 0; index < values.Length; index++)
        {
            var time = values.Length == 1 ? 1f : (float)index / (values.Length - 1);
            if (index == 0)
            {
                animation.InsertKeyFrame(time, (float)values[index]);
            }
            else
            {
                animation.InsertKeyFrame(time, (float)values[index], linear);
            }
            Keyframes++;
        }
        return Finish(animation, delay, duration);
    }

    /// <summary>Linear vector keyframes at even times.</summary>
    public Vector3KeyFrameAnimation Sampled(ReadOnlySpan<Vector3> values, double delay, double duration)
    {
        Vector3KeyFrameAnimation animation = _compositor.CreateVector3KeyFrameAnimation();
        var linear = _linear ??= _compositor.CreateLinearEasingFunction();
        for (var index = 0; index < values.Length; index++)
        {
            var time = values.Length == 1 ? 1f : (float)index / (values.Length - 1);
            if (index == 0)
            {
                animation.InsertKeyFrame(time, values[index]);
            }
            else
            {
                animation.InsertKeyFrame(time, values[index], linear);
            }
            Keyframes++;
        }
        return Finish(animation, delay, duration);
    }

    /// <summary>A vector track whose values are already sampled at <paramref name="keyTimes"/> under <paramref name="curve"/>.</summary>
    public Vector3KeyFrameAnimation Path(ReadOnlySpan<double> keyTimes, ReadOnlySpan<Vector3> values, CubicBezier curve, double delay, double duration)
    {
        if (keyTimes.Length != values.Length || keyTimes.Length < 2)
        {
            throw new ArgumentException("A path needs one value per key time and at least two.", nameof(values));
        }
        Vector3KeyFrameAnimation animation = _compositor.CreateVector3KeyFrameAnimation();
        foreach ((float time, double eased, CompositionEasingFunction? easing) in Schedule(keyTimes, curve))
        {
            var value = Interpolate(keyTimes, values, eased);
            if (easing is null)
            {
                animation.InsertKeyFrame(time, value);
            }
            else
            {
                animation.InsertKeyFrame(time, value, easing);
            }
            Keyframes++;
        }
        return Finish(animation, delay, duration);
    }

    /// <summary>A scalar track sampled at <paramref name="keyTimes"/> under <paramref name="curve"/>.</summary>
    public ScalarKeyFrameAnimation Values(ReadOnlySpan<double> keyTimes, ReadOnlySpan<double> values, CubicBezier curve, double delay, double duration)
    {
        if (keyTimes.Length != values.Length || keyTimes.Length < 2)
        {
            throw new ArgumentException("A track needs one value per key time and at least two.", nameof(values));
        }
        var track = new ScalarTrack(keyTimes.ToArray(), values.ToArray(), curve);
        return Scalar(track, static value => (float)value, delay, duration);
    }

    /// <summary>Keyframes of the schedule, skipping any that would not move forward in time.</summary>
    private List<(float Time, double Eased, CompositionEasingFunction? Easing)> Schedule(ReadOnlySpan<double> keyTimes, CubicBezier curve)
    {
        List<KeyframePoint> points = KeyframeSchedule.Exact(keyTimes, curve);
        var result = new List<(float, double, CompositionEasingFunction?)>(points.Count);
        var previous = -1f;
        for (var index = 0; index < points.Count; index++)
        {
            KeyframePoint point = points[index];
            var time = (float)Math.Clamp(point.Time, 0, 1);
            var last = index == points.Count - 1;
            if (index > 0 && time <= previous && !last)
            {
                continue;
            }
            if (last && time <= previous && result.Count > 1)
            {
                // The final keyframe replaces one that landed at the same time.
                result.RemoveAt(result.Count - 1);
            }
            result.Add((last ? 1f : time, point.Eased, index == 0 ? null : Easing(point.Easing)));
            previous = time;
        }
        return result;
    }

    private CompositionEasingFunction Easing(CubicBezier curve)
    {
        if (curve.IsLinear)
        {
            return _linear ??= _compositor.CreateLinearEasingFunction();
        }
        if (!_easings.TryGetValue(curve, out CompositionEasingFunction? easing))
        {
            easing = _compositor.CreateCubicBezierEasingFunction(new Vector2((float)curve.X1, (float)curve.Y1),
                                                                new Vector2((float)curve.X2, (float)curve.Y2));
            _easings[curve] = easing;
        }
        return easing;
    }

    private T Finish<T>(T animation, double delay, double duration)
        where T : KeyFrameAnimation
    {
        animation.Duration = TimeSpan.FromSeconds(Math.Max(0.001, duration));
        animation.DelayTime = TimeSpan.FromSeconds(Math.Max(0, delay));
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        animation.IterationBehavior = AnimationIterationBehavior.Count;
        animation.IterationCount = 1;
        Created++;
        return animation;
    }

    private static Vector3 Interpolate(ReadOnlySpan<double> keyTimes, ReadOnlySpan<Vector3> values, double eased)
    {
        if (!(eased > keyTimes[0]))
        {
            return values[0];
        }
        if (!(eased < keyTimes[^1]))
        {
            return values[^1];
        }
        var index = 0;
        while (index < keyTimes.Length - 2 && eased > keyTimes[index + 1])
        {
            index++;
        }
        var span = keyTimes[index + 1] - keyTimes[index];
        return span > 0 ? Vector3.Lerp(values[index], values[index + 1], (float)((eased - keyTimes[index]) / span)) : values[index + 1];
    }

    /// <summary>Releases the shared easing functions.</summary>
    public void Clear()
    {
        foreach (CompositionEasingFunction easing in _easings.Values)
        {
            easing.Dispose();
        }
        _easings.Clear();
        _linear?.Dispose();
        _linear = null;
    }
}
