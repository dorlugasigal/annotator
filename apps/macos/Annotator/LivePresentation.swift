import AppKit
import QuartzCore
import NativePlatform
import AnnotationCore
import PresentationRendering
import MotionCore

enum PresentationWindowLevels {
    static func base(screenZoom: Bool) -> Int {
        screenZoom ? NSWindow.Level.statusBar.rawValue + 1 : NSWindow.Level.floating.rawValue - 2
    }
    static func toolbar(screenZoom: Bool) -> NSWindow.Level { NSWindow.Level(rawValue: base(screenZoom: screenZoom) + 5) }
}
@MainActor
final class LivePresentation {
    private weak var session: OverlaySession?
    private weak var model: WorkspaceModel?
    weak var regionSharing: RegionSharingController?
    private var capture = DisplayCapture()
    private var reader: Task<Void, Never>?
    private var lastFrame: CaptureFrame?
    private var effectsPanel: NSPanel?
    private var effectsView: PixelEffectsView?
    private var lensPanel: NSPanel?
    private var lensView: PixelEffectsView?
    private var lensCover: NSView?
    private var lensGuide: LensGuideView?
    private var screenPanel: NSPanel?
    private var screenView: PixelEffectsView?
    private var screenCover: NSView?
    private var selectionPanel: NSPanel?
    private var selectionView: SelectionOverlayView?
    private var zoomGestures: ZoomGestureMonitor?
    private var gestureStartAttempted = false
    private var readinessTask: Task<Void, Never>?
    private var hasPresented = false
    private var pointerMonitor: Any?
    private var localMonitor: Any?
    private var requested = false
    private var blocked = false
    private var version: UInt64 = 0
    private var captureEpoch: UInt64 = 0
    private var effectTokens: [UUID] = []
    private var pixelRegionTokens: [UUID] = []
    private var previousSettings = PresentationSettings()
    private var frozenViewport: Viewport?
    private var displayedMode = PresentationSettings.Mode.off
    private var displayedMagnification = 2.0
    private var zoomRamp: ScreenZoomRamp?
    private let zoomTicker = MotionTicker()
    /// The displayed mode. Setting it directly ends any zoom ramp at its final state.
    var presentationMode: PresentationSettings.Mode {
        get { displayedMode }
        set { endZoomRamp(); displayedMode = newValue }
    }
    /// The displayed factor that drawing, hit-testing and captured pixels share.
    /// Setting it directly ends any zoom ramp at its final state.
    var magnification: Double {
        get { displayedMagnification }
        set { endZoomRamp(); displayedMagnification = newValue }
    }
    var isRampingScreenZoom: Bool { zoomRamp != nil }
    var isExitingScreenZoom: Bool { zoomRamp?.exitZoom != nil }
    /// The factor on screen while a zoom ramp runs, so interruptions continue from what the person sees.
    var rampingScreenZoomFactor: Double? { zoomRamp != nil && displayedMode == .screen ? displayedMagnification : nil }
    var displayedScreenZoomFactor: Double? { displayedMode == .screen ? displayedMagnification : nil }
    var selectionOverlayLevel: NSWindow.Level? { selectionPanel?.level }
    var effectsOverlayLevel: NSWindow.Level? { effectsPanel?.level }
    var usesPointerOverlay: Bool { selectionPanel != nil }
    var pointerOverlayRenderPassCount: UInt64 { selectionView?.renderPassCount ?? 0 }
    var selectionOverlayIgnoresMouseEvents: Bool { selectionPanel?.ignoresMouseEvents ?? true }
    var hasRequestedCapture: Bool { requested }
    private(set) var captureAttemptCount = 0
    private var needsLocalPixels: Bool {
        model?.workspaceHidden != true &&
            (presentationMode != .off || session?.document.elements.contains(where: { $0.tool.isPixelEffect }) == true)
    }

    var screenViewport: Viewport? {
        guard model?.workspaceHidden != true, presentationMode == .screen, let session else { return nil }
        if let frozenViewport { return frozenViewport }
        let size = session.canvas.bounds.size
        let pointer = NSEvent.mouseLocation
        let local = Point(pointer.x - session.panel.frame.minX, session.panel.frame.maxY - pointer.y)
        do {
            return try Viewport.cursorAnchored(display: Rect(x: 0, y: 0, width: size.width, height: size.height),
                                               factor: magnification, cursor: local)
        } catch { model?.report(error); return nil }
    }

    init(session: OverlaySession, model: WorkspaceModel) { self.session = session; self.model = model }

