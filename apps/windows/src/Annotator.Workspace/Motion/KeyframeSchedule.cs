using Annotator.Core.Motion;

namespace Annotator.Workspace.Motion;

/// <summary>
/// One keyframe of a compositor animation: at linear <see cref="Time"/> (0...1 of the duration) the property reaches the
/// track's value at eased progress <see cref="Eased"/>, arriving along <see cref="Easing"/> from the previous keyframe.
/// </summary>
public readonly record struct KeyframePoint(double Time, double Eased, CubicBezier Easing);

/// <summary>
/// Turns a Core Animation track (one timing curve over the whole timeline, linear between key times in eased time) into
/// per-segment keyframes for a compositor whose easing applies between two keyframes, such as Windows.UI.Composition.
/// Each key time is placed at the linear time where the curve reaches it. Between two keyframes the eased progress follows
/// that stretch of the curve, which is itself a cubic Bezier and becomes the segment's easing, so the compositor
/// reproduces Core Animation's values exactly. Stretches whose normalized control points leave 0...1 are split until
/// they fit. The easings depend only on the curve and the key times, so tracks of many layers can share them.
/// </summary>
public static class KeyframeSchedule
{
    /// <summary>Exact keyframes for <paramref name="keyTimes"/> under <paramref name="curve"/>; the first is at time 0.</summary>
    public static List<KeyframePoint> Exact(ReadOnlySpan<double> keyTimes, CubicBezier curve)
    {
        if (keyTimes.Length < 2)
        {
            throw new ArgumentException("A schedule needs at least two key times.", nameof(keyTimes));
        }
        var points = new List<KeyframePoint>(keyTimes.Length + 4) { new(0, keyTimes[0], MotionCurves.Linear) };
        var linear = curve.IsLinear;
        var previousParameter = linear ? keyTimes[0] : curve.ParameterForProgress(keyTimes[0]);
        for (var index = 1; index < keyTimes.Length; index++)
        {
            var eased = keyTimes[index];
            if (linear)
            {
                points.Add(new KeyframePoint(eased, eased, MotionCurves.Linear));
                continue;
            }
            var parameter = curve.ParameterForProgress(eased);
            Segment(points, curve, previousParameter, parameter, keyTimes[index - 1], eased, depth: 0);
            previousParameter = parameter;
        }
        // The compositor needs times ending at 1; keep the last keyframe at the end of the duration.
        points[^1] = points[^1] with { Time = 1 };
        return points;
    }

    /// <summary>Adds the keyframe that ends the curve's stretch between parameters <paramref name="s0"/> and <paramref name="s1"/>.</summary>
    private static void Segment(List<KeyframePoint> points, CubicBezier curve, double s0, double s1, double eased0, double eased1, int depth)
    {
        var (x0, y0) = curve.Point(s0);
        var (x3, y3) = curve.Point(s1);
        var h = s1 - s0;
        var dx0 = Derivative(s0, curve.X1, curve.X2);
        var dy0 = Derivative(s0, curve.Y1, curve.Y2);
        var dx1 = Derivative(s1, curve.X1, curve.X2);
        var dy1 = Derivative(s1, curve.Y1, curve.Y2);
        var width = x3 - x0;
        var height = y3 - y0;
        if (!(width > 1e-12))
        {
            // The stretch takes no time: arrive at once.
            points.Add(new KeyframePoint(x3, eased1, MotionCurves.Linear));
            return;
        }
        if (!(height > 1e-12))
        {
            points.Add(new KeyframePoint(x3, eased1, MotionCurves.Linear));
            return;
        }
        var cx1 = h / 3 * dx0 / width;
        var cy1 = h / 3 * dy0 / height;
        var cx2 = 1 - h / 3 * dx1 / width;
        var cy2 = 1 - h / 3 * dy1 / height;
        const double slack = 1e-9;
        if ((cx1 < -slack || cx1 > 1 + slack || cx2 < -slack || cx2 > 1 + slack) && depth < 8)
        {
            var middle = (s0 + s1) / 2;
            var easedMiddle = curve.Point(middle).Y;
            Segment(points, curve, s0, middle, eased0, easedMiddle, depth + 1);
            Segment(points, curve, middle, s1, easedMiddle, eased1, depth + 1);
            return;
        }
        points.Add(new KeyframePoint(x3, eased1, new CubicBezier(Math.Clamp(cx1, 0, 1), cy1, Math.Clamp(cx2, 0, 1), cy2)));
    }

    private static double Derivative(double t, double a, double b) =>
        3 * (1 - t) * (1 - t) * a + 6 * (1 - t) * t * (b - a) + 3 * t * t * (1 - b);

    /// <summary>
    /// Evaluates a schedule the way the compositor does, for tests: the eased progress at linear <paramref name="time"/>.
    /// </summary>
    public static double EasedAt(IReadOnlyList<KeyframePoint> points, double time)
    {
        if (time <= points[0].Time)
        {
            return points[0].Eased;
        }
        for (var index = 1; index < points.Count; index++)
        {
            var end = points[index];
            if (time <= end.Time)
            {
                var start = points[index - 1];
                var span = end.Time - start.Time;
                var local = span > 0 ? (time - start.Time) / span : 1;
                return start.Eased + (end.Eased - start.Eased) * end.Easing.Progress(local);
            }
        }
        return points[^1].Eased;
    }
}
