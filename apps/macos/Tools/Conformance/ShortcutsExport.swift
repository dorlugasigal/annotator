import AnnotationCore
import Carbon.HIToolbox
import Foundation

/// Local and global keyboard shortcuts: parsing, labels, refusals, collisions and macOS key codes.
@MainActor
enum ShortcutsExport {
    static func run(_ corpus: Corpus) throws {
        corpus.describe("shortcuts", "LocalShortcut parsing and labels, command IDs, global shortcut checks, scripted bind and assign sessions, key lookups and the macOS key codes the app stores.")
        try localKeys(corpus)
        try commands(corpus)
        try globalShortcuts(corpus)
        try bindScript(corpus)
        try assignScript(corpus)
        try keyCodes(corpus)
    }

    private static func describe(_ shortcut: LocalShortcut?) -> J {
        guard let shortcut else { return .null }
        return ["key": .string(shortcut.key), "shift": .bool(shortcut.shift), "storageKey": .string(shortcut.storageKey),
                "label": .string(shortcut.label), "spokenLabel": .string(shortcut.spokenLabel), "isInkColor": .bool(shortcut.isInkColor)]
    }

    private static func localKeys(_ corpus: Corpus) throws {
        let keys = ["a", "z", "A", "Z", "0", "9", "ß", "é", "É", "e\u{301}", "ı", "İ", "ǅ", "Ａ", "٣", "²", "Ⅻ", "ω", "Ω", "中",
                    "ŉ", "ﬁ", "!", "-", " ", "", "ab", "⇧", "⇧a", "\u{0}", "r", "k", "1️⃣"]
        var records: [J] = []
        for key in keys {
            for shift in [false, true] {
                records.append(["input": ["key": .string(key), "shift": .bool(shift)], "result": describe(LocalShortcut(key: key, shift: shift))])
            }
        }
        for storageKey in ["a", "A", "⇧a", "⇧A", "⇧1", "⇧", "⇧⇧a", "1", "é", "⇧é", "⇧ß", "⇧Ω", "⇧ω", "shift+a", "⇧ a"] {
            records.append(["input": ["storageKey": .string(storageKey)], "result": describe(LocalShortcut(storageKey: storageKey))])
        }
        try corpus.lines("shortcuts/local-keys.jsonl", records)
        try corpus.json("shortcuts/ink-colors.json", .object(InkShortcut.choices.mapValues { ink in
            ["name": .string(ink.name), "color": .value(ink.color)]
        }))
    }

    private static func commands(_ corpus: Corpus) throws {
        var records: [J] = []
        for id in ["tool.pen", "tool.select", "tool.cover", "tool.", "tool.Pen", "tool.pen ", "pen", "action.spotlight",
                   "action.clearAll", "action.", "action.dance", "Action.board", "tool.action.board"] {
            records.append(["id": .string(id), "result": ShortcutCommand(id: id).map { ["id": .string($0.id), "label": .string($0.label)] } ?? .null])
        }
        let tools: [J] = Tool.allCases.map { tool in
            let command = ShortcutCommand.tool(tool)
            return ["id": .string(command.id), "label": .string(command.label), "toolLabel": .string(tool.label),
                    "sharesStyle": .bool(tool.sharesStyle), "isShape": .bool(tool.isShape), "supportsFill": .bool(tool.supportsFill),
                    "isProtection": .bool(tool.isProtection), "systemShapeAspectRatio": tool.systemShapeAspectRatio.map { .double($0) } ?? .null]
        }
        let actions: [J] = ShortcutAction.allCases.map { action in
            ["id": .string(ShortcutCommand.action(action).id), "label": .string(action.label), "detail": .string(action.detail),
             "symbol": .string(action.symbol), "canBeInToolbar": .bool(action.canBeInToolbar), "isMenu": .bool(action.isMenu)]
        }
        try corpus.json("shortcuts/commands.json", ["parsing": .array(records), "tools": .array(tools), "actions": .array(actions),
            "toolbarChoices": .array(ShortcutAction.toolbarChoices.map { .string($0.rawValue) })])
    }

