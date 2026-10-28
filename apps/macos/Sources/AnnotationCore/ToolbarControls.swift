import Foundation

/// The toolbar's own buttons and menus, next to the tools. People can turn each one off in Settings; the tools and
/// added buttons such as Spotlight are arranged separately (`toolbarItems`).
public enum ToolbarControl: String, CaseIterable, Codable, Sendable {
    case library, board, mode, undo, redo, clearAll, properties, settings, hide, focusMenu, zoomMenu

    public var label: String {
        switch self {
        case .library: "More tools"
        case .board: "Board"
        case .mode: "Draw and Use apps"
        case .undo: "Undo"
        case .redo: "Redo"
        case .clearAll: "Clear all"
        case .properties: "Properties"
        case .settings: "Settings"
        case .hide: "Hide"
        case .focusMenu: "Spotlight and cleanup menu"
        case .zoomMenu: "Zoom menu"
        }
    }

    public var detail: String {
        switch self {
        case .library: "Every shape and tool in one place."
        case .board: "Desktop, whiteboard or blackboard."
        case .mode: "Switch between drawing and clicking your apps."
        case .undo: "Take back your last change."
        case .redo: "Bring back what Undo took."
        case .clearAll: "Remove all drawings. You can undo it."
        case .properties: "Quick controls, full properties or tools only."
        case .settings: "Open Settings."
        case .hide: "Hide drawings and tools."
        case .focusMenu: "Spotlight, cursor halo and cleanup."
        case .zoomMenu: "Pointer lens and screen zoom."
        }
    }

    public var symbol: String {
        switch self {
        case .library: "square.grid.2x2"
        case .board: "desktopcomputer"
        case .mode: "pencil.tip"
        case .undo: "arrow.uturn.backward"
        case .redo: "arrow.uturn.forward"
        case .clearAll: "trash"
        case .properties: "slider.horizontal.3"
        case .settings: "gearshape"
        case .hide: "xmark"
        case .focusMenu: "light.beacon.max"
        case .zoomMenu: "plus.magnifyingglass"
        }
    }

    /// The toolbar button key this control shares, for controls that have one.
    public var action: ShortcutAction? {
        switch self {
        case .board: .board
        case .clearAll: .clearAll
        case .properties: .properties
        default: nil
        }
    }
}

extension WorkspacePreferences {
    /// How long the pointer can stay off the toolbar before it shrinks to one button, in seconds.
    public static let toolbarCollapseDelays: [Double] = [3, 5, 10, 30]
    static let toolbarCollapseDelayRange: ClosedRange<Double> = 1...600

    public func showsToolbarControl(_ control: ToolbarControl) -> Bool {
        !(hiddenToolbarControls ?? []).contains(control.rawValue)
    }

    /// Shows or hides one toolbar control. The hidden list keeps the controls' own order, so equal toolbars compare equal.
    public mutating func setToolbarControl(_ control: ToolbarControl, shown: Bool) {
        var hidden = Set(hiddenToolbarControls ?? [])
        if shown { hidden.remove(control.rawValue) } else { hidden.insert(control.rawValue) }
        let ordered = ToolbarControl.allCases.map(\.rawValue).filter(hidden.contains)
        hiddenToolbarControls = ordered.isEmpty ? nil : ordered
    }

    func validateToolbarControls() throws {
        if let hiddenToolbarControls {
            guard hiddenToolbarControls.count <= ToolbarControl.allCases.count,
                  Set(hiddenToolbarControls).count == hiddenToolbarControls.count,
                  hiddenToolbarControls.allSatisfy({ ToolbarControl(rawValue: $0) != nil }) else {
                throw CanvasError.invalidDocument("Hidden toolbar buttons must be known buttons, each listed once.")
            }
        }
        if let toolbarCollapseDelay {
            guard toolbarCollapseDelay.isFinite, Self.toolbarCollapseDelayRange.contains(toolbarCollapseDelay) else {
                throw CanvasError.invalidDocument("The toolbar can shrink after 1 to 600 seconds.")
            }
        }
    }

    /// Buttons from a newer version are dropped, so a profile with them still opens.
    mutating func normalizeToolbarControls() {
        guard let hidden = hiddenToolbarControls else { return }
        var seen = Set<String>()
        let known = hidden.filter { ToolbarControl(rawValue: $0) != nil && seen.insert($0).inserted }
        hiddenToolbarControls = known.isEmpty ? nil : known
    }
}