    func transitionOverlay(elements: [Element]? = nil) throws -> CGImage? {
        guard let lastFrame, let session, session.document.elements.contains(where: { $0.tool.isPixelEffect }) else { return nil }
        return try SnapshotExporter.effectOverlay(buffer: lastFrame.pixelBuffer, logicalSize: session.canvas.bounds.size,
                                                  elements: elements ?? session.document.elements)
    }

    func refresh() {
        guard let session, session.sourceImage == nil else { return }
        if model?.workspaceHidden == true {
            clearWorkspaceViews()
            guard regionSharing?.usesCapture(from: session) == true else { stop(); return }
            guard !blocked else { return }
            if !requested { start() }
            render()
            return
        }
        let canvasLevel = NSWindow.Level(rawValue: PresentationWindowLevels.base(screenZoom: presentationMode == .screen) + 1)
        if session.panel.level != canvasLevel { session.panel.level = canvasLevel }
        refreshSelectionOverlay()
        let masks = session.document.elements.filter(\.tool.isProtection)
        let tokens = session.document.elements.filter { $0.tool.isProtection || $0.tool == .image }.map(\.geometryToken)
        let regions = masks.filter(\.tool.isPixelEffect).map(\.geometryToken)
        let settings = PresentationSettings(mode: presentationMode, zoom: magnification)
        if effectTokens != tokens || previousSettings != settings {
            if pixelRegionTokens != regions { session.effectsReady = false }
            if previousSettings.mode != settings.mode {
                lensCover?.isHidden = false
                screenCover?.isHidden = false
            }
            effectTokens = tokens; pixelRegionTokens = regions
            previousSettings = settings; version &+= 1
        }
        let required = needsLocalPixels || regionSharing?.usesCapture(from: session) == true
        if !required { stop(preserveSelection: true); return }
        guard !blocked else { return }
        if !requested { start() }
        render()
    }

    func retry() { blocked = false; refresh() }
    func retryZoomGestures() { gestureStartAttempted = false; render() }
    func suspend() { blocked = true; stop() }
    func hideWorkspace() { endZoomRamp(); refresh() }

    /// Applies document presentation settings. Logical state changes at once; with `enteringFrom`,
    /// the displayed screen factor ramps to the new zoom, and with `animatesExit`, a visible screen
    /// zoom ramps to 1x before it turns off. Returns whether the displayed mode changed.
    @discardableResult
    func apply(_ settings: PresentationSettings, enteringFrom start: Double? = nil, animatesExit: Bool = false) -> Bool {
        var interruptedExit = false
        if let ramp = zoomRamp {
            if ramp.exitZoom != nil && settings.mode == .off { return false }
            if ramp.exitZoom == nil && start == nil && settings.mode == .screen && settings.zoom == ramp.to { return false }
            zoomRamp = nil
            zoomTicker.stop()
            interruptedExit = ramp.exitZoom != nil && settings.mode != .screen
        }
        defer { if interruptedExit { model?.screenZoomExitFinished() } }
        let previous = displayedMode, current = displayedMagnification
        if previous == .screen, settings.mode == .off, animatesExit, screenZoomIsVisible, current > 1.001 {
            startZoomRamp(ScreenZoomRamp(from: current, to: 1, duration: MotionTiming.zoomOutDuration, curve: Motion.standard,
                                         exitZoom: settings.zoom))
            return false
        }
        displayedMode = settings.mode
        displayedMagnification = settings.zoom
        if settings.mode == .screen, let start, start.isFinite, (1...16).contains(start), abs(start - settings.zoom) > 0.001 {
            displayedMagnification = start
            startZoomRamp(ScreenZoomRamp(from: start, to: settings.zoom, duration: MotionTiming.zoomInDuration,
                                         curve: Motion.enter, exitZoom: nil))
        }
        return previous != displayedMode
    }

    private var screenZoomIsVisible: Bool {
        requested && !blocked && model?.workspaceHidden != true && screenPanel != nil && screenCover?.isHidden == true
    }

    private func startZoomRamp(_ ramp: ScreenZoomRamp) {
        zoomTicker.stop()
        zoomRamp = ramp
        // An entrance waits for the first zoomed frame, so the whole ramp is visible.
        if ramp.exitZoom != nil || screenZoomIsVisible { runZoomRamp() }
    }

    private func runZoomRamp() {
        guard zoomRamp != nil, !zoomTicker.isRunning, let session else { return }
        zoomTicker.start(view: session.canvas) { [weak self] time in self?.stepZoomRamp(at: time) ?? false }
    }

