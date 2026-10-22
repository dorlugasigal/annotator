using System.Globalization;
using System.Text;

namespace Annotator.Core.Geometry;

/// <summary>The elements of a path, in the order Core Graphics reports them through <c>CGPath.applyWithBlock</c>.</summary>
public enum PathVerb : byte
{
    /// <summary>One point. Starts a subpath. A subpath that is only a move draws nothing.</summary>
    MoveTo,

    /// <summary>One point.</summary>
    LineTo,

    /// <summary>Two points: the control point, then the end point.</summary>
    QuadTo,

    /// <summary>Three points: two control points, then the end point.</summary>
    CubicTo,

    /// <summary>No points. Closes the subpath; the current point returns to the subpath start.</summary>
    Close,
}

/// <summary>How a filled or clipping path decides its inside (Direct2D: WINDING or ALTERNATE).</summary>
public enum FillRule : byte
{
    NonZero,
    EvenOdd,
}

/// <summary>One element of a <see cref="PathData"/>.</summary>
public readonly ref struct PathElement
{
    internal PathElement(PathVerb verb, ReadOnlySpan<Point> points)
    {
        Verb = verb;
        Points = points;
    }

    public PathVerb Verb { get; }

    /// <summary>The element's points: see <see cref="PathVerb"/> for their number and order.</summary>
    public ReadOnlySpan<Point> Points { get; }
}

/// <summary>
/// An immutable path in points with Core Graphics element semantics, stored as two flat arrays (verbs
/// and points). Copies share storage. An in-progress stroke grows by appending lines into the same
/// storage beyond the end of every existing view, so older views stay valid and unchanged and can be
/// read on another thread.
/// </summary>
/// <remarks>
/// Direct2D translation: <c>MoveTo</c> ends any open figure (<c>D2D1_FIGURE_END_OPEN</c>) and begins a new
/// one; <c>Close</c> ends the figure with <c>D2D1_FIGURE_END_CLOSED</c>; a segment that follows a
/// <c>Close</c> without a <c>MoveTo</c> begins a new figure at the closed subpath's start (Core Graphics
/// stores no move there). Figures always begin filled; fills close open figures implicitly.
/// </remarks>
public sealed class PathData
{
    private readonly PathStorage storage;
    private readonly int verbCount;
    private readonly int pointCount;
    private readonly PathCursor cursor;
    private TightBoundsBox? tight;

    internal PathData(PathStorage storage, int verbCount, int pointCount, Rect? bounds, bool hasCurves, PathCursor cursor)
    {
        this.storage = storage;
        this.verbCount = verbCount;
        this.pointCount = pointCount;
        Bounds = bounds;
        HasCurves = hasCurves;
        this.cursor = cursor;
    }

    public static PathData Empty { get; } = new(new PathStorage([], []), 0, 0, null, false, default);

    public int VerbCount => verbCount;

    public int PointCount => pointCount;

    public bool IsEmpty => verbCount == 0;

    /// <summary>Whether any element is a quadratic or cubic curve.</summary>
    public bool HasCurves { get; }

    public ReadOnlySpan<PathVerb> Verbs => new(storage.Verbs, 0, verbCount);

    public ReadOnlySpan<Point> Points => new(storage.Points, 0, pointCount);

    /// <summary>
    /// <c>CGPath.boundingBox</c>: the box of every point, control points included; null (<c>CGRect.null</c>)
    /// for an empty path. Move points count, which is assumed to match Core Graphics.
    /// </summary>
    public Rect? Bounds { get; }

    /// <summary><c>CGPath.boundingBoxOfPath</c>: the box of the drawn outline without control points.</summary>
    public Rect? TightBounds
    {
        get
        {
            if (!HasCurves)
            {
                return Bounds;
            }
            var box = tight;
            if (box is null)
            {
                box = new TightBoundsBox(ComputeTightBounds());
                tight = box;
            }
            return box.Value;
        }
    }

    /// <summary>The current point after the last element, or null for an empty path.</summary>
    public Point? CurrentPoint => cursor.HasCurrent ? cursor.Current : null;

    internal PathCursor Cursor => cursor;

    public static int PointsPerVerb(PathVerb verb) => verb switch
    {
        PathVerb.MoveTo or PathVerb.LineTo => 1,
        PathVerb.QuadTo => 2,
        PathVerb.CubicTo => 3,
        _ => 0,
    };

