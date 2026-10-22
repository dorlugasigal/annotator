using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Workspace.FirstUse;

/// <summary>A gradient stop of an <see cref="IllustrationPaint"/>: a color, straight alpha, at a location from 0 to 1.</summary>
public readonly record struct IllustrationStop(double Location, RGBA Color);

/// <summary>What an <see cref="IllustrationPaint"/> paints with (SwiftUI GraphicsContext.Shading).</summary>
public enum IllustrationPaintKind
{
    Solid,

    /// <summary>From <see cref="IllustrationPaint.Start"/> to <see cref="IllustrationPaint.End"/>, extended beyond both.</summary>
    Linear,

    /// <summary>Around <see cref="IllustrationPaint.Center"/> out to <see cref="IllustrationPaint.Radius"/>, extended beyond it.</summary>
    Radial,

    /// <summary>Around <see cref="IllustrationPaint.Center"/>, from the positive x axis clockwise on screen.</summary>
    Conic,
}

/// <summary>A paint of the tour's illustrations. Gradient stops interpolate in gamma-encoded sRGB, like Core Graphics.</summary>
public sealed class IllustrationPaint
{
    private IllustrationPaint(IllustrationPaintKind kind, RGBA color, IReadOnlyList<IllustrationStop> stops, Point start, Point end, double radius)
    {
        Kind = kind;
        Color = color;
        Stops = stops;
        Start = start;
        End = end;
        Radius = radius;
    }

    public IllustrationPaintKind Kind { get; }

    public RGBA Color { get; }

    public IReadOnlyList<IllustrationStop> Stops { get; }

    public Point Start { get; }

    public Point End { get; }

    /// <summary>The radial or conic gradient's center (the start point).</summary>
    public Point Center => Start;

    public double Radius { get; }

    public static IllustrationPaint Solid(RGBA color) => new(IllustrationPaintKind.Solid, color, [], default, default, 0);

    public static IllustrationPaint Linear(IReadOnlyList<IllustrationStop> stops, Point start, Point end) =>
        new(IllustrationPaintKind.Linear, default, stops, start, end, 0);

    public static IllustrationPaint Radial(IReadOnlyList<IllustrationStop> stops, Point center, double radius) =>
        new(IllustrationPaintKind.Radial, default, stops, center, center, radius);

    public static IllustrationPaint Conic(IReadOnlyList<IllustrationStop> stops, Point center) =>
        new(IllustrationPaintKind.Conic, default, stops, center, center, 0);

    /// <summary>SwiftUI Gradient(colors:): the colors evenly spaced from 0 to 1.</summary>
    public static IllustrationStop[] Even(params RGBA[] colors)
    {
        var stops = new IllustrationStop[colors.Length];
        for (int index = 0; index < colors.Length; index++)
        {
            stops[index] = new IllustrationStop(colors.Length == 1 ? 0 : index / (double)(colors.Length - 1), colors[index]);
        }
        return stops;
    }
}

/// <summary>Where a text's anchor point is, as SwiftUI's draw(_:at:anchor:) places a resolved text.</summary>
public enum IllustrationAnchor
{
    Center,

    /// <summary>The left edge, centered vertically.</summary>
    Leading,

    /// <summary>The right edge, centered vertically.</summary>
    Trailing,
}

/// <summary>
/// The drawing surface the tour's illustrations draw on, with SwiftUI GraphicsContext's operations: every push changes the
/// state for what follows until its pop. A shadow or a blur applies to each later drawing operation on its own, as filters
/// on a GraphicsContext do. Coordinates are in points from the illustration's top-left corner.
/// </summary>
public interface IIllustrationCanvas
{
    /// <summary>Later drawing is composited at <paramref name="opacity"/>.</summary>
    void PushOpacity(double opacity);

    /// <summary>Later drawing is transformed by <paramref name="transform"/> in front of the current transform.</summary>
    void PushTransform(in AffineTransform transform);

    /// <summary>Later drawing is clipped to the inside of <paramref name="path"/>.</summary>
    void PushClip(PathData path);

    /// <summary>Each later drawing casts a shadow of <paramref name="color"/>, blurred by <paramref name="radius"/> points and offset.</summary>
    void PushShadow(RGBA color, double radius, double offsetX, double offsetY);

    /// <summary>Each later drawing is blurred by <paramref name="radius"/> points.</summary>
    void PushBlur(double radius);

    void Pop();

    void Fill(PathData path, IllustrationPaint paint, FillRule rule = FillRule.NonZero);

