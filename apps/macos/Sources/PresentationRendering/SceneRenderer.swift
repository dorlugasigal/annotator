import AppKit
import AnnotationCore
import OSLog

extension Point {
    public var cgPoint: CGPoint { CGPoint(x: x, y: y) }
    public init(_ value: CGPoint) { self.init(value.x, value.y) }
}

extension Rect {
    public var cgRect: CGRect { CGRect(x: x, y: y, width: width, height: height) }
    public init(_ value: CGRect) { self.init(x: value.minX, y: value.minY, width: value.width, height: value.height) }
}

extension RGBA {
    public var nsColor: NSColor {
        NSColor(srgbRed: red, green: green, blue: blue, alpha: alpha)
    }
    public init(_ color: NSColor) throws {
        guard let rgb = color.usingColorSpace(.sRGB) else { throw CanvasError.invalidStyle }
        self.init(rgb.redComponent, rgb.greenComponent, rgb.blueComponent, rgb.alphaComponent)
    }
}

@MainActor
public final class SceneRenderer {
    private struct CachedPath {
        let token: UUID
        let appendEpoch: UUID
        let pointCount: Int
        let tool: Tool
        let roughness: Double
        let path: CGMutablePath
    }
    private var paths: [UUID: CachedPath] = [:]
    private let images = DecodedImageCache()
    private var invalidImages = Set<UUID>()
    public private(set) var fullPathBuilds = 0
    public private(set) var incrementalAppends = 0
    public var decodedImageBuilds: Int { images.decodeCount }
    public var decodedImageBytes: Int { images.bytes }

    public init() {}

    public func reset() { paths.removeAll(); images.reset(); invalidImages.removeAll() }

    public func drawingBounds(for element: Element) -> CGRect {
        var bounds = element.frame.cgRect
        if element.tool.isShape || element.tool.isFreehand || element.tool.isConnector {
            bounds = bounds.union(path(for: element).boundingBox)
        }
        if let label = ShapeLabelLayout.textElement(for: element) { bounds = bounds.union(label.frame.cgRect) }
        if element.tool == .counter {
            bounds = bounds.union(element.frame.cgRect.offsetBy(dx: 0, dy: element.frame.height * 0.12))
        }
        if element.tool == .ruler, let start = element.points.first, let end = element.points.last {
            bounds = bounds.union(rulerLabelFrame(from: start, to: end).cgRect)
        }
        if element.tool == .arrow {
            for start in [true, false] {
                guard let head = ArrowHeadGeometry.make(for: element, atStart: start) else { continue }
                let points = head.polygon + [head.tip, head.left, head.right] +
                    head.relationshipSegments.flatMap { [$0.0, $0.1] }
                bounds = bounds.union(Rect.enclosing(points).cgRect)
                if head.kind == .circle || head.kind == .circleOutline {
                    bounds = bounds.union(CGRect(x: head.tip.x - head.length / 3, y: head.tip.y - head.length / 3,
                                                width: head.length * 2 / 3, height: head.length * 2 / 3))
                }
                if let circle = head.relationshipCircle {
                    bounds = bounds.union(CGRect(x: circle.center.x - circle.radius, y: circle.center.y - circle.radius,
                                                width: circle.radius * 2, height: circle.radius * 2))
                }
            }
        }
        let padding = max(3, element.style.width * 2.5 + 2)
        let center = element.frame.center
        let rotation = CGAffineTransform(translationX: center.x, y: center.y).rotated(by: element.rotation)
            .translatedBy(x: -center.x, y: -center.y)
        return bounds.insetBy(dx: -padding, dy: -padding).applying(rotation)
    }

