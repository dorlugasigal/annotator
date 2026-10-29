import Foundation

/// The toolbar's own buttons, next to the tools. People can turn each one off in Settings; the tools and added buttons
/// such as Spotlight and the Zoom menu are arranged separately (`toolbarItems`).
public enum ToolbarControl: String, CaseIterable, Codable, Sendable {
    case library, board, mode, undo, redo, clearAll, properties, settings, hide

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
        case .properties: "Show or hide the properties below the tools."
        case .settings: "Open Settings."
        case .hide: "Hide drawings and tools."
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

/// Properties with a few choices. Each shows as one button that steps through its choices, which is the default, or as all
/// its choices side by side.
public enum PropertyChoice: String, CaseIterable, Codable, Sendable {
    case pattern, roughness, fillStyle, edges, arrowType, pressure, font, alignment

    public var label: String {
        switch self {
        case .pattern: "Pattern"
        case .roughness: "Roughness"
        case .fillStyle: "Fill style"
        case .edges: "Edges"
        case .arrowType: "Arrow type"
        case .pressure: "Pressure"
        case .font: "Font"
        case .alignment: "Alignment"
        }
    }

    /// The property's choices, in order. One button starts at the first and steps through them.
    public var choices: [String] {
        switch self {
        case .pattern: ["Solid", "Dashed", "Dotted"]
        case .roughness: ["Clean", "Sketchy", "Wild"]
        case .fillStyle: ["Solid", "Hatch", "Crosshatch"]
        case .edges: ["Sharp corners", "Rounded corners"]
        case .arrowType: ["Straight arrow", "Curved arrow"]
        case .pressure: ["Constant pressure", "Ink pressure"]
        case .font: ["System", "Mono"]
        case .alignment: ["Left", "Center", "Right"]
        }
    }

    /// How many choices the property has.
    public var choiceCount: Int { choices.count }
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

    /// Whether a property shows all its choices side by side instead of one button.
    public func showsAllChoices(_ choice: PropertyChoice) -> Bool {
        (expandedPropertyChoices ?? []).contains(choice.rawValue)
    }

    public mutating func setShowsAllChoices(_ choice: PropertyChoice, _ shown: Bool) {
        var expanded = Set(expandedPropertyChoices ?? [])
        if shown { expanded.insert(choice.rawValue) } else { expanded.remove(choice.rawValue) }
        let ordered = PropertyChoice.allCases.map(\.rawValue).filter(expanded.contains)
        expandedPropertyChoices = ordered.isEmpty ? nil : ordered
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
        if let expandedPropertyChoices {
            guard expandedPropertyChoices.count <= PropertyChoice.allCases.count,
                  Set(expandedPropertyChoices).count == expandedPropertyChoices.count,
                  expandedPropertyChoices.allSatisfy({ PropertyChoice(rawValue: $0) != nil }) else {
                throw CanvasError.invalidDocument("Properties that show all their choices must be known properties, each listed once.")
            }
        }
    }

    /// Buttons and properties from a newer version are dropped, so a profile with them still opens.
    mutating func normalizeToolbarControls() {
        if let hidden = hiddenToolbarControls {
            var seen = Set<String>()
            let known = hidden.filter { ToolbarControl(rawValue: $0) != nil && seen.insert($0).inserted }
            hiddenToolbarControls = known.isEmpty ? nil : known
        }
        if let expanded = expandedPropertyChoices {
            var seen = Set<String>()
            let known = expanded.filter { PropertyChoice(rawValue: $0) != nil && seen.insert($0).inserted }
            expandedPropertyChoices = known.isEmpty ? nil : known
        }
    }
}
