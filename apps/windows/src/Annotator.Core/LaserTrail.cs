// Port of apps/macos/Sources/AnnotationCore/LaserTrail.swift.
namespace Annotator.Core;

/// <summary>
/// Swift <c>LaserTrail</c>: the laser pointer's trail. While the button is down, the pointer leaves a glowing line. Each
/// point of it lasts <see cref="Lifetime"/> seconds after the pointer passed and thins out as it ages, so the line
/// disappears behind the pointer; when the button goes up, what is left fades out as well. People choose the lifetime
/// with the laser pointer's Fade. The trail is never part of a
/// drawing: it has no Undo step and is never saved, copied or exported. A value, like the Swift struct (see Element.cs).
/// </summary>
public struct LaserTrail : IEquatable<LaserTrail>
{
    /// <summary>The laser pointer's width in a new profile.</summary>
    public const double DefaultWidth = 6.0;

    /// <summary>Seconds a point of the trail lasts after the pointer passed it, unless the person chose another Fade.</summary>
    public const double DefaultLifetime = 0.8;

    /// <summary>The shortest Fade people can choose, in seconds (Swift <c>lifetimeRange</c>).</summary>
    public const double MinimumLifetime = 0.5;

    /// <summary>The longest Fade people can choose, in seconds (Swift <c>lifetimeRange</c>).</summary>
    public const double MaximumLifetime = 5;

    /// <summary>A new position closer than this to the point before the newest moves the newest point instead of adding one.</summary>
    public const double MinimumStep = 1.0;

    /// <summary>The tail narrows to nothing over this many line widths.</summary>
    public const double TailTaper = 4.0;

    /// <summary>A pause longer than this on one spot is kept as a second point there, at the time the pointer moved on.</summary>
    public const double RestInterval = 0.05;

    /// <summary>A stroke keeps at most this many points; the oldest go first.</summary>
    public const int MaximumSamples = 2_048;

    private ValueList<Stroke> strokes;
    private double lifetime;

    /// <summary>A trail whose points last <paramref name="lifetime"/> seconds.</summary>
    public LaserTrail(double lifetime) => this.lifetime = lifetime;

    /// <summary>
    /// Seconds a point of the trail lasts after the pointer passed it (Swift <c>lifetime</c>); a trail made with
    /// <c>new LaserTrail()</c> or <c>default</c> uses <see cref="DefaultLifetime"/>. A change applies to the whole trail at once.
    /// </summary>
    public double Lifetime
    {
        readonly get => lifetime > 0 ? lifetime : DefaultLifetime;
        set => lifetime = value;
    }

    /// <summary>The laser pointer's color in a new profile.</summary>
    public static RGBA DefaultColor => new(0.98, 0.16, 0.2);

    /// <summary>
    /// The layers of the trail's look, drawn in order: four glows in the trail's color that get stronger towards the line,
    /// so the glow fades out softly, then the line itself and a thin white core. Each layer is one filled outline, so where
    /// the trail crosses itself it is no darker.
    /// </summary>
    public static ValueList<Pass> Passes { get; } =
    [
        new Pass(2.8, 3, 0.05, false),
        new Pass(2.2, 2, 0.08, false),
        new Pass(1.7, 1.5, 0.12, false),
        new Pass(1.35, 1, 0.18, false),
        new Pass(1, 0, 1, false),
        new Pass(0.4, 0, 0.85, true),
    ];

    /// <summary>One press of the button each, oldest first.</summary>
    public readonly ValueList<Stroke> Strokes => strokes;

    public readonly bool IsEmpty => strokes.IsEmpty;

    /// <summary>The button is down and the newest stroke follows the pointer.</summary>
    public readonly bool IsDrawing => !strokes.IsEmpty && strokes[strokes.Count - 1].Live;

    /// <summary>The button is down and only the dot under the pointer is left, so nothing changes until the pointer moves or lets go.</summary>
    public readonly bool IsResting => strokes.Count == 1 && strokes[0].Live && strokes[0].Samples.Count == 1;

    /// <summary>Starts a new stroke at <paramref name="point"/>. A stroke that is still fading keeps fading on its own.</summary>
    public void Press(Point point, double time, RGBA color, double width)
    {
        Release(null, time);
        strokes = strokes.Add(new Stroke([new Sample(point, time)], color, width, true));
    }

    /// <summary>Follows the pointer to <paramref name="point"/>.</summary>
    public void Move(Point point, double time)
    {
        if (strokes.IsEmpty || !strokes[strokes.Count - 1].Live)
        {
            return;
        }
        int index = strokes.Count - 1;
        ValueList<Sample> samples = strokes[index].Samples;
        var sample = new Sample(point, time);
        if (samples.Count >= 2 && samples[samples.Count - 2].Point.Distance(point) < MinimumStep)
        {
            SetSamples(index, samples.SetItem(samples.Count - 1, sample));
            return;
        }
        NoteRest(index, time);
        ValueList<Sample> grown = strokes[index].Samples.Add(sample);
        int extra = grown.Count - MaximumSamples;
        if (extra > 0)
        {
            grown = ValueList.Create(grown.AsSpan()[extra..]);
        }
        SetSamples(index, grown);
    }

