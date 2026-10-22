import AppKit
import AnnotationCore
import OSLog

public enum GradientPainter {
    public static func paint(_ value: ColorGradient?, bounds: CGRect, in context: CGContext, opacity: Double = 1,
                             extent: CGRect? = nil,
                             mask: () -> Void) {
        guard let value else { mask(); return }
        do { try value.validate() }
        catch {
            Logger(subsystem: "com.dorlugasigal.annotator", category: "paint").error("Invalid gradient paint.")
            return
        }
        guard let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                                        colors: value.colors.map { $0.nsColor.cgColor } as CFArray,
                                        locations: nil) else {
            Logger(subsystem: "com.dorlugasigal.annotator", category: "paint").error("Could not create gradient paint.")
            return
        }
        let angle = value.angle * .pi / 180
        let dx = cos(angle), dy = sin(angle)
        let length = max(1, abs(dx) * bounds.width + abs(dy) * bounds.height) / 2
        let start = CGPoint(x: bounds.midX - dx * length, y: bounds.midY - dy * length)
        let end = CGPoint(x: bounds.midX + dx * length, y: bounds.midY + dy * length)
        context.saveGState()
        if let extent { context.clip(to: extent) }
        context.setAlpha(1)
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        context.setAlpha(opacity)
        mask()
        context.setAlpha(1)
        context.setBlendMode(.sourceIn)
        context.drawLinearGradient(gradient, start: start, end: end,
                                   options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
        context.endTransparencyLayer()
        context.restoreGState()
    }
}
