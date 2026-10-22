// Port of apps/macos/Sources/AnnotationCore/RoundedDiamond.swift.
namespace Annotator.Core;

public static class RoundedDiamond
{
    public readonly record struct Corner(Point Entry, Point Control, Point Exit);

    public static ValueList<Corner> Corners(Rect frame, double radius)
    {
        Point[] vertices =
        [
            new(frame.Center.X, frame.Y), new(frame.MaxX, frame.Center.Y),
            new(frame.Center.X, frame.MaxY), new(frame.X, frame.Center.Y),
        ];
        var length = vertices[0].Distance(vertices[1]);
        if (!(length > 0))
        {
            return default;
        }
        var fraction = SwiftMath.Min(SwiftMath.Max(0, radius) / length, 0.5);
        var corners = new Corner[4];
        for (var index = 0; index < 4; index++)
        {
            var point = vertices[index];
            corners[index] = new Corner(
                point + (vertices[(index + 3) % 4] - point) * fraction,
                point,
                point + (vertices[(index + 1) % 4] - point) * fraction);
        }
        return ValueList.Create<Corner>(corners);
    }

    public static bool Contains(Point point, Rect frame, double radius, double tolerance)
    {
        var outline = new List<Point>(4 * 17);
        foreach (var corner in Corners(frame, radius))
        {
            for (var index = 0; index <= 16; index++)
            {
                var t = index / 16.0;
                var u = 1 - t;
                outline.Add(corner.Entry * (u * u) + corner.Control * (2 * u * t) + corner.Exit * (t * t));
            }
        }
        if (outline.Count == 0)
        {
            return false;
        }
        var previous = outline[^1];
        var inside = false;
        foreach (var current in outline)
        {
            if (HitTesting.Distance(point, previous, current) <= tolerance)
            {
                return true;
            }
            if ((current.Y > point.Y) != (previous.Y > point.Y)
                && point.X < (previous.X - current.X) * (point.Y - current.Y) / (previous.Y - current.Y) + current.X)
            {
                inside = !inside;
            }
            previous = current;
        }
        return inside;
    }
}
