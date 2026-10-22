// Port of apps/macos/Sources/AnnotationCore/PolygonShape.swift.
namespace Annotator.Core;

public enum PolygonShapeKind { Triangle, Hexagon, Star }

public static class PolygonShape
{
    public static ValueList<Point> Vertices(PolygonShapeKind kind, Rect frame)
    {
        Point[] normalized;
        switch (kind)
        {
            case PolygonShapeKind.Triangle:
                normalized = [new(0.5, 0), new(1, 1), new(0, 1)];
                break;
            case PolygonShapeKind.Hexagon:
                normalized = [new(0.25, 0), new(0.75, 0), new(1, 0.5), new(0.75, 1), new(0.25, 1), new(0, 0.5)];
                break;
            case PolygonShapeKind.Star:
                var points = new Point[10];
                for (var index = 0; index < 10; index++)
                {
                    var angle = -Math.PI / 2 + index * Math.PI / 5;
                    var radius = index % 2 == 0 ? 0.5 : 0.22;
                    points[index] = new Point(0.5 + Math.Cos(angle) * radius, 0.5 + Math.Sin(angle) * radius);
                }
                double minX = 1, maxX = 0, minY = 1, maxY = 0;
                foreach (var point in points)
                {
                    minX = SwiftMath.Min(minX, point.X);
                }
                foreach (var point in points)
                {
                    maxX = SwiftMath.Max(maxX, point.X);
                }
                foreach (var point in points)
                {
                    minY = SwiftMath.Min(minY, point.Y);
                }
                foreach (var point in points)
                {
                    maxY = SwiftMath.Max(maxY, point.Y);
                }
                normalized = new Point[10];
                for (var index = 0; index < 10; index++)
                {
                    normalized[index] = new Point((points[index].X - minX) / (maxX - minX), (points[index].Y - minY) / (maxY - minY));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var result = new Point[normalized.Length];
        for (var index = 0; index < normalized.Length; index++)
        {
            result[index] = new Point(frame.X + normalized[index].X * frame.Width, frame.Y + normalized[index].Y * frame.Height);
        }
        return ValueList.Create<Point>(result);
    }

    public static bool Contains(Point point, ValueList<Point> vertices, double tolerance)
    {
        if (vertices.Count < 3)
        {
            return false;
        }
        var inside = false;
        for (var index = 0; index < vertices.Count; index++)
        {
            var a = vertices[index];
            var b = vertices[(index + 1) % vertices.Count];
            if (HitTesting.Distance(point, a, b) <= tolerance)
            {
                return true;
            }
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
