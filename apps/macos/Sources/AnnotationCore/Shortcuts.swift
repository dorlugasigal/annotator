import Foundation

/// Toolbar density. Expanded is the original layout; Minimal uses smaller buttons without key labels.
public enum ToolbarSize: String, Codable, CaseIterable, Sendable { case expanded, minimal }

/// A key pressed while a drawing canvas has keyboard focus: one letter or number, optionally with Shift for letters.
public struct LocalShortcut: Hashable, Sendable {
    public let key: String
    public let shift: Bool

    public init?(key: String, shift: Bool = false) {
        let normalized = key.lowercased()
        guard normalized.count == 1, normalized.unicodeScalars.count == 1,
              let scalar = normalized.unicodeScalars.first,
              CharacterSet.alphanumerics.contains(scalar) else { return nil }
        // Shift changes the character of digits and symbols on most layouts, so only letters take Shift.
        guard !shift || (CharacterSet.letters.contains(scalar) && normalized.uppercased() != normalized) else { return nil }
        self.key = normalized
        self.shift = shift
    }

    public init?(storageKey: String) {
        if storageKey.hasPrefix(Self.shiftPrefix) {
            self.init(key: String(storageKey.dropFirst(Self.shiftPrefix.count)), shift: true)
        } else {
            guard storageKey == storageKey.lowercased() else { return nil }
            self.init(key: storageKey)
        }
    }

    static let shiftPrefix = "⇧"
    public var storageKey: String { shift ? Self.shiftPrefix + key : key }
    public var label: String { (shift ? "⇧" : "") + key.uppercased() }
    public var spokenLabel: String { shift ? "Shift + \(key.uppercased())" : key.uppercased() }
    public var isInkColor: Bool { InkShortcut.choices[key] != nil }
    public var unshifted: LocalShortcut { shift ? LocalShortcut(key: key)! : self }
}

/// Toolbar buttons that are not drawing tools but can have a single-key shortcut.
public enum ShortcutAction: String, CaseIterable, Codable, Sendable {
    case properties, board, spotlight, cursorHalo, pointerLens, smartDraw, shareRegion, clearAll, focusMenu, zoomMenu

    /// Buttons people can add to the toolbar. Properties, Board and Clear all have fixed places (`ToolbarControl`).
    public static let toolbarChoices: [ShortcutAction] =
        [.spotlight, .cursorHalo, .pointerLens, .smartDraw, .shareRegion, .focusMenu, .zoomMenu]
    public var canBeInToolbar: Bool { Self.toolbarChoices.contains(self) }
    /// The Spotlight and Zoom menus open with a click; they have no key.
    public var isMenu: Bool { self == .focusMenu || self == .zoomMenu }

    public var label: String {
        switch self {
        case .properties: "Properties"
        case .board: "Board"
        case .spotlight: "Spotlight"
        case .cursorHalo: "Cursor halo"
        case .pointerLens: "Pointer lens"
        case .smartDraw: "Smart draw"
        case .shareRegion: "Share region"
        case .clearAll: "Clear all"
        case .focusMenu: "Spotlight menu"
        case .zoomMenu: "Zoom menu"
        }
    }

    public var detail: String {
        switch self {
        case .properties: "Show or hide the properties below the tools."
        case .board: "Switch between desktop, whiteboard and blackboard."
        case .spotlight: "Turn the spotlight on or off."
        case .cursorHalo: "Turn the ring around your pointer on or off."
        case .pointerLens: "Turn the zoom lens under your pointer on or off."
        case .smartDraw: "Turn shape recognition for the pen on or off."
        case .shareRegion: "Choose part of a display and open a window to share in your call."
        case .clearAll: "Remove all drawings from every display. You can undo it."
        case .focusMenu: "Spotlight, cursor halo and cleanup, in one menu."
        case .zoomMenu: "Pointer lens, screen zoom and zoom levels, in one menu."
        }
    }

