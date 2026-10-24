using Annotator.Core.Geometry;
using Annotator.Core.Motion;

namespace Annotator.Core.Tutorial;

/// <summary>
/// The tutorial's motion (Swift <c>TutorialMotion</c>): durations in seconds, distances in points, and the curves both apps
/// use. Nothing loops: every animation plays once and ends in a still state. With Reduce Motion (Animation effects off on
/// Windows) the ghost and the confetti stay away and the cards cross-fade.
/// </summary>
public static class TutorialMotion
{
    /// <summary>A small overshoot for the check mark's circle and keycaps that pop in.</summary>
    public static CubicBezier Pop { get; } = new(0.34, 1.4, 0.64, 1);

    /// <summary>The card enters: it rises from below, grows from 96% and fades in.</summary>
    public const double CardEnterDuration = 0.42;
    public const double CardEnterRise = 16;
    public const double CardEnterScale = 0.96;
    public const double CardEnterFade = 0.24;

    /// <summary>The card leaves: it sinks a little and fades out.</summary>
    public const double CardExitDuration = 0.2;
    public const double CardExitSink = 8;
    public const double CardExitScale = 0.98;

    /// <summary>A step's words leave upward, then the next step's words rise into place.</summary>
    public const double StepOutDuration = 0.16;
    public const double StepOutRise = 10;
    public const double StepInDelay = 0.06;
    public const double StepInDuration = 0.38;
    public const double StepInRise = 14;
    public const double StepInFade = 0.24;

    /// <summary>The check mark draws itself while its circle pops in.</summary>
    public const double CheckDuration = 0.34;
    public const double CheckStartScale = 0.6;

    /// <summary>The pause after a step is done before the next step shows; shorter when the next is done already.</summary>
    public const double DoneBeat = 0.85;
    public const double QuickDoneBeat = 0.45;

    /// <summary>The progress track's leading edge moves first and the trailing edge follows (the old tour's step capsule).</summary>
    public const double TrackLeadDuration = 0.32;
    public const double TrackTrailDuration = 0.5;
    public const double TrackTrailDelay = 0.07;

    /// <summary>A keycap goes down and comes back up; keys of a chord go down one after another.</summary>
    public const double KeyDownDuration = 0.08;
    public const double KeyUpDuration = 0.14;
    public const double KeyStagger = 0.07;
    public const double KeyTravel = 2;

    /// <summary>The ghost starts after the step's words settle.</summary>
    public const double GhostStartDelay = 0.55;
    public const double GhostAppearDuration = 0.22;
    public const double GhostAppearScale = 0.8;
    public const double GhostVanishDuration = 0.28;
    public const double GhostPause = 0.25;
    public const double GhostPressDuration = 0.08;
    public const double GhostReleaseDuration = 0.14;
    public const double GhostPressScale = 0.86;

    /// <summary>The pause between the two clicks of the ghost's double-click.</summary>
    public const double DoubleClickGap = 0.1;

    /// <summary>A click sends out one ring.</summary>
    public const double RippleDuration = 0.5;
    public const double RippleStartRadius = 4;
    public const double RippleEndRadius = 24;
    public const double RippleStartOpacity = 0.55;

    /// <summary>The ghost's ink fades once the ghost lets go.</summary>
    public const double InkFadeDelay = 0.35;
    public const double InkFadeDuration = 0.4;
    public const double InkOpacity = 0.6;
    public const double InkWidth = 3;

    /// <summary>The ring around the toolbar control to use: one pulse in, then it stays until the goal is done.</summary>
    public const double TargetPulseDuration = 0.6;
    public const double TargetPulseScale = 1.18;
    public const double TargetOutset = 3;
    public const double TargetCornerRadius = 10;
    public const double TargetWidth = 2;
    public const double TargetFadeDuration = 0.2;

    /// <summary>With no progress for this long, the card adds a hint and points at Show me once.</summary>
    public const double HintDelay = 15;

    /// <summary>
    /// How long the ghost travels <paramref name="distance"/> points: quick for short hops, never slower than 1.1 seconds.
    /// </summary>
    public static double TravelDuration(double distance) => Math.Clamp(0.35 + Math.Abs(distance) / 1400, 0.45, 1.1);

    /// <summary>
    /// How long the ghost takes to draw or drag along a path of <paramref name="length"/> points: 480 points a second, between
    /// half a second and 1.6 seconds. It follows the path by length with the standard curve.
    /// </summary>
    public static double DragDuration(double length) => Math.Clamp(Math.Abs(length) / 480, 0.5, 1.6);

