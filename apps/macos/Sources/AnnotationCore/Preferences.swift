import Foundation

public enum ToolbarOrientation: String, Codable, CaseIterable, Sendable { case horizontal, vertical }
public enum ToolbarDockEdge: String, Codable, CaseIterable, Sendable { case automatic, top, bottom, left, right }
public enum VisibilityAnimationStyle: String, Codable, CaseIterable, Sendable {
    case slide, fluid, quick, fade, reveal, curtain, fold, rise, dissolve, magic, corners, pop, drop, focus, none

    public static let available: [Self] = [.dissolve, .slide, .magic, .corners, .pop, .drop, .focus, .fade, .none]
    public var resolved: Self {
        switch self {
        case .fluid, .quick: .slide
        case .reveal, .curtain, .fold, .rise: .fade
        default: self
        }
    }
}
public enum AppTheme: String, Codable, CaseIterable, Sendable { case system, light, dark }
public enum ToolCursorStyle: String, Codable, CaseIterable, Sendable, Hashable { case toolPreview, crosshair, system }

public struct ActivationShortcut: Codable, Equatable, Sendable {
    public var keyCode: UInt32
    public var control: Bool
    public var option: Bool
    public var command: Bool
    public var shift: Bool
    public var character: String

    public init(keyCode: UInt32, character: String, control: Bool = false,
                option: Bool = false, command: Bool = false, shift: Bool = false) {
        self.keyCode = keyCode; self.character = character
        self.control = control; self.option = option; self.command = command; self.shift = shift
    }
    public static let standard = ActivationShortcut(keyCode: 19, character: "2", control: true)
    public static let hideTools = ActivationShortcut(keyCode: 19, character: "2", control: true, option: true)
    public static let liveZoom = ActivationShortcut(keyCode: 18, character: "1", control: true)
    public static let resumeDrawing = ActivationShortcut(keyCode: 19, character: "2", control: true, shift: true)
    public static let breakTimer = ActivationShortcut(keyCode: 20, character: "3", control: true)

    public func usesSameKeys(as other: ActivationShortcut) -> Bool {
        keyCode == other.keyCode && control == other.control && option == other.option &&
            command == other.command && shift == other.shift
    }
    public var label: String {
        (control ? "⌃" : "") + (option ? "⌥" : "") + (shift ? "⇧" : "") + (command ? "⌘" : "") + character.uppercased()
    }
    public func validate() throws {
        guard keyCode <= 127, character.count == 1,
              character.unicodeScalars.allSatisfy({ CharacterSet.alphanumerics.contains($0) }),
              control || option || command else {
            throw CanvasError.invalidDocument("Choose a letter or number with Control, Option, or Command for the global shortcut.")
        }
    }
}

public struct WorkspacePreferences: Codable, Equatable, Sendable {
    public var schemaVersion = 1
    public var toolStyles: [String: Style] = [:]
    public static let legacyToolShortcuts: [String: String] = [
        "v": "select", "p": "pen", "h": "highlighter", "r": "rectangle", "o": "ellipse",
        "d": "diamond", "a": "arrow", "l": "line", "t": "text", "e": "eraser"
    ]
    public static let defaultToolShortcuts: [String: String] = [
        "1": "select", "2": "rectangle", "3": "diamond", "4": "ellipse", "5": "arrow",
        "6": "line", "7": "pen", "8": "text", "9": "highlighter", "0": "eraser",
        "u": "blur", "s": "cover", "x": "pixelate", "t": "distort", "m": "ruler",
        "n": "counter", "i": "image", "d": "database", "q": "queue", "h": "person",
        "f": "grid", "a": "axes"
    ]
    public var toolShortcuts = WorkspacePreferences.defaultToolShortcuts
    public var shortcutVersion: Int? = 2
    public var activationShortcut = ActivationShortcut.standard
    public var smartDraw = false
    public var spotlightRadius: Double = 130
    public var hideShortcut: ActivationShortcut?
    public var zoomShortcut: ActivationShortcut?
    public var resumeShortcut: ActivationShortcut?
    public var toolbarOrientation: ToolbarOrientation?
    public var toolbarDockEdge: ToolbarDockEdge?
    public var lastCoverMode: Tool?
    public var toolbarTools: [String]?
    public var showMenuBarIcon: Bool?
    public var cursorStyle: ToolCursorStyle?
    public var quickPreferencesExpanded: Bool?
    public var inspectorPinned: Bool?
    public var animateVisibility: Bool?
    public var visibilityAnimationStyle: VisibilityAnimationStyle?
    public var theme: AppTheme?
    public var showShortcutHints: Bool?
    public var cursorHighlight: CursorHighlightSettings?
    public var commandShortcuts: [String: String]?
    public var toolbarSize: ToolbarSize?
    public var shareToolStyles: Bool?
    /// Toolbar order including buttons such as Spotlight. `toolbarTools` keeps the tools for older versions.
    public var toolbarItems: [String]?
    /// Break screen choices (specification 014). `nil` means the defaults.
    public var breakTimer: BreakTimerSettings?
    public var breakShortcut: ActivationShortcut?
    /// The toolbar's own buttons and menus that are turned off (`ToolbarControl` raw values).
    public var hiddenToolbarControls: [String]?
    /// Seconds the pointer can stay off the toolbar before it shrinks to one button. `nil` keeps it open.
    public var toolbarCollapseDelay: Double?
    public static let defaultToolbar: [Tool] = [.select, .rectangle, .diamond, .ellipse, .arrow, .line, .pen, .text, .highlighter, .eraser, .cover]