    void Stroke(PathData path, IllustrationPaint paint, double width, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter);

    /// <summary>
    /// Draws one line of text at <paramref name="point"/>: centered on it, or starting or ending there, centered vertically on
    /// the Mac's line box, so its baseline sits where the Mac's does (D-4).
    /// </summary>
    void DrawText(string text, TextFont font, IllustrationPaint paint, Point point, IllustrationAnchor anchor);

    /// <summary>The width of one line of text in this platform's font.</summary>
    double MeasureText(string text, TextFont font);

    /// <summary>A Mac SF Symbol name drawn as its Windows icon (D-6), centered on <paramref name="center"/>.</summary>
    void DrawSymbol(string symbol, Point center, double size, RGBA color);
}

/// <summary>Arc-length trimming of a path (SwiftUI Path.trimmedPath(from:to:)), as a polyline of the curves.</summary>
public static class PathTrim
{
    /// <summary>
    /// The first <paramref name="fraction"/> of the path's length as lines, with every curve split finely enough to look
    /// curved at any scale the tour uses, and the point where it ends.
    /// </summary>
    public static (PathData Path, Point? End) Start(PathData path, double fraction)
    {
        ArgumentNullException.ThrowIfNull(path);
        List<Point> points = Flatten(path, out List<bool> starts);
        double total = 0;
        for (int index = 1; index < points.Count; index++)
        {
            if (!starts[index])
            {
                total += Distance(points[index - 1], points[index]);
            }
        }
        double target = Math.Clamp(fraction, 0, 1) * total;
        if (!(target > 0) || points.Count == 0)
        {
            return (PathData.Empty, null);
        }
        var builder = new PathBuilder(points.Count, points.Count);
        double walked = 0;
        Point end = points[0];
        for (int index = 0; index < points.Count; index++)
        {
            if (starts[index])
            {
                builder.MoveTo(points[index]);
                end = points[index];
                continue;
            }
            double length = Distance(points[index - 1], points[index]);
            if (walked + length >= target)
            {
                double share = length > 0 ? (target - walked) / length : 0;
                end = new Point(points[index - 1].X + (points[index].X - points[index - 1].X) * share,
                                points[index - 1].Y + (points[index].Y - points[index - 1].Y) * share);
                builder.LineTo(end);
                return (builder.ToPathData(), end);
            }
            walked += length;
            builder.LineTo(points[index]);
            end = points[index];
        }
        return (builder.ToPathData(), end);
    }

    private static List<Point> Flatten(PathData path, out List<bool> starts)
    {
        var points = new List<Point>();
        starts = [];
        Point current = default, first = default;
        var flags = starts;
        void Add(Point point, bool start)
        {
            points.Add(point);
            flags.Add(start);
        }
        foreach (PathElement element in path)
        {
            ReadOnlySpan<Point> p = element.Points;
            switch (element.Verb)
            {
                case PathVerb.MoveTo:
                    current = first = p[0];
                    Add(current, true);
                    break;
                case PathVerb.LineTo:
                    current = p[0];
                    Add(current, false);
                    break;
                case PathVerb.QuadTo:
                {
                    Point start = current;
                    int steps = Steps(Distance(start, p[0]) + Distance(p[0], p[1]));
                    for (int step = 1; step <= steps; step++)
                    {
                        double t = step / (double)steps, u = 1 - t;
                        Add(new Point(u * u * start.X + 2 * u * t * p[0].X + t * t * p[1].X,
                                      u * u * start.Y + 2 * u * t * p[0].Y + t * t * p[1].Y), false);
                    }
                    current = p[1];
                    break;
                }
                case PathVerb.CubicTo:
                {
                    Point start = current;
                    int steps = Steps(Distance(start, p[0]) + Distance(p[0], p[1]) + Distance(p[1], p[2]));
                    for (int step = 1; step <= steps; step++)
                    {
                        double t = step / (double)steps, u = 1 - t;
                        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
                        Add(new Point(a * start.X + b * p[0].X + c * p[1].X + d * p[2].X,
                                      a * start.Y + b * p[0].Y + c * p[1].Y + d * p[2].Y), false);
                    }
                    current = p[2];
                    break;
                }
                case PathVerb.Close:
                    if (current != first)
                    {
                        Add(first, false);
                    }
                    current = first;
                    break;
            }
        }
        return points;
    }

    /// <summary>One line per half point of the curve's control polygon, at least 8.</summary>
    private static int Steps(double length) => Math.Clamp((int)Math.Ceiling(length * 2), 8, 256);

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