    public func draw(_ elements: [Element], selection: Set<UUID> = [], in context: CGContext,
                     pixelsAvailableForBlur: Bool = false, selectionScale: Double = 1,
                     fadedIDs: Set<UUID> = [], hiddenLabelIDs: Set<UUID> = []) {
        let validIDs = Set(elements.map(\.id))
        paths = paths.filter { validIDs.contains($0.key) }
        images.beginFrame(validIDs: validIDs)
        defer { images.endFrame() }
        invalidImages.formIntersection(validIDs)
        for element in ElementStacking.ordered(elements) {
            if fadedIDs.contains(element.id) && !element.tool.isProtection {
                let center = element.frame.center
                let transform = CGAffineTransform(translationX: center.x, y: center.y).rotated(by: element.rotation)
                    .translatedBy(x: -center.x, y: -center.y)
                let footprint = element.tool == .arrow ? 8 * (element.style.headScale ?? 1) : 5
                let padding = max(70, element.style.width * footprint + element.style.roughness * 3)
                let geometryBounds = element.tool.isShape || element.tool.isFreehand || element.tool.isConnector
                    ? path(for: element).boundingBox.union(element.frame.cgRect) : element.frame.cgRect
                let bounds = geometryBounds.applying(transform).insetBy(dx: -padding, dy: -padding)
                context.saveGState()
                context.setAlpha(0.22)
                context.beginTransparencyLayer(in: bounds, auxiliaryInfo: nil)
                draw(element, in: context, pixelsAvailableForBlur: pixelsAvailableForBlur,
                     hideLabel: hiddenLabelIDs.contains(element.id))
                context.endTransparencyLayer()
                context.restoreGState()
            } else {
                draw(element, in: context, pixelsAvailableForBlur: pixelsAvailableForBlur,
                     hideLabel: hiddenLabelIDs.contains(element.id))
            }
        }
        drawSelection(elements, ids: selection.subtracting(fadedIDs), in: context, scale: selectionScale)
    }

    public func selection(_ elements: [Element], ids: Set<UUID>, in context: CGContext) {
        drawSelection(elements, ids: ids, in: context, scale: 1)
    }

    private func drawSelection(_ elements: [Element], ids: Set<UUID>, in context: CGContext, scale: Double) {
        let group = SelectionGeometry(elements: elements, selection: ids)
        for element in elements where ids.contains(element.id) {
            drawSelection(element, in: context, scale: scale, handles: group == nil)
        }
        if let group {
            drawSelection(Element(tool: group.canRotate ? .rectangle : .cover, frame: group.bounds),
                          in: context, scale: scale)
        }
    }