    private func stepZoomRamp(at time: CFTimeInterval) -> Bool {
        guard var ramp = zoomRamp else { return false }
        let began = ramp.began ?? time
        ramp.began = began
        let elapsed = ramp.duration > 0 ? min(1, max(0, (time - began) / ramp.duration)) : 1
        guard elapsed < 1, model?.workspaceHidden != true, !blocked else {
            endZoomRamp()
            refresh()
            session?.canvas.needsDisplay = true
            return false
        }
        zoomRamp = ramp
        displayedMagnification = min(16, max(1, ramp.from + (ramp.to - ramp.from) * Motion.progress(ramp.curve, at: elapsed)))
        refresh()
        session?.canvas.needsDisplay = true
        return true
    }

    /// Points a running zoom-in ramp at a new target. The display keeps easing from what is on
    /// screen instead of dropping back or jumping. Returns false when no zoom-in ramp is running.
    func retargetZoomRamp(to zoom: Double) -> Bool {
        guard let ramp = zoomRamp, ramp.exitZoom == nil, zoom.isFinite else { return false }
        zoomRamp = ScreenZoomRamp(from: ramp.began == nil ? ramp.from : displayedMagnification,
                                  to: min(16, max(1, zoom)), duration: ramp.duration, curve: ramp.curve, exitZoom: nil)
        return true
    }

    /// Lands an unfinished ramp on its final state: the target factor, or zoom off after an exit.
    private func endZoomRamp() {
        guard let ramp = zoomRamp else { return }
        zoomRamp = nil
        zoomTicker.stop()
        if let exitZoom = ramp.exitZoom {
            displayedMode = .off
            displayedMagnification = exitZoom
            model?.screenZoomExitFinished()
        } else { displayedMagnification = ramp.to }
    }
    func freezeViewportForText() -> Viewport? {
        let current = screenViewport
        frozenViewport = current
        return current
    }
    func releaseTextViewport() { frozenViewport = nil }

    func stop(preserveSelection: Bool = false) {
        endZoomRamp()
        let sharing = regionSharing
        regionSharing = nil
        sharing?.captureStopped("Live screen access stopped. Check the region, then start sharing.")
        if !preserveSelection { clearSelectionOverlay() }
        clearScreenZoom()
        guard requested || effectsPanel != nil || lensPanel != nil else { return }
        requested = false; captureEpoch &+= 1; version &+= 1
        reader?.cancel(); reader = nil; lastFrame = nil
        readinessTask?.cancel(); readinessTask = nil
        if let pointerMonitor { NSEvent.removeMonitor(pointerMonitor) }
        if let localMonitor { NSEvent.removeMonitor(localMonitor) }
        pointerMonitor = nil; localMonitor = nil
        effectsView?.clearFrame(); lensView?.clearFrame()
        effectsPanel?.orderOut(nil); lensPanel?.orderOut(nil)
        effectsPanel = nil; effectsView = nil
        lensPanel = nil; lensView = nil; lensCover = nil; lensGuide = nil
        session?.effectsReady = false
        session?.canvas.needsDisplay = true
        model?.objectWillChange.send()
        Task { [capture, weak model] in
            do { try await capture.stop() } catch { model?.report(error) }
        }
    }

    func refreshSelectionOverlay() {
        guard let session, let model, !model.workspaceHidden, session.sourceImage == nil,
              presentationMode == .screen || model.spotlight || model.cursorHalo ||
                (session.document.elements.contains(where: { $0.tool.isPixelEffect })
                  && ((model.mode == .draw && !session.document.selection.isEmpty) || model.spotlight || model.cursorHalo
                      || session.document.elements.contains(where: { $0.tool == .cover }))) else {
            clearSelectionOverlay(); return
        }
        if selectionPanel == nil {
            let panel = makePanel(frame: session.panel.frame)
            let view = SelectionOverlayView(frame: CGRect(origin: .zero, size: session.canvas.bounds.size))
            view.autoresizingMask = [.width, .height]
            view.session = session; view.model = model
            panel.contentView = view
            selectionPanel = panel; selectionView = view
        }
        let level = NSWindow.Level(rawValue: PresentationWindowLevels.base(screenZoom: presentationMode == .screen) + 3)
        if selectionPanel?.level != level { selectionPanel?.level = level }
        if selectionPanel?.frame != session.panel.frame { selectionPanel?.setFrame(session.panel.frame, display: false) }
        selectionView?.needsDisplay = true
        if selectionPanel?.isVisible != true { selectionPanel?.orderFrontRegardless() }
    }