    /// <summary>The button is up: the stroke ends at <paramref name="point"/>, if given, and its newest point starts to fade too.</summary>
    public void Release(Point? point, double time)
    {
        if (strokes.IsEmpty || !strokes[strokes.Count - 1].Live)
        {
            return;
        }
        int index = strokes.Count - 1;
        if (point is { } end)
        {
            Move(end, time);
        }
        NoteRest(index, time);
        strokes = strokes.SetItem(index, strokes[index] with { Live = false });
    }

    /// <summary>Drops what has faded by <paramref name="now"/>, keeping the last faded point of each stroke so its tail can end between points.</summary>
    public void Prune(double now)
    {
        double horizon = now - Lifetime;
        for (int index = 0; index < strokes.Count; index++)
        {
            ValueList<Sample> samples = strokes[index].Samples;
            int faded = 0;
            // Times only grow along a stroke, so faded points come first.
            while (faded + 1 < samples.Count && samples[faded + 1].Time <= horizon)
            {
                faded++;
            }
            if (faded > 0)
            {
                SetSamples(index, ValueList.Create(samples.AsSpan()[faded..]));
            }
        }
        strokes = strokes.Filter(stroke => stroke.Live || !((stroke.Samples.IsEmpty ? double.NegativeInfinity : stroke.Samples[stroke.Samples.Count - 1].Time) <= horizon));
    }

    /// <summary>Every stroke as drawn at <paramref name="now"/>, oldest first. Strokes that have faded are left out.</summary>
    public readonly ValueList<Shape> Shapes(double now)
    {
        if (strokes.IsEmpty)
        {
            return [];
        }
        var shapes = new List<Shape>(strokes.Count);
        foreach (ref readonly Stroke stroke in strokes)
        {
            if (ShapeOf(stroke, now, Lifetime) is { } shape)
            {
                shapes.Add(shape);
            }
        }
        return ValueList.CreateRange(shapes);
    }

    /// <summary>
    /// The area the trail covers at <paramref name="now"/>, glow included, or null when nothing shows. Every dot counts, even
    /// one with no width: the band from the tail's point to the next dot is drawn too.
    /// </summary>
    public readonly Rect? Bounds(double now)
    {
        Rect? area = null;
        foreach (ref readonly Shape shape in Shapes(now))
        {
            foreach (ref readonly Dot dot in shape.Dots)
            {
                double reach = Reach(dot);
                var rect = new Rect(dot.Center.X - reach, dot.Center.Y - reach, reach * 2, reach * 2);
                area = area is { } known ? known.Union(rect) : rect;
            }
        }
        return area;
    }

    /// <summary>
    /// The band that joins two neighboring dots in one layer: four corners, each end as wide as its dot's circle, wound like
    /// the circles (counterclockwise with y up), or null when the dots meet or both are empty.
    /// </summary>
    public static ValueList<Point>? Band(Dot start, Dot end, Pass pass)
    {
        double length = start.Center.Distance(end.Center);
        double first = pass.Radius(start), last = pass.Radius(end);
        if (!(length > 0) || !(first > 0 || last > 0))
        {
            return null;
        }
        var normal = new Point(-(end.Center.Y - start.Center.Y) / length, (end.Center.X - start.Center.X) / length);
        Point[] corners = [start.Center - normal * first, end.Center - normal * last, end.Center + normal * last, start.Center + normal * first];
        if (SignedArea(corners) < 0)
        {
            Array.Reverse(corners);
        }
        return ValueList.Create<Point>(corners);
    }

