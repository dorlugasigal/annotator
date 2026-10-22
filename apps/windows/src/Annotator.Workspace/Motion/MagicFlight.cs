using Annotator.Core.Motion;

namespace Annotator.Workspace.Motion;

/// <summary>
/// Port of the Mac's <c>MagicFlight</c>: one confident swoop (one to one and a half turns) that is strongest early and
/// glides straight into place, with slight banking. Transforms are in the Mac's y-up space.
/// </summary>
public static class MagicFlight
{
    public const int Steps = 96;
    public const double MaximumBend = 110;
    public const double MaximumBank = 0.15;

    /// <summary>Peak of <c>(1-t)^1.6 · sin(πt)</c>, so the envelope tops out at one.</summary>
    private const double EnvelopePeak = 0.4572;

    /// <summary>The flight's timing curve, applied to the whole timeline (shape, trails and sparks).</summary>
    public static CubicBezier TimingFunction { get; } = new(0.22, 0.06, 0.24, 1);

    /// <summary>Swift <c>MagicFlight.seed(for:)</c>: 64-bit FNV-1a over the identifier's UTF-8 bytes.</summary>
    public static ulong Seed(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var hash = 14_695_981_039_346_656_037UL;
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in identifier.EnumerateRunes())
        {
            var count = rune.EncodeToUtf8(buffer);
            for (var index = 0; index < count; index++)
            {
                hash = unchecked((hash ^ buffer[index]) * 1_099_511_628_211UL);
            }
        }
        return hash;
    }

    /// <summary>Swift <c>MagicFlight.transforms(from:to:seed:)</c>: <see cref="Steps"/> + 1 keyframes with exact endpoints.</summary>
    public static MotionTransform[] Transforms(MotionTransform from, MotionTransform to, ulong seed)
    {
        double dx = to.Tx - from.Tx, dy = to.Ty - from.Ty;
        var distance = double.Hypot(dx, dy);
        var direction = distance > 0.001 ? new MotionPoint(dx / distance, dy / distance) : new MotionPoint(1, 0);
        var normal = new MotionPoint(-direction.Y, direction.X);
        var amplitude = Math.Min(MaximumBend, Math.Max(24, distance * 0.18));
        var cycles = 1 + seed % 2 * 0.5;
        var phase = (seed >> 8) % 360 * Math.PI / 180;
        double startScale = from.Scale, endScale = to.Scale;
        double startAngle = from.Angle, endAngle = to.Angle;

        MotionPoint Point(double t)
        {
            var envelope = Math.Pow(1 - t, 1.6) * Math.Sin(Math.PI * t) / EnvelopePeak;
            var side = amplitude * envelope * Math.Sin(2 * Math.PI * cycles * t + phase);
            var forward = amplitude * 0.12 * envelope * Math.Sin(2 * Math.PI * t + phase);
            return new MotionPoint(from.Tx + dx * t + direction.X * forward + normal.X * side,
                                   from.Ty + dy * t + direction.Y * forward + normal.Y * side);
        }

        var result = new MotionTransform[Steps + 1];
        for (var step = 0; step <= Steps; step++)
        {
            if (step == 0)
            {
                result[step] = from;
                continue;
            }
            if (step == Steps)
            {
                result[step] = to;
                continue;
            }
            var t = (double)step / Steps;
            var location = Point(t);
            MotionPoint before = Point(Math.Max(0, t - 0.003)), after = Point(Math.Min(1, t + 0.003));
            double vx = after.X - before.X, vy = after.Y - before.Y;
            var turn = Math.Atan2(vx * normal.X + vy * normal.Y, vx * direction.X + vy * direction.Y);
            var bank = Math.Min(MaximumBank, Math.Max(-MaximumBank,
                startAngle * (1 - t) + endAngle * t + turn * 0.18 * Math.Sin(Math.PI * t)));
            var scale = startScale + (endScale - startScale) * t;
            result[step] = MotionTransform.MakeRotation(bank).Scaled(scale).WithTranslation(location.X, location.Y);
        }
        return result;
    }
}

/// <summary>One light trail stroke of a Magic flight: width in points, peak opacity and sRGB color.</summary>
public readonly record struct MagicStroke(double Width, double PeakOpacity, double Red, double Green, double Blue);