    private func clearSelectionOverlay() {
        selectionPanel?.orderOut(nil); selectionPanel?.contentView = nil
        selectionPanel = nil; selectionView = nil
    }

    private func clearWorkspaceViews() {
        clearSelectionOverlay()
        clearScreenZoom()
        effectsView?.clearFrame(); lensView?.clearFrame()
        effectsPanel?.orderOut(nil); lensPanel?.orderOut(nil)
        effectsPanel = nil; effectsView = nil
        lensPanel = nil; lensView = nil; lensCover = nil; lensGuide = nil
        session?.effectsReady = false
    }

    private func clearScreenZoom() {
        let ownedZoomControls = zoomGestures != nil || gestureStartAttempted || screenPanel != nil
        frozenViewport = nil
        zoomGestures?.stop(); zoomGestures = nil
        gestureStartAttempted = false
        if ownedZoomControls { model?.noteZoomGestures(available: false) }
        screenView?.clearFrame(); screenPanel?.orderOut(nil)
        screenPanel = nil; screenView = nil; screenCover = nil
        if let session {
            let level = NSWindow.Level(rawValue: PresentationWindowLevels.base(screenZoom: false) + 1)
            if presentationMode != .screen && session.panel.level != level { session.panel.level = level }
        }
    }

    private func start() {
        guard let session, let number = session.panel.screen?.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber,
              let identifier = Bundle.main.bundleIdentifier else { fail(CaptureFailure.displayUnavailable); return }
        guard DisplayCapture.hasPermission else { fail(CaptureFailure.permissionRequired); return }
        requested = true; captureEpoch &+= 1; hasPresented = false
        let epoch = captureEpoch
        readinessTask = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(8)) } catch { return }
            guard let self, requested, captureEpoch == epoch, needsLocalPixels, !hasPresented else { return }
            fail(CaptureFailure.invalidFrame)
        }
        let capture = DisplayCapture()
        captureAttemptCount += 1
        self.capture = capture
        reader = Task { [weak self, capture] in
            guard let self else { return }
            do {
                let frames = try await capture.start(displayID: number.uint32Value, bundleIdentifier: identifier)
                guard requested, captureEpoch == epoch, !Task.isCancelled else { return }
                for try await frame in frames {
                    guard requested, captureEpoch == epoch, !Task.isCancelled else { break }
                    lastFrame = frame
                    model?.noteCaptureFrame()
                    render()
                }
                if requested && captureEpoch == epoch && !Task.isCancelled { fail(CaptureFailure.invalidFrame) }
            } catch {
                if requested && captureEpoch == epoch && !Task.isCancelled { fail(error) }
            }
        }
        pointerMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.mouseMoved, .leftMouseDragged, .rightMouseDragged]) {
            [weak self] _ in MainActor.assumeIsolated { self?.pointerMoved() }
        }
        localMonitor = NSEvent.addLocalMonitorForEvents(matching: [.mouseMoved, .leftMouseDragged]) {
            [weak self] event in MainActor.assumeIsolated { self?.pointerMoved() }; return event
        }
    }

    private func pointerMoved() {
        regionSharing?.refreshPointer()
        if presentationMode == .screen {
            render()
            session?.canvas.needsDisplay = true
            selectionView?.needsDisplay = true
        } else { renderLens() }
    }

    private func render() {
        guard requested, let frame = lastFrame, let session else { return }
        regionSharing?.receive(frame, from: session)
        guard model?.workspaceHidden != true else { return }
        do {
            let masks = session.document.elements.filter(\.tool.isProtection)
            let size = session.canvas.bounds.size
            try renderScreenZoom(frame: frame, size: size)
            if masks.contains(where: { $0.tool.isPixelEffect }) {
                if effectsPanel == nil {
                    let panel = makePanel(frame: session.panel.frame)
                    let view = try PixelEffectsView(renderingFrame: CGRect(origin: .zero, size: size))
                    view.autoresizingMask = [.width, .height]
                    view.onFailure = { [weak self] in self?.fail($0) }
                    view.onPresented = { [weak self, weak session] completed in
                        guard let self, requested, completed == version else { return }
                        hasPresented = true
                        let wasReady = session?.effectsReady ?? false
                        session?.effectsReady = true
                        if !wasReady {
                            model?.objectWillChange.send()
                            session?.canvas.needsDisplay = true
                        }

                    }
                    panel.contentView = view
                    effectsPanel = panel; effectsView = view
                    panel.orderFrontRegardless()
                }
                let effectLevel = NSWindow.Level(rawValue: PresentationWindowLevels.base(screenZoom: presentationMode == .screen) + 2)
                if effectsPanel?.level != effectLevel { effectsPanel?.level = effectLevel }
                let area = Rect(x: 0, y: 0, width: size.width, height: size.height)
                let viewport = try screenViewport ?? Viewport(output: area, source: area)
                effectsView?.present(frame.pixelBuffer, viewport: viewport, sourceSize: size,
                                     effects: session.document.elements, regionsOnly: true, generation: version)
            } else {
                effectsView?.clearFrame(); effectsPanel?.orderOut(nil)
                effectsPanel = nil; effectsView = nil; session.effectsReady = false
            }
            renderLens()
        } catch { fail(error) }
    }

    func refreshSharedRegion() {
        guard requested, let frame = lastFrame, let session else { return }
        regionSharing?.receive(frame, from: session)
    }

    private func renderScreenZoom(frame: CaptureFrame, size: CGSize) throws {
        guard presentationMode == .screen, let session else { clearScreenZoom(); return }
        guard let viewport = screenViewport else { throw CanvasError.invalidGeometry }
        if zoomGestures == nil && !gestureStartAttempted && ZoomGestureMonitor.hasPermission {
            gestureStartAttempted = true
            let gestures = ZoomGestureMonitor(scroll: { [weak model] in model?.scrollScreenZoom($0) },
                                              exit: { [weak model] in model?.handleZoomEscape() },
                                              failure: { [weak self] in self?.fail($0) },
                                              shouldHandleScroll: { [weak session] event in
                                                  guard let session, let primary = NSScreen.screens.first else { return false }
                                                  return ZoomGestureMonitor.eventIsOnDisplay(event, frame: session.panel.frame,
                                                                                           primaryScreenTop: primary.frame.maxY)
                                              })
            do {
                try gestures.start()
                zoomGestures = gestures
                model?.noteZoomGestures(available: true)
            } catch {
                model?.noteZoomGestures(available: false, error: error)
            }
        }
        if screenPanel == nil {
            let panel = makePanel(frame: session.panel.frame)
            let view = try PixelEffectsView(renderingFrame: CGRect(origin: .zero, size: size))
            view.autoresizingMask = [.width, .height]
            view.onFailure = { [weak self] in self?.fail($0) }
            let container = NSView(frame: view.frame)
            container.addSubview(view)
            let cover = NSView(frame: view.frame)
            cover.wantsLayer = true; cover.layer?.backgroundColor = NSColor.black.cgColor
            cover.autoresizingMask = [.width, .height]
            container.addSubview(cover)
            view.onPresented = { [weak self, weak session] completed in
                guard let self, requested, presentationMode == .screen, completed == version else { return }
                let firstPresentation = screenCover?.isHidden != true
                hasPresented = true; screenCover?.isHidden = true
                model?.noteZoomPresented()
                if firstPresentation { session?.canvas.needsDisplay = true; runZoomRamp() }
            }
            panel.contentView = container
            screenPanel = panel; screenView = view; screenCover = cover
            panel.orderFrontRegardless()
        }
        screenView?.present(frame.pixelBuffer, viewport: viewport, sourceSize: size,
                            effects: session.document.elements.filter(\.tool.isProtection),
                            generation: version, includeImageLayers: false)
    }

    private func renderLens() {
        guard presentationMode == .lens, requested, let frame = lastFrame, let session else {
            lensPanel?.orderOut(nil); return
        }
        let pointer = NSEvent.mouseLocation
        guard session.panel.frame.contains(pointer) else { lensPanel?.orderOut(nil); return }
        do {
            let diameter = 240.0
            if lensPanel == nil {
                let panel = makePanel(frame: CGRect(x: 0, y: 0, width: diameter, height: diameter))
                panel.level = NSWindow.Level(rawValue: NSWindow.Level.floating.rawValue + 1)
                panel.hasShadow = true
                panel.alphaValue = 0
                let view = try PixelEffectsView(renderingFrame: CGRect(x: 0, y: 0, width: diameter, height: diameter))
                view.layer?.cornerRadius = 18; view.layer?.masksToBounds = true
                view.layer?.borderColor = NSColor.controlAccentColor.cgColor; view.layer?.borderWidth = 2
                view.onFailure = { [weak self] in self?.fail($0) }
                view.onPresented = { [weak self] completed in
                    guard let self, requested, presentationMode == .lens, completed == version else { return }
                    hasPresented = true; lensCover?.isHidden = true
                }
                let container = NSView(frame: view.frame)
                container.addSubview(view)
                let cover = NSView(frame: view.frame)
                cover.wantsLayer = true; cover.layer?.backgroundColor = NSColor.black.cgColor
                cover.layer?.cornerRadius = 18
                container.addSubview(cover)
                let guide = LensGuideView(frame: view.frame)
                container.addSubview(guide)
                lensGuide = guide
                panel.contentView = container
                lensCover = cover; lensPanel = panel; lensView = view
            }
            let bounds = session.panel.screen?.visibleFrame ?? session.panel.frame
            let origin = CGPoint(x: min(max(pointer.x + 28, bounds.minX), bounds.maxX - diameter),
                                 y: min(max(pointer.y + 28, bounds.minY), bounds.maxY - diameter))
            if lensPanel?.frame.origin != origin { lensPanel?.setFrameOrigin(origin) }
            if let lensPanel, !lensPanel.isVisible {
                lensPanel.orderFrontRegardless()
                if lensPanel.alphaValue < 1 {
                    NSAnimationContext.runAnimationGroup { context in
                        context.duration = 0.12
                        context.timingFunction = Motion.fade
                        lensPanel.animator().alphaValue = 1
                    }
                }
            }
            let local = Point(pointer.x - session.panel.frame.minX, session.panel.frame.maxY - pointer.y)
            let size = session.canvas.bounds.size, cropSize = diameter / magnification
            let source = Rect(x: max(0, min(local.x - cropSize / 2, size.width - cropSize)),
                              y: max(0, min(local.y - cropSize / 2, size.height - cropSize)),
                              width: min(cropSize, size.width), height: min(cropSize, size.height))
            let target = Rect(x: 0, y: 0, width: diameter, height: diameter)
            let viewport = try Viewport(output: target, source: source)
            lensGuide?.point = viewport.outputPoint(for: local).cgPoint
            lensGuide?.needsDisplay = true
            lensView?.present(frame.pixelBuffer, viewport: viewport,
                              sourceSize: size, effects: session.document.elements, generation: version)
        } catch { fail(error) }
    }

    private struct ScreenZoomRamp {
        let from: Double, to: Double
        let duration: CFTimeInterval
        let curve: CAMediaTimingFunction
        /// Set for a zoom-out ramp: the document zoom to keep once the display reaches 1x and zoom turns off.
        let exitZoom: Double?
        var began: CFTimeInterval?
    }

    @MainActor
    private final class LensGuideView: NSView {
        var point = CGPoint(x: 120, y: 120)
        override var isFlipped: Bool { true }
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
        override func draw(_ dirtyRect: NSRect) {
            let cross = NSBezierPath()
            cross.move(to: NSPoint(x: point.x - 6, y: point.y)); cross.line(to: NSPoint(x: point.x + 6, y: point.y))
            cross.move(to: NSPoint(x: point.x, y: point.y - 6)); cross.line(to: NSPoint(x: point.x, y: point.y + 6))
            NSColor.black.withAlphaComponent(0.7).setStroke(); cross.lineWidth = 3; cross.stroke()
            NSColor.white.setStroke(); cross.lineWidth = 1; cross.stroke()
        }
    }

    private func makePanel(frame: CGRect) -> NSPanel {
        let panel = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false; panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.title = "Annotator live overlay"
        panel.setAccessibilityCustomActions([
            NSAccessibilityCustomAction(name: "Focus drawing tools", handler: { [weak model] in
                MainActor.assumeIsolated { model?.focusDrawingTools() ?? false }
            })
        ])
        panel.ignoresMouseEvents = true; panel.hidesOnDeactivate = false
        panel.level = NSWindow.Level(rawValue: PresentationWindowLevels.base(screenZoom: presentationMode == .screen))
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isReleasedWhenClosed = false
        return panel
    }

    private func fail(_ error: Error) {
        let failedMode = presentationMode
        let sharing = regionSharing
        regionSharing = nil
        sharing?.captureStopped("Screen access failed. \(error.localizedDescription)")
        blocked = true
        session?.document.stopPresentationWithoutHistory()
        presentationMode = .off
        stop()
        model?.synchronize()
        model?.reportCaptureFailure(error, presentationMode: failedMode)
    }
}
