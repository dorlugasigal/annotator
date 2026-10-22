import Foundation

public enum Tool: String, Codable, CaseIterable, Sendable {
    case select, pen, highlighter, line, arrow, rectangle, ellipse, diamond, text, eraser, cover, blur
    case pixelate, distort, ruler, counter, image
    case database, queue, person, grid, axes
    case triangle, hexagon, star
    case databaseStack, server, messaging, web, mobile, desktop, laptop, document, configuration
    case loadBalancer, apiGateway, envelope, cart, bell

    public static let systemDesignShapes: [Tool] = [
        .databaseStack, .server, .messaging, .web, .mobile, .desktop, .laptop,
        .document, .configuration, .loadBalancer, .apiGateway, .envelope, .cart, .bell
    ]

    public var label: String {
        switch self {
        case .select: "Select"
        case .pen: "Pen"
        case .highlighter: "Highlighter"
        case .line: "Line"
        case .arrow: "Arrow"
        case .rectangle: "Rectangle"
        case .ellipse: "Ellipse"
        case .diamond: "Diamond"
        case .text: "Text"
        case .eraser: "Eraser"
        case .cover: "Censor (solid)"
        case .blur: "Blur region"
        case .pixelate: "Pixelate region"
        case .distort: "Distort region"
        case .ruler: "Pixel ruler"
        case .counter: "Numbered counter"
        case .image: "Paste image"
        case .database: "Cylinder"
        case .queue: "Queue"
        case .person: "Person"
        case .grid: "Grid"
        case .axes: "Axes"
        case .triangle: "Triangle"
        case .hexagon: "Hexagon"
        case .star: "Star"
        case .databaseStack: "Database"
        case .server: "Server"
        case .messaging: "Messaging"
        case .web: "Web"
        case .mobile: "Mobile phone"
        case .desktop: "Desktop"
        case .laptop: "Laptop"
        case .document: "Document"
        case .configuration: "Configuration"
        case .loadBalancer: "Load balancer"
        case .apiGateway: "API gateway"
        case .envelope: "Envelope"
        case .cart: "Cart"
        case .bell: "Bell"
        }
    }

    public var isPixelEffect: Bool { self == .blur || self == .pixelate || self == .distort }
    public var isProtection: Bool { self == .cover || isPixelEffect }
    public var isFreehand: Bool { self == .pen || self == .highlighter }
    public var isConnector: Bool { self == .line || self == .arrow }
    public var isShape: Bool {
        [.rectangle, .ellipse, .diamond, .database, .queue, .person, .grid, .axes, .triangle, .hexagon, .star].contains(self)
            || Self.systemDesignShapes.contains(self)
    }
    public var polygonKind: PolygonShapeKind? {
        switch self {
        case .triangle: .triangle
        case .hexagon: .hexagon
        case .star: .star
        default: nil
        }
    }
    public var supportsFill: Bool { isShape && self != .axes }
}

public struct RGBA: Codable, Hashable, Sendable {
    public var red: Double
    public var green: Double
    public var blue: Double
    public var alpha: Double
    public init(_ red: Double, _ green: Double, _ blue: Double, _ alpha: Double = 1) {
        self.red = red; self.green = green; self.blue = blue; self.alpha = alpha
    }
    public static let ink = RGBA(0.12, 0.15, 0.16)
    public static let coral = RGBA(0.94, 0.27, 0.22)
    public static let yellow = RGBA(0.96, 0.89, 0.22)
    public static let blue = RGBA(0.17, 0.45, 0.88)
    public static let white = RGBA(1, 1, 1)
    public var isValid: Bool {
        [red, green, blue, alpha].allSatisfy { $0.isFinite && (0...1).contains($0) }
    }
}

