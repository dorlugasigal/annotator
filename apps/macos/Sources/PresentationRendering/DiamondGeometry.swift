import CoreGraphics
import AnnotationCore

enum DiamondGeometry {
    static func path(frame: Rect, cornerRadius: Double) -> CGPath {
        let vertices = [
            Point(frame.center.x, frame.y), Point(frame.maxX, frame.center.y),
            Point(frame.center.x, frame.maxY), Point(frame.x, frame.center.y)
        ]
        let path = CGMutablePath()
        guard cornerRadius > 0, frame.width > 0, frame.height > 0 else {
            path.addLines(between: vertices.map(\.cgPoint))
            path.closeSubpath()
            return path
        }
        for (index, corner) in RoundedDiamond.corners(frame: frame, radius: cornerRadius).enumerated() {
            if index == 0 { path.move(to: corner.entry.cgPoint) }
            else { path.addLine(to: corner.entry.cgPoint) }
            path.addQuadCurve(to: corner.exit.cgPoint, control: corner.control.cgPoint)
        }
        path.closeSubpath()
        return path
    }
}