    public var symbol: String {
        switch self {
        case .properties: "slider.horizontal.3"
        case .board: "rectangle.inset.filled"
        case .spotlight: "flashlight.on.fill"
        case .cursorHalo: "circle.dotted"
        case .pointerLens: "plus.magnifyingglass"
        case .smartDraw: "wand.and.stars"
        case .shareRegion: "rectangle.on.rectangle"
        case .clearAll: "trash"
        case .focusMenu: "light.beacon.max"
        case .zoomMenu: "arrow.up.left.and.down.right.magnifyingglass"
        }
    }
}

/// Something a local shortcut can do: pick a tool or press a toolbar button.
public enum ShortcutCommand: Hashable, Sendable {
    case tool(Tool)
    case action(ShortcutAction)

    public var id: String {
        switch self {
        case .tool(let tool): "tool.\(tool.rawValue)"
        case .action(let action): "action.\(action.rawValue)"
        }
    }

    public init?(id: String) {
        if id.hasPrefix("tool."), let tool = Tool(rawValue: String(id.dropFirst(5))) { self = .tool(tool) }
        else if id.hasPrefix("action."), let action = ShortcutAction(rawValue: String(id.dropFirst(7))) { self = .action(action) }
        else { return nil }
    }

    public var label: String {
        switch self {
        case .tool(let tool): tool == .cover ? "Effects" : tool.label
        case .action(let action): action.label
        }
    }
}

extension WorkspacePreferences {
    /// Every local key binding. Plain tool keys stay in `toolShortcuts` so older versions can still read them;
    /// Shift tool keys and toolbar button keys live in `commandShortcuts`, which older versions ignore.
    public var localShortcuts: [LocalShortcut: ShortcutCommand] {
        var result: [LocalShortcut: ShortcutCommand] = [:]
        for (key, name) in toolShortcuts {
            if let shortcut = LocalShortcut(key: key), let tool = Tool(rawValue: name) { result[shortcut] = .tool(tool) }
        }
        for (key, id) in commandShortcuts ?? [:] {
            if let shortcut = LocalShortcut(storageKey: key), let command = ShortcutCommand(id: id) { result[shortcut] = command }
        }
        return result
    }

    public func shortcut(for command: ShortcutCommand) -> LocalShortcut? {
        if case .tool(let tool) = command,
           let key = toolShortcuts.first(where: { $0.value == tool.rawValue })?.key {
            return LocalShortcut(key: key)
        }
        return (commandShortcuts ?? [:]).first { $0.value == command.id }.flatMap { LocalShortcut(storageKey: $0.key) }
    }

    /// The command for a key press. Shift with a letter that has no Shift binding keeps its plain meaning.
    public func command(forPressed shortcut: LocalShortcut) -> ShortcutCommand? {
        let bindings = localShortcuts
        return bindings[shortcut] ?? (shortcut.shift ? bindings[shortcut.unshifted] : nil)
    }

    /// Gives `shortcut` to `command` and removes the command's previous key. A key that belonged to another
    /// command moves; that command is returned so the caller can say what changed. `nil` clears the key.
    @discardableResult
    public mutating func bind(_ shortcut: LocalShortcut?, to command: ShortcutCommand) throws -> ShortcutCommand? {
        var updated = self
        updated.removeBinding(for: command)
        var displaced: ShortcutCommand?
        if let shortcut, case .action(let action) = command, action.isMenu {
            throw CanvasError.invalidDocument("\(action.label) opens with a click, so it has no key. \(shortcut.label) stays free.")
        }
        if let shortcut {
            guard !shortcut.isInkColor else {
                throw CanvasError.invalidDocument("\(shortcut.key.uppercased()) changes the ink color. Choose another key.")
            }
            if let owner = updated.localShortcuts[shortcut] {
                displaced = owner
                updated.removeBinding(for: owner)
            }
            if case .tool(let tool) = command, !shortcut.shift {
                updated.toolShortcuts[shortcut.key] = tool.rawValue
            } else {
                updated.commandShortcuts = (updated.commandShortcuts ?? [:]).merging([shortcut.storageKey: command.id]) { $1 }
            }
        }
        try updated.validate()
        self = updated
        return displaced
    }

