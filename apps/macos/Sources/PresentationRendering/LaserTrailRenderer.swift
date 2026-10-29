import CoreGraphics
import AnnotationCore

/// Draws the laser pointer's trail (`LaserTrail`). Each layer is one filled outline of circles around the trail's dots
/// and the bands between them, all wound the same way, so the parts merge and a trail that crosses itself is no darker.
public enum LaserTrailRenderer {
    public static func draw(_ trail: LaserTrail, at now: Double, in context: CGContext) {
        for shape in trail.shapes(at: now) { draw(shape, in: context) }
    }

    public static func draw(_ shape: LaserTrail.Shape, in context: CGContext) {
        guard shape.opacity > 0 else { return }
        context.saveGState()
        defer { context.restoreGState() }
        for pass in LaserTrail.passes {
            let path = outline(shape.dots, pass: pass)
            guard !path.isEmpty else { continue }
            let color = pass.white ? RGBA(1, 1, 1) : shape.color
            context.setFillColor(CGColor(srgbRed: color.red, green: color.green, blue: color.blue,
                                         alpha: color.alpha * pass.alpha * shape.opacity))
            context.addPath(path)
            context.fillPath(using: .winding)
        }
    }

    /// One layer's outline: a circle around each dot and a band to the next dot, counterclockwise with y up.
    public static func outline(_ dots: [LaserTrail.Dot], pass: LaserTrail.Pass) -> CGPath {
        let path = CGMutablePath()
        for (index, dot) in dots.enumerated() {
            let radius = pass.radius(dot)
            if radius > 0 {
                path.move(to: CGPoint(x: dot.center.x + radius, y: dot.center.y))
                path.addArc(center: dot.center.cgPoint, radius: radius, startAngle: 0, endAngle: .pi * 2, clockwise: false)
                path.closeSubpath()
            }
            if index > 0, let band = LaserTrail.band(from: dots[index - 1], to: dot, pass: pass) {
                path.addLines(between: band.map(\.cgPoint))
                path.closeSubpath()
            }
        }
        return path
    }
}
