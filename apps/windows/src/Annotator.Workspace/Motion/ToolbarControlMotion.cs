using Annotator.Core.Motion;

namespace Annotator.Workspace.Motion;

/// <summary>A timeline sampled for the compositor's linear keyframes: a value at each time, in seconds from its start.</summary>
public sealed class MotionSamples
{
    private readonly double[] _times;
    private readonly double[] _values;

    public MotionSamples(double[] times, double[] values)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(values);
        if (times.Length < 2 || times.Length != values.Length || times[0] != 0)
        {
            throw new ArgumentException("A timeline needs at least two samples, one value per time, from time 0.", nameof(times));
        }
        _times = times;
        _values = values;
    }

    /// <summary>Seconds from the first sample to the last.</summary>
    public double Duration => _times[^1];

    public IReadOnlyList<double> Times => _times;

    public IReadOnlyList<double> Values => _values;

    /// <summary>The value at <paramref name="time"/> seconds, linear between samples and held outside them.</summary>
    public double At(double time)
    {
        if (!(time > 0))
        {
            return _values[0];
        }
        if (time >= Duration)
        {
            return _values[^1];
        }
        var index = Array.BinarySearch(_times, time);
        if (index >= 0)
        {
            return _values[index];
        }
        index = ~index;
        var share = (time - _times[index - 1]) / (_times[index] - _times[index - 1]);
        return _values[index - 1] + (_values[index] - _values[index - 1]) * share;
    }
}

/// <summary>
/// The toolbar's own control motion (Mac ToolbarView), as timelines the WinUI toolbar plays with composition keyframes:
/// the one selection marker that slides between tools and changes its look between Draw and Use apps, the mode
/// button's colors, the dot's pulse when drawing resumes, and its symbol's replacement.
/// </summary>
public static class ToolbarControlMotion
{
    /// <summary>Samples per second of each timeline: two per frame at 60 Hz, smooth at 240 Hz between linear keyframes.</summary>
    public const double SampleRate = 120;

    /// <summary>Mac phaseAnimator([1.0, 1.6, 1.0]) on the mode dot.</summary>
    public const double PulsePeak = 1.6;

    /// <summary>How long the old symbol stays before it goes (reference/ux-reference/macos-live/sharing-live).</summary>
    public const double SymbolHold = 0.08;

    /// <summary>How long the old symbol takes to go before the new one appears, from the same recording.</summary>
    public const double SymbolSwap = 0.09;

    /// <summary>
    /// Mac selectionAnimation: none while the workspace is hidden or a show or hide plays, so the toolbar's snapshot
    /// shows the state the motion lands on.
    /// </summary>
    public static bool Animates(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Visibility is null && !snapshot.WorkspaceHidden;
    }

    /// <summary>
    /// Mac Motion.resolve(Motion.snappy, reduceMotion): progress from 0 to 1 for the marker's slide and the Draw and Use
    /// apps colors, on the snappy spring, or Motion.reduced's ease-out with Reduce Motion (Windows: Animation effects off).
    /// </summary>
    public static MotionSamples Selection(bool reduceMotion) =>
        reduceMotion ? Timed(MotionTiming.Reduced, 0, 1) : Spring(MotionTiming.Snappy, 0, 1);

    /// <summary>
    /// The mode dot's scale when drawing resumes: ease-out for 0.12 s up to 1.6, then the playful spring back to 1 (it dips
    /// just below 1 once). Null with Reduce Motion, where the Mac keeps the dot's size.
    /// </summary>
    public static MotionSamples? DotPulse(bool reduceMotion)
    {
        if (reduceMotion)
        {
            return null;
        }
        MotionSamples up = Timed(new TimedMotion(0.12, MotionCurves.EaseOut), 1, PulsePeak);
        MotionSamples down = Spring(MotionTiming.Playful, PulsePeak, 1);
        return Join(up, down);
    }

    /// <summary>
    /// The mode button's symbol leaving (Mac contentTransition(.symbolEffect(.replace))): its opacity, which stays, then
    /// fades. The Mac's symbol also shrinks; WinUI draws a scaling icon blurred, so Windows keeps its size. Null with
    /// Reduce Motion, where the symbol changes at once.
    /// </summary>
    public static MotionSamples? SymbolOut(bool reduceMotion) =>
        reduceMotion ? null : new MotionSamples([0, SymbolHold, SymbolHold + SymbolSwap], [1, 1, 0]);

    /// <summary>The new symbol's opacity: none while the old one leaves, then it appears on the snappy spring.</summary>
    public static MotionSamples? SymbolIn(bool reduceMotion)
    {
        if (reduceMotion)
        {
            return null;
        }
        var start = SymbolHold + SymbolSwap;
        MotionSamples appear = Spring(MotionTiming.Snappy, 0, 1);
        var times = new double[appear.Times.Count + 1];
        var values = new double[times.Length];
        for (var index = 0; index < appear.Times.Count; index++)
        {
            times[index + 1] = start + appear.Times[index];
            values[index + 1] = Math.Min(1, appear.Values[index]);
        }
        return new MotionSamples(times, values);
    }

    /// <summary>A spring from <paramref name="from"/> to <paramref name="to"/>, sampled until it settles (within 0.1%).</summary>
    public static MotionSamples Spring(SpringMotion spring, double from, double to)
    {
        var settle = spring.SettleTime();
        var count = Math.Max(2, (int)Math.Ceiling(settle * SampleRate) + 1);
        var times = new double[count];
        var values = new double[count];
        for (var index = 0; index < count; index++)
        {
            var time = Math.Min(settle, index / SampleRate);
            times[index] = time;
            values[index] = index == count - 1 ? to : from + (to - from) * spring.Value(time);
        }
        return new MotionSamples(times, values);
    }

    /// <summary>An eased change from <paramref name="from"/> to <paramref name="to"/> over the motion's duration.</summary>
    public static MotionSamples Timed(TimedMotion motion, double from, double to)
    {
        var count = Math.Max(2, (int)Math.Ceiling(motion.Duration * SampleRate) + 1);
        var times = new double[count];
        var values = new double[count];
        for (var index = 0; index < count; index++)
        {
            var time = Math.Min(motion.Duration, index / SampleRate);
            times[index] = time;
            values[index] = index == count - 1 ? to : from + (to - from) * motion.Curve.Progress(time / motion.Duration);
        }
        return new MotionSamples(times, values);
    }

    /// <summary><paramref name="second"/> after <paramref name="first"/>, which must end where it starts.</summary>
    private static MotionSamples Join(MotionSamples first, MotionSamples second)
    {
        var times = new double[first.Times.Count + second.Times.Count - 1];
        var values = new double[times.Length];
        for (var index = 0; index < first.Times.Count; index++)
        {
            times[index] = first.Times[index];
            values[index] = first.Values[index];
        }
        for (var index = 1; index < second.Times.Count; index++)
        {
            times[first.Times.Count + index - 1] = first.Duration + second.Times[index];
            values[first.Times.Count + index - 1] = second.Values[index];
        }
        return new MotionSamples(times, values);
    }
}
