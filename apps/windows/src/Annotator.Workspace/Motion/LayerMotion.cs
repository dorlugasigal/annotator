using Annotator.Core;
using Annotator.Core.Motion;

namespace Annotator.Workspace.Motion;

/// <summary>A layer's presentation state at one moment: transform, opacity and, for posed styles, how hidden it is.</summary>
public readonly record struct LayerState(MotionTransform Transform, double Opacity, double? Progress = null);

/// <summary>
/// Keyframes of one animated property with Core Animation's semantics: the timing <see cref="Curve"/> eases the whole
/// timeline, and values are interpolated linearly between key times in eased time.
/// </summary>
public abstract class MotionTrack<T>
{
    protected MotionTrack(double[] keyTimes, T[] values, CubicBezier curve)
    {
        if (keyTimes.Length < 2 || keyTimes.Length != values.Length)
        {
            throw new ArgumentException("A track needs at least two keyframes and one key time per value.", nameof(keyTimes));
        }
        KeyTimes = keyTimes;
        Values = values;
        Curve = curve;
    }

    public double[] KeyTimes { get; }

    public T[] Values { get; }

    public CubicBezier Curve { get; }

    /// <summary>The value at linear time <paramref name="time"/> in 0...1 (clamped, as with fill mode both).</summary>
    public T At(double time) => AtEased(Curve.IsLinear ? Math.Clamp(time, 0, 1) : Curve.Progress(time));

    /// <summary>The value at eased progress <paramref name="eased"/>.</summary>
    public T AtEased(double eased)
    {
        if (!(eased > KeyTimes[0]))
        {
            return Values[0];
        }
        if (!(eased < KeyTimes[^1]))
        {
            return Values[^1];
        }
        var index = 0;
        while (index < KeyTimes.Length - 2 && eased > KeyTimes[index + 1])
        {
            index++;
        }
        var span = KeyTimes[index + 1] - KeyTimes[index];
        return span > 0 ? Interpolate(Values[index], Values[index + 1], (eased - KeyTimes[index]) / span) : Values[index + 1];
    }

    protected abstract T Interpolate(T from, T to, double fraction);
}

public sealed class TransformTrack(double[] keyTimes, MotionTransform[] values, CubicBezier curve)
    : MotionTrack<MotionTransform>(keyTimes, values, curve)
{
    protected override MotionTransform Interpolate(MotionTransform from, MotionTransform to, double fraction) =>
        MotionTransform.Interpolate(from, to, fraction);
}

public sealed class ScalarTrack(double[] keyTimes, double[] values, CubicBezier curve)
    : MotionTrack<double>(keyTimes, values, curve)
{
    protected override double Interpolate(double from, double to, double fraction) => from + (to - from) * fraction;
}

/// <summary>The inputs of a <see cref="LayerMotion"/>, as the Mac's <c>WorkspaceLayerMotion</c> initializer takes them.</summary>
public sealed record LayerMotionOptions
{
    public required VisibilityAnimationStyle Style { get; init; }

    public required bool Appearing { get; init; }

    public required double Duration { get; init; }

    /// <summary>Seconds from the transition's start.</summary>
    public double Started { get; init; }

    /// <summary>The interrupted state a reversal starts from; null for a fresh transition.</summary>
    public LayerState? Initial { get; init; }

    public bool Flipped { get; init; }

    public ToolbarDockEdge Edge { get; init; } = ToolbarDockEdge.Bottom;

    /// <summary>Where the layer goes when hidden (gathering shapes and toolbar snapshots); null uses the style's own movement.</summary>
    public MotionTransform? HiddenTransform { get; init; }

    public bool Annotation { get; init; }

    public ulong? MagicSeed { get; init; }

    public double CurveSign { get; init; } = 1;

    public ulong? PoseSeed { get; init; }

    /// <summary>The layer's resting opacity.</summary>
    public double RestingOpacity { get; init; } = 1;

    /// <summary>The layer's size in points, for the style's own movement.</summary>
    public double Width { get; init; } = 1;

    public double Height { get; init; } = 1;
}

/// <summary>
/// Port of the Mac's <c>WorkspaceLayerMotion</c>: the timeline of one snapshot or toolbar layer for a style, with the
/// same keyframes, curves, reversal blend and proportional reversal duration. <see cref="StateAt"/> is the presentation
/// state a reversal starts from. Transforms are in the Mac's y-up space.
/// </summary>
public sealed class LayerMotion
{

    private readonly VisibilityAnimationStyle _poseStyle;
    private readonly double _poseFrom;
    private readonly double _poseTo;
    private readonly bool _poseAppearing;
    private readonly bool _poseReversing;
    private readonly bool _posed;