public enum StrokePattern: String, Codable, CaseIterable, Sendable { case solid, dashed, dotted }
public enum FillPattern: String, Codable, CaseIterable, Sendable { case solid, hatch, crosshatch }
public enum ArrowHead: String, Codable, CaseIterable, Sendable {
    case none, open, filled, circle, square, bar
    case triangleOutline, circleOutline, diamond, diamondOutline
    case cardinalityOne, cardinalityMany, cardinalityOneOrMany
    case cardinalityExactlyOne, cardinalityZeroOrOne, cardinalityZeroOrMany

    public var label: String {
        switch self {
        case .none: "None"
        case .open: "Arrow"
        case .filled: "Filled triangle"
        case .triangleOutline: "Outlined triangle"
        case .circle: "Filled circle"
        case .circleOutline: "Outlined circle"
        case .diamond: "Filled diamond"
        case .diamondOutline: "Outlined diamond"
        case .square: "Square"
        case .bar: "Bar"
        case .cardinalityOne: "One"
        case .cardinalityMany: "Many"
        case .cardinalityOneOrMany: "One or many"
        case .cardinalityExactlyOne: "Exactly one"
        case .cardinalityZeroOrOne: "Zero or one"
        case .cardinalityZeroOrMany: "Zero or many"
        }
    }
    public static let shapeChoices: [Self] = [.none, .open, .filled, .triangleOutline, .circle,
        .circleOutline, .diamond, .diamondOutline, .bar, .square]
    public static let relationshipChoices: [Self] = [.cardinalityOne, .cardinalityMany,
        .cardinalityOneOrMany, .cardinalityExactlyOne, .cardinalityZeroOrOne, .cardinalityZeroOrMany]
}
public enum TextAlignment: String, Codable, CaseIterable, Sendable { case left, center, right }

public struct Style: Codable, Equatable, Sendable {
    public var color: RGBA = .coral
    public var fill: RGBA?
    public var width: Double = 3
    public var opacity: Double = 1
    public var pattern: StrokePattern = .solid
    public var fillPattern: FillPattern = .solid
    public var roughness: Double = 0
    public var cornerRadius: Double = 0
    public var fontSize: Double = 28
    public var fontName: String = "system"
    public var bold: Bool = false
    public var alignment: TextAlignment = .left
    public var curved: Bool = false
    public var startHead: ArrowHead = .none
    public var endHead: ArrowHead = .open
    public var pressure: Bool = false
    public var headScale: Double?
    public var effectAmount: Double?
    public var gridRows: Int?
    public var gridColumns: Int?
    public var negativeAxes: Bool?
    public var axisTickCount: Int?
    public var strokeGradient: ColorGradient?
    public var fillGradient: ColorGradient?

    public init() {}

    public func validate() throws {
        try strokeGradient?.validate()
        try fillGradient?.validate()
        guard fillGradient == nil || fill != nil else { throw CanvasError.invalidStyle }
        if let axisTickCount, !(0...24).contains(axisTickCount) { throw CanvasError.invalidStyle }
        if let gridRows, !(1...24).contains(gridRows) { throw CanvasError.invalidStyle }
        if let gridColumns, !(1...24).contains(gridColumns) { throw CanvasError.invalidStyle }
        if let headScale, !headScale.isFinite || !(0.5...2).contains(headScale) {
            throw CanvasError.invalidStyle
        }
        if let effectAmount, !effectAmount.isFinite || !(0...100).contains(effectAmount) {
            throw CanvasError.invalidStyle
        }
        guard color.isValid, fill?.isValid != false,
              width.isFinite, (0.25...256).contains(width),
              opacity.isFinite, (0...1).contains(opacity),
              roughness.isFinite, (0...12).contains(roughness),
              cornerRadius.isFinite, (0...1_000).contains(cornerRadius),
              fontSize.isFinite, (4...1_024).contains(fontSize),
              fontName.utf8.count <= 256 else { throw CanvasError.invalidStyle }
    }
}

public struct Anchor: Codable, Equatable, Sendable {
    public var elementID: UUID
    public var relative: Point
    public init(elementID: UUID, relative: Point) { self.elementID = elementID; self.relative = relative }
}

