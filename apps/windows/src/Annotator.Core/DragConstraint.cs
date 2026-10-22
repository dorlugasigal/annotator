// Port of apps/macos/Sources/AnnotationCore/DragConstraint.swift.
namespace Annotator.Core;

/// <summary>Constraints applied while a drag creates a shape, connector or ruler.</summary>
public static class DragConstraint
{
    /// <summary>
    /// Shift with a line, arrow, ruler or straight highlighter: the end moves to the nearest 45-degree direction and
    /// keeps its distance from the start. Ties round away from zero.
    /// </summary>
    public static Point SnappedLineEnd(Point start, Point point)
    {
        var angle = Math.Round(Math.Atan2(point.Y - start.Y, point.X - start.X) / (Math.PI / 4), MidpointRounding.AwayFromZero)
                    * (Math.PI / 4);
        return start + new Point(Math.Cos(angle), Math.Sin(angle)) * start.Distance(point);
    }

    /// <summary>Shift with a shape: a square on the longer side of the drag, keeping its direction.</summary>
    public static Point SquareEnd(Point start, Point point)
    {
        var length = SwiftMath.Max(Math.Abs(point.X - start.X), Math.Abs(point.Y - start.Y));
        return start + new Point(point.X < start.X ? -length : length, point.Y < start.Y ? -length : length);
    }

    /// <summary>Shapes with a fixed width-to-height <paramref name="ratio"/>: the largest such box inside the drag, keeping its direction.</summary>
    public static Point AspectEnd(Point start, Point finish, double ratio)
    {
        var height = SwiftMath.Min(Math.Abs(finish.Y - start.Y), Math.Abs(finish.X - start.X) / ratio);
        return start + new Point(finish.X < start.X ? -height * ratio : height * ratio, finish.Y < start.Y ? -height : height);
    }
}
