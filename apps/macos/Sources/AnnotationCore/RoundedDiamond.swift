import Foundation

public enum RoundedDiamond {
    public struct Corner: Sendable {
        public let entry: Point
        public let control: Point
        public let exit: Point
    }

    public static func corners(frame: Rect, radius: Double) -> [Corner] {
        let vertices = [Point(frame.center.x, frame.y), Point(frame.maxX, frame.center.y),
                        Point(frame.center.x, frame.maxY), Point(frame.x, frame.center.y)]
        let length = vertices[0].distance(to: vertices[1])
        guard length > 0 else { return [] }
        let fraction = min(max(0, radius) / length, 0.5)
        return vertices.indices.map { index in
            let point = vertices[index]
            return Corner(entry: point + (vertices[(index + 3) % 4] - point) * fraction,
                          control: point, exit: point + (vertices[(index + 1) % 4] - point) * fraction)
        }
    }

    public static func contains(_ point: Point, frame: Rect, radius: Double, tolerance: Double) -> Bool {
        let outline = corners(frame: frame, radius: radius).flatMap { corner in
            (0...16).map { index -> Point in
                let t = Double(index) / 16, u = 1 - t
                return corner.entry * (u * u) + corner.control * (2 * u * t) + corner.exit * (t * t)
            }
        }
        guard let last = outline.last else { return false }
        var previous = last, inside = false
        for current in outline {
            if HitTesting.distance(point, segment: previous, current) <= tolerance { return true }
            if (current.y > point.y) != (previous.y > point.y),
               point.x < (previous.x - current.x) * (point.y - current.y) / (previous.y - current.y) + current.x {
                inside.toggle()
            }
            previous = current
        }
        return inside
    }
}