    public LayerMotion(LayerMotionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Started = options.Started;
        var reversing = options.Initial is not null;
        var resting = options.RestingOpacity;
        Reversing = reversing;
        if (options.PoseSeed is { } poseSeed && VisibilityPose.UsesPose(options.Style))
        {
            var from = options.Initial is { } state ? state.Progress ?? 1 - state.Opacity : options.Appearing ? 1 : 0;
            double to = options.Appearing ? 0 : 1;
            _posed = true;
            _poseStyle = options.Style;
            _poseFrom = from;
            _poseTo = to;
            _poseAppearing = options.Appearing;
            _poseReversing = reversing;
            var start = VisibilityPose.PoseAt(options.Style, from, options.Appearing, options.Annotation, poseSeed);
            InitialState = options.Initial ?? new LayerState(start.Transform, start.Opacity * resting, from);
            EffectiveDuration = reversing ? options.Duration * Math.Max(MotionTiming.MinimumReversalShare, Math.Abs(to - from)) : options.Duration;
            const int steps = VisibilityPose.StepCount;
            var keyTimes = new double[steps + 1];
            var transforms = new MotionTransform[steps + 1];
            var opacities = new double[steps + 1];
            var blurs = new double[steps + 1];
            for (var step = 0; step <= steps; step++)
            {
                var time = (double)step / steps;
                var hidden = step == steps ? to : VisibilityPose.Hidden(options.Style, from, to, time, options.Appearing, reversing);
                var pose = VisibilityPose.PoseAt(options.Style, hidden, options.Appearing, options.Annotation, poseSeed);
                var transform = pose.Transform;
                var opacity = pose.Opacity * resting;
                if (options.Initial is { } initial && time < 0.35)
                {
                    // Blend out of the interrupted pose so a reversal never jumps.
                    var weight = MotionCurves.Smooth(time / 0.35);
                    transform = MotionTransform.Mix(initial.Transform, transform, weight);
                    opacity = initial.Opacity + (opacity - initial.Opacity) * weight;
                }
                keyTimes[step] = time;
                transforms[step] = transform;
                opacities[step] = opacity;
                blurs[step] = pose.Blur;
            }
            Transform = new TransformTrack(keyTimes, transforms, MotionCurves.Linear);
            Opacity = new ScalarTrack(keyTimes, opacities, MotionCurves.Linear);
            if (options.Style == VisibilityAnimationStyle.Focus)
            {
                Blur = new ScalarTrack(keyTimes, blurs, MotionCurves.Linear);
            }
            return;
        }
        var hiddenTransform = options.HiddenTransform ?? ContentTransform(options.Style, options.Width, options.Height, 0,
            options.Flipped, options.Edge, options.Annotation);
        InitialState = options.Initial ?? new LayerState(options.Appearing ? hiddenTransform : MotionTransform.Identity,
                                                         options.Appearing ? 0 : resting);
        var end = options.Appearing ? MotionTransform.Identity : hiddenTransform;
        var endOpacity = options.Appearing ? resting : 0;
        EffectiveDuration = reversing
            ? options.Duration * Math.Max(MotionTiming.MinimumReversalShare, RemainingFraction(InitialState, end, endOpacity, hiddenTransform))
            : options.Duration;
        var curve = reversing ? MotionCurves.Standard : options.Appearing ? MotionCurves.Enter : MotionCurves.Exit;
        if (options.MagicSeed is { } magicSeed)
        {
            FlightTransforms = MagicFlight.Transforms(InitialState.Transform, end, magicSeed);
            Transform = new TransformTrack(EvenKeyTimes(FlightTransforms.Length), FlightTransforms, MagicFlight.TimingFunction);
        }
        else if ((options.Style is VisibilityAnimationStyle.Slide or VisibilityAnimationStyle.Corners) && options.HiddenTransform is not null)
        {
            Transform = GatherPath(InitialState.Transform, end, options, curve);
        }
        else
        {
            Transform = new TransformTrack([0, 1], [InitialState.Transform, end], curve);
        }
        if (options.HiddenTransform is not null)
        {
            double[] values = options.Appearing
                ? [InitialState.Opacity, resting, resting]
                : [InitialState.Opacity, InitialState.Opacity, 0];
            if (options.MagicSeed is not null)
            {
                Opacity = new ScalarTrack(options.Appearing ? [0, 0.15, 1] : [0, 0.82, 1], values, MagicFlight.TimingFunction);
            }
            else
            {
                Opacity = new ScalarTrack(options.Appearing ? [0, 0.2, 1] : [0, 0.72, 1], values,
                                          options.Appearing ? MotionCurves.Fade : MotionCurves.Linear);
            }
        }
        else
        {
            Opacity = new ScalarTrack([0, 1], [InitialState.Opacity, endOpacity], options.Appearing ? MotionCurves.Fade : MotionCurves.Linear);
        }
    }

    /// <summary>Seconds from the transition's start.</summary>
    public double Started { get; }

    /// <summary>The duration after a reversal shortened it: reversals with less distance left finish proportionally sooner.</summary>
    public double EffectiveDuration { get; }

    public double EndTime => Started + EffectiveDuration;

    public bool Reversing { get; }

    public LayerState InitialState { get; }

    public TransformTrack Transform { get; }

    public ScalarTrack Opacity { get; }

    /// <summary>Soft focus's blur radius in points; null for other styles.</summary>
    public ScalarTrack? Blur { get; }

    /// <summary>The Magic route's keyframes (empty for other styles); the light trails follow them.</summary>
    public MotionTransform[] FlightTransforms { get; } = [];

