import AppKit
import AnnotationCore
import PresentationRendering

enum RegionSelectionResult: Equatable {
    case selected(displayID: CGDirectDisplayID, bounds: Rect), cancelled, focusLost
}

@MainActor
final class RegionSharingGuide {
    private var outline: NSPanel?
    private var outlineDisplayFrame: CGRect?
    private var selections: [CGDirectDisplayID: RegionSelectionPanel] = [:]
    var isSelecting: Bool { !selections.isEmpty }
    var outlineFrame: CGRect? { outline?.frame }
    var outlineWindow: NSPanel? { outline }
    var selectionWindows: [CGDirectDisplayID: NSPanel] { selections }
    var outlineFillOpacity: Double {
        (outline?.contentView as? RegionOutlineView)?.effectiveFillOpacity ?? 0
    }

    func showOutline(region: SharingRegion, displayFrame: CGRect, selectionFill: Bool = true) {
        if outlineDisplayFrame != displayFrame { hideOutline() }
        let frame = Self.screenFrame(for: region.bounds, on: displayFrame)
        let panel: NSPanel
        if let outline { panel = outline }
        else {
            let screen = NSScreen.screens.first { $0.frame == displayFrame }
            panel = RegionOutlinePanel(contentRect: Self.contentRect(for: frame, on: screen),
                            styleMask: [.borderless, .nonactivatingPanel],
                            backing: .buffered, defer: false,
                            screen: screen)
            panel.title = "Annotator region outline"
            panel.isReleasedWhenClosed = false
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.hasShadow = false
            panel.ignoresMouseEvents = true
            panel.hidesOnDeactivate = false
            panel.sharingType = .readOnly
            panel.level = .screenSaver
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .canJoinAllApplications]
            panel.contentView = RegionOutlineView(frame: CGRect(origin: .zero, size: frame.size))
            outline = panel
            outlineDisplayFrame = displayFrame
        }
        if let view = panel.contentView as? RegionOutlineView {
            view.showsSelectionFill = selectionFill
            view.needsDisplay = true
        }
        panel.setFrame(frame, display: true)
        panel.orderFrontRegardless()
    }

    func hideOutline() {
        outline?.orderOut(nil)
        outline?.contentView = nil
        outline?.close()
        outline = nil
        outlineDisplayFrame = nil
    }

    func choose(on displays: [RegionSharingDisplay], preferredDisplayID: CGDirectDisplayID,
                completed: @escaping (RegionSelectionResult) -> Void) {
        cancelSelection()
        for display in displays {
            let panel = makeSelectionPanel(on: display.frame)
            let view = RegionSelectionView(frame: CGRect(origin: .zero, size: display.frame.size))
            view.completed = { [weak self] bounds in
                self?.cancelSelection()
                completed(bounds.map { .selected(displayID: display.id, bounds: $0) } ?? .cancelled)
            }
            view.onCursorExit = { [weak self] in
                let point = NSEvent.mouseLocation
                (self?.cursor(at: point, windowNumber: NSWindow.windowNumber(
                    at: point, belowWindowWithWindowNumber: 0)) ?? .arrow).set()
            }
            panel.contentView = view
            panel.makeFirstResponder(view)
            selections[display.id] = panel
        }
        for panel in selections.values { panel.orderFrontRegardless() }
        selections[preferredDisplayID]?.makeKey()
        for panel in selections.values {
            panel.onResignKey = { [weak self, weak panel] in
                // AppKit resigns the old panel before making the destination key.
                DispatchQueue.main.async { [weak self, weak panel] in
                    guard let self, let panel,
                          selections.values.contains(where: { $0 === panel }),
                          !selections.values.contains(where: { $0.isKeyWindow }) else { return }
                    cancelSelection()
                    completed(.focusLost)
                }
            }
        }
        let point = NSEvent.mouseLocation
        cursor(at: point, windowNumber: NSWindow.windowNumber(
            at: point, belowWindowWithWindowNumber: 0))?.set()
    }

    func cursor(at point: CGPoint, windowNumber: Int) -> NSCursor? {
        selections.values.contains {
            $0.windowNumber == windowNumber && $0.isVisible &&
            !$0.ignoresMouseEvents && $0.frame.contains(point)
        } ? .crosshair : nil
    }

    private func makeSelectionPanel(on displayFrame: CGRect) -> RegionSelectionPanel {
        let screen = NSScreen.screens.first { $0.frame == displayFrame }
        let panel = RegionSelectionPanel(contentRect: Self.contentRect(for: displayFrame, on: screen),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false,
            screen: screen)
        panel.setFrame(displayFrame, display: false)
        panel.title = "Choose a sharing region"
        panel.isReleasedWhenClosed = false
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.sharingType = .readOnly
        panel.acceptsMouseMovedEvents = true
        panel.level = .screenSaver
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .canJoinAllApplications]
        return panel
    }

    func cancelSelection() {
        let panels = Array(selections.values)
        selections.removeAll()
        for panel in panels { panel.onResignKey = nil }
        for panel in panels {
            panel.ignoresMouseEvents = true
            panel.orderOut(nil)
            panel.contentView = nil
            panel.close()
        }
        if !panels.isEmpty { NSCursor.arrow.set() }
    }

    func close() { cancelSelection(); hideOutline() }

    static func contentRect(for frame: CGRect, on screen: NSScreen?) -> CGRect {
        guard let screen else { return frame }
        // NSWindow's screen initializer takes screen-local coordinates; setFrame takes global coordinates.
        return frame.offsetBy(dx: -screen.frame.minX, dy: -screen.frame.minY)
    }

    static func screenFrame(for region: Rect, on displayFrame: CGRect) -> CGRect {
        CGRect(x: displayFrame.minX + region.x, y: displayFrame.maxY - region.maxY,
               width: region.width, height: region.height)
    }
}