    private func draw(_ element: Element, in context: CGContext, pixelsAvailableForBlur: Bool, hideLabel: Bool = false)
    {
        if element.tool.isPixelEffect && pixelsAvailableForBlur { return }
        context.saveGState()
        defer {
            if !hideLabel, element.label?.hasVisibleText == true,
                let label = ShapeLabelLayout.textElement(for: element)
            {
                context.setAlpha(1)
                drawText(
                    label, in: context, paintBounds: element.label?.followsStroke == true ? element.frame.cgRect : nil)
            }
            context.restoreGState()
        }
        context.translateBy(x: element.frame.center.x, y: element.frame.center.y)
        context.rotate(by: element.rotation)
        context.translateBy(x: -element.frame.center.x, y: -element.frame.center.y)
        if element.tool == .image {
            do {
                guard let data = element.imagePNG else { throw RenderFailure.imageUnavailable }
                let image = try images.image(for: data, elementID: element.id)
                context.setAlpha(element.style.opacity)
                context.translateBy(x: element.frame.x, y: element.frame.maxY)
                context.scaleBy(x: 1, y: -1)
                context.draw(image, in: CGRect(x: 0, y: 0, width: element.frame.width, height: element.frame.height))
            } catch {
                if invalidImages.insert(element.id).inserted {
                    Logger(subsystem: "com.dorlugasigal.annotator", category: "images").error(
                        "Image rendering failed: \(error.localizedDescription, privacy: .private)")
                }
                context.setStrokeColor(NSColor.systemRed.cgColor)
                context.stroke(element.frame.cgRect)
                context.move(to: element.frame.origin.cgPoint)
                context.addLine(to: CGPoint(x: element.frame.maxX, y: element.frame.maxY))
                context.strokePath()
            }
            return
        }
        if element.tool.isProtection {
            context.setFillColor(NSColor.black.cgColor)
            context.fill(element.frame.cgRect.integral)
            return
        }
        if element.tool == .text {
            drawText(element, in: context)
            return
        }
        let measurementOpacity = (element.tool == .counter || element.tool == .ruler) && element.style.opacity < 1
        if measurementOpacity {
            if element.style.opacity == 0 { return }
            var bounds = element.frame.cgRect.insetBy(dx: -element.style.width - 4, dy: -element.style.width - 4)
            if element.tool == .counter {
                bounds = bounds.union(element.frame.cgRect.offsetBy(dx: 0, dy: element.frame.height * 0.12))
            } else if let start = element.points.first, let end = element.points.last {
                bounds = bounds.union(rulerLabelFrame(from: start, to: end).cgRect)
            }
            context.setAlpha(element.style.opacity)
            context.beginTransparencyLayer(in: bounds, auxiliaryInfo: nil)
        }
        defer { if measurementOpacity { context.endTransparencyLayer() } }
        if element.tool == .counter {
            GradientPainter.paint(
                element.style.strokeGradient, bounds: element.frame.cgRect, in: context,
                extent: element.frame.cgRect.insetBy(dx: -2, dy: -2)
            ) {
                context.setFillColor(element.style.color.nsColor.cgColor)
                context.fillEllipse(in: element.frame.cgRect)
            }
            var label = element
            label.style.color = .white
            label.style.strokeGradient = nil
            label.style.opacity = 1
            label.style.bold = true
            label.style.alignment = .center
            label.style.fontSize = element.frame.height * 0.56
            label.frame.y += element.frame.height * 0.12
            drawText(label, in: context)
            return
        }
        if element.tool == .ruler, let start = element.points.first, let end = element.points.last {
            GradientPainter.paint(
                element.style.strokeGradient, bounds: element.frame.cgRect, in: context,
                extent: element.frame.cgRect.insetBy(dx: -element.style.width - 4, dy: -element.style.width - 4)
            ) {
                context.setStrokeColor(element.style.color.nsColor.cgColor)
                context.setLineWidth(element.style.width)
                context.move(to: start.cgPoint); context.addLine(to: end.cgPoint); context.strokePath()
                for point in [start, end] {
                    context.stroke(CGRect(x: point.x - 3, y: point.y - 3, width: 6, height: 6))
                }
            }
            var label = element
            label.style.opacity = 1
            label.style.fontSize = 13; label.style.alignment = .center
            label.frame = rulerLabelFrame(from: start, to: end)
            context.setFillColor(NSColor.windowBackgroundColor.cgColor)
            context.fill(label.frame.cgRect)
            drawText(label, in: context)
            return
        }
        let style = element.style
        context.setAlpha(style.opacity * (element.tool == .highlighter ? 0.35 : 1))
        context.setStrokeColor(style.color.nsColor.cgColor)
        context.setFillColor(style.color.nsColor.cgColor)
        context.setLineWidth(element.tool == .highlighter ? style.width * 5 : style.width)
        context.setLineCap(element.tool == .highlighter ? .butt : .round)
        context.setLineJoin(element.tool == .highlighter ? .bevel : .round)
        switch style.pattern {
        case .solid: context.setLineDash(phase: 0, lengths: [])
        case .dashed: context.setLineDash(phase: 0, lengths: [style.width * 5, style.width * 3])
        case .dotted: context.setLineDash(phase: 0, lengths: [0.01, style.width * 3])
        }
        if let fill = style.fill, element.tool.supportsFill {
            context.saveGState()
            context.addPath(canonicalShape(element))
            context.clip()
            GradientPainter.paint(
                style.fillGradient, bounds: element.frame.cgRect, in: context, opacity: style.opacity,
                extent: element.frame.cgRect.insetBy(dx: -2, dy: -2)
            ) {
                context.setFillColor(fill.nsColor.cgColor)
                if style.fillPattern == .solid {
                    context.fill(element.frame.cgRect)
                } else {
                    context.setStrokeColor(fill.nsColor.cgColor)
                    context.setLineWidth(max(1, style.width / 2))
                    context.setLineDash(phase: 0, lengths: [])
                    let frame = element.frame
                    for x in Self.hatchPositions(frame: frame, width: style.width) {
                        context.move(to: CGPoint(x: x, y: frame.y))
                        context.addLine(to: CGPoint(x: x + frame.height, y: frame.maxY))
                        if style.fillPattern == .crosshatch {
                            context.move(to: CGPoint(x: x, y: frame.y))
                            context.addLine(to: CGPoint(x: x - frame.height, y: frame.maxY))
                        }
                    }
                    context.strokePath()
                }
            }
            context.restoreGState()
        }
        let strokeMargin = max(
            4, style.width * (element.tool == .highlighter ? 3 : 1) + style.roughness * 3,
            element.tool == .arrow ? max(24, style.width * 8) * (style.headScale ?? 1) : 0)
        let usesPressureSegments =
            element.tool == .pen && style.pressure && element.pressures.count == element.points.count
        let strokeBounds =
            style.strokeGradient != nil && !usesPressureSegments
            ? path(for: element).boundingBox.union(element.frame.cgRect) : element.frame.cgRect
        GradientPainter.paint(
            style.strokeGradient, bounds: element.frame.cgRect, in: context,
            opacity: style.opacity * (element.tool == .highlighter ? 0.35 : 1),
            extent: strokeBounds.insetBy(dx: -strokeMargin, dy: -strokeMargin)
        ) {
            if element.tool.isFreehand && element.points.count == 1, let point = element.points.first {
                if element.tool == .highlighter {
                    context.fill(
                        CGRect(
                            x: point.x - style.width * 0.8, y: point.y - style.width * 2.5,
                            width: style.width * 1.6, height: style.width * 5))
                } else {
                    let radius = style.width / 2
                    context.fillEllipse(
                        in: CGRect(x: point.x - radius, y: point.y - radius, width: radius * 2, height: radius * 2))
                }
            } else if element.tool == .pen && style.pressure && element.pressures.count == element.points.count {
                let clip = context.boundingBoxOfClipPath
                let deviceMargin = context.convertToUserSpace(CGRect(x: 0, y: 0, width: 2, height: 2))
                let margin = max(deviceMargin.width, deviceMargin.height)
                let cullSegments = !clip.contains(
                    element.frame.cgRect.insetBy(
                        dx: -style.width / 2 - margin, dy: -style.width / 2 - margin))
                for index in element.points.indices.dropFirst() {
                    let start = element.points[index - 1], end = element.points[index]
                    let width = style.width * max(0.15, element.pressures[index])
                    if cullSegments {
                        let padding = width / 2 + margin
                        let bounds = CGRect(
                            x: min(start.x, end.x) - padding, y: min(start.y, end.y) - padding,
                            width: abs(end.x - start.x) + padding * 2,
                            height: abs(end.y - start.y) + padding * 2)
                        if !bounds.intersects(clip) { continue }
                    }
                    context.setLineWidth(width)
                    context.move(to: start.cgPoint)
                    context.addLine(to: end.cgPoint)
                    context.strokePath()
                }
            } else {
                context.saveGState()
                if element.label?.hasVisibleText == true, let text = ShapeLabelLayout.textElement(for: element) {
                    let interior = element.symbolFrame.cgRect.insetBy(dx: style.width + 1, dy: style.width + 1)
                    let gap = text.frame.cgRect.insetBy(dx: -3, dy: -2).intersection(interior)
                    if !gap.isNull && !gap.isEmpty {
                        let mask = CGMutablePath()
                        mask.addRect(context.boundingBoxOfClipPath)
                        mask.addRect(gap)
                        context.addPath(mask)
                        context.clip(using: .evenOdd)
                    }
                }
                if element.tool == .arrow {
                    for start in [true, false] {
                        guard let head = ArrowHeadGeometry.make(for: element, atStart: start) else { continue }
                        guard head.hollow || head.relationshipCircle != nil else { continue }
                        let mask = CGMutablePath()
                        mask.addRect(context.boundingBoxOfClipPath)
                        if head.kind == .circleOutline {
                            mask.addEllipse(
                                in: CGRect(
                                    x: head.tip.x - head.length / 3, y: head.tip.y - head.length / 3,
                                    width: head.length * 2 / 3, height: head.length * 2 / 3))
                        } else if head.hollow {
                            mask.addLines(between: head.polygon.map(\.cgPoint)); mask.closeSubpath()
                        } else if let circle = head.relationshipCircle {
                            mask.addEllipse(
                                in: CGRect(
                                    x: circle.center.x - circle.radius, y: circle.center.y - circle.radius,
                                    width: circle.radius * 2, height: circle.radius * 2))
                        } else {
                            continue
                        }
                        context.addPath(mask); context.clip(using: .evenOdd)
                    }
                }
                context.addPath(path(for: element))
                context.strokePath()
                context.restoreGState()
            }
            if element.tool == .arrow, element.points.count >= 2 {
                context.setLineDash(phase: 0, lengths: [])
                for start in [true, false] {
                    if let head = ArrowHeadGeometry.make(for: element, atStart: start) { drawHead(head, in: context) }
                }
            }
        }
    }

