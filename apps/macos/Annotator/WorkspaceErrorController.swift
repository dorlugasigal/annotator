import AppKit
import Combine

@MainActor
final class WorkspaceErrorController: NSObject, NSWindowDelegate {
    let window: NSPanel
    private let model: WorkspaceModel
    private let messageLabel = NSTextField(wrappingLabelWithString: "")
    private let stack = NSStackView()
    private var observation: AnyCancellable?
    private(set) var displayedMessage: String?

    init(model: WorkspaceModel) {
        self.model = model
        window = ErrorPanel(contentRect: CGRect(x: 0, y: 0, width: 460, height: 160),
            styleMask: [.titled, .closable, .nonactivatingPanel], backing: .buffered, defer: false)
        super.init()
        window.title = "Annotator"
        window.isReleasedWhenClosed = false
        window.hidesOnDeactivate = false
        window.delegate = self
        let heading = NSTextField(labelWithString: "Could not complete this action")
        heading.font = .boldSystemFont(ofSize: 14)
        messageLabel.preferredMaxLayoutWidth = 420
        messageLabel.isSelectable = true
        let button = NSButton(title: "OK", target: self, action: #selector(dismiss))
        button.bezelStyle = .rounded
        button.keyEquivalent = "\r"
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 16
        for view in [heading, messageLabel, button] { stack.addArrangedSubview(view) }
        stack.translatesAutoresizingMaskIntoConstraints = false
        let content = NSView()
        content.addSubview(stack)
        window.contentView = content
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 20),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -20),
            stack.topAnchor.constraint(equalTo: content.topAnchor, constant: 20),
            messageLabel.widthAnchor.constraint(equalToConstant: 420)
        ])
        observation = model.$errorMessage.receive(on: RunLoop.main).sink { [weak self] message in
            MainActor.assumeIsolated { self?.present(message) }
        }
    }

    private func present(_ message: String?) {
        guard message == model.errorMessage else { return }
        displayedMessage = message
        guard let message else { window.orderOut(nil); return }
        messageLabel.stringValue = message
        window.setContentSize(CGSize(width: 460, height: max(160, stack.fittingSize.height + 40)))
        window.level = NSWindow.Level(rawValue: PresentationWindowLevels.toolbar(screenZoom: model.screenZoomEnabled).rawValue + 1)
        if !window.isVisible { window.center() }
        window.makeKeyAndOrderFront(nil)
    }

    @objc func dismiss() {
        guard displayedMessage == model.errorMessage else {
            present(model.errorMessage)
            return
        }
        model.errorMessage = nil
        displayedMessage = nil
        window.orderOut(nil)
    }

    func dismissIfShown() -> Bool {
        guard window.isVisible else { return false }
        dismiss()
        return true
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        dismiss()
        return false
    }

    func close() {
        observation?.cancel()
        observation = nil
        window.delegate = nil
        window.orderOut(nil)
        window.close()
    }
}

private final class ErrorPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
    override func cancelOperation(_ sender: Any?) { performClose(sender) }
}
