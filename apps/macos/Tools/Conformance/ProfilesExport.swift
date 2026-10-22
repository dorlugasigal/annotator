import AnnotationCore
import Foundation

/// Preference profiles: reading, migration, validation and what the app writes back.
@MainActor
enum ProfilesExport {
    static func run(_ corpus: Corpus) throws {
        corpus.describe("profiles", "Preference files read with WorkspacePreferences.read: the model after migration, or the error, and the model after writing it with data() and reading it again.")
        var cases: [(String, String, Data)] = []
        func add(_ name: String, _ note: String, _ change: (inout WorkspacePreferences) -> Void) throws {
            var preferences = WorkspacePreferences()
            change(&preferences)
            cases.append((name, note, try bytes(preferences)))
        }
        try add("default", "A new profile.") { _ in }
        try add("full", "Every optional setting.") { full(&$0) }
        try add("legacy-defaults", "A profile from before shortcut version 2 with the old default keys.") {
            $0.shortcutVersion = nil; $0.toolShortcuts = WorkspacePreferences.legacyToolShortcuts
        }
        try add("legacy-custom", "Old keys plus custom ones; custom keys move onto the new defaults in key order.") {
            $0.shortcutVersion = nil
            $0.toolShortcuts = WorkspacePreferences.legacyToolShortcuts.merging(["z": "pen", "j": "blur", "1": "star"]) { $1 }
            $0.toolShortcuts["p"] = nil
        }
        try add("legacy-ink-key", "Shortcut version 1 with a custom key on an ink color letter: the key is dropped.") {
            $0.shortcutVersion = 1
            $0.toolShortcuts = WorkspacePreferences.legacyToolShortcuts
            $0.toolShortcuts["g"] = "grid"
        }
        try add("fluid-style", "The retired Fluid style reads as Slide.") { $0.visibilityAnimationStyle = .fluid }
        try add("retired-styles", "Other retired styles are kept and resolved when used.") { $0.visibilityAnimationStyle = .curtain }
        try add("zoom-conflict", "Show and hide on Control-1 moves Live zoom to the first free alternative.") {
            $0.activationShortcut = .liveZoom
        }
        try add("zoom-conflict-twice", "Two global shortcuts take the first Live zoom alternatives.") {
            $0.activationShortcut = .liveZoom
            $0.hideShortcut = ActivationShortcut(keyCode: 18, character: "1", control: true, option: true)
        }
        try add("resume-conflict", "Show and hide on Control-Shift-2 moves Resume drawing to the next free alternative.") {
            $0.activationShortcut = .resumeDrawing
        }
        try add("gradient-styles", "Gradient tool styles need profile format 2.") {
            $0.schemaVersion = 2
            var style = Style.defaults(for: .pen)
            style.setGradient(GradientPreset.spectrum.gradient, fill: false)
            $0.toolStyles["pen"] = style
        }
        let base = String(decoding: try bytes(WorkspacePreferences()), as: UTF8.self)
        cases.append(("unknown-keys", "Keys from a future version are ignored.",
                      Data(("{\"futureSetting\":[1,{\"a\":null}]," + base.dropFirst()).utf8)))

        // One problem per file: Swift checks dictionaries in an order that changes from run to run.
        try add("gradient-in-format-1", "Gradient tool styles in profile format 1.") {
            var style = Style.defaults(for: .rectangle)
            style.fill = RGBA(1, 0, 0, 0.4); style.fillGradient = GradientPreset.ember.gradient
            $0.toolStyles["rectangle"] = style
        }
        try add("format-3", "A profile format this version cannot read.") { $0.schemaVersion = 3 }
        try add("shortcut-version-3", "A shortcut format this version cannot read.") { $0.shortcutVersion = 3 }
        try add("global-without-modifier", "A global shortcut without Control, Option or Command.") {
            $0.activationShortcut = ActivationShortcut(keyCode: 19, character: "2", shift: true)
        }
        try add("global-two-characters", "A global shortcut with two characters.") {
            $0.activationShortcut = ActivationShortcut(keyCode: 19, character: "22", control: true)
        }
        try add("global-symbol", "A global shortcut on a symbol key.") {
            $0.activationShortcut = ActivationShortcut(keyCode: 27, character: "-", control: true)
        }
        try add("global-key-code-128", "A global shortcut with a key code above 127.") {
            $0.activationShortcut = ActivationShortcut(keyCode: 128, character: "2", control: true)
        }
        try add("global-duplicate", "Hide only uses the same keys as Show and hide.") {
            $0.hideShortcut = ActivationShortcut(keyCode: 19, character: "x", control: true)
        }
        try add("toolbar-without-select", "A toolbar tool list without Select.") { $0.toolbarTools = ["pen", "arrow"] }
        try add("toolbar-duplicate", "A toolbar tool list with a tool twice.") { $0.toolbarTools = ["select", "pen", "pen"] }
        try add("toolbar-unknown-tool", "A toolbar tool list with an unknown tool.") { $0.toolbarTools = ["select", "lasso"] }
        try add("toolbar-empty", "An empty toolbar tool list.") { $0.toolbarTools = [] }
        try add("spotlight-49", "Spotlight radius below 50.") { $0.spotlightRadius = 49.9 }
        try add("spotlight-351", "Spotlight radius above 350.") { $0.spotlightRadius = 350.1 }
        try add("style-unknown-tool", "A tool style for an unknown tool.") { $0.toolStyles["lasso"] = Style() }
        try add("style-invalid", "A tool style with a width of zero.") {
            var style = Style(); style.width = 0
            $0.toolStyles["pen"] = style
        }
        try add("tool-key-uppercase", "A tool key in uppercase.") { $0.toolShortcuts["7"] = nil; $0.toolShortcuts["P"] = "pen" }
        try add("tool-key-two-characters", "A tool key with two characters.") { $0.toolShortcuts["7"] = nil; $0.toolShortcuts["pp"] = "pen" }
        try add("tool-key-symbol", "A tool key on a symbol.") { $0.toolShortcuts["7"] = nil; $0.toolShortcuts["-"] = "pen" }
        try add("tool-key-ink-color", "A tool key on an ink color letter.") { $0.toolShortcuts["f"] = nil; $0.toolShortcuts["g"] = "grid" }
        try add("tool-key-twice", "Two keys for one tool.") { $0.toolShortcuts["j"] = "pen" }
        try add("tool-key-unknown-tool", "A key for an unknown tool.") { $0.toolShortcuts["j"] = "lasso" }
        try add("command-key-shift-digit", "A button key on Shift with a digit.") { $0.commandShortcuts = ["⇧1": "action.spotlight"] }
        try add("command-key-unknown", "A key for an unknown button.") { $0.commandShortcuts = ["j": "action.dance"] }
        try add("command-key-ink-color", "A button key on an ink color letter with Shift.") { $0.commandShortcuts = ["⇧r": "action.board"] }
        try add("command-key-plain-tool", "A plain tool key stored with the button keys.") {
            $0.toolShortcuts["7"] = nil; $0.commandShortcuts = ["j": "tool.pen"]
        }
        try add("command-key-collision", "A button key that a tool already uses.") { $0.commandShortcuts = ["a": "action.spotlight"] }
        try add("command-twice", "Two keys for one button.") { $0.commandShortcuts = ["j": "action.spotlight", "⇧j": "action.spotlight"] }
        try add("toolbar-items-without-select", "A toolbar item list without Select.") { $0.toolbarItems = ["tool.pen"] }
        try add("toolbar-items-fixed-button", "A toolbar item list with Properties, which is always shown.") {
            $0.toolbarItems = ["tool.select", "action.properties"]
        }
        try add("cursor-highlight-invalid", "A pointer halo diameter below 16.") {
            var halo = CursorHighlightSettings(); halo.diameter = 15
            $0.cursorHighlight = halo
        }
        try add("cover-mode-pen", "A last cover mode that is not a cover or effect.") { $0.lastCoverMode = .pen }

        var records: [J] = []
        for (name, note, input) in cases {
            corpus.data("profiles/\(name).json", input)
            let read = outcome { .value(try WorkspacePreferences.read(input)) }
            var record: [String: J] = ["name": .string(name), "note": .string(note), "file": .string("\(name).json"), "read": read]
            if let preferences = try? WorkspacePreferences.read(input) {
                record["afterSaving"] = outcome { .value(try WorkspacePreferences.read(preferences.data())) }
                record["localShortcuts"] = .array(preferences.localShortcuts
                    .map { ($0.key.storageKey, $0.value.id) }.sorted { $0.0 < $1.0 }.map { [.string($0.0), .string($0.1)] })
            }
            records.append(.object(record))
        }
        records.append(["name": "file-over-256-kb", "recipe": "262,145 bytes of spaces, read with WorkspacePreferences.read.",
                        "read": outcome { .value(try WorkspacePreferences.read(Data(repeating: 0x20, count: 256 * 1_024 + 1))) }])
        try corpus.lines("profiles/index.jsonl", records)

        try corpus.json("profiles/visibility-styles.json", [
            "available": .array(VisibilityAnimationStyle.available.map { .string($0.rawValue) }),
            "resolved": .object(Dictionary(uniqueKeysWithValues: VisibilityAnimationStyle.allCases.map { ($0.rawValue, J.string($0.resolved.rawValue)) })),
            "defaultToolbar": .array(WorkspacePreferences.defaultToolbar.map { .string($0.rawValue) }),
            "defaultToolShortcuts": .object(WorkspacePreferences.defaultToolShortcuts.mapValues { .string($0) }),
            "legacyToolShortcuts": .object(WorkspacePreferences.legacyToolShortcuts.mapValues { .string($0) })
        ])
    }