    /// Where hatch lines start on the top edge of `frame`. Each line runs to `x + frame.height` on the bottom edge,
    /// and crosshatch adds one to `x - frame.height`. Swift's floating-point stride computes each position as
    /// `start + index × spacing` with a fused multiply-add, so positions do not drift on large frames.
    nonisolated static func hatchPositions(frame: Rect, width: Double) -> StrideThrough<Double> {
        let spacing = max(6, width * 3, (frame.width + frame.height * 2) / 4_096)
        return stride(from: frame.x - frame.height, through: frame.maxX + frame.height, by: spacing)
    }

    func path(for element: Element) -> CGPath {
        if let cached = paths[element.id], cached.token == element.geometryToken { return cached.path }
        if let cached = paths[element.id], element.tool.isFreehand,
           cached.tool == element.tool, cached.roughness == element.style.roughness,
           cached.appendEpoch == element.appendEpoch, cached.pointCount > 0,
           element.style.roughness == 0, cached.pointCount < element.points.count {
            for point in element.points.dropFirst(cached.pointCount) { cached.path.addLine(to: point.cgPoint) }
            paths[element.id] = CachedPath(token: element.geometryToken, appendEpoch: element.appendEpoch, pointCount: element.points.count,
                                          tool: element.tool, roughness: 0, path: cached.path)
            incrementalAppends += 1
            return cached.path
        }
        var path = CGMutablePath()
        if element.tool.isShape {
            let canonical = canonicalShape(element)
            path.addPath(element.style.roughness > 0 ? RoughGeometry.outline(element, canonical: canonical) : canonical)
        } else if let first = element.points.first {
            path.move(to: first.cgPoint)
            if element.style.curved && element.tool.isConnector && element.points.count > 2 {
                for curve in ConnectorGeometry.curves(through: element.points) {
                    path.addCurve(to: curve.end.cgPoint, control1: curve.firstControl.cgPoint,
                                  control2: curve.secondControl.cgPoint)
                }
            } else {
                for (index, point) in element.points.enumerated().dropFirst() {
                    path.addLine(to: (element.tool.isConnector ? point : jitter(point, index: index, element: element)).cgPoint)
                }
            }
            if element.tool.isConnector && element.style.roughness > 0 {
                let rough = CGMutablePath()
                rough.addPath(RoughGeometry.outline(element, canonical: path))
                path = rough
            }
        }
        if paths.count >= 512, let key = paths.keys.first { paths.removeValue(forKey: key) }
        paths[element.id] = CachedPath(token: element.geometryToken, appendEpoch: element.appendEpoch, pointCount: element.points.count,
                                      tool: element.tool, roughness: element.style.roughness, path: path)
        fullPathBuilds += 1
        return path
    }

