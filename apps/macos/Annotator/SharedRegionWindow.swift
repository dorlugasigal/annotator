import AppKit
import CoreVideo
import AnnotationCore
import PresentationRendering

@MainActor
final class SharedRegionWindow: NSObject, NSWindowDelegate {
    static var title: String {
        Bundle.main.bundleIdentifier?.hasSuffix(".dev") == true
            ? "Annotator Dev - Shared Region" : "Annotator - Shared Region"
    }
    let window: NSWindow
    let content: SharedRegionView
    var onClosed: (() -> Void)?
    private var fitted: (region: SharingRegion, display: CGRect)?

    init(region: SharingRegion, displayFrame: CGRect) throws {
        content = try SharedRegionView(region: region)
        window = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 720, height: 440),
            styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = Self.title
        window.isReleasedWhenClosed = false
        window.isRestorable = false
        window.isOpaque = true
        window.backgroundColor = .black
        window.sharingType = .readOnly
        window.contentMinSize = CGSize(width: SharingRegion.outputMinimumWidth, height: SharingRegion.outputMinimumHeight)
        window.contentView = content
        window.delegate = self
        try fit(region: region, displayFrame: displayFrame)
    }

    /// Meeting apps send the window at its own size, so it shows the region at the region's size, or as large as fits
    /// on its display, beside the region when there is room. It keeps the size people give it until the region changes.
    func fit(region: SharingRegion, displayFrame: CGRect) throws {
        if let fitted, fitted.region == region, fitted.display == displayFrame { return }
        let visible = NSScreen.screens.first { $0.frame == displayFrame }?.visibleFrame ?? displayFrame
        let probe = CGRect(x: 0, y: 0, width: 100, height: 100)
        let outer = window.frameRect(forContentRect: probe)
        let insets = FrameInsets(top: outer.maxY - probe.maxY, left: probe.minX - outer.minX,
                                 bottom: probe.minY - outer.minY, right: outer.maxX - probe.maxX)
        let available = Rect(x: visible.minX - displayFrame.minX, y: displayFrame.maxY - visible.maxY,
                             width: visible.width, height: visible.height)
        let content = RegionSharingGuide.screenFrame(
            for: try region.outputContentFrame(available: available, frame: insets), on: displayFrame)
        let whole = CGRect(x: content.minX.rounded(), y: (content.maxY - content.height.rounded(.down)).rounded(),
                           width: content.width.rounded(.down), height: content.height.rounded(.down))
        window.setFrame(window.frameRect(forContentRect: whole), display: window.isVisible)
        fitted = (region, displayFrame)
    }

    func show(inBackground: Bool = false) {
        if inBackground { window.orderBack(nil) }
        else { window.makeKeyAndOrderFront(nil) }
    }

    func close() {
        content.cover("Region stopped", generation: content.generation &+ 1)
        window.delegate = nil
        window.orderOut(nil)
        window.contentView = nil
        window.close()
    }

    func windowWillClose(_ notification: Notification) { onClosed?() }
}

@MainActor
final class SharedRegionView: NSView {
    private let pixels: PixelEffectsView
    private let drawings = SharedRegionDrawingLayer()
    private let pointer = SharedRegionPointerView()
    private let coverView = NSView()
    private let coverText = NSTextField(labelWithString: "Starting the region...")
    private(set) var workspaceTransition: SharedWorkspaceTransition?
    private var transitionCompletionGeneration: UInt64?
    private(set) var region: SharingRegion
    private(set) var generation: UInt64 = 0
    var onPresented: ((UInt64) -> Void)?
    var onFailure: ((Error) -> Void)?
    var onFrameMetrics: ((PixelFrameMetrics) -> Void)? {
        didSet { pixels.onFrameMetrics = onFrameMetrics }
    }
    var drawingRenderCount: Int { drawings.renderCount }
    var smoothDownscaling: Bool { pixels.smoothDownscaling }
    var isCovered: Bool { !coverView.isHidden }
    var contentFrame: CGRect { pixels.frame }
    override var isOpaque: Bool { true }
    override var isFlipped: Bool { true }

    init(region: SharingRegion) throws {
        self.region = region
        pixels = try PixelEffectsView(renderingFrame: CGRect(x: 0, y: 0, width: 720, height: 440))
        pixels.smoothDownscaling = true
        super.init(frame: CGRect(x: 0, y: 0, width: 720, height: 440))
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        autoresizingMask = [.width, .height]
        addSubview(pixels)
        addSubview(pointer)
        coverView.wantsLayer = true
        coverView.layer?.backgroundColor = NSColor.black.cgColor
        coverText.textColor = .white
        coverText.font = .systemFont(ofSize: 18, weight: .medium)
        coverText.alignment = .center
        coverText.maximumNumberOfLines = 4
        coverView.addSubview(coverText)
        addSubview(coverView)
        pixels.onFailure = { [weak self] in self?.onFailure?($0) }
        pixels.onPresented = { [weak self] completed in
            guard let self, completed == generation else { return }
            if transitionCompletionGeneration == completed {
                workspaceTransition?.cancel()
                workspaceTransition = nil
                transitionCompletionGeneration = nil
            }
            coverView.isHidden = true
            onPresented?(completed)
        }
        setAccessibilityElement(true)
        setAccessibilityRole(.image)
        setAccessibilityLabel("Shared region. View only.")
    }

    required init?(coder: NSCoder) { fatalError("Programmatic view only") }

