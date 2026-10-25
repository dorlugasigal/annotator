import AppKit
import SwiftUI
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

/// Test-only host for deterministic canvas pixels and input, never included in the app target.
@MainActor
final class NativeCanvasFixture: NSObject, NSWindowDelegate {
    let model: WorkspaceModel
    let session: OverlaySession
    let scroll = NSScrollView()
    private var toolbar: NSHostingView<ToolbarView>?
    private var toolbarWidth: NSLayoutConstraint?
    private var toolbarHeight: NSLayoutConstraint?
    private var measuredHeight: CGFloat = 0
    private var closed = false
    private(set) var toolsVisible = true

    init(image: CGImage, settingsOwner: WorkspaceModel? = nil, defaults: UserDefaults = .standard,
         pasteboard: NSPasteboard = .general) {
        model = WorkspaceModel(defaults: settingsOwner?.preferenceDefaults ?? defaults, imagePasteboard: pasteboard)
        model.connectSettingsOwner(settingsOwner)
        // Fixture tests were written for quick controls, the default before full properties; keep their meaning.
        if model.preferences.inspectorPinned == nil, model.preferenceDefaults !== UserDefaults.standard {
            model.setInspectorPinned(false)
        }
        let visible = NSScreen.main?.visibleFrame ?? CGRect(x: 0, y: 0, width: 1200, height: 900)
        let frame = CGRect(x: visible.midX - min(1100, visible.width - 80)/2,
                           y: visible.midY - min(820, visible.height - 80)/2,
                           width: min(1100, visible.width - 80), height: min(820, visible.height - 80))
        session = OverlaySession(referenceImage: image, model: model, frame: frame)
        super.init()
        session.panel.delegate = self
        model.adoptReferenceSession(session)
        scroll.documentView = session.canvas
        scroll.hasHorizontalScroller = true
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.allowsMagnification = true
        scroll.minMagnification = 0.1
        scroll.maxMagnification = 4
        scroll.drawsBackground = true
        scroll.backgroundColor = .underPageBackgroundColor
        let view = NSHostingView(rootView: ToolbarView(model: model,
            availableSize: CGSize(width: frame.width - 24, height: frame.height), showInspector: false))
        view.sizingOptions = [.intrinsicContentSize]
        toolbar = view
        let container = NSView(frame: CGRect(origin: .zero, size: frame.size))
        for child in [scroll, view] { child.translatesAutoresizingMaskIntoConstraints = false; container.addSubview(child) }
        measuredHeight = ceil(view.fittingSize.height)
        let width = view.widthAnchor.constraint(equalToConstant: ToolbarToolLayout.horizontalWidth(tools: model.toolbarTools,
            available: frame.width - 24, propertiesVisible: ToolbarPropertiesMode(model: model) != .toolsOnly))
        let height = view.heightAnchor.constraint(equalToConstant: measuredHeight)
        toolbarWidth = width
        toolbarHeight = height
        NSLayoutConstraint.activate([
            scroll.topAnchor.constraint(equalTo: container.topAnchor),
            scroll.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            scroll.bottomAnchor.constraint(equalTo: view.topAnchor, constant: -6),
            view.centerXAnchor.constraint(equalTo: container.centerXAnchor),
            view.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -8), width, height,
        ])
        model.onToolbarSizeChanged = { [weak self] size in self?.updateToolbarSize(size) }
        model.onHideToolbar = { [weak self] in self?.setToolsVisible(false) }
        model.onDismissToolbar = { [weak self] in self?.setToolsVisible(false) }
        model.onResumeDrawingRequested = { [weak self] in self?.setToolsVisible(true) }
        session.panel.contentView = container
        container.layoutSubtreeIfNeeded()
        setZoom(min(1, max(0.1, min((scroll.contentSize.width - 24)/Double(image.width),
                                  scroll.contentSize.height/Double(image.height)))))
    }
    func setZoom(_ value: Double) {
        scroll.setMagnification(min(4, max(0.1, value)),
            centeredAt: CGPoint(x: session.canvas.bounds.midX, y: session.canvas.bounds.midY))
    }
    func show() { session.panel.makeKeyAndOrderFront(nil); session.panel.makeFirstResponder(session.canvas) }
    func setToolsVisible(_ visible: Bool) {
        toolsVisible = visible
        toolbar?.isHidden = !visible
        toolbarHeight?.constant = visible ? measuredHeight : 0
        if visible { if model.mode != .draw { model.beginDrawing() } }
        else { model.interact() }
        session.panel.contentView?.layoutSubtreeIfNeeded()
    }
    func windowDidResize(_ notification: Notification) {
        guard let container = session.panel.contentView, let toolbar else { return }
        let size = CGSize(width: container.bounds.width - 24, height: container.bounds.height)
        guard toolbar.rootView.availableSize != size else { return }
        toolbarWidth?.constant = ToolbarToolLayout.horizontalWidth(tools: model.toolbarTools,
            available: size.width, propertiesVisible: ToolbarPropertiesMode(model: model) != .toolsOnly)
        toolbar.rootView = ToolbarView(model: model, availableSize: size, showInspector: false)
        updateToolbarSize(toolbar.fittingSize)
    }
    private func updateToolbarSize(_ size: CGSize) {
        guard size.height.isFinite, size.height > 0 else { return }
        measuredHeight = ceil(size.height)
        let height = toolsVisible ? measuredHeight : 0
        let width = ToolbarToolLayout.horizontalWidth(tools: model.toolbarTools,
            available: toolbar?.rootView.availableSize.width ?? size.width,
            propertiesVisible: ToolbarPropertiesMode(model: model) != .toolsOnly)
        guard toolbarHeight?.constant != height || toolbarWidth?.constant != width else { return }
        toolbarHeight?.constant = height
        toolbarWidth?.constant = width
        session.panel.contentView?.layoutSubtreeIfNeeded()
    }
    func output() throws -> Data {
        session.canvas.commitTextIfNeeded()
        guard let image = session.sourceImage else { throw RenderFailure.imageUnavailable }
        return try PNGExporter.encode(SnapshotExporter.image(source: image, elements: session.document.elements))
    }
    func close() {
        guard !closed else { return }
        closed = true
        model.closeAll()
        session.panel.close()
    }
    func windowWillClose(_ notification: Notification) { close() }
}
