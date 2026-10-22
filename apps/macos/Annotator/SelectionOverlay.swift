import AppKit
import AnnotationCore
import PresentationRendering
import MotionCore

@MainActor
final class SelectionOverlayView: NSView {
    weak var session: OverlaySession?
    weak var model: WorkspaceModel?
    private let renderer = SceneRenderer()
    private(set) var renderPassCount: UInt64 = 0
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext, let session, let model else { return }
        renderPassCount &+= 1
        context.clear(bounds)
        context.saveGState()
        if let viewport = session.presentation?.screenViewport {
            context.scaleBy(x: viewport.output.width / viewport.source.width, y: viewport.output.height / viewport.source.height)
            context.translateBy(x: -viewport.source.x, y: -viewport.source.y)
        }
        renderer.draw(session.document.elements.filter { $0.tool == .cover }, in: context)
        if model.mode == .draw && !session.canvas.hasTextEditor {
            renderer.selection(session.document.elements, ids: session.document.selection.subtracting(session.canvas.eraserPreviewIDs), in: context)
        }
        context.restoreGState()
        if let window {
            let pointer = convert(window.convertPoint(fromScreen: NSEvent.mouseLocation), from: nil)
            if model.spotlightFade.isVisible {
                do {
                    try SpotlightRenderer.draw(in: context, bounds: bounds, center: pointer,
                        radius: model.spotlightFade.radius(for: model.spotlightRadius), opacity: model.spotlightFade.intensity)
                } catch { model.report(error) }
            }
            if model.cursorHighlightVisible {
                CursorHighlightRenderer.draw(model.cursorHighlightSettings, center: pointer,
                    clickProgress: model.pointerClickAnimation.progress,
                    clickCenter: model.cursorClickLocation.map { convert(window.convertPoint(fromScreen: $0), from: nil) },
                    reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion, in: context)
            }
        }
        if session.document.presentation.mode == .screen && model.preferences.showShortcutHints != false {
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
            WorkspaceHint.draw(WorkspaceHint.text(model: model), in: bounds, origin: CGPoint(x:16,y:14))
            NSGraphicsContext.restoreGraphicsState()
        }
    }
}

/// Owns the spotlight's fade and size changes. It ticks only while something changes,
/// so a resting spotlight (on or off) keeps no animation running.
@MainActor
final class SpotlightFade {
    static let fadeIn: CFTimeInterval = MotionTiming.spotlightFadeInDuration
    static let fadeOut: CFTimeInterval = MotionTiming.spotlightFadeOutDuration
    static let resize: CFTimeInterval = MotionTiming.spotlightResizeDuration
    private(set) var intensity = 0.0
    private(set) var visible = false
    private var intensityFrom = 0.0
    private var intensityStart: CFTimeInterval?
    private var radiusFrom = 0.0
    private var radiusTarget: Double?
    private var displayedRadius: Double?
    private var radiusStart: CFTimeInterval?
    private var fading = false
    private var resizing = false
    private let ticker = MotionTicker()
    private let onFrame: @MainActor () -> Bool
    var reducesMotion: () -> Bool = { Motion.systemReducesMotion }
    var isVisible: Bool { intensity > 0 }
    var isAnimating: Bool { ticker.isRunning }

    /// `onFrame` redraws the spotlight's views; returning `false` (for example when the workspace is hidden)
    /// lands the animation on its end state.
    init(onFrame: @escaping @MainActor () -> Bool) { self.onFrame = onFrame }

    func setVisible(_ visible: Bool) {
        guard visible != self.visible else { return }
        self.visible = visible
        intensityFrom = intensity
        intensityStart = nil
        fading = true
        run()
    }

    /// Animates from the size on screen. Without a visible spotlight, or with Reduce Motion, the size changes at once.
    func setRadius(_ radius: Double, from previous: Double) {
        guard radius.isFinite else { return }
        let current = displayedRadius ?? previous
        radiusTarget = radius
        guard isVisible, !reducesMotion(), current.isFinite, current != radius else {
            displayedRadius = radius
            resizing = false
            if !fading { ticker.stop() }
            return
        }
        radiusFrom = current
        displayedRadius = current
        radiusStart = nil
        resizing = true
        run()
    }

    /// The radius to draw for the model's current `target`. A target set elsewhere is shown at once.
    func radius(for target: Double) -> Double {
        radiusTarget == target ? displayedRadius ?? target : target
    }

    func finish() {
        ticker.stop()
        intensity = visible ? 1 : 0
        if let radiusTarget { displayedRadius = radiusTarget }
        fading = false
        resizing = false
    }

    private func run() {
        ticker.start(view: nil) { [weak self] now in self?.step(now) ?? false }
    }

    private func step(_ now: CFTimeInterval) -> Bool {
        if fading {
            let start = intensityStart ?? now
            intensityStart = start
            let target = visible ? 1.0 : 0.0
            let duration = (visible ? Self.fadeIn : Self.fadeOut) * max(0.001, abs(target - intensityFrom))
            let time = min(1, max(0, (now - start) / duration))
            intensity = intensityFrom + (target - intensityFrom) * Motion.progress(Motion.fade, at: time)
            if time >= 1 { intensity = target; fading = false }
        }
        if resizing, let radiusTarget {
            let start = radiusStart ?? now
            radiusStart = start
            let time = min(1, max(0, (now - start) / Self.resize))
            displayedRadius = radiusFrom + (radiusTarget - radiusFrom) * Motion.progress(Motion.standard, at: time)
            if time >= 1 { displayedRadius = radiusTarget; resizing = false }
        }
        guard onFrame() else {
            finish()
            return false
        }
        return fading || resizing
    }
}