    public Enumerator GetEnumerator() => new(this);

    /// <summary>A new path with every point transformed, like <c>CGMutablePath.addPath(_:transform:)</c>.</summary>
    public PathData Transformed(in AffineTransform transform) =>
        new PathBuilder(verbCount, pointCount).AddPath(this, transform).ToPathData();

    /// <summary>Exact element-by-element equality of verbs and coordinates.</summary>
    public bool ContentEquals(PathData? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (verbCount != other.verbCount || pointCount != other.pointCount || !Verbs.SequenceEqual(other.Verbs))
        {
            return false;
        }
        var left = Points;
        var right = other.Points;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index].X != right[index].X || left[index].Y != right[index].Y)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Whether the filled path covers <paramref name="point"/> (<c>CGPath.contains(_:using:)</c>). Curves
    /// are flattened finely enough for hit tests; points exactly on an edge may go either way.
    /// </summary>
    public bool Contains(Point point, FillRule rule = FillRule.NonZero)
    {
        var winding = 0;
        var points = Points;
        var index = 0;
        var current = default(Point);
        var start = default(Point);
        var open = false;
        foreach (var verb in Verbs)
        {
            switch (verb)
            {
                case PathVerb.MoveTo:
                    if (open)
                    {
                        winding += Crossing(current, start, point);
                    }
                    current = start = points[index++];
                    open = true;
                    break;
                case PathVerb.LineTo:
                    winding += Crossing(current, points[index], point);
                    current = points[index++];
                    open = true;
                    break;
                case PathVerb.QuadTo:
                {
                    var control = points[index];
                    var end = points[index + 1];
                    var steps = FlattenSteps(current, control, control, end);
                    var previous = current;
                    for (var step = 1; step <= steps; step++)
                    {
                        var t = (double)step / steps;
                        var u = 1 - t;
                        var next = current * (u * u) + control * (2 * u * t) + end * (t * t);
                        winding += Crossing(previous, next, point);
                        previous = next;
                    }
                    current = end;
                    index += 2;
                    open = true;
                    break;
                }
                case PathVerb.CubicTo:
                {
                    var first = points[index];
                    var second = points[index + 1];
                    var end = points[index + 2];
                    var steps = FlattenSteps(current, first, second, end);
                    var previous = current;
                    for (var step = 1; step <= steps; step++)
                    {
                        var t = (double)step / steps;
                        var u = 1 - t;
                        var next = current * (u * u * u) + first * (3 * u * u * t) + second * (3 * u * t * t) + end * (t * t * t);
                        winding += Crossing(previous, next, point);
                        previous = next;
                    }
                    current = end;
                    index += 3;
                    open = true;
                    break;
                }
                case PathVerb.Close:
                    if (open)
                    {
                        winding += Crossing(current, start, point);
                    }
                    current = start;
                    open = false;
                    break;
            }
        }
        if (open)
        {
            winding += Crossing(current, start, point);
        }
        return rule == FillRule.NonZero ? winding != 0 : (winding & 1) != 0;
    }

    /// <summary>An SVG-like description, for test messages and diagnostics.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var element in this)
        {
            text.Append(element.Verb switch
            {
                PathVerb.MoveTo => "M",
                PathVerb.LineTo => "L",
                PathVerb.QuadTo => "Q",
                PathVerb.CubicTo => "C",
                _ => "Z",
            });
            foreach (var point in element.Points)
            {
                text.Append(CultureInfo.InvariantCulture, $" {point.X:R},{point.Y:R}");
            }
            text.Append(' ');
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Appends lines into shared storage when this view ends at the newest claimed slot; otherwise copies.</summary>
    internal PathData AppendLines(ReadOnlySpan<Point> points)
    {
        if (points.IsEmpty)
        {
            return this;
        }
        if (!cursor.HasCurrent)
        {
            throw new InvalidOperationException("A path needs a current point before lines can be added.");
        }
        var newVerbCount = checked(verbCount + points.Length);
        var newPointCount = checked(pointCount + points.Length);
        var target = storage.TryClaim(verbCount, pointCount, newVerbCount, newPointCount)
            ? storage
            : storage.CopyWithCapacity(verbCount, pointCount, Grow(newVerbCount), Grow(newPointCount), newVerbCount, newPointCount);
        target.Verbs.AsSpan(verbCount, points.Length).Fill(PathVerb.LineTo);
        points.CopyTo(target.Points.AsSpan(pointCount));
        var box = Bounds;
        var minX = box?.X ?? double.PositiveInfinity;
        var minY = box?.Y ?? double.PositiveInfinity;
        var maxX = box?.MaxX ?? double.NegativeInfinity;
        var maxY = box?.MaxY ?? double.NegativeInfinity;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
        var next = cursor with { Current = points[^1], Open = true };
        return new PathData(target, newVerbCount, newPointCount, new Rect(minX, minY, maxX - minX, maxY - minY), HasCurves, next);
    }

    private static int Grow(int needed) => (int)Math.Min(Array.MaxLength, Math.Max(16L, (long)needed * 2));

    private static int Crossing(Point a, Point b, Point point)
    {
        if (a.Y <= point.Y)
        {
            if (b.Y > point.Y && Side(a, b, point) > 0)
            {
                return 1;
            }
        }
        else if (b.Y <= point.Y && Side(a, b, point) < 0)
        {
            return -1;
        }
        return 0;
    }

    private static double Side(Point a, Point b, Point point) => (b.X - a.X) * (point.Y - a.Y) - (point.X - a.X) * (b.Y - a.Y);

    private static int FlattenSteps(Point start, Point first, Point second, Point end)
    {
        var length = start.Distance(first) + first.Distance(second) + second.Distance(end);
        if (!double.IsFinite(length))
        {
            return 1;
        }
        return (int)Math.Clamp(Math.Ceiling(Math.Sqrt(length) * 4), 8, 1_024);
    }

    private Rect? ComputeTightBounds()
    {
        if (verbCount == 0)
        {
            return null;
        }
        var box = new BoundsAccumulator();
        Span<double> roots = stackalloc double[4];
        var points = Points;
        var index = 0;
        var current = default(Point);
        var start = default(Point);
        foreach (var verb in Verbs)
        {
            switch (verb)
            {
                case PathVerb.MoveTo:
                    current = start = points[index++];
                    box.Include(current);
                    break;
                case PathVerb.LineTo:
                    current = points[index++];
                    box.Include(current);
                    break;
                case PathVerb.QuadTo:
                {
                    var control = points[index];
                    var end = points[index + 1];
                    box.Include(end);
                    var count = QuadExtremum(current.X, control.X, end.X, roots);
                    count += QuadExtremum(current.Y, control.Y, end.Y, roots[count..]);
                    for (var root = 0; root < count; root++)
                    {
                        var t = roots[root];
                        var u = 1 - t;
                        box.Include(current * (u * u) + control * (2 * u * t) + end * (t * t));
                    }
                    current = end;
                    index += 2;
                    break;
                }
                case PathVerb.CubicTo:
                {
                    var first = points[index];
                    var second = points[index + 1];
                    var end = points[index + 2];
                    box.Include(end);
                    var count = CubicExtrema(current.X, first.X, second.X, end.X, roots);
                    count += CubicExtrema(current.Y, first.Y, second.Y, end.Y, roots[count..]);
                    for (var root = 0; root < count; root++)
                    {
                        var t = roots[root];
                        var u = 1 - t;
                        box.Include(current * (u * u * u) + first * (3 * u * u * t) + second * (3 * u * t * t) + end * (t * t * t));
                    }
                    current = end;
                    index += 3;
                    break;
                }
                case PathVerb.Close:
                    current = start;
                    break;
            }
        }
        return box.Rect;
    }
    private static int QuadExtremum(double p0, double p1, double p2, Span<double> roots)
    {
        var denominator = p0 - 2 * p1 + p2;
        if (denominator != 0)
        {
            var t = (p0 - p1) / denominator;
            if (t > 0 && t < 1)
            {
                roots[0] = t;
                return 1;
            }
        }
        return 0;
    }

    private static int CubicExtrema(double p0, double p1, double p2, double p3, Span<double> roots)
    {
        // Roots of the derivative 3(a t^2 + b t + c) inside (0, 1).
        var a = -p0 + 3 * p1 - 3 * p2 + p3;
        var b = 2 * (p0 - 2 * p1 + p2);
        var c = p1 - p0;
        var count = 0;
        if (Math.Abs(a) < 1e-12)
        {
            if (b != 0)
            {
                var t = -c / b;
                if (t > 0 && t < 1)
                {
                    roots[count++] = t;
                }
            }
            return count;
        }
        var discriminant = b * b - 4 * a * c;
        if (discriminant < 0)
        {
            return 0;
        }
        var root = Math.Sqrt(discriminant);
        var t1 = (-b + root) / (2 * a);
        var t2 = (-b - root) / (2 * a);
        if (t1 > 0 && t1 < 1)
        {
            roots[count++] = t1;
        }
        if (t2 > 0 && t2 < 1)
        {
            roots[count++] = t2;
        }
        return count;
    }

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<PathVerb> verbs;
        private readonly ReadOnlySpan<Point> points;
        private int verbIndex;
        private int pointIndex;
        private int pointsInCurrent;

        internal Enumerator(PathData path)
        {
            verbs = path.Verbs;
            points = path.Points;
            verbIndex = -1;
            pointIndex = 0;
            pointsInCurrent = 0;
        }

        public readonly PathElement Current => new(verbs[verbIndex], points.Slice(pointIndex, pointsInCurrent));

        public bool MoveNext()
        {
            pointIndex += pointsInCurrent;
            if (++verbIndex >= verbs.Length)
            {
                pointsInCurrent = 0;
                return false;
            }
            pointsInCurrent = PointsPerVerb(verbs[verbIndex]);
            return true;
        }
    }

    private sealed class TightBoundsBox(Rect? value)
    {
        public Rect? Value { get; } = value;
    }
}

