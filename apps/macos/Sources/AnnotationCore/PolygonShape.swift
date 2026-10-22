import Foundation

public enum PolygonShapeKind: Sendable { case triangle, hexagon, star }

public enum PolygonShape {
    public static func vertices(_ kind: PolygonShapeKind, in frame: Rect) -> [Point] {
        let normalized: [Point]
        switch kind {
        case .triangle:
            normalized = [Point(0.5,0),Point(1,1),Point(0,1)]
        case .hexagon:
            normalized = [Point(0.25,0),Point(0.75,0),Point(1,0.5),Point(0.75,1),Point(0.25,1),Point(0,0.5)]
        case .star:
            let points = (0..<10).map { index in
                let angle = -Double.pi/2 + Double(index)*Double.pi/5
                let radius = index.isMultiple(of:2) ? 0.5 : 0.22
                return Point(0.5 + cos(angle)*radius, 0.5 + sin(angle)*radius)
            }
            let minX = points.reduce(1) { min($0,$1.x) }, maxX = points.reduce(0) { max($0,$1.x) }
            let minY = points.reduce(1) { min($0,$1.y) }, maxY = points.reduce(0) { max($0,$1.y) }
            normalized = points.map { Point(($0.x-minX)/(maxX-minX),($0.y-minY)/(maxY-minY)) }
        }
        return normalized.map { Point(frame.x + $0.x*frame.width,frame.y + $0.y*frame.height) }
    }

    public static func contains(_ point: Point, vertices: [Point], tolerance: Double) -> Bool {
        guard vertices.count >= 3 else { return false }
        var inside = false
        for index in vertices.indices {
            let a = vertices[index], b = vertices[(index+1)%vertices.count]
            if HitTesting.distance(point, segment:a,b) <= tolerance { return true }
            if (a.y > point.y) != (b.y > point.y),
               point.x < (b.x-a.x)*(point.y-a.y)/(b.y-a.y)+a.x { inside.toggle() }
        }
        return inside
    }
}