    public mutating func resetLocalShortcuts() {
        toolShortcuts = Self.defaultToolShortcuts
        commandShortcuts = nil
    }

    private mutating func removeBinding(for command: ShortcutCommand) {
        if case .tool(let tool) = command { toolShortcuts = toolShortcuts.filter { $0.value != tool.rawValue } }
        let remaining = (commandShortcuts ?? [:]).filter { $0.value != command.id }
        commandShortcuts = remaining.isEmpty ? nil : remaining
    }

    func validateToolbarItems() throws {
        guard let toolbarItems else { return }
        let commands = toolbarItems.compactMap(ShortcutCommand.init(id:))
        guard !toolbarItems.isEmpty, commands.count == toolbarItems.count,
              toolbarItems.count <= Tool.allCases.count + ShortcutAction.toolbarChoices.count,
              Set(toolbarItems).count == toolbarItems.count, commands.contains(.tool(.select)),
              commands.allSatisfy({ if case .action(let action) = $0 { action.canBeInToolbar } else { true } }) else {
            throw CanvasError.invalidDocument("The toolbar must contain unique supported tools and buttons, including Select.")
        }
    }

    func validateCommandShortcuts() throws {
        var commands = Set(toolShortcuts.values.map { "tool.\($0)" })
        guard let commandShortcuts else { return }
        guard commandShortcuts.count <= Tool.allCases.count + ShortcutAction.allCases.count else {
            throw CanvasError.invalidDocument("The preferences contain invalid limits.")
        }
        for (key, id) in commandShortcuts {
            guard let shortcut = LocalShortcut(storageKey: key), let command = ShortcutCommand(id: id) else {
                throw CanvasError.invalidDocument("Keyboard shortcuts must use one letter or number. Shift works with letters.")
            }
            guard !shortcut.isInkColor else {
                throw CanvasError.invalidDocument("\(shortcut.key.uppercased()) changes the ink color. Choose another key.")
            }
            if case .tool = command, !shortcut.shift {
                throw CanvasError.invalidDocument("Plain tool keys belong with the other tool keys.")
            }
            if case .action(let action) = command, action.isMenu {
                throw CanvasError.invalidDocument("\(action.label) opens with a click, so it has no key.")
            }
            guard shortcut.shift || toolShortcuts[shortcut.key] == nil, commands.insert(command.id).inserted else {
                throw CanvasError.invalidDocument("Each key and each tool or button can have only one shortcut.")
            }
        }
    }
}

extension Tool {
    /// Tools that take part when "Share style between tools" is on.
    public var sharesStyle: Bool { !isProtection && ![.select, .eraser, .image].contains(self) }
}

extension Style {
    /// Copies the parts of the look that changed from `old` to `new`: colors, fill, opacity, width,
    /// line pattern and sloppiness. Tool-specific settings such as text size and arrowheads stay separate.
    public mutating func adoptSharedChanges(from old: Style, to new: Style, supportsFill: Bool) {
        if old.color != new.color || old.strokeGradient != new.strokeGradient {
            color = new.color
            strokeGradient = new.strokeGradient
        }
        if supportsFill, old.fill != new.fill || old.fillGradient != new.fillGradient {
            fill = new.fill
            fillGradient = new.fillGradient
        }
        if supportsFill, old.fillPattern != new.fillPattern { fillPattern = new.fillPattern }
        if old.opacity != new.opacity { opacity = new.opacity }
        if old.width != new.width { width = new.width }
        if old.pattern != new.pattern { pattern = new.pattern }
        if old.roughness != new.roughness { roughness = new.roughness }
    }
}
