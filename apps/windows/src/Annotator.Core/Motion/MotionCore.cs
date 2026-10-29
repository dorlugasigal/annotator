namespace Annotator.Core.Motion;

/// <summary>
/// A cubic Bézier timing curve from (0, 0) to (1, 1) with two control points, as Core Animation's
/// <c>CAMediaTimingFunction(controlPoints:)</c> and CSS <c>cubic-bezier()</c> define it (Swift <c>MotionCore.CubicBezier</c>).
/// </summary>
public readonly record struct CubicBezier(double X1, double Y1, double X2, double Y2)
{
    /// <summary>Eased progress at linear time <paramref name="x"/> in 0...1. Times outside 0...1 clamp to the endpoints.</summary>
    public double Progress(double x) => MotionCurves.Cubic(x, X1, Y1, X2, Y2);

    /// <summary>Windows: true when both control points lie on the diagonal, so eased time equals linear time.</summary>
    public bool IsLinear => X1 == Y1 && X2 == Y2;

    /// <summary>Windows: the curve's point at Bézier parameter <paramref name="s"/>: (linear time, eased progress).</summary>
    public (double X, double Y) Point(double s) => (MotionCurves.Coordinate(s, X1, X2), MotionCurves.Coordinate(s, Y1, Y2));

    /// <summary>
    /// Windows: the Bézier parameter where the eased progress reaches <paramref name="y"/>, by 60 bisection steps (the
    /// curves here rise monotonically). Splitting a curve at keyframes needs it.
    /// </summary>
    public double ParameterForProgress(double y)
    {
        if (y <= 0)
        {
            return 0;
        }
        if (y >= 1)
        {
            return 1;
        }
        double low = 0, high = 1;
        for (var step = 0; step < 60; step++)
        {
            var middle = (low + high) / 2;
            if (MotionCurves.Coordinate(middle, Y1, Y2) < y)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return (low + high) / 2;
    }
}

/// <summary>
/// Curves shared by the app's native motion (Swift <c>MotionCurves</c>). Magic flights and Dissolve particles keep their own
/// art-directed curves.
/// </summary>
public static class MotionCurves
{
    /// <summary>Entrances: quick start, long gentle settle that fills the whole duration.</summary>
    public static CubicBezier Enter { get; } = new(0.2, 0, 0, 1);

    /// <summary>Exits: starts moving visibly early and still speeds up into the target, without a last-frame snap.</summary>
    public static CubicBezier Exit { get; } = new(0.45, 0, 0.55, 0.4);

    /// <summary>Reversals and two-way changes: the object turns around without a hitch.</summary>
    public static CubicBezier Standard { get; } = new(0.3, 0, 0.2, 1);

    /// <summary>Opacity-only changes. Perceived brightness follows this more evenly than the movement curves.</summary>
    public static CubicBezier Fade { get; } = new(0.2, 0, 0.4, 1);

    /// <summary>Core Animation's named curves, which some timelines and SwiftUI's eased animations use.</summary>
    public static CubicBezier Linear { get; } = new(0, 0, 1, 1);

    public static CubicBezier EaseIn { get; } = new(0.42, 0, 1, 1);

    public static CubicBezier EaseOut { get; } = new(0, 0, 0.58, 1);

    public static CubicBezier EaseInEaseOut { get; } = new(0.42, 0, 0.58, 1);

    public static IReadOnlyList<(string Name, CubicBezier Curve)> Named { get; } =
    [
        ("enter", Enter), ("exit", Exit), ("standard", Standard), ("fade", Fade),
        ("linear", Linear), ("easeIn", EaseIn), ("easeOut", EaseOut), ("easeInEaseOut", EaseInEaseOut),
    ];

    /// <summary>Solves the curve for <paramref name="x"/> by 40 bisection steps on the parameter, then evaluates y.</summary>
    public static double Cubic(double x, double x1, double y1, double x2, double y2)
    {
        if (!(x > 0))
        {
            return 0;
        }
        if (!(x < 1))
        {
            return 1;
        }
        double low = 0, high = 1;
        for (var step = 0; step < 40; step++)
        {
            var middle = (low + high) / 2;
            if (Coordinate(middle, x1, x2) < x)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return Coordinate((low + high) / 2, y1, y2);
    }

    /// <summary>Smoothstep: 3t² − 2t³ with t clamped to 0...1.</summary>
    public static double Smooth(double value)
    {
        var t = Math.Min(1, Math.Max(0, value));
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The Drop style's entrance: lands with one small bounce, 1 − e^(−6t) cos(9.5t), clamped to 0 and 1 outside 0...1.
    /// Drop's exit is linear in time; the pose squares it into constant acceleration.
    /// </summary>
    public static double DropSettle(double time) =>
        time >= 1 ? 1 : time <= 0 ? 0 : 1 - Math.Exp(-6 * time) * Math.Cos(9.5 * time);

    /// <summary>One coordinate of the Bézier at parameter <paramref name="t"/> with inner control values <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static double Coordinate(double t, double a, double b) =>
        3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
}

/// <summary>A spring described as SwiftUI's <c>spring(response:dampingFraction:)</c> (Swift <c>SpringMotion</c>).</summary>
public readonly record struct SpringMotion(double Response, double DampingFraction)
{
    /// <summary>
    /// Unit step response at <paramref name="time"/> seconds for a spring with mass 1, stiffness (2π / response)² and damping
    /// 4π × dampingFraction / response, starting at rest at 0 and settling at 1. This is the published model of SwiftUI's
    /// spring, not SwiftUI's own implementation.
    /// </summary>
    public double Value(double time)
    {
        if (!(time > 0))
        {
            return 0;
        }
        var omega = 2 * Math.PI / Response;
        var zeta = DampingFraction;
        if (zeta < 1)
        {
            var damped = omega * Math.Sqrt(1 - zeta * zeta);
            return 1 - Math.Exp(-zeta * omega * time) * (Math.Cos(damped * time) + zeta * omega / damped * Math.Sin(damped * time));
        }
        if (zeta == 1)
        {
            return 1 - Math.Exp(-omega * time) * (1 + omega * time);
        }
        var root = omega * Math.Sqrt(zeta * zeta - 1);
        double fast = -zeta * omega - root, slow = -zeta * omega + root;
        return 1 - (fast * Math.Exp(slow * time) - slow * Math.Exp(fast * time)) / (fast - slow);
    }

    /// <summary>The first whole millisecond after which the response stays within <paramref name="tolerance"/> of 1.</summary>
    public double SettleTime(double tolerance = 0.001, double limit = 5)
    {
        var settled = limit;
        var millisecond = (int)(limit * 1000);
        while (millisecond >= 0)
        {
            if (Math.Abs(1 - Value(millisecond / 1000.0)) > tolerance)
            {
                break;
            }
            settled = millisecond / 1000.0;
            millisecond--;
        }
        return settled;
    }
}

/// <summary>An eased animation with a fixed duration (Swift <c>TimedMotion</c>).</summary>
public readonly record struct TimedMotion(double Duration, CubicBezier Curve);

/// <summary>Durations and presets shared by the app's native motion (Swift <c>MotionTiming</c>).</summary>
public static class MotionTiming
{
    /// <summary>The toolbar's show and hide durations for styles without their own.</summary>
    public const double ToolbarShowDuration = 0.36;

    public const double ToolbarHideDuration = 0.28;

    /// <summary>
    /// A show or hide that reverses mid-way takes this share of the full duration at least, otherwise the share of the
    /// distance left.
    /// </summary>
    public const double MinimumReversalShare = 0.35;

    /// <summary>Screen zoom ramps: zooming in uses <see cref="MotionCurves.Enter"/>, zooming out to 1x <see cref="MotionCurves.Standard"/>.</summary>
    public const double ZoomInDuration = 0.26;

    public const double ZoomOutDuration = 0.2;

    /// <summary>
    /// Spotlight dimming uses <see cref="MotionCurves.Fade"/>; each fade duration is scaled by the share of the change still
    /// to go, at least 0.001. A radius change uses <see cref="MotionCurves.Standard"/> over the full resize duration.
    /// </summary>
    public const double SpotlightFadeInDuration = 0.18;

    public const double SpotlightFadeOutDuration = 0.14;

    public const double SpotlightResizeDuration = 0.12;

    /// <summary>
    /// Smart Draw's morph from ink to shape uses <see cref="MotionCurves.Enter"/>; the shape fades in with smoothstep from 55%
    /// of the time.
    /// </summary>
    public const double SnapMorphDuration = 0.22;

    /// <summary>An erased object fades with <see cref="MotionCurves.Fade"/> and, when it scales, shrinks 3% with <see cref="MotionCurves.Exit"/>.</summary>
    public const double EraseFadeDuration = 0.12;

    /// <summary>Workspace hints fade in (Core Animation's fade curve) and out, and cross-fade their text.</summary>
    public const double HintFadeInDuration = 0.16;

    public const double HintFadeOutDuration = 0.12;

    public const double HintTextCrossfadeDuration = 0.14;

    /// <summary>How long the on-screen tip stays after it wakes, before it fades out.</summary>
    public const double HintDwellDuration = 5.0;

    /// <summary>The toolbar grows or shrinks sideways to fit a tool's properties, with <see cref="MotionCurves.Standard"/>.</summary>
    public const double ToolbarResizeDuration = 0.22;

    /// <summary>
    /// The toolbar shrinks into one button, and grows back, with <see cref="MotionCurves.Standard"/>. The outgoing face
    /// fades out and the incoming face fades in as <see cref="ToolbarMorph"/> describes.
    /// </summary>
    public const double ToolbarCollapseDuration = 0.3;

    public const double ToolbarExpandDuration = 0.34;

    /// <summary>Selection, toggles and small state changes.</summary>
    public static SpringMotion Snappy { get; } = new(0.28, 0.86);

    /// <summary>Layout and panel size changes.</summary>
    public static SpringMotion Smooth { get; } = new(0.38, 0.9);

    /// <summary>Confirmation bounces only, such as a newly assigned key.</summary>
    public static SpringMotion Playful { get; } = new(0.3, 0.62);

    public static TimedMotion FadeIn { get; } = new(0.16, MotionCurves.EaseOut);

    public static TimedMotion FadeOut { get; } = new(0.12, MotionCurves.EaseIn);

    /// <summary>The replacement for movement when Reduce Motion (Windows: Animation effects off) is on.</summary>
    public static TimedMotion Reduced { get; } = new(0.12, MotionCurves.EaseOut);

    public static IReadOnlyList<(string Name, SpringMotion Spring)> Springs { get; } =
        [("snappy", Snappy), ("smooth", Smooth), ("playful", Playful)];

    public static IReadOnlyList<(string Name, TimedMotion Motion)> Timed { get; } =
        [("fadeIn", FadeIn), ("fadeOut", FadeOut), ("reduced", Reduced)];

    /// <summary>Seconds for one shape's or the toolbar's show (<paramref name="appearing"/>) or hide in each workspace style.</summary>
    public static double VisibilityDuration(VisibilityAnimationStyle style, bool appearing) => style switch
    {
        VisibilityAnimationStyle.None => 0,
        VisibilityAnimationStyle.Quick => appearing ? 0.18 : 0.14,
        VisibilityAnimationStyle.Fade => appearing ? 0.20 : 0.16,
        VisibilityAnimationStyle.Dissolve => appearing ? 0.65 : 0.55,
        VisibilityAnimationStyle.Magic => appearing ? 0.62 : 0.54,
        VisibilityAnimationStyle.Corners => appearing ? 0.38 : 0.30,
        VisibilityAnimationStyle.Pop => appearing ? 0.34 : 0.26,
        VisibilityAnimationStyle.Drop => appearing ? 0.40 : 0.32,
        VisibilityAnimationStyle.Focus => appearing ? 0.36 : 0.28,
        _ => appearing ? ToolbarShowDuration : ToolbarHideDuration,
    };
}

/// <summary>
/// Swift <c>ToolbarMorph</c>: the toolbar's change between its full face and its one-button face. The background's frame
/// and corner radius follow <see cref="MotionCurves.Standard"/>, while the faces cross-fade so the old one is gone before
/// the new one settles.
/// </summary>
public static class ToolbarMorph
{
    /// <summary>The share of the morph after which the outgoing face is gone.</summary>
    public const double OutgoingEnd = 0.5;

    /// <summary>The share of the morph after which the incoming face starts to appear.</summary>
    public const double IncomingStart = 0.35;

    /// <summary>Progress of the background's frame and corner radius at linear time <paramref name="time"/> in 0...1.</summary>
    public static double Shape(double time) => MotionCurves.Standard.Progress(time);

    /// <summary>Opacity of the face that is leaving at linear time <paramref name="time"/> in 0...1.</summary>
    public static double OutgoingOpacity(double time) => 1 - MotionCurves.Fade.Progress(time / OutgoingEnd);

    /// <summary>Opacity of the face that is arriving at linear time <paramref name="time"/> in 0...1.</summary>
    public static double IncomingOpacity(double time) =>
        MotionCurves.Fade.Progress((time - IncomingStart) / (1 - IncomingStart));
}
