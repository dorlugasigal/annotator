import Foundation

public enum ElementStacking {
    public static func ordered(_ elements: [Element]) -> [Element] {
        elements.filter { !$0.tool.isProtection }
            + elements.filter { $0.tool.isPixelEffect }
            + elements.filter { $0.tool == .cover }
    }

    public static func frontmost(at point: Point, in elements: [Element]) -> Element? {
        let stack = ordered(elements).filter { HitTesting.contains(point, element: $0) }
        guard let top = stack.last else { return nil }
        guard top.tool.isShape, isSeeThrough(top) else { return top }
        // Text drawn inside an outline shape stays clickable even when the shape is newer.
        for element in stack.reversed() {
            if element.tool == .text { return element }
            guard element.tool.isShape, isSeeThrough(element) else { break }
        }
        return top
    }

    private static func isSeeThrough(_ shape: Element) -> Bool {
        guard let fill = shape.style.fill, fill.alpha * shape.style.opacity >= 0.5 else { return true }
        return shape.style.fillPattern != .solid && shape.style.fillGradient == nil
    }
}

public struct ConnectorCurve: Sendable {
    public let start: Point
    public let firstControl: Point
    public let secondControl: Point
    public let end: Point

    public var bounds: Rect { Rect.enclosing([start, firstControl, secondControl, end]) }

    public func point(at t: Double) -> Point {
        let u = 1 - t
        return start * (u * u * u) + firstControl * (3 * u * u * t)
            + secondControl * (3 * u * t * t) + end * (t * t * t)
    }

    public func contains(_ point: Point, tolerance: Double) -> Bool {
        hits(point, tolerance: tolerance, depth: 0)
    }

    private func hits(_ point: Point, tolerance: Double, depth: Int) -> Bool {
        guard bounds.contains(point, padding: tolerance) else { return false }
        let flatness = max(HitTesting.distance(firstControl, segment: start, end),
                           HitTesting.distance(secondControl, segment: start, end))
        if flatness <= 0.5 || depth >= 10 {
            return HitTesting.distance(point, segment: start, end) <= tolerance
        }
        let a = (start + firstControl) * 0.5
        let b = (firstControl + secondControl) * 0.5
        let c = (secondControl + end) * 0.5
        let d = (a + b) * 0.5
        let e = (b + c) * 0.5
        let middle = (d + e) * 0.5
        return ConnectorCurve(start: start, firstControl: a, secondControl: d, end: middle)
            .hits(point, tolerance: tolerance, depth: depth + 1)
            || ConnectorCurve(start: middle, firstControl: e, secondControl: c, end: end)
            .hits(point, tolerance: tolerance, depth: depth + 1)
    }
}

public enum ConnectorGeometry {
    public static func curves(through points: [Point]) -> [ConnectorCurve] {
        guard points.count >= 3 else { return [] }
        return (0..<(points.count - 1)).map { segment(points, index: $0) }
    }

    public static func endpointCurve(through points: [Point], atStart: Bool) -> ConnectorCurve? {
        guard points.count >= 3 else { return nil }
        return segment(points, index: atStart ? 0 : points.count - 2)
    }

    private static func segment(_ points: [Point], index: Int) -> ConnectorCurve {
        let a = points[max(0, index - 1)], b = points[index]
        let c = points[index + 1], d = points[min(points.count - 1, index + 2)]
        return ConnectorCurve(start: b, firstControl: b + (c - a) * (1 / 6.0),
                              secondControl: c - (d - b) * (1 / 6.0), end: c)
    }
}