/// <summary>One warm or white spark following a Magic flight: its positions at the flight's keyframes.</summary>
public sealed record MagicSpark(double Diameter, double Red, double Green, double Blue, MotionPoint[] Positions);

/// <summary>
/// Port of the Mac's <c>MagicTrail</c> geometry: three trail strokes along the flight path, revealed between a moving
/// start and end with the flight's timeline, and six sparks. Points are in the parent's y-up space, in points.
/// </summary>
public sealed class MagicTrailGeometry
{
    public static readonly MagicStroke[] Strokes =
    [
        new(9.0, 0.10, 0.3, 0.75, 1),
        new(4.0, 0.30, 0.3, 0.75, 1),
        new(1.25, 0.85, 0.85, 0.96, 1),
    ];

    /// <summary>Opacity keyframe times of every trail layer: fade in, hold, fade out by landing.</summary>
    public static readonly double[] FadeTimes = [0, 0.12, 0.75, 1];

    private MagicTrailGeometry(MotionPoint[] points, double[] ends, double[] starts, MagicSpark[] sparks, double length)
    {
        Points = points;
        Ends = ends;
        Starts = starts;
        Sparks = sparks;
        Length = length;
    }

    /// <summary>The flight path: the image center moved by each flight transform.</summary>
    public MotionPoint[] Points { get; }

    /// <summary>The share of the path behind each point (<c>strokeEnd</c> keyframes).</summary>
    public double[] Ends { get; }

    /// <summary>The trail's tail (<c>strokeStart</c> keyframes): at most 110 points or 28% behind the head.</summary>
    public double[] Starts { get; }

    public MagicSpark[] Sparks { get; }

    /// <summary>The path's length in points.</summary>
    public double Length { get; }

    /// <summary>Opacity values for <see cref="FadeTimes"/> with <paramref name="peak"/>.</summary>
    public static double[] Fade(double peak) => [0, peak, peak * 0.7, 0];

    /// <summary>Builds the trail of an image whose center is at <paramref name="position"/>; throws for fewer than two keyframes.</summary>
    public static MagicTrailGeometry Create(MotionPoint position, IReadOnlyList<MotionTransform> transforms, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        if (transforms.Count <= 1)
        {
            throw Core.CanvasError.InvalidGeometry;
        }
        var points = new MotionPoint[transforms.Count];
        for (var index = 0; index < points.Length; index++)
        {
            points[index] = new MotionPoint(position.X + transforms[index].Tx, position.Y + transforms[index].Ty);
        }
        var lengths = new double[points.Length];
        for (var index = 1; index < points.Length; index++)
        {
            lengths[index] = lengths[index - 1] + points[index].Distance(points[index - 1]);
        }
        var total = Math.Max(1, lengths[^1]);
        var ends = new double[points.Length];
        var starts = new double[points.Length];
        var tail = Math.Min(0.28, 110 / total);
        for (var index = 0; index < points.Length; index++)
        {
            ends[index] = lengths[index] / total;
            starts[index] = Math.Max(0, ends[index] - tail);
        }
        var sparks = new MagicSpark[6];
        for (var index = 0; index < sparks.Length; index++)
        {
            var diameter = index % 2 == 0 ? 2.0 : 3.0;
            var phase = unchecked(seed + (ulong)index * 31) % 360 * Math.PI / 180;
            var positions = new MotionPoint[points.Length];
            for (var sample = 0; sample < points.Length; sample++)
            {
                var t = (double)sample / (points.Length - 1);
                var point = points[Math.Max(0, sample - 2 - index * 2)];
                var spread = Math.Sin(Math.PI * t) * (3 + index);
                positions[sample] = new MotionPoint(point.X + Math.Sin(t * 12 + phase) * spread, point.Y + Math.Cos(t * 9 + phase) * spread);
            }
            sparks[index] = index % 3 == 0
                ? new MagicSpark(diameter, 1, 0.76, 0.3, positions)
                : new MagicSpark(diameter, 0.85, 0.96, 1, positions);
        }
        return new MagicTrailGeometry(points, ends, starts, sparks, lengths[^1]);
    }
}

/// <summary>
/// Port of the Mac's <c>MagicToolbarEffect</c> poses: the toolbar shrinks into a blue point, which bursts into twelve sparks.
/// Showing plays it backwards.
/// </summary>
public static class MagicToolbarPoof
{
    public const int SparkCount = 12;

