import AppKit

@MainActor
enum MenuBarIcon {
    static func makeImage(color: NSColor = .black, size: CGFloat = 18) -> NSImage {
        // The original glyph from website/public/mark.svg, without its tile or accent dot.
        let path = CGMutablePath()
        path.move(to: CGPoint(x: 39, y: 19))
        path.addCurve(to: CGPoint(x: 20, y: 45),
                      control1: CGPoint(x: 22, y: 9), control2: CGPoint(x: 7, y: 36))
        path.addCurve(to: CGPoint(x: 37, y: 36),
                      control1: CGPoint(x: 26, y: 49), control2: CGPoint(x: 33, y: 44))
        path.addLine(to: CGPoint(x: 36, y: 45))
        path.addLine(to: CGPoint(x: 43, y: 45))
        path.addLine(to: CGPoint(x: 48, y: 17))
        path.addLine(to: CGPoint(x: 40, y: 17))
        path.closeSubpath()
        path.move(to: CGPoint(x: 35, y: 26))
        path.addCurve(to: CGPoint(x: 24, y: 39),
                      control1: CGPoint(x: 33, y: 36), control2: CGPoint(x: 27, y: 42))
        path.addCurve(to: CGPoint(x: 35, y: 26),
                      control1: CGPoint(x: 20, y: 35), control2: CGPoint(x: 28, y: 21))
        path.closeSubpath()
        let glyph: CGPath = path
        let bounds = glyph.boundingBoxOfPath
        let image = NSImage(size: NSSize(width: size, height: size), flipped: true) { rect in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            let inset = size / 9
            let scale = min((rect.width - inset) / bounds.width, (rect.height - inset) / bounds.height)
            context.saveGState()
            defer { context.restoreGState() }
            context.translateBy(x: rect.midX - bounds.midX * scale, y: rect.midY - bounds.midY * scale)
            context.scaleBy(x: scale, y: scale)
            context.addPath(glyph)
            context.setFillColor(color.cgColor)
            context.drawPath(using: .eoFill)
            return true
        }

        image.isTemplate = true
        image.accessibilityDescription = "Annotator"
        return image
    }
}

@MainActor
final class SharingStatusDot: NSView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.current?.cgContext.clear(bounds)
        NSColor.systemRed.setFill()
        NSBezierPath(ovalIn: bounds.insetBy(dx: 0.5, dy: 0.5)).fill()
    }
}