    /// <summary>
    /// The ghost's position at linear time <paramref name="t"/> (0...1) on its way from <paramref name="from"/> to
    /// <paramref name="to"/>: a gentle arc that bends 12% of the distance to the right of travel, eased with the standard curve.
    /// </summary>
    public static Point TravelPoint(Point from, Point to, double t)
    {
        double eased = MotionCurves.Standard.Progress(Math.Clamp(t, 0, 1));
        Point delta = to - from;
        double distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        Point middle = (from + to) * 0.5;
        Point control = distance < 1e-9 ? middle : middle + new Point(-delta.Y / distance, delta.X / distance) * (distance * 0.12);
        double inverse = 1 - eased;
        return from * (inverse * inverse) + control * (2 * inverse * eased) + to * (eased * eased);
    }

    /// <summary>
    /// A smooth line through <paramref name="points"/> (Catmull-Rom), <paramref name="segments"/> samples between each pair:
    /// the ghost's curved arrow.
    /// </summary>
    public static IReadOnlyList<Point> Smooth(IReadOnlyList<Point> points, int segments = 12)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3)
        {
            return [.. points];
        }
        var result = new List<Point> { points[0] };
        for (int index = 0; index < points.Count - 1; index++)
        {
            Point p0 = points[Math.Max(0, index - 1)], p1 = points[index], p2 = points[index + 1], p3 = points[Math.Min(points.Count - 1, index + 2)];
            for (int step = 1; step <= segments; step++)
            {
                double t = step / (double)segments, t2 = t * t, t3 = t2 * t;
                result.Add((p1 * 2 + (p2 - p0) * t + (p0 * 2 - p1 * 5 + p2 * 4 - p3) * t2 + (p1 * 3 - p0 - p2 * 3 + p3) * t3) * 0.5);
            }
        }
        return result;
    }

    /// <summary>The arrowhead's two barbs, in points, at the end of the ghost's curved arrow.</summary>
    public const double ArrowheadLength = 14;

    /// <summary>The angle between each barb and the arrow's line, in degrees.</summary>
    public const double ArrowheadAngle = 28;

    /// <summary>
    /// The ends of the arrowhead's two barbs at <paramref name="tip"/>, for a line arriving from <paramref name="from"/>; the
    /// barbs are <paramref name="length"/> long (scale it with the display).
    /// </summary>
    public static (Point Left, Point Right) Arrowhead(Point from, Point tip, double length = ArrowheadLength)
    {
        double heading = Math.Atan2(tip.Y - from.Y, tip.X - from.X);
        double spread = ArrowheadAngle * Math.PI / 180;
        Point Barb(double angle) => new(tip.X - Math.Cos(angle) * length, tip.Y - Math.Sin(angle) * length);
        return (Barb(heading - spread), Barb(heading + spread));
    }

    /// <summary>A loop around <paramref name="center"/> for the ghost's pen: a little wider than tall, closing past its start.</summary>
    public static IReadOnlyList<Point> Loop(Point center, double radius, int samples = 48)
    {
        var result = new List<Point>(samples + 1);
        for (int index = 0; index <= samples; index++)
        {
            double angle = -Math.PI / 2 + index / (double)samples * Math.PI * 2.15;
            double wobble = 1 + 0.04 * Math.Sin(angle * 3);
            result.Add(new Point(center.X + Math.Cos(angle) * radius * 1.25 * wobble, center.Y + Math.Sin(angle) * radius * wobble));
        }
        return result;
    }
}

/// <summary>One piece of confetti (Swift <c>ConfettiPiece</c>).</summary>
/// <param name="Start">Where it leaves, in points from the display's top-left corner.</param>
/// <param name="Velocity">Its launch speed in points per second; y grows downward.</param>
/// <param name="Delay">Seconds after the burst before it leaves.</param>
/// <param name="Lifetime">Seconds it lives after it leaves; it fades out over the last <see cref="ConfettiBurst.FadeDuration"/>.</param>
/// <param name="Angle">Its first turn in degrees.</param>
/// <param name="Spin">How fast it turns, in degrees per second.</param>
/// <param name="Phase">Where its flutter starts, in radians.</param>
/// <param name="Flutter">How fast it flutters (its width follows the cosine), in radians per second.</param>
/// <param name="Shape">0 a rectangle, 1 a dot, 2 a ribbon.</param>
/// <param name="Color">Its index in <see cref="ConfettiBurst.Palette"/>.</param>
public readonly record struct ConfettiPiece(Point Start, Point Velocity, double Delay, double Lifetime, double Angle, double Spin,
                                           double Phase, double Flutter, int Shape, int Color);

/// <summary>Where a piece of confetti is at one moment.</summary>
public readonly record struct ConfettiState(Point Position, double Angle, double WidthScale, double Opacity);

/// <summary>
/// The celebration at the end (Swift <c>ConfettiBurst</c>): two waves of confetti pop from both bottom corners of the display,
/// rise up to nine tenths of its height, drift as far as its middle and float back down. The pieces come from a fixed seed and
/// their paths have a closed form, so both apps show the same burst.
/// </summary>
public static class ConfettiBurst
{
    public const ulong Seed = 0x5EED_A110_7A7E_C0DE;
    public const int PiecesPerCorner = 110;
    public const int Waves = 2;

