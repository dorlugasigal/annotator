import Foundation

public struct Point: Codable, Hashable, Sendable {
    public var x: Double
    public var y: Double

    public init(_ x: Double, _ y: Double) { self.x = x; self.y = y }
    public static let zero = Point(0, 0)
    public var isFinite: Bool { x.isFinite && y.isFinite && abs(x) <= 10_000_000 && abs(y) <= 10_000_000 }
    public func distance(to other: Point) -> Double { hypot(x - other.x, y - other.y) }
    public static func + (lhs: Self, rhs: Self) -> Self { Point(lhs.x + rhs.x, lhs.y + rhs.y) }
    public static func - (lhs: Self, rhs: Self) -> Self { Point(lhs.x - rhs.x, lhs.y - rhs.y) }
    public static func * (lhs: Self, rhs: Double) -> Self { Point(lhs.x * rhs, lhs.y * rhs) }
    public func rotated(around center: Point, by angle: Double) -> Point {
        let offset = self - center
        return center + Point(offset.x * cos(angle) - offset.y * sin(angle),
                              offset.x * sin(angle) + offset.y * cos(angle))
    }
}

public struct Rect: Codable, Hashable, Sendable {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x; self.y = y; self.width = width; self.height = height
    }

    public init(from a: Point, to b: Point) {
        self.init(x: min(a.x, b.x), y: min(a.y, b.y),
                  width: abs(a.x - b.x), height: abs(a.y - b.y))
    }

    public static let zero = Rect(x: 0, y: 0, width: 0, height: 0)
    public var origin: Point { Point(x, y) }
    public var center: Point { Point(x + width / 2, y + height / 2) }
    public var maxX: Double { x + width }
    public var maxY: Double { y + height }
    public var isValid: Bool {
        x.isFinite && y.isFinite && width.isFinite && height.isFinite
            && maxX.isFinite && maxY.isFinite && width >= 0 && height >= 0
            && abs(x) <= 10_000_000 && abs(y) <= 10_000_000 && width <= 1_000_000 && height <= 1_000_000
    }

    public func contains(_ point: Point, padding: Double = 0) -> Bool {
        point.x >= x - padding && point.x <= maxX + padding
            && point.y >= y - padding && point.y <= maxY + padding
    }

    public func contains(_ other: Rect) -> Bool {
        contains(other.origin) && contains(Point(other.maxX, other.maxY))
    }

    public func translated(by offset: Point) -> Rect {
        Rect(x: x + offset.x, y: y + offset.y, width: width, height: height)
    }

    public func union(_ other: Rect) -> Rect {
        Rect(x: min(x, other.x), y: min(y, other.y),
             width: max(maxX, other.maxX) - min(x, other.x),
             height: max(maxY, other.maxY) - min(y, other.y))
    }

    public static func enclosing(_ points: [Point]) -> Rect {
        guard let first = points.first else { return .zero }
        return points.dropFirst().reduce(Rect(from: first, to: first)) {
            $0.union(Rect(from: $1, to: $1))
        }
    }
}

public struct SelectionGeometry: Sendable {
    public let bounds: Rect
    public let canRotate: Bool

    public init?(elements: [Element], selection: Set<UUID>) {
        let selected = elements.filter { selection.contains($0.id) }
        guard selected.count > 1 else { return nil }
        let editable = selected.filter { !$0.locked }
        guard !editable.isEmpty else { return nil }
        bounds = Rect.enclosing(editable.flatMap { element in
            let box = element.frame
            return [box.origin, Point(box.maxX, box.y), Point(box.maxX, box.maxY), Point(box.x, box.maxY)]
                .map { $0.rotated(around: box.center, by: element.rotation) }
        })
        canRotate = !editable.contains { $0.tool.isProtection }
    }

    public var corners: [Point] {
        [bounds.origin, Point(bounds.maxX, bounds.y), Point(bounds.maxX, bounds.maxY), Point(bounds.x, bounds.maxY)]
    }
}

public struct Viewport: Equatable, Sendable {
    public let output: Rect
    public let source: Rect

    public init(output: Rect, source: Rect) throws {
        guard output.isValid, source.isValid,
              output.width > 0, output.height > 0,
              source.width > 0, source.height > 0 else { throw CanvasError.invalidGeometry }
        self.output = output
        self.source = source
    }

    public static func zoom(display: Rect, factor: Double, focus: Point) throws -> Viewport {
        guard factor.isFinite, factor >= 1, factor <= 16, focus.isFinite,
              display.isValid, display.width > 0, display.height > 0 else {
            throw CanvasError.invalidGeometry
        }
        let width = display.width / factor
        let height = display.height / factor
        let x = min(max(focus.x - width / 2, display.x), display.maxX - width)
        let y = min(max(focus.y - height / 2, display.y), display.maxY - height)
        return try Viewport(output: display,
                            source: Rect(x: x, y: y, width: width, height: height))
    }

    public func sourcePoint(for outputPoint: Point) -> Point {
        Point(source.x + (outputPoint.x - output.x) * source.width / output.width,
              source.y + (outputPoint.y - output.y) * source.height / output.height)
    }

    public static func cursorAnchored(display: Rect, factor: Double, cursor: Point) throws -> Viewport {
        guard factor.isFinite, (1...16).contains(factor), cursor.isFinite,
              display.isValid, display.width > 0, display.height > 0 else { throw CanvasError.invalidGeometry }
        let x = min(display.maxX, max(display.x, cursor.x))
        let y = min(display.maxY, max(display.y, cursor.y))
        return try Viewport(output: display, source: Rect(
            x: display.x + (x - display.x) * (1 - 1 / factor),
            y: display.y + (y - display.y) * (1 - 1 / factor),
            width: display.width / factor, height: display.height / factor))
    }

    public func outputPoint(for sourcePoint: Point) -> Point {
        Point(output.x + (sourcePoint.x - source.x) * output.width / source.width,
              output.y + (sourcePoint.y - source.y) * output.height / source.height)
    }
}

public enum CanvasError: Error, LocalizedError, Equatable, Sendable {
    case invalidGeometry
    case invalidStyle
    case invalidDocument(String)
    case unsupportedSchema(Int)
    case missingElement
    case noTransaction

    public var errorDescription: String? {
        switch self {
        case .invalidGeometry: "The drawing contains invalid coordinates or dimensions."
        case .invalidStyle: "The drawing contains an unsupported style value."
        case .invalidDocument(let message): "The drawing could not be opened: \(message)"
        case .unsupportedSchema(let version): "Drawing format \(version) is not supported by this version."
        case .missingElement: "The selected object no longer exists."
        case .noTransaction: "There is no active edit to complete."
        }
    }
}
