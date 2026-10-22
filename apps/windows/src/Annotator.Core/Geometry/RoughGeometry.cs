// MIT-derived geometry from Rough.js renderer.ts and math.ts.
// Copyright (c) 2019 Preet Shihn. See THIRD_PARTY_NOTICES.md.
// Port of apps/macos/Sources/PresentationRendering/RoughGeometry.swift; operation order is kept for WIN-CORE-04/05.
namespace Annotator.Core.Geometry;

public static class RoughGeometry
{
    /// <summary>Rough.js's seeded generator: a 32-bit multiply that wraps around.</summary>
    public struct Random
    {
        public Random(ulong seed)
        {
            State = unchecked((uint)seed);
            if (State == 0)
            {
                State = 1;
            }
        }

        public uint State { get; private set; }

        public double Next()
        {
            State = unchecked(State * 48_271u);
            return (State & 0x7fff_ffffu) / 2_147_483_648.0;
        }
    }

    /// <summary>The sketchy outline of a shape or connector from its canonical path.</summary>
    public static PathData Outline(in Element element, PathData canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        var generator = new Generator(new Random(element.Seed), element.Style.Roughness);
        var output = new PathBuilder(32, 96);
        if (element.Tool == Tool.Ellipse)
        {
            generator.Ellipse(element.Frame, output);
            return output.ToPathData();
        }
        Point current = Point.Zero, first = Point.Zero;
        var preserve = element.Style.CornerRadius > 0 || element.Tool.IsConnector;
        foreach (var operation in canonical)
        {
            var points = operation.Points;
            switch (operation.Verb)
            {
                case PathVerb.MoveTo:
                    current = points[0];
                    first = current;
                    break;
                case PathVerb.LineTo:
                {
                    var next = points[0];
                    generator.Line(current, next, preserve, output);
                    current = next;
                    break;
                }
                case PathVerb.QuadTo:
                {
                    Point control = points[0], end = points[1];
                    generator.Bezier(current, current + (control - current) * (2 / 3.0),
                                     end + (control - end) * (2 / 3.0), end, preserve: true, output);
                    current = end;
                    break;
                }
                case PathVerb.CubicTo:
                {
                    var end = points[2];
                    generator.Bezier(current, points[0], points[1], end, preserve, output);
                    current = end;
                    break;
                }
                case PathVerb.Close:
                    if (current != first)
                    {
                        generator.Line(current, first, preserve, output);
                    }
                    current = first;
                    break;
            }
        }
        return output.ToPathData();
    }

    private struct Generator
    {
        private readonly double roughness;
        private Random random;

        public Generator(Random random, double roughness)
        {
            this.random = random;
            this.roughness = roughness;
        }

        // Swift's offset(_:_:gain:); a separate name keeps C# overload resolution from choosing offset(_:gain:).
        private double OffsetBetween(double low, double high, double gain = 1) =>
            roughness * gain * (random.Next() * (high - low) + low);

        private double Offset(double amount, double gain = 1) => OffsetBetween(-amount, amount, gain);

