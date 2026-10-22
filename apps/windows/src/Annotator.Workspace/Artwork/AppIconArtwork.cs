using Annotator.Core;

namespace Annotator.Workspace.Artwork;

/// <summary>
/// The app icon of apps/macos/Artwork/main.swift, drawn from the same geometry (original artwork): a dark rounded tile
/// inset 6% with a 20% corner radius, the A mark of <see cref="MarkGlyph"/> in a square inset 18%, and the orange dot
/// 70% to 80% across and 20% to 30% down. The colors are the sRGB values in the Mac's output PNGs (AppKit converts its
/// calibrated colors when it draws into the bitmap), so a Windows logo matches the Mac icon pixel for pixel in flat areas.
/// </summary>
public static class AppIconArtwork
{
    /// <summary>The tile, the mark and the dot as 8-bit sRGB (icon_512x512@2x.png).</summary>
    public static readonly (byte Red, byte Green, byte Blue) TileColor = (49, 57, 52);

    public static readonly (byte Red, byte Green, byte Blue) MarkColor = (247, 247, 247);

    public static readonly (byte Red, byte Green, byte Blue) DotColor = (243, 136, 97);

    private const double TileInset = 0.06;
    private const double TileSize = 0.88;
    private const double TileRadius = 0.20;
    private const double MarkInset = 0.18;
    private const double MarkSize = 0.64;
    private const double DotLeft = 0.70;
    private const double DotTop = 0.20;
    private const double DotSize = 0.10;
    private const int SubRows = 16;