    /// <summary>
    /// The presentation state at <paramref name="time"/> (seconds from the transition's start), as the Mac reads it from
    /// the presentation layer; posed styles also report how hidden the layer is.
    /// </summary>
    public LayerState StateAt(double time)
    {
        var local = EffectiveDuration > 0 ? Math.Min(1, Math.Max(0, (time - Started) / EffectiveDuration)) : 1;
        double? progress = _posed ? VisibilityPose.Hidden(_poseStyle, _poseFrom, _poseTo, local, _poseAppearing, _poseReversing) : null;
        return new LayerState(Transform.At(local), Opacity.At(local), progress);
    }

    /// <summary>Swift <c>WorkspaceLayerMotion.duration(_:appearing:)</c>.</summary>
    public static double Duration(VisibilityAnimationStyle style, bool appearing) => MotionTiming.VisibilityDuration(style, appearing);

    /// <summary>Share of the full travel still left between <paramref name="current"/> and <paramref name="end"/>, from movement, scale or opacity.</summary>
    public static double RemainingFraction(LayerState current, MotionTransform end, double endOpacity, MotionTransform hidden)
    {
        var fraction = Math.Abs(endOpacity - current.Opacity);
        var span = double.Hypot(hidden.Tx, hidden.Ty);
        if (span > 1)
        {
            fraction = Math.Max(fraction, double.Hypot(end.Tx - current.Transform.Tx, end.Ty - current.Transform.Ty) / span);
        }
        var scaleSpan = Math.Abs(1 - hidden.Scale);
        if (scaleSpan > 0.01)
        {
            fraction = Math.Max(fraction, Math.Abs(end.Scale - current.Transform.Scale) / scaleSpan);
        }
        return Math.Min(1, fraction);
    }

    /// <summary>
    /// Swift <c>WorkspaceLayerMotion.contentTransform</c> for the styles that can still play: the toolbar's own slide
    /// (12 points from its dock edge) and Dissolve's rise; the others stay in place. Retired styles resolve before they
    /// play (<see cref="VisibilityAnimationStyleExtensions"/>), so Fold has no transform here.
    /// </summary>
    public static MotionTransform ContentTransform(VisibilityAnimationStyle style, double width, double height, double progress,
                                                   bool flipped, ToolbarDockEdge edge = ToolbarDockEdge.Bottom, bool annotation = false)
    {
        var remaining = 1 - Math.Min(1, Math.Max(0, progress));
        if (!(remaining > 0) || !(width > 0) || !(height > 0))
        {
            return MotionTransform.Identity;
        }
        switch (style)
        {
            case VisibilityAnimationStyle.Slide or VisibilityAnimationStyle.Fluid or VisibilityAnimationStyle.Quick
                or VisibilityAnimationStyle.Magic:
            {
                var offset = ToolbarSlideMotion.Offset(edge, flipped);
                var distance = remaining * (style == VisibilityAnimationStyle.Quick ? 0.5 : 1);
                return MotionTransform.MakeTranslation(offset.X * distance, offset.Y * distance);
            }
            case VisibilityAnimationStyle.Rise or VisibilityAnimationStyle.Dissolve:
                return MotionTransform.MakeTranslation(0, (flipped ? 1 : -1) * (annotation ? 40 : 10) * remaining);
            default:
                return MotionTransform.Identity;
        }
    }

    /// <summary>
    /// Gather and Corners: a curved path with uniform scaling. Shapes keep their size early in a hide and grow early in a
    /// show; log space keeps the shrink even.
    /// </summary>
    private static TransformTrack GatherPath(MotionTransform from, MotionTransform end, LayerMotionOptions options, CubicBezier curve)
    {
        double dx = end.Tx - from.Tx, dy = end.Ty - from.Ty;
        var length = Math.Max(1, double.Hypot(dx, dy));
        var bend = options.Style == VisibilityAnimationStyle.Corners
            ? Math.Min(28, length * 0.06) * options.CurveSign
            : Math.Min(40, length * 0.12);
        double startScale = Math.Max(0.001, from.A), endScale = Math.Max(0.001, end.A);
        const int steps = 24;
        var keyTimes = EvenKeyTimes(steps + 1);
        var values = new MotionTransform[steps + 1];
        for (var step = 0; step <= steps; step++)
        {
            var t = (double)step / steps;
            var arc = Math.Sin(t * Math.PI) * bend;
            var weight = options.Appearing ? MotionCurves.Smooth(t / 0.8) : MotionCurves.Smooth((t - 0.2) / 0.8);
            var scale = weight <= 0 ? startScale
                : weight >= 1 ? endScale
                : Math.Exp(Math.Log(startScale) + (Math.Log(endScale) - Math.Log(startScale)) * weight);
            values[step] = MotionTransform.MakeScale(scale)
                .WithTranslation(from.Tx + dx * t - dy / length * arc, from.Ty + dy * t + dx / length * arc);
        }
        return new TransformTrack(keyTimes, values, curve);
    }

    private static double[] EvenKeyTimes(int count)
    {
        var times = new double[count];
        for (var index = 0; index < count; index++)
        {
            times[index] = (double)index / (count - 1);
        }
        return times;
    }
}