    static func bytes(_ preferences: WorkspacePreferences) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(preferences)
    }

    private static func full(_ preferences: inout WorkspacePreferences) {
        preferences.hideShortcut = ActivationShortcut(keyCode: 5, character: "g", option: true, command: true)
        preferences.zoomShortcut = ActivationShortcut(keyCode: 6, character: "z", control: true, shift: true)
        preferences.resumeShortcut = ActivationShortcut(keyCode: 15, character: "r", control: true, option: true)
        preferences.smartDraw = true
        preferences.spotlightRadius = 200
        preferences.toolbarOrientation = .vertical
        preferences.toolbarDockEdge = .left
        preferences.lastCoverMode = .pixelate
        preferences.toolbarTools = ["select", "pen", "arrow", "rectangle"]
        preferences.showMenuBarIcon = false
        preferences.cursorStyle = .crosshair
        preferences.quickPreferencesExpanded = true
        preferences.inspectorPinned = true
        preferences.animateVisibility = true
        preferences.visibilityAnimationStyle = .magic
        preferences.theme = .dark
        preferences.showShortcutHints = false
        var halo = CursorHighlightSettings()
        halo.shape = .hexagon; halo.visibility = .moving; halo.color = RGBA(0.2, 0.6, 1)
        halo.diameter = 48; halo.borderWidth = 4; halo.glowRadius = 8; halo.borderStyle = .dashed; halo.clickEffect = .ripple
        preferences.cursorHighlight = halo
        preferences.toolShortcuts["5"] = nil
        preferences.commandShortcuts = ["⇧a": "tool.arrow", "j": "action.spotlight", "⇧l": "action.pointerLens"]
        preferences.toolbarSize = .minimal
        preferences.shareToolStyles = true
        preferences.toolbarItems = ["tool.select", "tool.pen", "action.spotlight", "tool.text", "action.shareRegion"]
        var pen = Style.defaults(for: .pen)
        pen.width = 5; pen.color = .blue
        var arrow = Style.defaults(for: .arrow)
        arrow.headScale = 1.25; arrow.endHead = .filled
        preferences.toolStyles = ["pen": pen, "arrow": arrow]
    }
}