    private static func globalShortcuts(_ corpus: Corpus) throws {
        let cases: [(String, ActivationShortcut)] = [
            ("standard", .standard), ("hideTools", .hideTools), ("liveZoom", .liveZoom), ("resumeDrawing", .resumeDrawing),
            ("all modifiers", ActivationShortcut(keyCode: 0, character: "a", control: true, option: true, command: true, shift: true)),
            ("command only", ActivationShortcut(keyCode: 37, character: "l", command: true)),
            ("shift only", ActivationShortcut(keyCode: 0, character: "a", shift: true)),
            ("no modifiers", ActivationShortcut(keyCode: 0, character: "a")),
            ("lowercase stored", ActivationShortcut(keyCode: 0, character: "a", option: true)),
            ("non-Latin letter", ActivationShortcut(keyCode: 0, character: "ש", control: true)),
            ("accented letter", ActivationShortcut(keyCode: 0, character: "é", control: true)),
            ("decomposed letter", ActivationShortcut(keyCode: 0, character: "e\u{301}", control: true)),
            ("German sharp s", ActivationShortcut(keyCode: 0, character: "ß", control: true)),
            ("symbol", ActivationShortcut(keyCode: 27, character: "-", control: true)),
            ("space", ActivationShortcut(keyCode: 49, character: " ", control: true)),
            ("empty character", ActivationShortcut(keyCode: 0, character: "", control: true)),
            ("key code 127", ActivationShortcut(keyCode: 127, character: "a", control: true)),
            ("key code 128", ActivationShortcut(keyCode: 128, character: "a", control: true))
        ]
        var records: [J] = cases.map { name, shortcut in
            ["name": .string(name), "shortcut": .value(shortcut), "label": .string(shortcut.label),
             "validate": outcome { try shortcut.validate(); return .null }]
        }
        let pairs: [(ActivationShortcut, ActivationShortcut)] = [
            (.standard, ActivationShortcut(keyCode: 19, character: "@", control: true)),
            (.standard, ActivationShortcut(keyCode: 19, character: "2", control: true, shift: true)),
            (.standard, ActivationShortcut(keyCode: 84, character: "2", control: true))
        ]
        records += pairs.map { a, b in ["a": .value(a), "b": .value(b), "usesSameKeys": .bool(a.usesSameKeys(as: b))] }
        try corpus.lines("shortcuts/global.jsonl", records)
    }

    private static func state(_ preferences: WorkspacePreferences) -> [String: J] {
        let bindings = preferences.localShortcuts.map { ($0.key.storageKey, $0.value.id) }.sorted { $0.0 < $1.0 }
        let pressed = ["a", "j", "l", "1", "5", "7", "z"].flatMap { key in [false, true].compactMap { LocalShortcut(key: key, shift: $0) } }
        let commandIDs = Tool.allCases.map { ShortcutCommand.tool($0) } + ShortcutAction.allCases.map { ShortcutCommand.action($0) }
        return [
            "toolShortcuts": .object(preferences.toolShortcuts.mapValues { .string($0) }),
            "commandShortcuts": preferences.commandShortcuts.map { .object($0.mapValues { .string($0) }) } ?? .null,
            "localShortcuts": .array(bindings.map { [.string($0.0), .string($0.1)] }),
            "commandForPressed": .object(Dictionary(uniqueKeysWithValues: pressed.map {
                ($0.storageKey, preferences.command(forPressed: $0).map { J.string($0.id) } ?? .null)
            })),
            "shortcutFor": .object(Dictionary(uniqueKeysWithValues: commandIDs.map {
                ($0.id, preferences.shortcut(for: $0).map { J.string($0.storageKey) } ?? .null)
            }))
        ]
    }

    private static func bindScript(_ corpus: Corpus) throws {
        var preferences = WorkspacePreferences()
        var records: [J] = [J.object(["step": "start with the defaults"].merging(state(preferences)) { $1 })]
        func bind(_ key: String?, shift: Bool = false, _ id: String) {
            let shortcut = key.flatMap { LocalShortcut(key: $0, shift: shift) }
            let command = ShortcutCommand(id: id)!
            let label = key.map { (shift ? "⇧" : "") + $0 } ?? "no key"
            let result = outcome { .string(try preferences.bind(shortcut, to: command)?.id ?? "") }
            records.append(.object(["step": .string("bind \(label) to \(id)"), "result": result].merging(state(preferences)) { $1 }))
        }
        bind("k", "action.zoomMenu")
        bind("j", "action.spotlight")
        bind("a", shift: true, "tool.arrow")
        bind("a", "action.board")
        bind("r", "tool.pen")
        bind("b", shift: true, "action.cursorHalo")
        bind(nil, "action.spotlight")
        bind("7", "tool.text")
        bind("5", "action.clearAll")
        bind("j", shift: true, "tool.select")
        bind("j", "tool.select")
        bind("q", shift: true, "action.smartDraw")
        bind("q", shift: true, "action.pointerLens")
        preferences.resetLocalShortcuts()
        records.append(.object(["step": "reset local shortcuts"].merging(state(preferences)) { $1 }))
        try corpus.lines("shortcuts/bind-script.jsonl", records)
    }