    /// <summary>Swift <c>signedArea</c>: half the sum of the cross products of each point and the next, folded in order.</summary>
    internal static double SignedArea(ReadOnlySpan<Point> points)
    {
        double sum = 0;
        for (int index = 0; index < points.Length; index++)
        {
            Point a = points[index], b = points[(index + 1) % points.Length];
            sum = sum + a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    /// <summary>A stroke as drawn at <paramref name="now"/>, or null once it has faded.</summary>
    internal static Shape? ShapeOf(in Stroke stroke, double now, double lifetime)
    {
        double horizon = now - lifetime;
        ValueList<Sample> samples = stroke.Samples;
        if (samples.IsEmpty)
        {
            return null;
        }
        Sample head = samples[samples.Count - 1];
        if (!(stroke.Live || head.Time > horizon))
        {
            return null;
        }
        // The tail ends where the pointer was Lifetime seconds ago, between the last faded point and the first fresh one.
        var visible = new List<Sample>(samples.Count + 1);
        for (int index = 0; index < samples.Count; index++)
        {
            if (!(samples[index].Time > horizon))
            {
                continue;
            }
            if (visible.Count == 0 && index > 0)
            {
                Sample previous = samples[index - 1], next = samples[index];
                double span = next.Time - previous.Time;
                double share = span > 0 ? SwiftMath.Min(1, SwiftMath.Max(0, (horizon - previous.Time) / span)) : 1;
                visible.Add(new Sample(previous.Point + (next.Point - previous.Point) * share, horizon));
            }
            visible.Add(samples[index]);
        }
        // While the button is down, the dot under the pointer stays, even after resting longer than the lifetime.
        if (visible.Count == 0)
        {
            visible.Add(head);
        }
        double radius = stroke.Width / 2;
        double taper = SwiftMath.Max(stroke.Width * TailTaper, 1);
        double travelled = 0.0;
        var dots = new Dot[visible.Count];
        for (int index = 0; index < visible.Count; index++)
        {
            Sample sample = visible[index];
            if (index > 0)
            {
                travelled += visible[index - 1].Point.Distance(sample.Point);
            }
            bool isHead = index == visible.Count - 1;
            double life = isHead && stroke.Live ? 1 : Fraction(sample.Time - horizon, lifetime);
            double thickness = life * (2 - life);
            double tail = isHead ? 1 : SwiftMath.Min(1, travelled / taper);
            dots[index] = new Dot(sample.Point, radius * thickness * tail);
        }
        return new Shape(ValueList.Create<Dot>(dots), stroke.Color, stroke.Live ? 1 : Fraction(head.Time - horizon, lifetime));
    }

    public readonly bool Equals(LaserTrail other) => strokes == other.strokes && Lifetime == other.Lifetime;

    public override readonly bool Equals(object? obj) => obj is LaserTrail other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(strokes, Lifetime);

    public static bool operator ==(LaserTrail left, LaserTrail right) => left.Equals(right);

    public static bool operator !=(LaserTrail left, LaserTrail right) => !left.Equals(right);

    private static double Fraction(double remaining, double lifetime) =>
        lifetime > 0 ? SwiftMath.Min(1, SwiftMath.Max(0, remaining / lifetime)) : 0;

    /// <summary>The widest layer's radius at <paramref name="dot"/> (Swift <c>passes.map { $0.radius(dot) }.max()</c>).</summary>
    private static double Reach(in Dot dot)
    {
        double reach = 0;
        bool first = true;
        foreach (ref readonly Pass pass in Passes)
        {
            double radius = pass.Radius(dot);
            if (first || reach < radius)
            {
                reach = radius;
                first = false;
            }
        }
        return reach;
    }

    /// <summary>The pointer stayed on the newest point from when it got there until <paramref name="time"/>.</summary>
    private void NoteRest(int index, double time)
    {
        ValueList<Sample> samples = strokes[index].Samples;
        if (samples.IsEmpty)
        {
            return;
        }
        Sample newest = samples[samples.Count - 1];
        if (!(time - newest.Time > RestInterval))
        {
            return;
        }
        SetSamples(index, samples.Add(new Sample(newest.Point, time)));
    }

    private void SetSamples(int index, ValueList<Sample> samples) =>
        strokes = strokes.SetItem(index, strokes[index] with { Samples = samples });

    /// <summary>One layer of the trail's look (Swift <c>LaserTrail.Pass</c>).</summary>
    /// <param name="Scale">The layer's radius at each dot is the dot's radius times this, plus <paramref name="Extra"/> points.</param>
    /// <param name="White">Draws white instead of the trail's color.</param>
    public readonly record struct Pass(double Scale, double Extra, double Alpha, bool White)
    {
        public double Radius(Dot dot) => dot.Radius > 0 ? dot.Radius * Scale + Extra : 0;

        public bool Equals(Pass other) => Scale == other.Scale && Extra == other.Extra && Alpha == other.Alpha && White == other.White;

        public override int GetHashCode() => HashCode.Combine(Scale, Extra, Alpha, White);
    }

    /// <summary>A point the pointer passed, and when (seconds on any steady clock).</summary>
    public readonly record struct Sample(Point Point, double Time)
    {
        public bool Equals(Sample other) => Point == other.Point && Time == other.Time;

        public override int GetHashCode() => HashCode.Combine(Point, Time);
    }

    /// <summary>One press of the button: the points the pointer passed, oldest first, with the color and width it had.</summary>
    /// <param name="Live">The button is still down, so the newest point stays fresh.</param>
    public readonly record struct Stroke(ValueList<Sample> Samples, RGBA Color, double Width, bool Live)
    {
        public bool Equals(Stroke other) => Samples == other.Samples && Color == other.Color && Width == other.Width && Live == other.Live;

        public override int GetHashCode() => HashCode.Combine(Samples, Color, Width, Live);
    }

    /// <summary>One round point of a stroke as drawn.</summary>
    public readonly record struct Dot(Point Center, double Radius)
    {
        public bool Equals(Dot other) => Center == other.Center && Radius == other.Radius;

        public override int GetHashCode() => HashCode.Combine(Center, Radius);
    }

    /// <summary>
    /// A stroke as drawn at one moment: round dots from the tail to the head, each joined to the next by a band whose ends
    /// are the two dots' widths.
    /// </summary>
    public readonly record struct Shape(ValueList<Dot> Dots, RGBA Color, double Opacity)
    {
        public bool Equals(Shape other) => Dots == other.Dots && Color == other.Color && Opacity == other.Opacity;

        public override int GetHashCode() => HashCode.Combine(Dots, Color, Opacity);
    }
}
