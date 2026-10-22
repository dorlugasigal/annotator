import AppKit
import SwiftUI

@MainActor
final class ToolLibraryWindowController: NSObject, NSWindowDelegate {
    private final class State: ObservableObject {
        @Published var section = ToolLibrarySection.shapes
    }

    private struct Content: View {
        @ObservedObject var model: WorkspaceModel
        @ObservedObject var state: State
        let dismiss: () -> Void

        var body: some View {
            GeometryReader { geometry in
                ToolLibraryPanel(model: model, section: $state.section, dismiss: dismiss,
                                 maximumHeight: geometry.size.height, width: geometry.size.width)
                    .frame(width: geometry.size.width, height: geometry.size.height, alignment: .top)
            }
            .onExitCommand(perform: dismiss)
        }
    }

    private let state = State()
    private weak var model: WorkspaceModel?
    private(set) var window: AnnotationPanel?
    private var savedFrame: CGRect?

    init(model: WorkspaceModel) { self.model = model }

    func show(section: ToolLibrarySection?, near anchor: NSWindow?) {
        guard let model else { return }
        if let section { state.section = section }
        let panel: AnnotationPanel
        if let window { panel = window }
        else {
            panel = AnnotationPanel(contentRect: CGRect(x: 0, y: 0, width: 440, height: 500),
                                    styleMask: [.titled, .closable, .resizable, .nonactivatingPanel],
                                    backing: .buffered, defer: false)
            panel.title = "Tools"
            panel.isReleasedWhenClosed = false
            panel.hidesOnDeactivate = false
            panel.contentMinSize = CGSize(width: 368, height: 260)
            panel.imagePasteModel = model
            panel.delegate = self
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            if let screen = anchor?.screen ?? NSScreen.main {
                panel.maxSize = screen.visibleFrame.size
                var frame = panel.frame
                let source = anchor?.frame ?? screen.visibleFrame
                frame.origin.x = min(max(source.minX,screen.visibleFrame.minX),screen.visibleFrame.maxX-frame.width)
                frame.origin.y = min(max(source.minY-frame.height-12,screen.visibleFrame.minY),
                                     screen.visibleFrame.maxY-frame.height)
                panel.setFrame(frame, display: false)
            }
            window = panel
        }
        panel.level = anchor?.level ?? .floating
        let frame = panel.isVisible ? panel.frame : savedFrame ?? panel.frame
        if panel.contentViewController == nil {
            panel.setFrame(frame, display: false)
            let host = NSHostingController(rootView: Content(model: model, state: state, dismiss: { [weak self] in self?.hide() }))
            host.sizingOptions = []
            host.view.frame = CGRect(origin: .zero, size: panel.contentRect(forFrameRect: frame).size)
            host.view.autoresizingMask = [.width, .height]
            panel.contentViewController = host
            panel.setFrame(frame, display: false)
        }
        panel.makeKeyAndOrderFront(nil)
    }

    func hide() {
        if let window, window.contentViewController != nil { savedFrame = window.frame }
        window?.orderOut(nil)
    }

    func close() {
        hide()
        window?.contentViewController = nil
        window?.close()
        window = nil
    }

    func windowWillClose(_ notification: Notification) {
        if let window { savedFrame = window.frame }
    }
}
