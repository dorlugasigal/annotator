import Foundation

public struct ArrowHeadGeometry: Sendable {
    public let kind: ArrowHead
    public let tip: Point
    public let direction: Point
    public let length: Double
    public var side: Point { Point(-direction.y, direction.x) }
    public var base: Point { kind == .bar ? tip : tip - direction * length }
    public var left: Point { base + side * (length * 0.6) }
    public var right: Point { base - side * (length * 0.6) }
    public var square: [Point] {
        let along = direction * (length / 3), across = side * (length / 3)
        return [tip + along + across, tip + along - across, tip - along - across, tip - along + across]
    }
    public var polygon: [Point] {
        if kind == .square { return square }
        if kind == .diamond || kind == .diamondOutline {
            let center = tip - direction * (length * 0.5)
            return [tip, center + side * (length * 0.36), tip - direction * length, center - side * (length * 0.36)]
        }
        return [left, tip, right]
    }
    public var hollow: Bool { [.triangleOutline, .circleOutline, .diamondOutline].contains(kind) }
    public var relationshipSegments: [(Point, Point)] {
        func bar(_ offset: Double) -> (Point, Point) {
            let center = tip - direction * (length * offset)
            return (center + side * (length * 0.4), center - side * (length * 0.4))
        }
        let fork = [(base, tip + side * (length * 0.5)), (base, tip), (base, tip - side * (length * 0.5))]
        switch kind {
        case .cardinalityOne: return [bar(0.25)]
        case .cardinalityMany: return fork
        case .cardinalityOneOrMany: return fork + [bar(0.85)]
        case .cardinalityExactlyOne: return [bar(0.2), bar(0.7)]
        case .cardinalityZeroOrOne: return [bar(0.2)]
        case .cardinalityZeroOrMany: return fork
        default: return []
        }
    }
    public var relationshipCircle: (center: Point, radius: Double)? {
        guard kind == .cardinalityZeroOrOne || kind == .cardinalityZeroOrMany else { return nil }
        return (tip - direction * (length * 1.25), length * 0.23)
    }

    public static func make(for element: Element, atStart: Bool) -> ArrowHeadGeometry? {
        let kind = atStart ? element.style.startHead : element.style.endHead
        guard element.tool == .arrow, kind != .none, element.points.count >= 2 else { return nil }
        let tip = atStart ? element.points[0] : element.points[element.points.count - 1]
        let neighbor = atStart
            ? element.points.dropFirst().first { $0.distance(to: tip) > 0.001 }
            : element.points.dropLast().reversed().first { $0.distance(to: tip) > 0.001 }
        guard let neighbor else { return nil }
        let length = min(2 * max(12, element.style.width * 4) * (element.style.headScale ?? 1),
                         tip.distance(to: neighbor) * 0.7)
        var reference = neighbor
        if element.style.curved, element.points.count > 2 {
            if let curve = ConnectorGeometry.endpointCurve(through: element.points, atStart: atStart) {
                // Aim along the visible approach across the head's span, not a distant chord.
                let reach = min(length, tip.distance(to: neighbor) * 0.4)
                let tangent = atStart ? curve.start - curve.firstControl : curve.end - curve.secondControl
                let tangentLength = hypot(tangent.x, tangent.y)
                for step in 1...24 {
                    let t = Double(step) / 48
                    let sample = curve.point(at: atStart ? t : 1 - t)
                    let distance = sample.distance(to: tip)
                    if distance > 0.001 {
                        let delta = tip - sample
                        if tangentLength > 0.001,
                           (delta.x * tangent.x + delta.y * tangent.y) / (distance * tangentLength) < 0.866 { break }
                        reference = sample
                    }
                    if distance >= reach { break }
                }
            }
        }
        let delta = tip - reference
        let magnitude = tip.distance(to: reference)
        guard magnitude > 0.001 else { return nil }
        return ArrowHeadGeometry(kind: kind, tip: tip, direction: delta * (1 / magnitude), length: length)
    }

    public func contains(_ point: Point, tolerance: Double) -> Bool {
        switch kind {
        case .none: return false
        case .circle: return point.distance(to: tip) <= length / 3 + tolerance
        case .square:
            let delta = point - tip
            return abs(delta.x * direction.x + delta.y * direction.y) <= length / 3 + tolerance
                && abs(delta.x * side.x + delta.y * side.y) <= length / 3 + tolerance
        case .open:
            return HitTesting.distance(point, segment: left, tip) <= tolerance
                || HitTesting.distance(point, segment: tip, right) <= tolerance
        case .bar: return HitTesting.distance(point, segment: left, right) <= tolerance
        case .filled:
            let delta = tip - point
            let along = delta.x * direction.x + delta.y * direction.y
            let across = abs(delta.x * side.x + delta.y * side.y)
            return along >= -tolerance && along <= length + tolerance && across <= max(0, along) * 0.6 + tolerance
        case .circleOutline:
            return abs(point.distance(to: tip) - length / 3) <= tolerance
        case .triangleOutline, .diamondOutline, .diamond:
            let edges = zip(polygon, Array(polygon.dropFirst()) + [polygon[0]])
            if edges.contains(where: { HitTesting.distance(point, segment: $0.0, $0.1) <= tolerance }) { return true }
            guard kind == .diamond else { return false }
            let center = tip - direction * (length * 0.5), delta = point - center
            return abs(delta.x * direction.x + delta.y * direction.y) / (length * 0.5)
                + abs(delta.x * side.x + delta.y * side.y) / (length * 0.36) <= 1
        case .cardinalityOne, .cardinalityMany, .cardinalityOneOrMany,
             .cardinalityExactlyOne, .cardinalityZeroOrOne, .cardinalityZeroOrMany:
            if relationshipSegments.contains(where: { HitTesting.distance(point, segment: $0.0, $0.1) <= tolerance }) { return true }
            if let circle = relationshipCircle { return abs(point.distance(to: circle.center) - circle.radius) <= tolerance }
            return false
        }
    }
}