    /// <summary>Keyframes of the poof (61 samples, linear between them).</summary>
    public const int Samples = 60;

    public readonly record struct Pose(double Scale, double ImageOpacity, double GlowOpacity, double Radius, double SparkOpacity);

    public static double SmoothStep(double lower, double upper, double value)
    {
        var t = Math.Min(1, Math.Max(0, (value - lower) / (upper - lower)));
        return t * t * (3 - 2 * t);
    }

    public static Pose PoseAt(double progress)
    {
        var shrink = SmoothStep(0, 0.62, progress);
        var burst = SmoothStep(0.62, 1, progress);
        return new Pose(1 - 0.985 * shrink,
                        1 - SmoothStep(0.44, 0.64, progress),
                        SmoothStep(0.30, 0.60, progress) * (1 - SmoothStep(0.70, 0.95, progress)),
                        46 * burst,
                        SmoothStep(0.62, 0.72, progress) * (1 - SmoothStep(0.78, 1, progress)));
    }

    /// <summary>The poof's progress (0 at rest, 1 gone) at linear <paramref name="time"/> in 0...1.</summary>
    public static double Progress(double from, double to, double time) => from + (to - from) * SmoothStep(0, 1, time);

    /// <summary>A reversal that is nearly home finishes proportionally sooner.</summary>
    public static double Duration(double duration, double from, double to, bool reversing) =>
        reversing ? duration * Math.Max(MotionTiming.MinimumReversalShare, Math.Abs(to - from)) : duration;

    /// <summary>Spark <paramref name="index"/>'s offset from the toolbar's center at <paramref name="radius"/>, in y-up points.</summary>
    public static MotionPoint SparkOffset(int index, double radius)
    {
        var angle = index * 2 * Math.PI / SparkCount;
        var distance = 0.7 + index % 4 * 0.1;
        return new MotionPoint(Math.Cos(angle) * radius * distance, Math.Sin(angle) * radius * distance);
    }

    /// <summary>Spark color: every third spark is white, the others blue.</summary>
    public static bool SparkIsWhite(int index) => index % 3 == 0;
}

/// <summary>Port of the Mac's <c>CornerRoutes</c>: each shape's nearest inset corner of its own display, in y-up space.</summary>
public sealed class CornerRoutes
{
    public readonly record struct Item(string Id, string Display, MotionRect VisibleFrame, MotionPoint Center);

    public readonly record struct Destination(int Corner, MotionPoint Point, double BendSign);

    private Item[] _signature = [];
    private readonly Dictionary<string, Destination> _destinations = [];

    public IReadOnlyDictionary<string, Destination> Destinations => _destinations;

    /// <summary>Recomputes the assignments when the shapes, their positions or the displays changed; an unchanged set keeps them.</summary>
    public void Update(IEnumerable<Item> items)
    {
        var sorted = items.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (sorted.AsSpan().SequenceEqual(_signature))
        {
            return;
        }
        _signature = sorted;
        _destinations.Clear();
        foreach (var item in sorted)
        {
            var nearest = 0;
            var distance = double.PositiveInfinity;
            for (var corner = 0; corner < 4; corner++)
            {
                var target = Point(corner, item.VisibleFrame);
                var candidate = target.Distance(item.Center);
                if (candidate < distance)
                {
                    nearest = corner;
                    distance = candidate;
                }
            }
            byte parity = 0;
            foreach (var value in System.Text.Encoding.UTF8.GetBytes(item.Id))
            {
                parity ^= value;
            }
            _destinations[item.Id] = new Destination(nearest, Point(nearest, item.VisibleFrame), parity % 2 == 0 ? 1 : -1);
        }
    }

    /// <summary>Corner 0 is top-left, 1 top-right, 2 bottom-left and 3 bottom-right, inset by up to 24 points.</summary>
    public static MotionPoint Point(int corner, MotionRect frame)
    {
        var inset = Math.Min(24, Math.Min(frame.Width, frame.Height) / 4);
        return new MotionPoint(corner % 2 == 0 ? frame.MinX + inset : frame.MaxX - inset,
                               corner < 2 ? frame.MaxY - inset : frame.MinY + inset);
    }
}