    override func layout() {
        super.layout()
        guard bounds.width > 0, bounds.height > 0 else { return }
        do {
            let frame = try region.aspectFit(in: Rect(bounds)).cgRect
            pixels.frame = frame
            pointer.frame = frame
            if let workspaceTransition {
                workspaceTransition.frame = frame
                workspaceTransition.bounds = workspaceTransition.screenRegion
            }
            coverView.frame = bounds
            coverText.frame = CGRect(x: 24, y: (bounds.height - 100) / 2,
                                     width: max(0, bounds.width - 48), height: 100)
        } catch { onFailure?(error) }
    }

    func configure(region: SharingRegion) {
        self.region = region
        needsLayout = true
    }

    func installWorkspaceTransition(_ transition: SharedWorkspaceTransition) {
        workspaceTransition?.cancel()
        workspaceTransition = transition
        transitionCompletionGeneration = nil
        addSubview(transition, positioned: .above, relativeTo: pixels)
        needsLayout = true
        layoutSubtreeIfNeeded()
    }

    func holdWorkspaceTransition(generation: UInt64) {
        self.generation = generation
        transitionCompletionGeneration = nil
    }

    func present(_ buffer: CVPixelBuffer, sourceBounds: Rect, sourceSize: CGSize,
                 elements: [Element], background: RGBA?, screenOverlay: PixelScreenOverlay? = nil,
                 generation: UInt64, completingWorkspaceTransition: Bool = false) throws {
        self.generation = generation
        transitionCompletionGeneration = completingWorkspaceTransition ? generation : nil
        layoutSubtreeIfNeeded()
        guard pixels.bounds.width > 0, pixels.bounds.height > 0 else { throw RenderFailure.invalidSize }
        let layer = try drawings.image(elements: elements, region: sourceBounds, logicalSize: sourceSize,
            pixelSize: CGSize(width: CVPixelBufferGetWidth(buffer), height: CVPixelBufferGetHeight(buffer)),
            background: background)
        let viewport = try Viewport(output: Rect(pixels.bounds), source: sourceBounds)
        pixels.present(buffer, viewport: viewport, sourceSize: sourceSize,
            effects: elements.filter(\.tool.isProtection), generation: generation,
            includeImageLayers: false, drawingLayer: layer, privacyRegion: sourceBounds,
            screenOverlay: screenOverlay, drawImmediately: true)
    }

    func configurePointer(model: WorkspaceModel, sourceFrame: CGRect, laser: DrawingCanvas? = nil) {
        pointer.model = model
        pointer.laser = laser
        pointer.sourceFrame = sourceFrame
        pointer.region = region.bounds
        pointer.needsDisplay = true
    }

    func refreshPointer() { pointer.needsDisplay = true }

    func cover(_ message: String, generation: UInt64, preservingWorkspaceTransition: Bool = false) {
        self.generation = generation
        if !preservingWorkspaceTransition {
            workspaceTransition?.cancel()
            workspaceTransition = nil
        }
        transitionCompletionGeneration = nil
        coverText.stringValue = message
        coverView.isHidden = false
        pixels.clearFrame()
        drawings.reset()
        pointer.model = nil
        pointer.needsDisplay = true
        setAccessibilityValue(message)
        coverView.needsDisplay = true
    }
}

@MainActor
private final class SharedRegionPointerView: NSView {
    weak var model: WorkspaceModel?
    /// The shared display's canvas, whose laser pointer trail shows in the output too.
    weak var laser: DrawingCanvas?
    var sourceFrame = CGRect.zero
    var region = Rect.zero
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.clear(bounds)
        guard let model, region.width > 0, region.height > 0 else { return }
        let location = model.screenPointer()
        let local = Point(location.x - sourceFrame.minX, sourceFrame.maxY - location.y)
        context.saveGState()
        defer { context.restoreGState() }
        context.scaleBy(x: bounds.width / region.width, y: bounds.height / region.height)
        context.translateBy(x: -region.x, y: -region.y)
        if !model.workspaceHidden && model.spotlight {
            do { try SpotlightRenderer.draw(in: context, bounds: region.cgRect, center: local.cgPoint, radius: model.spotlightRadius) }
            catch { model.report(error) }
        }
        if !model.workspaceHidden, let canvas = laser, !canvas.laserTrail.isEmpty {
            context.saveGState()
            if let viewport = canvas.session?.presentation?.screenViewport {
                context.scaleBy(x: viewport.output.width / viewport.source.width, y: viewport.output.height / viewport.source.height)
                context.translateBy(x: -viewport.source.x, y: -viewport.source.y)
            }
            LaserTrailRenderer.draw(canvas.laserTrail, at: CACurrentMediaTime(), in: context)
            context.restoreGState()
        }
        if !model.workspaceHidden && model.cursorHighlightVisible {
            let click = model.cursorClickLocation.map { CGPoint(x: $0.x - sourceFrame.minX, y: sourceFrame.maxY - $0.y) }
            CursorHighlightRenderer.draw(model.cursorHighlightSettings, center: local.cgPoint,
                clickProgress: model.pointerClickAnimation.progress, clickCenter: click,
                reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion, in: context)
        }
        if region.contains(local) {
            let cursor = NSCursor.arrow
            cursor.image.draw(in: CGRect(x: local.x - cursor.hotSpot.x, y: local.y - cursor.hotSpot.y,
                width: cursor.image.size.width, height: cursor.image.size.height),
                from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        }
    }
}
