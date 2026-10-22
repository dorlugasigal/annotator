import CoreGraphics

public enum SpotlightRenderer {
    /// `opacity` scales the dimming so the spotlight can fade in and out; 0 draws nothing.
    public static func draw(in context: CGContext, bounds: CGRect, center: CGPoint, radius: CGFloat,
                            opacity: CGFloat = 1) throws {
        guard opacity.isFinite else { throw RenderFailure.contextUnavailable }
        guard opacity > 0 else { return }
        guard radius.isFinite, radius > 0,
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let gradient = CGGradient(colorSpace: space,
                                        colorComponents: [0, 0, 0, 0, 0, 0, 0, 0.62],
                                        locations: [0, 1], count: 2) else {
            throw RenderFailure.contextUnavailable
        }
        let feather = min(64, max(12, radius * 0.25))
        context.saveGState()
        context.clip(to: bounds)
        context.setAlpha(min(1, opacity))
        context.drawRadialGradient(gradient, startCenter: center, startRadius: max(0, radius - feather),
                                   endCenter: center, endRadius: radius + feather,
                                   options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
        context.restoreGState()
    }
}