public struct Element: Codable, Equatable, Identifiable, Sendable {
    public var id: UUID
    public var tool: Tool
    public var frame: Rect
    public var points: [Point]
    public var pressures: [Double]
    public var text: String
    public var style: Style
    public var rotation: Double
    public var groupID: UUID?
    public var locked: Bool
    public var startAnchor: Anchor?
    public var endAnchor: Anchor?
    public var seed: UInt64
    public var revision: UInt64
    public var geometryToken: UUID
    public var appendEpoch: UUID
    public var imagePNG: Data?
    public var label: ShapeLabel?

    public init(id: UUID = UUID(), tool: Tool, frame: Rect = .zero,
                points: [Point] = [], text: String = "", style: Style = Style()) {
        self.id = id; self.tool = tool
        self.frame = points.isEmpty ? frame : Rect.enclosing(points)
        self.points = points; pressures = []; self.text = text; self.style = style
        rotation = 0; groupID = nil; locked = false; startAnchor = nil; endAnchor = nil
        seed = id.uuidString.utf8.reduce(5381) { ($0 &* 33) &+ UInt64($1) }
        revision = 0
        geometryToken = UUID()
        appendEpoch = UUID()
    }

    public var estimatedBytes: Int {
        max(512, MemoryLayout<Element>.stride) + points.count * 16 + pressures.count * 8 + text.utf8.count
            + (imagePNG?.count ?? 0) + (label?.estimatedBytes ?? 0) + style.paintEstimatedBytes
    }

    public func validate() throws {
        if let label {
            guard tool.isShape else { throw CanvasError.invalidDocument("Only shapes can own a shape label.") }
            try label.validate()
        }
        if tool == .image {
            guard let imagePNG, imagePNG.count <= 32 * 1_024 * 1_024,
                  Array(imagePNG.prefix(8)) == [137, 80, 78, 71, 13, 10, 26, 10] else {
                throw CanvasError.invalidDocument("An image element must contain a bounded PNG image.")
            }
        } else if imagePNG != nil { throw CanvasError.invalidDocument("Unexpected image data on a drawing tool.") }
        guard !tool.isProtection || rotation == 0 else {
            throw CanvasError.invalidDocument("Censor and pixel-effect regions must remain axis-aligned.")
        }
        guard frame.isValid, points.allSatisfy(\.isFinite), rotation.isFinite,
              pressures.allSatisfy({ $0.isFinite && (0...1).contains($0) }),
              pressures.isEmpty || pressures.count == points.count,
              text.utf8.count <= 1_000_000, tool != .select, tool != .eraser else {
            throw CanvasError.invalidGeometry
        }
        for anchor in [startAnchor, endAnchor].compactMap({ $0 }) {
            guard tool.isConnector, anchor.relative.isFinite,
                  (0...1).contains(anchor.relative.x), (0...1).contains(anchor.relative.y) else {
                throw CanvasError.invalidGeometry
            }
        }
        try style.validate()
    }

    public mutating func translate(by offset: Point) {
        frame = frame.translated(by: offset)
        for index in points.indices { points[index] = points[index] + offset }
        revision &+= 1
        invalidateGeometry()
    }

    public mutating func resize(to newFrame: Rect) {
        let prior = frame
        if let label, prior.width > 0, prior.height > 0 {
            var labelStyle = label.style
            let factor = min(newFrame.width/prior.width, newFrame.height/prior.height)
            labelStyle.fontSize = min(1024,max(4,labelStyle.fontSize*factor))
            self.label = ShapeLabel(text: label.text, style: labelStyle, followsStroke: label.followsStroke)
        }
        for index in points.indices {
            points[index] = Point(
                newFrame.x + (points[index].x - prior.x) * newFrame.width / max(prior.width, 0.001),
                newFrame.y + (points[index].y - prior.y) * newFrame.height / max(prior.height, 0.001))
        }
        frame = newFrame
        revision &+= 1
        invalidateGeometry()
    }