    /// <summary>
    /// Renders the icon at <paramref name="iconSize"/> pixels with its top-left corner at (<paramref name="left"/>,
    /// <paramref name="top"/>) into a transparent image of <paramref name="width"/> by <paramref name="height"/> pixels.
    /// Returns straight-alpha RGBA rows, top row first. Edges are anti-aliased by area: 16 sample rows per pixel row with
    /// exact horizontal coverage. Layers blend in sRGB values, as Core Graphics blends in a device RGB bitmap.
    /// </summary>
    public static byte[] RenderRgba(int width, int height, double iconSize, double left, double top)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(iconSize, 0);
        return RenderRgba(width, height, left + iconSize * MarkInset, top + iconSize * MarkInset, iconSize * MarkSize, iconSize, left, top);
    }

    /// <summary>As <see cref="RenderRgba(int, int, double, double, double)"/>, with the mark's square given separately (tests).</summary>
    internal static byte[] RenderRgba(int width, int height, double markLeft, double markTop, double markSize, double iconSize, double left, double top)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 4096);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(iconSize, 0);
        float[] tile = Coverage(width, height, RoundedRectangle(left + iconSize * TileInset, top + iconSize * TileInset,
            iconSize * TileSize, iconSize * TileRadius));
        float[] mark = Coverage(width, height, Mark(markLeft, markTop, markSize));
        float[] dot = Coverage(width, height, Circle(left + iconSize * (DotLeft + DotSize / 2), top + iconSize * (DotTop + DotSize / 2),
            iconSize * DotSize / 2));
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < tile.Length; index++)
        {
            // Premultiplied source-over, layer by layer.
            double red = 0, green = 0, blue = 0, alpha = 0;
            Over(ref red, ref green, ref blue, ref alpha, TileColor, tile[index]);
            Over(ref red, ref green, ref blue, ref alpha, MarkColor, mark[index]);
            Over(ref red, ref green, ref blue, ref alpha, DotColor, dot[index]);
            int offset = index * 4;
            byte coverage = Channel(alpha * 255);
            if (coverage == 0)
            {
                continue;
            }
            pixels[offset] = Channel(red / alpha);
            pixels[offset + 1] = Channel(green / alpha);
            pixels[offset + 2] = Channel(blue / alpha);
            pixels[offset + 3] = coverage;
        }
        return pixels;
    }

    /// <summary>The icon filling a square of <paramref name="size"/> pixels, as main.swift draws each AppIcon size.</summary>
    public static byte[] RenderRgba(int size) => RenderRgba(size, size, size, 0, 0);

    private static void Over(ref double red, ref double green, ref double blue, ref double alpha, (byte Red, byte Green, byte Blue) color, float coverage)
    {
        double a = Math.Clamp(coverage, 0, 1);
        red = color.Red * a + red * (1 - a);
        green = color.Green * a + green * (1 - a);
        blue = color.Blue * a + blue * (1 - a);
        alpha = a + alpha * (1 - a);
    }

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>The covered fraction of every pixel by the even-odd rule, from closed polygons in pixel coordinates.</summary>
    private static float[] Coverage(int width, int height, List<Point[]> polygons)
    {
        var edges = new List<(Point A, Point B)>();
        foreach (Point[] polygon in polygons)
        {
            for (int index = 0; index < polygon.Length; index++)
            {
                Point a = polygon[index], b = polygon[(index + 1) % polygon.Length];
                if (a.Y != b.Y)
                {
                    edges.Add((a, b));
                }
            }
        }
        var coverage = new float[width * height];
        var crossings = new List<double>(32);
        for (int row = 0; row < height; row++)
        {
            for (int sample = 0; sample < SubRows; sample++)
            {
                double y = row + (sample + 0.5) / SubRows;
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
                    double start = Math.Clamp(crossings[pair], 0, width);
                    double end = Math.Clamp(crossings[pair + 1], 0, width);
                    for (int column = (int)Math.Floor(start); column < width && column < end; column++)
                    {
                        double covered = Math.Min(end, column + 1) - Math.Max(start, column);
                        if (covered > 0)
                        {
                            coverage[row * width + column] += (float)(covered / SubRows);
                        }
                    }
                }
            }
        }
        return coverage;
    }

    private static List<Point[]> RoundedRectangle(double left, double top, double size, double radius)
    {
        int steps = ArcSteps(radius);
        var points = new List<Point>(steps * 4 + 4);
        // Corners clockwise from the top right, each a quarter circle, with the straight sides between them.
        (double X, double Y, double From)[] corners =
        [
            (left + size - radius, top + radius, -Math.PI / 2),
            (left + size - radius, top + size - radius, 0),
            (left + radius, top + size - radius, Math.PI / 2),
            (left + radius, top + radius, Math.PI),
        ];
        foreach ((double x, double y, double from) in corners)
        {
            for (int step = 0; step <= steps; step++)
            {
                double angle = from + Math.PI / 2 * step / steps;
                points.Add(new Point(x + radius * Math.Cos(angle), y + radius * Math.Sin(angle)));
            }
        }
        return [points.ToArray()];
    }

    private static List<Point[]> Circle(double centerX, double centerY, double radius)
    {
        int steps = ArcSteps(radius) * 4;
        var points = new Point[steps];
        for (int step = 0; step < steps; step++)
        {
            double angle = 2 * Math.PI * step / steps;
            points[step] = new Point(centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle));
        }
        return [points];
    }

    /// <summary>Segments for a quarter circle: each chord stays within a hundredth of a pixel of the arc.</summary>
    private static int ArcSteps(double radius) => Math.Clamp((int)Math.Ceiling(Math.PI / 2 / Math.Acos(1 - Math.Min(0.01 / Math.Max(radius, 0.01), 1))), 4, 1024);

    /// <summary>The A mark fitted into a square of <paramref name="size"/> pixels as MenuBarIcon.makeImage draws it.</summary>
    private static List<Point[]> Mark(double left, double top, double size)
    {
        double inset = size / 9;
        Rect bounds = MarkGlyph.Bounds;
        double scale = Math.Min((size - inset) / bounds.Width, (size - inset) / bounds.Height);
        double offsetX = left + size / 2 - (bounds.X + bounds.Width / 2) * scale;
        double offsetY = top + size / 2 - (bounds.Y + bounds.Height / 2) * scale;
        Point Transform(Point point) => new(point.X * scale + offsetX, point.Y * scale + offsetY);
        var figures = new List<Point[]>();
        foreach (MarkGlyph.Figure figure in MarkGlyph.Figures)
        {
            var points = new List<Point> { Transform(figure.Start) };
            Point current = figure.Start;
            foreach (MarkGlyph.Segment segment in figure.Segments)
            {
                if (segment.IsLine)
                {
                    points.Add(Transform(segment.End));
                }
                else
                {
                    double length = (Distance(current, segment.Control1) + Distance(segment.Control1, segment.Control2) +
                        Distance(segment.Control2, segment.End)) * scale;
                    int steps = Math.Clamp((int)Math.Ceiling(length), 16, 2048);
                    for (int step = 1; step <= steps; step++)
                    {
                        double t = step / (double)steps, u = 1 - t;
                        points.Add(Transform(new Point(
                            u * u * u * current.X + 3 * u * u * t * segment.Control1.X + 3 * u * t * t * segment.Control2.X + t * t * t * segment.End.X,
                            u * u * u * current.Y + 3 * u * u * t * segment.Control1.Y + 3 * u * t * t * segment.Control2.Y + t * t * t * segment.End.Y)));
                    }
                }
                current = segment.End;
            }
            figures.Add(points.ToArray());
        }
        return figures;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
