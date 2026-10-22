using Annotator.Core;

namespace Annotator.Workspace.Artwork;

/// <summary>
/// Renders an app icon into pixels, for native menus, which cannot show XAML. An icon is parts of absolute path data on
/// a 24-unit grid (M, L, C, Q, A and Z, as Icons/LucideIcons.g.cs holds them): every part is stroked 2 units wide with
/// round caps and joins, like the XAML glyphs, and filled parts are also filled with the nonzero rule.
/// </summary>
public static class IconRaster
{
    /// <summary>One part of an icon: path data, and whether a fill covers it.</summary>
    public readonly record struct Part(string Data, bool Filled);

    private const double Grid = 24;
    private const int FillSamples = 4;

    /// <summary>
    /// Renders the icon into a square of <paramref name="size"/> pixels as premultiplied BGRA rows, top row first, as
    /// 32-bit menu bitmaps expect. Strokes are anti-aliased by their distance to each pixel's center.
    /// </summary>
    public static byte[] RenderBgra(IReadOnlyList<Part> parts, int size, RGBA color)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 256);
        double scale = size / Grid;
        var strokes = new List<(Point A, Point B)>();
        var fills = new List<(Point A, Point B)>();
        foreach (Part part in parts)
        {
            Flatten(part.Data, scale, strokes, part.Filled ? fills : null);
        }
        double halfWidth = scale;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var center = new Point(x + 0.5, y + 0.5);
                double distance = double.PositiveInfinity;
                foreach ((Point a, Point b) in strokes)
                {
                    distance = Math.Min(distance, Distance(center, a, b));
                }
                double coverage = Math.Clamp(halfWidth + 0.5 - distance, 0, 1);
                if (fills.Count > 0 && coverage < 1)
                {
                    coverage = Math.Max(coverage, FillCoverage(fills, x, y));
                }
                double alpha = coverage * color.Alpha;
                int index = (y * size + x) * 4;
                pixels[index] = Channel(color.Blue * alpha);
                pixels[index + 1] = Channel(color.Green * alpha);
                pixels[index + 2] = Channel(color.Red * alpha);
                pixels[index + 3] = Channel(alpha);
            }
        }
        return pixels;
    }

    /// <summary>
    /// Turns path data into line segments in pixels. Every subpath also closes into <paramref name="fills"/> when it is
    /// given, as a fill closes open subpaths.
    /// </summary>
    private static void Flatten(string data, double scale, List<(Point A, Point B)> strokes, List<(Point A, Point B)>? fills)
    {
        string[] tokens = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int index = 0;
        Point start = default, current = default;
        bool open = false;

        Point Next()
        {
            string[] pair = tokens[index++].Split(',');
            return new Point(double.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture) * scale,
                double.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture) * scale);
        }
        double Number() => double.Parse(tokens[index++], System.Globalization.CultureInfo.InvariantCulture);
        void LineTo(Point to)
        {
            strokes.Add((current, to));
            fills?.Add((current, to));
            current = to;
        }
        void CloseFill()
        {
            if (open && fills is not null && current != start)
            {
                fills.Add((current, start));
            }
            open = false;
        }

        while (index < tokens.Length)
        {
            string command = tokens[index++];
            switch (command)
            {
                case "M":
                    CloseFill();
                    start = current = Next();
                    open = true;
                    break;
                case "L":
                    LineTo(Next());
                    break;
                case "C":
                    {
                        Point from = current, c1 = Next(), c2 = Next(), to = Next();
                        int steps = Steps(Length(from, c1) + Length(c1, c2) + Length(c2, to));
                        for (int step = 1; step <= steps; step++)
                        {
                            double t = step / (double)steps, u = 1 - t;
                            LineTo(step == steps ? to : new Point(
                                u * u * u * from.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * to.X,
                                u * u * u * from.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * to.Y));
                        }
                        break;
                    }
                case "Q":
                    {
                        Point from = current, control = Next(), to = Next();
                        int steps = Steps(Length(from, control) + Length(control, to));
                        for (int step = 1; step <= steps; step++)
                        {
                            double t = step / (double)steps, u = 1 - t;
                            LineTo(step == steps ? to : new Point(
                                u * u * from.X + 2 * u * t * control.X + t * t * to.X,
                                u * u * from.Y + 2 * u * t * control.Y + t * t * to.Y));
                        }
                        break;
                    }
                case "A":
                    {
                        Point radius = Next();
                        double rotation = Number();
                        bool large = Number() != 0;
                        bool sweep = Number() != 0;
                        Arc(current, radius.X, radius.Y, rotation, large, sweep, Next(), LineTo);
                        break;
                    }
                case "Z":
                    if (current != start)
                    {
                        LineTo(start);
                    }
                    open = false;
                    current = start;
                    break;
                default:
                    throw new FormatException($"Unexpected path command \"{command}\".");
            }
        }
        CloseFill();
    }

    /// <summary>An SVG elliptical arc from <paramref name="from"/> to <paramref name="to"/>, as line segments (SVG 1.1, F.6.5).</summary>
    private static void Arc(Point from, double rx, double ry, double rotation, bool large, bool sweep, Point to, Action<Point> lineTo)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0 || from == to)
        {
            lineTo(to);
            return;
        }
        double phi = rotation * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (from.X - to.X) / 2, dy = (from.Y - to.Y) / 2;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1)
        {
            rx *= Math.Sqrt(lambda);
            ry *= Math.Sqrt(lambda);
        }
        double numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double root = (large != sweep ? 1 : -1) * Math.Sqrt(Math.Max(0, numerator / denominator));
        double cx1 = root * rx * y1 / ry, cy1 = -root * ry * x1 / rx;
        double cx = cos * cx1 - sin * cy1 + (from.X + to.X) / 2, cy = sin * cx1 + cos * cy1 + (from.Y + to.Y) / 2;
        double start = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        double delta = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }
        int steps = Steps(Math.Abs(delta) * Math.Max(rx, ry));
        for (int step = 1; step <= steps; step++)
        {
            double angle = start + delta * step / steps;
            lineTo(step == steps ? to : new Point(
                cx + rx * Math.Cos(angle) * cos - ry * Math.Sin(angle) * sin,
                cy + rx * Math.Cos(angle) * sin + ry * Math.Sin(angle) * cos));
        }
    }

    private static double Angle(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);

    /// <summary>Segments for a curve about <paramref name="length"/> pixels long: a quarter pixel or less off the curve.</summary>
    private static int Steps(double length) => Math.Clamp((int)Math.Ceiling(length / 1.5), 4, 64);

    private static double Length(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static double Distance(Point point, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared == 0 ? 0 : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double x = a.X + t * dx - point.X, y = a.Y + t * dy - point.Y;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>The covered fraction of the pixel at (<paramref name="x"/>, <paramref name="y"/>) by the nonzero rule, from a 4 by 4 grid of samples.</summary>
    private static double FillCoverage(List<(Point A, Point B)> edges, int x, int y)
    {
        int inside = 0;
        for (int row = 0; row < FillSamples; row++)
        {
            double py = y + (row + 0.5) / FillSamples;
            for (int column = 0; column < FillSamples; column++)
            {
                double px = x + (column + 0.5) / FillSamples;
                int winding = 0;
                foreach ((Point a, Point b) in edges)
                {
                    double side = (b.X - a.X) * (py - a.Y) - (px - a.X) * (b.Y - a.Y);
                    if (a.Y <= py)
                    {
                        if (b.Y > py && side > 0)
                        {
                            winding++;
                        }
                    }
                    else if (b.Y <= py && side < 0)
                    {
                        winding--;
                    }
                }
                if (winding != 0)
                {
                    inside++;
                }
            }
        }
        return inside / (double)(FillSamples * FillSamples);
    }

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
