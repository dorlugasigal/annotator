import Foundation

/// Screen zoom while drawing. The zoomed part of the screen holds still while the pointer moves inside it, and slides
/// when the pointer comes near an edge of the display. Coordinates are display points with y down.
public enum ZoomFollow {
    /// Below this factor, the zoomed part doesn't slide.
    public static let minimumSlidingFactor = 1.001
    /// The longest step one frame may slide, so a stalled frame doesn't jump.
    public static let maximumStep = 0.1

    /// How deep the edge zone is along a side of `length` points.
    public static func edgeZone(_ length: Double) -> Double { min(96, max(24, length * 0.08)) }

    /// The zoom of `factor` from the part shown before. When the factor changes, the point under the pointer stays under
    /// it; the part always stays inside the display. Without a part shown before, it starts where
    /// `Viewport.cursorAnchored` would.
    public static func viewport(previous: Rect?, display: Rect, factor: Double, pointer: Point) throws -> Viewport {
        guard let previous, previous.isValid, previous.width > 0, previous.height > 0 else {
            return try Viewport.cursorAnchored(display: display, factor: factor, cursor: pointer)
        }
        guard factor.isFinite, (1...16).contains(factor), pointer.isFinite,
              display.isValid, display.width > 0, display.height > 0 else { throw CanvasError.invalidGeometry }
        let width = display.width / factor, height = display.height / factor
        let px = min(display.maxX, max(display.x, pointer.x))
        let py = min(display.maxY, max(display.y, pointer.y))
        let x = previous.x + (px - display.x) * (previous.width - width) / display.width
        let y = previous.y + (py - display.y) * (previous.height - height) / display.height
        return try Viewport(output: display, source: Rect(
            x: min(max(x, display.x), display.maxX - width),
            y: min(max(y, display.y), display.maxY - height),
            width: width, height: height))
    }

    /// How fast the zoomed part slides, in display points per second on each axis, for the pointer at `pointer`. It is
    /// zero away from the edges and grows as the pointer goes deeper into an edge zone. A pointer on another display
    /// counts as at the edge it crossed, so the zoomed part slides on until it reaches that edge.
    public static func velocity(display: Rect, factor: Double, pointer: Point) -> Point {
        guard factor.isFinite, factor > minimumSlidingFactor, factor <= 16, pointer.isFinite,
              display.isValid, display.width > 0, display.height > 0 else { return Point(0, 0) }
        func axis(_ start: Double, _ length: Double, _ position: Double) -> Double {
            let zone = edgeZone(length)
            let value = min(start + length, max(start, position))
            let low = min(1, max(0, (start + zone - value) / zone))
            let high = min(1, max(0, (value - (start + length - zone)) / zone))
            return (high * high - low * low) * length * 0.75 / factor.squareRoot()
        }
        return Point(axis(display.x, display.width, pointer.x), axis(display.y, display.height, pointer.y))
    }

    /// The zoomed part after sliding for `elapsed` seconds (at most `maximumStep`), kept inside the display.
    public static func slide(_ viewport: Viewport, pointer: Point, elapsed: Double) throws -> Viewport {
        let display = viewport.output, source = viewport.source
        let speed = velocity(display: display, factor: display.width / source.width, pointer: pointer)
        let step = elapsed.isFinite ? min(maximumStep, max(0, elapsed)) : 0
        guard speed.x != 0 || speed.y != 0, step > 0 else { return viewport }
        return try Viewport(output: display, source: Rect(
            x: min(max(source.x + speed.x * step, display.x), display.maxX - source.width),
            y: min(max(source.y + speed.y * step, display.y), display.maxY - source.height),
            width: source.width, height: source.height))
    }

    /// The zoomed part after the pointer moved by `delta` display points to `pointer`. The part of a move toward an edge
    /// that happens inside that edge's zone pushes the zoomed part along as fast as the pointer moves on screen, also once
    /// the pointer stops at the display's edge or goes on to another display.
    public static func push(_ viewport: Viewport, pointer: Point, delta: Point) throws -> Viewport {
        let display = viewport.output, source = viewport.source
        let factor = display.width / source.width
        guard factor.isFinite, factor > minimumSlidingFactor, pointer.isFinite, delta.isFinite else { return viewport }
        func axis(_ start: Double, _ length: Double, _ position: Double, _ move: Double) -> Double {
            let zone = edgeZone(length)
            let value = min(start + length, max(start, position))
            if move > 0, value > start + length - zone { return min(move, value - (start + length - zone)) / factor }
            if move < 0, value < start + zone { return -min(-move, start + zone - value) / factor }
            return 0
        }
        let x = axis(display.x, display.width, pointer.x, delta.x), y = axis(display.y, display.height, pointer.y, delta.y)
        guard x != 0 || y != 0 else { return viewport }
        return try Viewport(output: display, source: Rect(
            x: min(max(source.x + x, display.x), display.maxX - source.width),
            y: min(max(source.y + y, display.y), display.maxY - source.height),
            width: source.width, height: source.height))
    }

    /// Whether the zoomed part would slide now: the pointer is in an edge zone with room left in that direction.
    public static func slides(_ viewport: Viewport, pointer: Point) -> Bool {
        (try? slide(viewport, pointer: pointer, elapsed: maximumStep)) != viewport
    }
}

/// Where screen zoom's map of the whole display sits.
public enum ZoomMapPosition: String, Codable, CaseIterable, Sendable {
    case bottomRight, bottomLeft, topRight, topLeft, off

    public var label: String {
        switch self {
        case .bottomRight: "Bottom right"
        case .bottomLeft: "Bottom left"
        case .topRight: "Top right"
        case .topLeft: "Top left"
        case .off: "Off"
        }
    }
}

/// Screen zoom's map: a small picture of the whole display in a corner, with the zoomed part outlined.
public enum ZoomMap {
    public static let inset = 16.0
    public static let cornerRadius = 10.0
    public static let borderWidth = 1.0
    public static let borderOpacity = 0.6
    public static let outlineWidth = 2.0
    /// The part outside the zoomed area is darkened by black at this opacity.
    public static let dimOpacity = 0.4
    /// The map fades in and out over this many seconds.
    public static let fadeDuration = 0.15
    /// While the pointer is over the map, it is this opaque so what's under it shows.
    public static let hoverOpacity = 0.2

    /// The map's frame on a display of `display`, or nil when the map is off or the display is too small for it.
    public static func frame(display: Rect, position: ZoomMapPosition) -> Rect? {
        guard position != .off, display.isValid, display.width > 0, display.height > 0 else { return nil }
        var width = min(260, max(140, display.width * 0.16))
        var height = width * display.height / display.width
        if height > display.height * 0.3 {
            height = display.height * 0.3
            width = height * display.width / display.height
        }
        guard width >= 80, width + inset * 2 <= display.width, height + inset * 2 <= display.height else { return nil }
        let left = position == .bottomLeft || position == .topLeft
        let top = position == .topLeft || position == .topRight
        return Rect(x: left ? display.x + inset : display.maxX - inset - width,
                    y: top ? display.y + inset : display.maxY - inset - height,
                    width: width, height: height)
    }

    /// The zoomed part of `viewport`, drawn in a map of `map`.
    public static func zoomedArea(_ viewport: Viewport, in map: Rect) -> Rect {
        let display = viewport.output, source = viewport.source
        let scaleX = map.width / display.width, scaleY = map.height / display.height
        return Rect(x: map.x + (source.x - display.x) * scaleX, y: map.y + (source.y - display.y) * scaleY,
                    width: source.width * scaleX, height: source.height * scaleY)
    }
}