    private func canonicalShape(_ element: Element) -> CGPath {
        let box = element.frame
        if Tool.systemDesignShapes.contains(element.tool) { return SystemDiagramGeometry.path(for: element) }
        if let kind = element.tool.polygonKind {
            let path = CGMutablePath()
            path.addLines(between: PolygonShape.vertices(kind, in: box).map(\.cgPoint))
            path.closeSubpath()
            return path
        }
        switch element.tool {
        case .database, .queue, .person, .grid, .axes: return DiagramGeometry.path(for: element)
        case .ellipse: return CGPath(ellipseIn: box.cgRect, transform: nil)
        case .diamond: return DiamondGeometry.path(frame: box, cornerRadius: element.style.cornerRadius)
        default:
            let radius = min(element.style.cornerRadius, box.width / 2, box.height / 2)
            return CGPath(roundedRect: box.cgRect, cornerWidth: radius, cornerHeight: radius, transform: nil)
        }
    }

    private func jitter(_ point: Point, index: Int, element: Element) -> Point {
        guard element.style.roughness > 0 else { return point }
        var value = element.seed &+ UInt64(index) &* 0x9e3779b97f4a7c15
        value = (value ^ (value >> 30)) &* 0xbf58476d1ce4e5b9
        value = (value ^ (value >> 27)) &* 0x94d049bb133111eb
        value ^= value >> 31
        let x = Double(value & 0xffff) / 65535 - 0.5
        let y = Double((value >> 16) & 0xffff) / 65535 - 0.5
        return point + Point(x, y) * element.style.roughness
    }

