import Foundation

/// Constraints applied while a drag creates a shape, connector or ruler.
public enum DragConstraint {
    /// Shift with a line, arrow, ruler or straight highlighter: the end moves to the nearest
    /// 45-degree direction and keeps its distance from the start. Ties round away from zero.
    public static func snappedLineEnd(from start: Point, to point: Point) -> Point {
        let angle = (atan2(point.y - start.y, point.x - start.x) / (.pi / 4)).rounded() * (.pi / 4)
        return start + Point(cos(angle), sin(angle)) * start.distance(to: point)
    }

    /// Shift with a shape: a square on the longer side of the drag, keeping its direction.
    public static func squareEnd(from start: Point, to point: Point) -> Point {
        let length = max(abs(point.x - start.x), abs(point.y - start.y))
        return start + Point(point.x < start.x ? -length : length, point.y < start.y ? -length : length)
    }

    /// Shapes with a fixed width-to-height `ratio`: the largest such box inside the drag, keeping its direction.
    public static func aspectEnd(from start: Point, to finish: Point, ratio: Double) -> Point {
        let height = min(abs(finish.y - start.y), abs(finish.x - start.x) / ratio)
        return start + Point(finish.x < start.x ? -height * ratio : height * ratio,
                             finish.y < start.y ? -height : height)
    }
}
