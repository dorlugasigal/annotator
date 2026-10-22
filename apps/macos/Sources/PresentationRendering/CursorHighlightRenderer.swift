import AppKit
import AnnotationCore

@MainActor
public enum CursorHighlightRenderer {
    nonisolated public static func path(for shape: CursorHighlightSettings.Shape, in frame: CGRect) -> CGPath {
        switch shape {
        case .ring, .disc: return CGPath(ellipseIn: frame, transform: nil)
        case .square: return CGPath(rect: frame, transform: nil)
        case .roundedSquare:
            let path = CGMutablePath()
            for index in 0...96 {
                let angle = Double(index) * .pi * 2 / 96
                let c = cos(angle), s = sin(angle)
                let point = CGPoint(x: frame.midX + (c < 0 ? -1 : 1) * sqrt(abs(c)) * frame.width / 2,
                                    y: frame.midY + (s < 0 ? -1 : 1) * sqrt(abs(s)) * frame.height / 2)
                if index == 0 { path.move(to: point) } else { path.addLine(to: point) }
            }
            path.closeSubpath()
            return path
        case .diamond:
            let side = frame.width / sqrt(2)
            let box = CGRect(x: frame.midX-side/2, y: frame.midY-side/2, width: side, height: side)
            var transform = CGAffineTransform(translationX: frame.midX, y: frame.midY).rotated(by: .pi/4)
                .translatedBy(x: -frame.midX, y: -frame.midY)
            return CGPath(roundedRect: box, cornerWidth: side*0.12, cornerHeight: side*0.12, transform: &transform)
        case .hexagon:
            let path = CGMutablePath()
            for index in 0..<6 {
                let angle = Double(index) * .pi / 3
                let point = CGPoint(x: frame.midX + cos(angle)*frame.width/2, y: frame.midY + sin(angle)*frame.height/2)
                if index == 0 { path.move(to: point) } else { path.addLine(to: point) }
            }
            path.closeSubpath()
            return path
        case .crosshair:
            let path = CGMutablePath(), gap = frame.width * 0.13
            for (start, end) in [
                (CGPoint(x: frame.minX, y: frame.midY), CGPoint(x: frame.midX-gap, y: frame.midY)),
                (CGPoint(x: frame.midX+gap, y: frame.midY), CGPoint(x: frame.maxX, y: frame.midY)),
                (CGPoint(x: frame.midX, y: frame.minY), CGPoint(x: frame.midX, y: frame.midY-gap)),
                (CGPoint(x: frame.midX, y: frame.midY+gap), CGPoint(x: frame.midX, y: frame.maxY))
            ] { path.move(to: start); path.addLine(to: end) }
            return path
        }
    }

    /// Pulse scale: a fast attack to the peak at 25% of the click, then a slower eased release back to 1.
    nonisolated public static func pulseScale(_ progress: Double) -> Double {
        let p = min(1, max(0, progress))
        if p < 0.25 { return 1 + 0.18 * (1 - pow(1 - p / 0.25, 3)) }
        return 1 + 0.18 * (1 - easeInOut((p - 0.25) / 0.75))
    }

    /// cubic-bezier(0.4, 0, 0.2, 1). This module cannot use the app's shared motion curves.
    nonisolated static func easeInOut(_ x: Double) -> Double {
        guard x > 0 else { return 0 }
        guard x < 1 else { return 1 }
        func sample(_ t: Double, _ a: Double, _ b: Double) -> Double {
            3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t
        }
        var low = 0.0, high = 1.0
        for _ in 0..<32 {
            let middle = (low + high) / 2
            if sample(middle, 0.4, 0.2) < x { low = middle } else { high = middle }
        }
        return sample((low + high) / 2, 0, 1)
    }

    public static func draw(_ settings: CursorHighlightSettings, center: CGPoint, clickProgress: Double?,
                            clickCenter: CGPoint? = nil, reduceMotion: Bool = false, in context: CGContext) {
        context.saveGState()
        defer { context.restoreGState() }
        let effect = settings.resolvedClickEffect
        let progress = clickProgress.map { min(1, max(0, $0)) }
        var scale = 1.0, rotation = 0.0
        if let progress, effect != .off, !reduceMotion {
            switch effect {
            case .pulse: scale = pulseScale(progress)
            case .turn:
                scale = 1 - 0.1 * sin(.pi * progress)
                rotation = (1 - pow(1 - progress, 3)) * (settings.shape == .hexagon ? .pi/3 : .pi/2)
            default: break
            }
        }
        context.translateBy(x: center.x, y: center.y)
        context.rotate(by: rotation)
        context.scaleBy(x: scale, y: scale)
        context.setAlpha(settings.color.alpha)
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        defer { context.endTransparencyLayer() }
        let color = RGBA(settings.color.red, settings.color.green, settings.color.blue).nsColor
        context.setStrokeColor(color.cgColor)
        context.setFillColor(color.cgColor)
        context.setLineCap(.round)
        context.setLineJoin(.round)
        let width = settings.borderWidth + (reduceMotion && progress != nil && effect != .off ? 2 : 0)
        context.setLineWidth(width)
        switch settings.resolvedBorder {
        case .solid: context.setLineDash(phase: 0, lengths: [])
        case .dashed: context.setLineDash(phase: 0, lengths: [width*3, width*2])
        case .dotted: context.setLineDash(phase: 0, lengths: [0.01, width*2.5])
        }
        if settings.resolvedGlow > 0 {
            context.setShadow(offset: .zero, blur: settings.resolvedGlow, color: color.withAlphaComponent(0.75).cgColor)
        }
        let radius = settings.diameter/2
        let frame = CGRect(x: -radius, y: -radius, width: settings.diameter, height: settings.diameter)
        context.addPath(path(for: settings.shape, in: frame))
        if settings.shape == .disc { context.fillPath() } else { context.strokePath() }
        if let progress, effect == .ripple, !reduceMotion {
            let expansion = settings.diameter * 0.28 * (1 - pow(1-progress, 3))
            context.setShadow(offset: .zero, blur: 0, color: nil)
            context.setAlpha(pow(1-progress, 2) * 0.75)
            context.setLineWidth(max(1, width*(1-progress*0.5)))
            context.setLineDash(phase: 0, lengths: [])
            let origin = clickCenter ?? center
            let ripple = frame.insetBy(dx: -expansion, dy: -expansion)
                .offsetBy(dx: origin.x-center.x, dy: origin.y-center.y)
            context.addPath(path(for: settings.shape, in: ripple))
            context.strokePath()
        }
    }
}