        public void Line(Point a, Point b, bool preserve, PathBuilder path)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var overlay = pass == 1;
                var length = a.Distance(b);
                var gain = length < 200 ? 1 : length > 500 ? 0.4 : -0.0016668 * length + 1.233334;
                var amount = SwiftMath.Min(2, length / 10) * (overlay ? 0.5 : 1);
                var divergence = 0.2 + random.Next() * 0.2;
                var displacementX = Offset(2 * (b.Y - a.Y) / 200, gain);
                var displacementY = Offset(2 * (a.X - b.X) / 200, gain);
                var start = new Point(a.X + (preserve ? 0 : Offset(amount, gain)),
                                      a.Y + (preserve ? 0 : Offset(amount, gain)));
                var firstControl = new Point(displacementX + a.X + (b.X - a.X) * divergence + Offset(amount, gain),
                                             displacementY + a.Y + (b.Y - a.Y) * divergence + Offset(amount, gain));
                var secondControl = new Point(displacementX + a.X + 2 * (b.X - a.X) * divergence + Offset(amount, gain),
                                              displacementY + a.Y + 2 * (b.Y - a.Y) * divergence + Offset(amount, gain));
                var end = new Point(b.X + (preserve ? 0 : Offset(amount, gain)),
                                    b.Y + (preserve ? 0 : Offset(amount, gain)));
                path.MoveTo(start);
                path.CubicTo(firstControl, secondControl, end);
            }
        }

        public void Bezier(Point start, Point first, Point second, Point end, bool preserve, PathBuilder path)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var amount = pass == 0 ? 2.0 : 2.3;
                var beginning = pass == 0 || preserve ? start : new Point(start.X + Offset(2), start.Y + Offset(2));
                var finish = preserve ? end : new Point(end.X + Offset(amount), end.Y + Offset(amount));
                var one = new Point(first.X + Offset(amount), first.Y + Offset(amount));
                var two = new Point(second.X + Offset(amount), second.Y + Offset(amount));
                path.MoveTo(beginning);
                path.CubicTo(one, two, finish);
            }
        }

        public void Ellipse(Rect frame, PathBuilder path)
        {
            double rx = frame.Width / 2, ry = frame.Height / 2;
            var estimate = Math.Sqrt(Math.PI * 2 * Math.Sqrt((rx * rx + ry * ry) / 2));
            var steps = Math.Ceiling(SwiftMath.Max(9, 9 / Math.Sqrt(200) * estimate));
            var increment = Math.PI * 2 / SwiftMath.Max(steps, 4);
            // curveFitting = 1 still advances Rough.js's seeded sequence.
            _ = Offset(0);
            _ = Offset(0);
            var upper = OffsetBetween(0.4, 1);
            var overlap = increment * OffsetBetween(0.1, upper);
            var center = frame.Center;
            var points = new List<Point>((int)steps + 16);
            for (var pass = 0; pass < 2; pass++)
            {
                points.Clear();
                var amount = pass == 0 ? 1.0 : 1.5;
                var extra = pass == 0 ? overlap : 0;
                var angle = Offset(0.5) - Math.PI / 2;
                var origin = Position(center, rx, ry, angle - increment, 0.9);
                points.Add(Jittered(origin, Offset(amount), Offset(amount)));
                var theta = angle;
                while (theta < Math.PI * 2 + angle - 0.01)
                {
                    var position = Position(center, rx, ry, theta, 1);
                    points.Add(Jittered(position, Offset(amount), Offset(amount)));
                    theta += increment;
                }
                var overlapStartAngle = angle + Math.PI * 2 + extra * 0.5;
                var overlapEndAngle = angle + Math.PI * 2 + extra;
                var overlapStartPosition = Position(center, rx, ry, overlapStartAngle, 1);
                var overlapStart = Jittered(overlapStartPosition, Offset(amount), Offset(amount));
                var overlapEndPosition = Position(center, rx, ry, angle + extra, 0.98);
                var overlapEnd = Jittered(overlapEndPosition, Offset(amount), Offset(amount));
                var guideX = Offset(amount);
                var guideOffset = new Point(guideX, Offset(amount));
                if (extra > increment)
                {
                    // Large seeded overlaps need perimeter samples, not a single cubic chord.
                    var last = points[^1];
                    AppendArc(points, center, rx, ry, increment, last, theta - increment, 1, overlapStart, overlapStartAngle, 1);
                    AppendArc(points, center, rx, ry, increment, overlapStart, overlapStartAngle, 1, overlapEnd, overlapEndAngle, 0.98);
                    var guideAngle = overlapEndAngle - SwiftMath.Min(extra * 0.5, increment);
                    points.Add(Position(center, rx, ry, guideAngle, 0.9) + guideOffset);
                }
                else
                {
                    points.Add(overlapStart);
                    points.Add(overlapEnd);
                    points.Add(Position(center, rx, ry, angle + extra * 0.5, 0.9) + guideOffset);
                }
                if (points.Count <= 3)
                {
                    continue;
                }
                path.MoveTo(points[1]);
                for (var index = 1; index < points.Count - 2; index++)
                {
                    Point a = points[index], b = points[index + 1];
                    path.CubicTo(a + (b - points[index - 1]) * (1 / 6.0), b + (a - points[index + 2]) * (1 / 6.0), b);
                }
            }
        }

        private static Point Position(Point center, double rx, double ry, double theta, double factor) =>
            new(center.X + factor * rx * Math.Cos(theta), center.Y + factor * ry * Math.Sin(theta));

        private static Point Jittered(Point point, double dx, double dy) => new(point.X + dx, point.Y + dy);

        private static void AppendArc(List<Point> points, Point center, double rx, double ry, double increment,
                                      Point start, double startAngle, double startFactor,
                                      Point end, double endAngle, double endFactor)
        {
            var segments = Math.Max(1, (int)Math.Ceiling((endAngle - startAngle) / increment));
            var startOffset = start - Position(center, rx, ry, startAngle, startFactor);
            var endOffset = end - Position(center, rx, ry, endAngle, endFactor);
            for (var index = 1; index < segments; index++)
            {
                var t = (double)index / segments;
                points.Add(Position(center, rx, ry, startAngle + (endAngle - startAngle) * t,
                                    startFactor + (endFactor - startFactor) * t)
                           + startOffset * (1 - t) + endOffset * t);
            }
            points.Add(end);
        }
    }
}