    /// <summary>The second wave leaves this long after the first.</summary>
    public const double WaveGap = 0.4;

    /// <summary>How strongly air slows a piece, per second: enough that pieces float down rather than drop.</summary>
    public const double Drag = 2.0;
    public const double FadeDuration = 0.45;

    /// <summary>The longest the burst can last, delays included.</summary>
    public const double Duration = 4.2;

    /// <summary>Sizes in points: a rectangle, a dot and a ribbon.</summary>
    public static IReadOnlyList<Size> ShapeSizes { get; } = [new Size(8, 13), new Size(7, 7), new Size(4, 16)];

    /// <summary>The ink colors people know from the palette, with the accent blue.</summary>
    public static IReadOnlyList<RGBA> Palette { get; } =
    [
        Annotator.Core.Palette.Coral, Annotator.Core.Palette.Blue, Annotator.Core.Palette.Yellow, new RGBA(0.28, 0.69, 0.4), new RGBA(0.89, 0.26, 0.4), new RGBA(0, 0.6, 0.7),
        new RGBA(1, 0.66, 0.23),
    ];

    /// <summary>Gravity for a display of <paramref name="height"/> points: taller displays get a stronger pull and launch.</summary>
    public static double Gravity(double height) => 900 * Scale(height);

    /// <summary>The pieces for a display of <paramref name="size"/> points: wave by wave, left corner first.</summary>
    public static IReadOnlyList<ConfettiPiece> Pieces(Size size)
    {
        var random = new SplitMix64(Seed);
        double scale = Scale(size.Height);
        var pieces = new List<ConfettiPiece>(PiecesPerCorner * 2 * Waves);
        for (int wave = 0; wave < Waves; wave++)
        {
            for (int corner = 0; corner < 2; corner++)
            {
                for (int index = 0; index < PiecesPerCorner; index++)
                {
                    // rise 0 peaks at about 35% of the height and 1 at about 90%; reach 0 lands a tenth of the width from its
                    // corner and 1 at the middle.
                    double rise = random.NextDouble();
                    double reach = random.NextDouble();
                    double direction = corner == 0 ? 1 : -1;
                    var start = new Point(corner == 0 ? random.NextDouble() * 24 : size.Width - random.NextDouble() * 24,
                                          size.Height + 6 - random.NextDouble() * 12);
                    var velocity = new Point((0.2 + reach * 0.8) * size.Width * direction, -(1220 + rise * 1240) * scale);
                    double delay = wave * WaveGap + random.NextDouble() * 0.18;
                    double lifetime = 1.8 + rise * 1.5 + random.NextDouble() * 0.3;
                    double turn = random.NextDouble() * 360;
                    double spin = (random.NextDouble() * 2 - 1) * 540;
                    double phase = random.NextDouble() * Math.PI * 2;
                    double flutter = 6 + random.NextDouble() * 8;
                    double roll = random.NextDouble();
                    int shape = roll < 0.6 ? 0 : roll < 0.8 ? 1 : 2;
                    int color = (int)(random.NextDouble() * Palette.Count) % Palette.Count;
                    pieces.Add(new ConfettiPiece(start, velocity, delay, lifetime, turn, spin, phase, flutter, shape, color));
                }
            }
        }
        return pieces;
    }

    /// <summary>
    /// Where <paramref name="piece"/> is <paramref name="time"/> seconds after the burst on a display of
    /// <paramref name="height"/> points. Air slows it in proportion to its speed and gravity pulls it down, so
    /// v(t) = g/k + (v0 - g/k)e^(-kt) vertically and v0·e^(-kt) sideways. Null before it leaves and after it ends.
    /// </summary>
    public static ConfettiState? State(ConfettiPiece piece, double time, double height)
    {
        double t = time - piece.Delay;
        if (t < 0 || t > piece.Lifetime + 1e-9)
        {
            return null;
        }
        t = Math.Min(t, piece.Lifetime);
        double g = Gravity(height);
        double decay = (1 - Math.Exp(-Drag * t)) / Drag;
        double x = piece.Start.X + piece.Velocity.X * decay;
        double y = piece.Start.Y + g / Drag * t + (piece.Velocity.Y - g / Drag) * decay;
        double opacity = Math.Clamp((piece.Lifetime - t) / FadeDuration, 0, 1);
        return new ConfettiState(new Point(x, y), piece.Angle + piece.Spin * t, Math.Cos(piece.Phase + piece.Flutter * t), opacity);
    }

    private static double Scale(double height) => Math.Clamp(height / 900, 0.75, 1.4);
}

/// <summary>SplitMix64: a small random number generator that gives the same numbers on every platform (Swift <c>SplitMix64</c>).</summary>
public struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A number in 0..&lt;1 from the top 53 bits.</summary>
    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
}
