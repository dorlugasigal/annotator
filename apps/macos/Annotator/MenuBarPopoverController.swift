import AppKit
import SwiftUI
import AnnotationCore

@MainActor
final class MenuBarPopoverController: NSObject, NSPopoverDelegate {
    let popover = NSPopover()
    private let model: WorkspaceModel
    private let action: (MenuBarAction) -> Void
    private let monitorsEnabled: Bool
    private let toolsVisible: () -> Bool
    private var localMonitor: Any?
    private var globalMonitor: Any?
    private var activationObserver: NSObjectProtocol?
    private var presentationID: UUID?
    private(set) var measuredContentSize = CGSize.zero
    var hasEventMonitors: Bool { localMonitor != nil || globalMonitor != nil || activationObserver != nil }

    init(model: WorkspaceModel,
         monitorsEnabled: Bool = ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil,
         toolsVisible: @escaping () -> Bool = { false },
         action: @escaping (MenuBarAction) -> Void) {
        self.model = model
        self.action = action
        self.monitorsEnabled = monitorsEnabled
        self.toolsVisible = toolsVisible
        super.init()
        popover.behavior = .applicationDefined
        popover.animates = false
        popover.delegate = self
    }

    func toggle(relativeTo button: NSView) {
        if popover.isShown { close(); return }
        guard let anchorWindow = button.window else {
            model.report(CanvasError.invalidDocument("The menu could not open. Use your Show / hide shortcut to open the tools."))
            return
        }
        let maximumHeight = max(240, (anchorWindow.screen?.visibleFrame.height ?? 700) - 60)
        let identifier = UUID()
        presentationID = identifier
        let preview = Bundle.main.bundleIdentifier?.hasSuffix(".dev") == true
        let host = NSHostingController(rootView: MenuBarPopoverView(model: model, developerPreview: preview,
            perform: { [weak self] in self?.perform($0) },
            resized: { [weak self] size in
                guard let self, presentationID == identifier, size.width > 0, size.height > 0 else { return }
                measuredContentSize = size
                popover.contentSize = size
                popover.contentViewController?.preferredContentSize = size
            }, maximumHeight: maximumHeight, toolsVisible: toolsVisible))
        popover.contentViewController = host
        popover.contentSize = CGSize(width: 320, height: min(380, maximumHeight))
        // Only the opening uses the system animation; later content-size updates and closing stay immediate.
        popover.animates = !Motion.systemReducesMotion
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        popover.animates = false
        popover.contentViewController?.view.window?.level = .popUpMenu
        if ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil {
            NSApp.activate(ignoringOtherApps: true)
        }
        popover.contentViewController?.view.window?.makeKey()
        popover.contentViewController?.view.window?.makeFirstResponder(host.view)
        if monitorsEnabled { installMonitors() }
    }

    @discardableResult
    func dismissIfShown() -> Bool {
        guard popover.isShown else { return false }
        close()
        return true
    }

    func close() {
        presentationID = nil
        popover.performClose(nil)
        removeMonitors()
        popover.contentViewController = nil
    }

    func popoverDidClose(_ notification: Notification) {
        presentationID = nil
        removeMonitors()
        popover.contentViewController = nil
    }

    func perform(_ selected: MenuBarAction) {
        guard popover.isShown else { return }
        if (selected == .saveBoard || selected == .exportPNG) && model.active == nil { return }
        close()
        action(selected)
    }

    func handleLocalEvent(_ event: NSEvent) -> NSEvent? {
        guard popover.isShown else { return event }
        if event.type == .keyDown {
            let modifiers = event.modifierFlags.intersection([.command, .control, .option, .shift])
            if event.keyCode == 53 && modifiers.isEmpty {
                close()
                return nil
            }
            if modifiers == .command {
                let selected: MenuBarAction?
                switch event.charactersIgnoringModifiers?.lowercased() {
                case "s": selected = model.active != nil ? .saveBoard : nil
                case "o": selected = .openBoard
                case ",": selected = .settings
                case "q": selected = .quit
                default: selected = nil
                }
                if let selected { perform(selected); return nil }
            }
        }
        if [.leftMouseDown, .rightMouseDown, .otherMouseDown].contains(event.type),
           event.window !== popover.contentViewController?.view.window {
            close()
            return nil
        }
        return event
    }

    private func installMonitors() {
        removeMonitors()
        localMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown, .keyDown]) {
            [weak self] event in
            let consumed = MainActor.assumeIsolated {
                guard let self else { return false }
                return self.handleLocalEvent(event) == nil
            }
            return consumed ? nil : event
        }
        globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown]) {
            [weak self] _ in MainActor.assumeIsolated { self?.close() }
        }
        activationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didResignActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in MainActor.assumeIsolated { self?.close() } }
    }

    private func removeMonitors() {
        if let localMonitor { NSEvent.removeMonitor(localMonitor) }
        if let globalMonitor { NSEvent.removeMonitor(globalMonitor) }
        if let activationObserver { NotificationCenter.default.removeObserver(activationObserver) }
        localMonitor = nil
        globalMonitor = nil
        activationObserver = nil
    }
}