    public mutating func invalidateGeometry() {
        geometryToken = UUID()
        appendEpoch = UUID()
    }

    /// Relative points where connectors can attach to this shape.
    public var anchorLocations: [Point] {
        guard let kind = tool.polygonKind else { return [Point(0.5, 0), Point(1, 0.5), Point(0.5, 1), Point(0, 0.5)] }
        let vertices = PolygonShape.vertices(kind, in: Rect(x: 0, y: 0, width: 1, height: 1))
        return vertices + vertices.indices.map { (vertices[$0] + vertices[($0 + 1) % vertices.count]) * 0.5 }
    }

    public func anchorPoint(_ relative: Point) -> Point {
        Point(symbolFrame.x + relative.x * symbolFrame.width, symbolFrame.y + relative.y * symbolFrame.height)
            .rotated(around: frame.center, by: rotation)
    }
}

public enum HitTesting {
    public static func contains(_ point: Point, element: Element, tolerance: Double = 6) -> Bool {
        let p = point.rotated(around: element.frame.center, by: -element.rotation)
        let strokeWidth = element.tool == .highlighter ? element.style.width * 5 : element.style.width
        let hitRadius = tolerance + strokeWidth / 2
        if element.tool == .arrow {
            for start in [true, false] {
                if let head = ArrowHeadGeometry.make(for: element, atStart: start), head.contains(p, tolerance: hitRadius) {
                    return true
                }
            }
        }
        if element.tool.isConnector && element.style.curved && element.points.count >= 3 {
            return ConnectorGeometry.curves(through: element.points).contains { $0.contains(p, tolerance: hitRadius) }
        }
        if element.tool == .ruler, let first = element.points.first, let last = element.points.last {
            let label = Rect(x: (first.x + last.x) / 2 - 60, y: (first.y + last.y) / 2 - 25, width: 120, height: 22)
            return label.contains(p, padding: tolerance) || distance(p, segment: first, last) <= hitRadius
        }
        let hitFrame = element.symbolFrame
        guard hitFrame.contains(p, padding: hitRadius) else { return false }
        if let kind = element.tool.polygonKind {
            return PolygonShape.contains(p, vertices: PolygonShape.vertices(kind, in: element.frame), tolerance: hitRadius)
        }
        if element.tool.isFreehand || element.tool.isConnector {
            if element.points.count == 1 { return p.distance(to: element.points[0]) <= hitRadius }
            return zip(element.points, element.points.dropFirst()).contains {
                distance(p, segment: $0.0, $0.1) <= hitRadius
            }
        }
        if (element.tool == .ellipse || element.tool == .counter || element.tool == .web),
           element.frame.width > 0, element.frame.height > 0 {
            let dx = (p.x - hitFrame.center.x) / (hitFrame.width / 2 + tolerance)
            let dy = (p.y - hitFrame.center.y) / (hitFrame.height / 2 + tolerance)
            return dx * dx + dy * dy <= 1
        }
        if element.tool == .diamond, element.frame.width > 0, element.frame.height > 0 {
            if element.style.cornerRadius > 0 {
                return RoundedDiamond.contains(p, frame: element.frame, radius: element.style.cornerRadius, tolerance: hitRadius)
            }
            return abs(p.x - element.frame.center.x) / (element.frame.width / 2 + tolerance)
                + abs(p.y - element.frame.center.y) / (element.frame.height / 2 + tolerance) <= 1
        }
        return true
    }

    public static func distance(_ p: Point, segment a: Point, _ b: Point) -> Double {
        let d = b - a
        let length = d.x * d.x + d.y * d.y
        guard length > 0 else { return p.distance(to: a) }
        let t = min(1, max(0, ((p.x - a.x) * d.x + (p.y - a.y) * d.y) / length))
        return p.distance(to: a + d * t)
    }
}