    private static func assignScript(_ corpus: Corpus) throws {
        var preferences = WorkspacePreferences()
        var records: [J] = []
        func assign(_ key: String, _ tool: Tool) {
            let result = outcome { try preferences.assignShortcut(key, to: tool); return .null }
            records.append(.object(["step": .string("assign \"\(key)\" to \(tool.rawValue)"), "result": result].merging(state(preferences)) { $1 }))
        }
        assign("z", .pen)
        assign("1", .pen)
        assign("", .pen)
        assign("R", .rectangle)
        assign("J", .star)
        assign("j", .hexagon)
        _ = try? preferences.bind(LocalShortcut(key: "l"), to: .action(.spotlight))
        assign("l", .line)
        assign("ab", .line)
        try corpus.lines("shortcuts/assign-script.jsonl", records)
    }

    private static func keyCodes(_ corpus: Corpus) throws {
        let letters: [(String, Int)] = [
            ("a", kVK_ANSI_A), ("b", kVK_ANSI_B), ("c", kVK_ANSI_C), ("d", kVK_ANSI_D), ("e", kVK_ANSI_E), ("f", kVK_ANSI_F),
            ("g", kVK_ANSI_G), ("h", kVK_ANSI_H), ("i", kVK_ANSI_I), ("j", kVK_ANSI_J), ("k", kVK_ANSI_K), ("l", kVK_ANSI_L),
            ("m", kVK_ANSI_M), ("n", kVK_ANSI_N), ("o", kVK_ANSI_O), ("p", kVK_ANSI_P), ("q", kVK_ANSI_Q), ("r", kVK_ANSI_R),
            ("s", kVK_ANSI_S), ("t", kVK_ANSI_T), ("u", kVK_ANSI_U), ("v", kVK_ANSI_V), ("w", kVK_ANSI_W), ("x", kVK_ANSI_X),
            ("y", kVK_ANSI_Y), ("z", kVK_ANSI_Z), ("0", kVK_ANSI_0), ("1", kVK_ANSI_1), ("2", kVK_ANSI_2), ("3", kVK_ANSI_3),
            ("4", kVK_ANSI_4), ("5", kVK_ANSI_5), ("6", kVK_ANSI_6), ("7", kVK_ANSI_7), ("8", kVK_ANSI_8), ("9", kVK_ANSI_9)
        ]
        let keypad: [(String, Int)] = [
            ("0", kVK_ANSI_Keypad0), ("1", kVK_ANSI_Keypad1), ("2", kVK_ANSI_Keypad2), ("3", kVK_ANSI_Keypad3),
            ("4", kVK_ANSI_Keypad4), ("5", kVK_ANSI_Keypad5), ("6", kVK_ANSI_Keypad6), ("7", kVK_ANSI_Keypad7),
            ("8", kVK_ANSI_Keypad8), ("9", kVK_ANSI_Keypad9)
        ]
        let special: [(String, Int, String)] = [
            ("Return", kVK_Return, "Finishes a line or arrow drawn point by point; plays the pointer preview in Settings."),
            ("Space", kVK_Space, "Plays the pointer preview in Settings."),
            ("Delete", kVK_Delete, "Deletes the current selection on the canvas."),
            ("ForwardDelete", kVK_ForwardDelete, "Deletes the current selection on the canvas."),
            ("Escape", kVK_Escape, "Checked by the canvas, the menu bar flyout, the region guide, the tutorial, image pasting and shortcut recording."),
            ("LeftArrow", kVK_LeftArrow, "Nudges the selection by 1 point, or 10 with Shift."),
            ("RightArrow", kVK_RightArrow, "Nudges the selection by 1 point, or 10 with Shift."),
            ("DownArrow", kVK_DownArrow, "Nudges the selection by 1 point, or 10 with Shift."),
            ("UpArrow", kVK_UpArrow, "Nudges the selection by 1 point, or 10 with Shift.")
        ]
        try corpus.json("shortcuts/macos-key-codes.json", [
            "note": "Carbon virtual key codes (kVK_*) for the US ANSI layout. ActivationShortcut.keyCode stores these codes in profiles; the character is stored next to it. When Settings records a global shortcut, the digit keys use the digits map and other keys their unmodified character in lowercase.",
            "letters": .array(letters.map { ["character": .string($0.0), "keyCode": .int($0.1)] }),
            "keypad": .array(keypad.map { ["character": .string($0.0), "keyCode": .int($0.1)] }),
            "special": .array(special.map { ["name": .string($0.0), "keyCode": .int($0.1), "appUse": .string($0.2)] }),
            "settingsDigitMap": .object(["29": "0", "18": "1", "19": "2", "20": "3", "21": "4", "23": "5", "22": "6", "26": "7", "28": "8", "25": "9"]),
            "toolLibrary": ["shortcut": .value(ActivationShortcut(keyCode: 37, character: "l", command: true)),
                            "rule": "Command-L opens the Tools window unless a global shortcut uses the same keys."]
        ])
    }
}
