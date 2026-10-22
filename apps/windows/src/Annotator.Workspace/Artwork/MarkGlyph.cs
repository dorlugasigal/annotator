using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace.Artwork;

/// <summary>
/// The Annotator "A" mark without its tile, from MenuBarIcon.swift (originally website/public/mark.svg).
/// Coordinates are y-down; the bowl is a counter, so fill with the even-odd rule.
/// </summary>
public static class MarkGlyph
{
    /// <summary>One segment: a straight line to <see cref="End"/>, or a cubic curve through the two controls.</summary>
    public readonly record struct Segment(bool IsLine, Point Control1, Point Control2, Point End)
    {
        public static Segment Line(double x, double y) => new(true, default, default, new Point(x, y));

        public static Segment Curve(double x1, double y1, double x2, double y2, double x, double y) =>
            new(false, new Point(x1, y1), new Point(x2, y2), new Point(x, y));
    }

    /// <summary>A closed figure.</summary>
    public sealed record Figure(Point Start, ImmutableArray<Segment> Segments);

    private const int CurveSteps = 48;

    public static ImmutableArray<Figure> Figures { get; } =
    [
        new Figure(new Point(39, 19),
        [
            Segment.Curve(22, 9, 7, 36, 20, 45),
            Segment.Curve(26, 49, 33, 44, 37, 36),
            Segment.Line(36, 45),
            Segment.Line(43, 45),
            Segment.Line(48, 17),
            Segment.Line(40, 17),
        ]),
        new Figure(new Point(35, 26),
        [
            Segment.Curve(33, 36, 27, 42, 24, 39),
            Segment.Curve(20, 35, 28, 21, 35, 26),
        ]),
    ];

    /// <summary>The same path as XAML or SVG path markup. Use the even-odd fill rule.</summary>
    public const string PathData =
        "M 39,19 C 22,9 7,36 20,45 C 26,49 33,44 37,36 L 36,45 L 43,45 L 48,17 L 40,17 Z " +
        "M 35,26 C 33,36 27,42 24,39 C 20,35 28,21 35,26 Z";

    /// <summary>The tight bounds of the path (the Mac's boundingBoxOfPath).</summary>
    public static Rect Bounds { get; } = ComputeBounds();

    /// <summary>
    /// The Mac's layout in a square of <paramref name="size"/>: scaled to fit with an inset of size / 9 in total
    /// and centered. Returns the scale and the offset to apply after scaling.
    /// </summary>
    public static (double Scale, Point Offset) Fit(double size)
    {
        double inset = size / 9;
        double scale = Math.Min((size - inset) / Bounds.Width, (size - inset) / Bounds.Height);
        var offset = new Point(size / 2 - (Bounds.X + Bounds.Width / 2) * scale, size / 2 - (Bounds.Y + Bounds.Height / 2) * scale);
        return (scale, offset);
    }

    /// <summary>
    /// Renders the mark into a square of <paramref name="size"/> pixels as BGRA rows, top row first, with
    /// anti-aliased edges (16 samples per pixel row, exact horizontal coverage). Premultiplied alpha suits
    /// composition surfaces; icon bitmaps expect straight alpha.
    /// </summary>
    public static byte[] RenderBgra(int size, RGBA color, bool premultiplied = true)
    {
        float[] coverage = Coverage(size);
        var pixels = new byte[size * size * 4];
        for (int index = 0; index < coverage.Length; index++)
        {
            double alpha = Math.Clamp(coverage[index], 0, 1) * color.Alpha;
            double weight = premultiplied ? alpha : alpha > 0 ? 1 : 0;
            pixels[index * 4] = Channel(color.Blue * weight);
            pixels[index * 4 + 1] = Channel(color.Green * weight);
            pixels[index * 4 + 2] = Channel(color.Red * weight);
            pixels[index * 4 + 3] = Channel(alpha);
        }
        return pixels;
    }

    /// <summary>The covered fraction of every pixel, row by row.</summary>
    public static float[] Coverage(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 1024);
        const int samples = 16;
        (double scale, Point offset) = Fit(size);
        List<(Point A, Point B)> edges = Edges(scale, offset);
        var coverage = new float[size * size];
        var crossings = new List<double>(16);
        for (int row = 0; row < size; row++)
        {
            for (int sample = 0; sample < samples; sample++)
            {
                double y = row + (sample + 0.5) / samples;
                crossings.Clear();
                foreach ((Point a, Point b) in edges)
                {
                    if ((a.Y <= y && y < b.Y) || (b.Y <= y && y < a.Y))
                    {
                        crossings.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                    }
                }
                crossings.Sort();
                for (int pair = 0; pair + 1 < crossings.Count; pair += 2)
                {
                    double start = Math.Clamp(crossings[pair], 0, size);
                    double end = Math.Clamp(crossings[pair + 1], 0, size);
                    for (int column = (int)Math.Floor(start); column < size && column < end; column++)
                    {
                        double covered = Math.Min(end, column + 1) - Math.Max(start, column);
                        if (covered > 0)
                        {
                            coverage[row * size + column] += (float)(covered / samples);
                        }
                    }
                }
            }
        }
        return coverage;
    }

    private static List<(Point A, Point B)> Edges(double scale, Point offset)
    {
        var edges = new List<(Point, Point)>();
        foreach (Figure figure in Figures)
        {
            Point Transform(Point point) => new(point.X * scale + offset.X, point.Y * scale + offset.Y);
            Point current = figure.Start;
            foreach (Segment segment in figure.Segments)
            {
                if (segment.IsLine)
                {
                    edges.Add((Transform(current), Transform(segment.End)));
                }
                else
                {
                    Point previous = current;
                    for (int step = 1; step <= CurveSteps; step++)
                    {
                        Point next = Evaluate(current, segment, step / (double)CurveSteps);
                        edges.Add((Transform(previous), Transform(next)));
                        previous = next;
                    }
                }
                current = segment.End;
            }
            edges.Add((Transform(current), Transform(figure.Start)));
        }
        return edges;
    }

    private static Rect ComputeBounds()
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        void Include(Point point)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        foreach (Figure figure in Figures)
        {
            Include(figure.Start);
            Point current = figure.Start;
            foreach (Segment segment in figure.Segments)
            {
                if (!segment.IsLine)
                {
                    for (int step = 1; step < 4096; step++)
                    {
                        Include(Evaluate(current, segment, step / 4096.0));
                    }
                }
                Include(segment.End);
                current = segment.End;
            }
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static Point Evaluate(Point start, Segment segment, double t)
    {
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new Point(
            a * start.X + b * segment.Control1.X + c * segment.Control2.X + d * segment.End.X,
            a * start.Y + b * segment.Control1.Y + c * segment.Control2.Y + d * segment.End.Y);
    }

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
