import AppKit
import AnnotationCore
import PresentationRendering
import os

final class PaintTextLayoutManager: NSLayoutManager {
    private struct PaintState: Sendable {
        var gradient: ColorGradient?
        var bounds = CGRect.zero
    }
    private let paint = OSAllocatedUnfairLock(initialState: PaintState())
    @MainActor var gradient: ColorGradient? {
        get { paint.withLock { $0.gradient } }
        set { paint.withLock { $0.gradient = newValue } }
    }
    @MainActor var paintBounds: CGRect {
        get { paint.withLock { $0.bounds } }
        set { paint.withLock { $0.bounds = newValue } }
    }

    nonisolated override func drawGlyphs(forGlyphRange glyphsToShow: NSRange, at origin: NSPoint) {
        // AppKit's callback is nonisolated. Take an immutable paint snapshot without transferring the layout manager.
        let state = paint.withLock { $0 }
        guard let context = NSGraphicsContext.current?.cgContext else {
            super.drawGlyphs(forGlyphRange: glyphsToShow, at: origin)
            return
        }
        GradientPainter.paint(state.gradient, bounds: state.bounds, in: context) {
            super.drawGlyphs(forGlyphRange: glyphsToShow, at: origin)
        }
    }
}
