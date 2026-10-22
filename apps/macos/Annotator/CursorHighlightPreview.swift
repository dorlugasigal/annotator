import AppKit
import SwiftUI
import AnnotationCore
import PresentationRendering

struct CursorHighlightPreview: NSViewRepresentable {
    let settings: CursorHighlightSettings

    func makeNSView(context: Context) -> CursorPreviewCanvas { CursorPreviewCanvas(settings: settings) }
    func updateNSView(_ view: CursorPreviewCanvas, context: Context) { view.update(settings) }
    static func dismantleNSView(_ view: CursorPreviewCanvas, coordinator: ()) { view.stop() }
}

@MainActor
final class CursorPreviewCanvas: NSView {
    private(set) var settings: CursorHighlightSettings
    let animation = CursorClickAnimation()
    private var tracking: NSTrackingArea?
    private var point: CGPoint?
    private var clickPoint: CGPoint?
    private var lastMove: Double?
    private var lastClick: Double?
    private var expiry: Task<Void, Never>?
    private var pointerInside = false
    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    init(settings: CursorHighlightSettings) {
        self.settings = settings
        super.init(frame: .zero)
        identifier = NSUserInterfaceItemIdentifier("cursor-highlight-preview")
        setAccessibilityElement(true)
        setAccessibilityRole(.button)
        setAccessibilityLabel("Preview click animation")
        setAccessibilityHelp("Move here to try the highlight. Click, right-click or press Space to preview a click.")
    }
    required init?(coder: NSCoder) { nil }

    func update(_ settings: CursorHighlightSettings) {
        let changedEffect = self.settings.resolvedClickEffect != settings.resolvedClickEffect
        self.settings = settings
        if changedEffect && window != nil && !isHiddenOrHasHiddenAncestor { previewClick() }
        needsDisplay = true
    }

    override func updateTrackingAreas() {
        if let tracking { removeTrackingArea(tracking) }
        tracking = NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                 owner: self, userInfo: nil)
        if let tracking { addTrackingArea(tracking) }
        super.updateTrackingAreas()
    }

    override func mouseEntered(with event: NSEvent) { pointerInside = true; mouseMoved(with: event) }
    override func mouseMoved(with event: NSEvent) {
        point = convert(event.locationInWindow, from: nil)
        lastMove = ProcessInfo.processInfo.systemUptime
        needsDisplay = true
        expiry?.cancel()
        if settings.visibility == .moving {
            expiry = Task { [weak self] in
                do { try await Task.sleep(for: .milliseconds(750)) } catch { return }
                self?.needsDisplay = true
            }
        }
    }
    override func mouseExited(with event: NSEvent) { pointerInside = false; point = nil; needsDisplay = true }
    override func mouseDown(with event: NSEvent) { mouseMoved(with: event); previewClick() }
    override func rightMouseDown(with event: NSEvent) { mouseDown(with: event) }
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 49 || event.keyCode == 36 { previewClick() } else { super.keyDown(with: event) }
    }
    override func accessibilityPerformPress() -> Bool { previewClick(); return true }

    func previewClick() {
        guard window?.isVisible == true, !isHiddenOrHasHiddenAncestor else { return }
        lastClick = ProcessInfo.processInfo.systemUptime
        clickPoint = point ?? CGPoint(x: bounds.midX, y: bounds.midY)
        animation.start(animated: settings.resolvedClickEffect != .off &&
            !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion, view: self) { [weak self] in
            guard let self else { return }
            if window?.isVisible != true || isHiddenOrHasHiddenAncestor { animation.stop(); return }
            needsDisplay = true
        }
    }

    func stop() { animation.stop(); expiry?.cancel(); expiry = nil }
    override func viewDidHide() { stop(); super.viewDidHide() }
    override func viewWillMove(toWindow newWindow: NSWindow?) {
        if newWindow == nil { stop() }
        super.viewWillMove(toWindow: newWindow)
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        let background = NSGradient(starting: NSColor(srgbRed: 0.09, green: 0.14, blue: 0.23, alpha: 1),
                                    ending: NSColor(srgbRed: 0.17, green: 0.24, blue: 0.34, alpha: 1))
        background?.draw(in: bounds, angle: -35)
        let center = point ?? CGPoint(x: bounds.midX, y: bounds.midY)
        if settings.isVisible(now: ProcessInfo.processInfo.systemUptime, lastMovement: lastMove, lastClick: lastClick) {
            let footprint = settings.diameter * 1.6 + settings.borderWidth * 2 + settings.resolvedGlow * 4
            let scale = min(1, max(1, min(bounds.width, bounds.height)-24) / footprint)
            context.saveGState()
            context.translateBy(x: center.x, y: center.y)
            context.scaleBy(x: scale, y: scale)
            let click = clickPoint.map { CGPoint(x: ($0.x-center.x)/scale, y: ($0.y-center.y)/scale) }
            CursorHighlightRenderer.draw(settings, center: .zero, clickProgress: animation.progress,
                clickCenter: click, reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion, in: context)
            context.restoreGState()
        }
        if !pointerInside {
            let cursor = NSCursor.arrow
            cursor.image.draw(in: CGRect(x: center.x-cursor.hotSpot.x, y: center.y-cursor.hotSpot.y,
                                        width: cursor.image.size.width, height: cursor.image.size.height),
                              from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        }
    }
}