    private func drawHead(_ head: ArrowHeadGeometry, in context: CGContext) {
        if head.kind == .circle || head.kind == .circleOutline {
            let bounds = CGRect(x: head.tip.x - head.length / 3, y: head.tip.y - head.length / 3,
                                width: head.length * 2 / 3, height: head.length * 2 / 3)
            if head.hollow { context.strokeEllipse(in: bounds) } else { context.fillEllipse(in: bounds) }
        } else if head.kind == .square || head.kind == .diamond || head.kind == .diamondOutline || head.kind == .triangleOutline {
            context.addLines(between: head.polygon.map(\.cgPoint))
            context.closePath()
            if head.hollow { context.strokePath() } else { context.fillPath() }
        } else if ArrowHead.relationshipChoices.contains(head.kind) {
            for (a,b) in head.relationshipSegments {
                context.move(to: a.cgPoint); context.addLine(to: b.cgPoint); context.strokePath()
            }
            if let circle = head.relationshipCircle {
                context.strokeEllipse(in: CGRect(x: circle.center.x-circle.radius, y: circle.center.y-circle.radius,
                                                width: circle.radius*2, height: circle.radius*2))
            }
        } else {
            context.move(to: head.left.cgPoint)
            if head.kind != .bar { context.addLine(to: head.tip.cgPoint) }
            context.addLine(to: head.right.cgPoint)
            if head.kind == .filled { context.closePath(); context.fillPath() }
            else { context.strokePath() }
        }
    }

    private func rulerLabelFrame(from start: Point, to end: Point) -> Rect {
        Rect(x: (start.x + end.x) / 2 - 60, y: (start.y + end.y) / 2 - 25, width: 120, height: 22)
    }

    private func drawText(_ element: Element, in context: CGContext, paintBounds: CGRect? = nil) {
        let style = element.style
        let font = NativeTextFont.resolve(style)
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = style.alignment == .center ? .center : style.alignment == .right ? .right : .left
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
        defer { NSGraphicsContext.restoreGraphicsState() }
        GradientPainter.paint(style.strokeGradient, bounds: paintBounds ?? element.frame.cgRect, in: context,
                              extent: element.frame.cgRect.insetBy(dx: -2, dy: -2)) {
            (element.text as NSString).draw(in: element.frame.cgRect, withAttributes: [
                .font: font, .foregroundColor: style.color.nsColor.withAlphaComponent(style.color.alpha * style.opacity),
                .paragraphStyle: paragraph
            ])
        }
    }

    private func drawSelection(_ element: Element, in context: CGContext, scale: Double, handles: Bool = true) {
        context.saveGState()
        defer { context.restoreGState() }
        context.translateBy(x: element.frame.center.x, y: element.frame.center.y)
        context.rotate(by: element.rotation)
        context.translateBy(x: -element.frame.center.x, y: -element.frame.center.y)
        context.setStrokeColor(NSColor.controlAccentColor.cgColor)
        context.setFillColor(NSColor.white.cgColor)
        context.setLineWidth(1 / max(scale, 0.1))
        context.setLineDash(phase: 0, lengths: element.locked ? [3 / scale, 3 / scale] : [])
        // A lone editable arrow or line shows its point handles; a bounding box around them only adds noise.
        let pointHandlesOnly = element.tool.isConnector && !element.locked && handles
        if !pointHandlesOnly {
            context.stroke(element.frame.cgRect.insetBy(dx: -3 / scale, dy: -3 / scale))
        }
        guard !element.locked, handles else { return }
        let radius = 4 / max(scale, 0.1)
        let box = element.frame
        let points = element.tool.isConnector ? element.points :
            [box.origin, Point(box.maxX, box.y), Point(box.maxX, box.maxY), Point(box.x, box.maxY)]
        for point in points {
            let handle = CGRect(x: point.x - radius, y: point.y - radius, width: radius * 2, height: radius * 2)
            context.fillEllipse(in: handle)
            context.strokeEllipse(in: handle)
        }
        if !element.tool.isProtection && !element.tool.isConnector {
            let rotation = Point(box.center.x, box.y - 24 / max(scale, 0.1))
            let rotationRadius = 6 / max(scale, 0.1)
            context.move(to: CGPoint(x: box.center.x, y: box.y))
            context.addLine(to: rotation.cgPoint)
            context.strokePath()
            context.fillEllipse(in: CGRect(x: rotation.x - rotationRadius, y: rotation.y - rotationRadius,
                                          width: rotationRadius * 2, height: rotationRadius * 2))
        }
    }
}