    public init() {}

    public func validate() throws {
        try cursorHighlight?.validate()
        guard lastCoverMode?.isProtection != false else { throw CanvasError.invalidDocument("Unsupported cover mode.") }
        guard (1...2).contains(schemaVersion) else { throw CanvasError.unsupportedSchema(schemaVersion) }
        let shortcuts = [activationShortcut, hideShortcut ?? .hideTools,
                         zoomShortcut ?? .liveZoom, resumeShortcut ?? .resumeDrawing]
        for (index, shortcut) in shortcuts.enumerated() {
            try shortcut.validate()
            guard !shortcuts.prefix(index).contains(where: { $0.usesSameKeys(as: shortcut) }) else {
                throw CanvasError.invalidDocument("Show / hide, Hide only, Live zoom and Resume drawing need different shortcuts.")
            }
        }
        let breakKeys = breakShortcut ?? .breakTimer
        try breakKeys.validate()
        guard !shortcuts.contains(where: { $0.usesSameKeys(as: breakKeys) }) else {
            throw CanvasError.invalidDocument("Break timer needs its own shortcut. Choose keys that no other global shortcut uses.")
        }
        try breakTimer?.validate()
        if let toolbarTools {
            guard !toolbarTools.isEmpty, toolbarTools.contains(Tool.select.rawValue),
                  toolbarTools.count <= Tool.allCases.count, Set(toolbarTools).count == toolbarTools.count,
                  toolbarTools.allSatisfy({ Tool(rawValue: $0) != nil }) else {
                throw CanvasError.invalidDocument("The toolbar must contain unique supported tools, including Select.")
            }
        }
        guard spotlightRadius.isFinite, (50...350).contains(spotlightRadius),
              toolStyles.count <= Tool.allCases.count,
              toolShortcuts.count <= Tool.allCases.count else {
            throw CanvasError.invalidDocument("The preferences contain invalid limits.")
        }
        for (tool, style) in toolStyles {
            guard Tool(rawValue: tool) != nil else { throw CanvasError.invalidDocument("Unknown tool in preferences.") }
            try style.validate()
        }
        var assignedTools = Set<String>()
        for (key, tool) in toolShortcuts {
            guard key.count == 1, key == key.lowercased(),
                  key.unicodeScalars.allSatisfy({ CharacterSet.alphanumerics.contains($0) }),
                  Tool(rawValue: tool) != nil, assignedTools.insert(tool).inserted else {
                throw CanvasError.invalidDocument("Tool shortcuts must use unique letters or numbers.")
            }
            guard InkShortcut.choices[key] == nil else {
                throw CanvasError.invalidDocument("\(key.uppercased()) changes ink color. Choose a number or another letter for a tool.")
            }
        }
        try validateCommandShortcuts()
        try validateToolbarItems()
        try validateToolbarControls()
    }