/// <summary>Accumulates the bounding box of points.</summary>
internal struct BoundsAccumulator
{
    private double minX;
    private double minY;
    private double maxX;
    private double maxY;
    private bool any;

    public readonly Rect? Rect => any ? new Rect(minX, minY, maxX - minX, maxY - minY) : null;

    public void Include(Point point)
    {
        if (!any)
        {
            minX = maxX = point.X;
            minY = maxY = point.Y;
            any = true;
            return;
        }
        minX = Math.Min(minX, point.X);
        minY = Math.Min(minY, point.Y);
        maxX = Math.Max(maxX, point.X);
        maxY = Math.Max(maxY, point.Y);
    }
}

/// <summary>The pen state at the end of a path: current point, subpath start and whether a subpath is open.</summary>
internal readonly record struct PathCursor(bool HasCurrent, Point Current, Point Start, bool Open);

/// <summary>Verb and point arrays shared by every <see cref="PathData"/> view of one growing path.</summary>
internal sealed class PathStorage
{
    internal readonly PathVerb[] Verbs;
    internal readonly Point[] Points;
    private int claimedVerbs;
    private int claimedPoints;

    internal PathStorage(PathVerb[] verbs, Point[] points, int claimedVerbs, int claimedPoints)
    {
        Verbs = verbs;
        Points = points;
        this.claimedVerbs = claimedVerbs;
        this.claimedPoints = claimedPoints;
    }

    internal PathStorage(PathVerb[] verbs, Point[] points)
        : this(verbs, points, verbs.Length, points.Length)
    {
    }

    /// <summary>Claims the slots after a view that ends exactly at the newest claim.</summary>
    internal bool TryClaim(int verbCount, int pointCount, int newVerbCount, int newPointCount)
    {
        lock (this)
        {
            if (claimedVerbs != verbCount || claimedPoints != pointCount
                || newVerbCount > Verbs.Length || newPointCount > Points.Length)
            {
                return false;
            }
            claimedVerbs = newVerbCount;
            claimedPoints = newPointCount;
            return true;
        }
    }

    internal PathStorage CopyWithCapacity(int verbCount, int pointCount, int verbCapacity, int pointCapacity, int claimVerbs, int claimPoints)
    {
        var verbs = new PathVerb[Math.Max(verbCapacity, claimVerbs)];
        var points = new Point[Math.Max(pointCapacity, claimPoints)];
        Array.Copy(Verbs, verbs, verbCount);
        Array.Copy(Points, points, pointCount);
        return new PathStorage(verbs, points, claimVerbs, claimPoints);
    }
}
