using System.Globalization;
using Annotator.Core;
using Annotator.Core.Geometry;

namespace Annotator.Workspace.Artwork;

/// <summary>
/// Reads an app icon's path data (absolute M, L, C, Q, A and Z with "x,y" pairs, as Icons/LucideIcons.g.cs holds them)
/// into a <see cref="PathData"/> on the icons' 24-unit grid. Elliptical arcs become cubic curves (SVG implementation
/// notes F.6.5), at most a quarter turn each.
/// </summary>
public static class IconPaths
{
    public static PathData Parse(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string[] tokens = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var builder = new PathBuilder(tokens.Length, tokens.Length * 2);
        Point current = default;
        int index = 0;
        Point Next()
        {
            string[] pair = tokens[index++].Split(',');
            return new Point(Number(pair[0]), Number(pair[1]));
        }
        double Scalar() => Number(tokens[index++]);
        while (index < tokens.Length)
        {
            string command = tokens[index++];
            switch (command)
            {
                case "M":
                    current = Next();
                    builder.MoveTo(current);
                    break;
                case "L":
                    current = Next();
                    builder.LineTo(current);
                    break;
                case "Q":
                {
                    Point control = Next();
                    current = Next();
                    builder.QuadTo(control, current);
                    break;
                }
                case "C":
                {
                    Point first = Next(), second = Next();
                    current = Next();
                    builder.CubicTo(first, second, current);
                    break;
                }
                case "A":
                {
                    Point radii = Next();
                    double rotation = Scalar();
                    bool large = Scalar() != 0, sweep = Scalar() != 0;
                    Point end = Next();
                    AddArc(builder, current, radii.X, radii.Y, rotation, large, sweep, end);
                    current = end;
                    break;
                }
                case "Z":
                    builder.Close();
                    break;
                default:
                    throw new FormatException($"The icon path command \"{command}\" is not supported.");
            }
        }
        return builder.ToPathData();
    }

    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static void AddArc(PathBuilder builder, Point start, double rx, double ry, double rotationDegrees, bool large, bool sweep, Point end)
    {
        if (start == end)
        {
            return;
        }
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            builder.LineTo(end);
            return;
        }
        double phi = rotationDegrees * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (start.X - end.X) / 2, dy = (start.Y - end.Y) / 2;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1)
        {
            double grow = Math.Sqrt(lambda);
            rx *= grow;
            ry *= grow;
        }
        double numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double factor = Math.Sqrt(Math.Max(0, numerator / denominator)) * (large == sweep ? -1 : 1);
        double cx1 = factor * rx * y1 / ry, cy1 = -factor * ry * x1 / rx;
        double cx = cos * cx1 - sin * cy1 + (start.X + end.X) / 2, cy = sin * cx1 + cos * cy1 + (start.Y + end.Y) / 2;
        double theta = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        double delta = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }
        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
        double step = delta / segments;
        double k = 4.0 / 3 * Math.Tan(step / 4);
        Point On(double angle) => new(cx + rx * Math.Cos(angle) * cos - ry * Math.Sin(angle) * sin,
                                      cy + rx * Math.Cos(angle) * sin + ry * Math.Sin(angle) * cos);
        Point Tangent(double angle) => new(-rx * Math.Sin(angle) * cos - ry * Math.Cos(angle) * sin,
                                           -rx * Math.Sin(angle) * sin + ry * Math.Cos(angle) * cos);
        for (int segment = 0; segment < segments; segment++)
        {
            double a0 = theta + segment * step, a1 = a0 + step;
            Point p0 = On(a0), p1 = segment == segments - 1 ? end : On(a1);
            Point t0 = Tangent(a0), t1 = Tangent(a1);
            builder.CubicTo(new Point(p0.X + k * t0.X, p0.Y + k * t0.Y), new Point(p1.X - k * t1.X, p1.Y - k * t1.Y), p1);
        }
    }

    private static double Angle(double ux, double uy, double vx, double vy)
    {
        double dot = ux * vx + uy * vy;
        double length = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double angle = Math.Acos(Math.Clamp(dot / length, -1, 1));
        return ux * vy - uy * vx < 0 ? -angle : angle;
    }
}