    public mutating func assignShortcut(_ key: String, to tool: Tool) throws {
        let normalized = key.lowercased()
        var updated = self
        updated.toolShortcuts = updated.toolShortcuts.filter { $0.value != tool.rawValue }
        if !normalized.isEmpty {
            guard updated.toolShortcuts[normalized] == nil, updated.commandShortcuts?[normalized] == nil else {
                throw CanvasError.invalidDocument("That key is already assigned to another tool.")
            }
            updated.toolShortcuts[normalized] = tool.rawValue
        }
        try updated.validate()
        self = updated
    }

    public func data() throws -> Data {
        var saved = self
        if toolStyles.values.contains(where: \.usesGradient) { saved.schemaVersion = 2 }
        try saved.validate()
        return try JSONEncoder().encode(saved)
    }

    public static func read(_ data: Data) throws -> WorkspacePreferences {
        guard data.count <= 256 * 1_024 else { throw CanvasError.invalidDocument("The preferences file is too large.") }
        var preferences = try JSONDecoder().decode(Self.self, from: data)
        guard preferences.schemaVersion >= 2 || !preferences.toolStyles.values.contains(where: \.usesGradient) else {
            throw CanvasError.invalidDocument("Gradient colors need a newer profile format.")
        }
        if preferences.visibilityAnimationStyle == .fluid { preferences.visibilityAnimationStyle = .slide }
        preferences.normalizeToolbarControls()
        if preferences.shortcutVersion == nil || preferences.shortcutVersion == 1 {
            let custom = preferences.toolShortcuts.filter { key, tool in
                legacyToolShortcuts[key] != tool && InkShortcut.choices[key] == nil
            }
            preferences.toolShortcuts = defaultToolShortcuts
            for (key, tool) in custom.sorted(by: { $0.key < $1.key }) {
                preferences.toolShortcuts = preferences.toolShortcuts.filter { $0.value != tool }
                preferences.toolShortcuts[key] = tool
            }
            preferences.shortcutVersion = 2
        }
        guard preferences.shortcutVersion == 2 else { throw CanvasError.invalidDocument("Unsupported shortcut profile version.") }
        if preferences.zoomShortcut == nil,
           preferences.activationShortcut.usesSameKeys(as: .liveZoom) ||
            (preferences.hideShortcut ?? .hideTools).usesSameKeys(as: .liveZoom) {
            preferences.zoomShortcut = [
                ActivationShortcut(keyCode: 18, character: "1", control: true, option: true),
                ActivationShortcut(keyCode: 18, character: "1", control: true, shift: true),
                ActivationShortcut(keyCode: 18, character: "1", control: true, command: true)
            ].first { candidate in
                ![preferences.activationShortcut, preferences.hideShortcut ?? .hideTools]
                    .contains { $0.usesSameKeys(as: candidate) }
            }
        }
        let existing = [preferences.activationShortcut, preferences.hideShortcut ?? .hideTools,
                        preferences.zoomShortcut ?? .liveZoom]
        if preferences.resumeShortcut == nil, existing.contains(where: { $0.usesSameKeys(as: .resumeDrawing) }) {
            preferences.resumeShortcut = [
                .resumeDrawing,
                ActivationShortcut(keyCode: 19, character: "2", control: true, option: true, shift: true),
                ActivationShortcut(keyCode: 19, character: "2", control: true, command: true, shift: true),
                ActivationShortcut(keyCode: 19, character: "2", control: true, option: true, command: true, shift: true)
            ].first { candidate in !existing.contains { $0.usesSameKeys(as: candidate) } }
        }
        let globals = existing + [preferences.resumeShortcut ?? .resumeDrawing]
        if preferences.breakShortcut == nil, globals.contains(where: { $0.usesSameKeys(as: .breakTimer) }) {
            preferences.breakShortcut = [
                ActivationShortcut(keyCode: 20, character: "3", control: true, option: true),
                ActivationShortcut(keyCode: 20, character: "3", control: true, shift: true),
                ActivationShortcut(keyCode: 20, character: "3", control: true, command: true),
                ActivationShortcut(keyCode: 20, character: "3", control: true, option: true, shift: true)
            ].first { candidate in !globals.contains { $0.usesSameKeys(as: candidate) } }
        }
        try preferences.validate()
        return preferences
    }
}