@MainActor
final class RegionOutlineView: NSView {
    static let fillOpacity = 0.08
    var showsSelectionFill = true
    var effectiveFillOpacity: Double { showsSelectionFill ? Self.fillOpacity : 0 }
    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.current?.cgContext.clear(bounds)
        if showsSelectionFill {
            NSColor.controlAccentColor.withAlphaComponent(Self.fillOpacity).setFill()
            bounds.fill()
        }
        NSColor.controlAccentColor.withAlphaComponent(0.95).setStroke()
        let border = NSBezierPath(rect: bounds.insetBy(dx: 1, dy: 1))
        border.lineWidth = 2
        border.stroke()
    }
}

private final class RegionOutlinePanel: NSPanel {
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
}

private final class RegionSelectionPanel: NSPanel {
    var onResignKey: (() -> Void)?
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
    override func resignKey() {
        super.resignKey()
        onResignKey?()
    }
}

@MainActor
final class RegionSelectionView: NSView {
    var completed: ((Rect?) -> Void)?
    var onCursorExit: (() -> Void)?
    private var cursorTracking: NSTrackingArea?
    private var start: Point?
    private var selected: Rect?
    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() { addCursorRect(bounds, cursor: .crosshair) }
    override func updateTrackingAreas() {
        if let cursorTracking { removeTrackingArea(cursorTracking) }
        let tracking = NSTrackingArea(rect: .zero,
            options: [.inVisibleRect, .activeAlways, .cursorUpdate, .mouseEnteredAndExited, .mouseMoved],
            owner: self)
        addTrackingArea(tracking)
        cursorTracking = tracking
        super.updateTrackingAreas()
    }
    override func cursorUpdate(with event: NSEvent) { NSCursor.crosshair.set() }
    override func mouseEntered(with event: NSEvent) { NSCursor.crosshair.set() }
    override func mouseMoved(with event: NSEvent) { NSCursor.crosshair.set() }
    override func mouseExited(with event: NSEvent) { onCursorExit?() }
    override func mouseDown(with event: NSEvent) {
        start = Point(convert(event.locationInWindow, from: nil))
        selected = nil
        needsDisplay = true
    }
    override func mouseDragged(with event: NSEvent) {
        guard let start else { return }
        let location = convert(event.locationInWindow, from: nil)
        let point = Point(min(max(0, location.x), bounds.width), min(max(0, location.y), bounds.height))
        selected = Rect(from: start, to: point)
        needsDisplay = true
    }
    override func mouseUp(with event: NSEvent) {
        mouseDragged(with: event)
        completed?(selected)
    }
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { completed?(nil) } else { super.keyDown(with: event) }
    }
    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.28).setFill()
        let shade = NSBezierPath(rect: bounds)
        if let selected { shade.appendRect(selected.cgRect) }
        shade.windingRule = .evenOdd
        shade.fill()
        if let selected {
            NSColor.controlAccentColor.setStroke()
            let border = NSBezierPath(rect: selected.cgRect)
            border.lineWidth = 2
            border.stroke()
        }
        let text = "Drag a rectangle to choose the region. Escape cancels."
        text.draw(at: CGPoint(x: 24, y: 24), withAttributes: [
            .font: NSFont.systemFont(ofSize: 16, weight: .medium), .foregroundColor: NSColor.white
        ])
    }
}
