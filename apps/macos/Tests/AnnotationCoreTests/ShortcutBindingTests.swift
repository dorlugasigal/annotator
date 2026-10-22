import Foundation
import Testing
@testable import AnnotationCore

@Test func localShortcutsParseStorageKeysAndRejectUnsafeKeys() throws {
    #expect(LocalShortcut(key: "T")?.key == "t")
    #expect(LocalShortcut(key: "t", shift: true)?.label == "⇧T")
    #expect(LocalShortcut(key: "t", shift: true)?.spokenLabel == "Shift + T")
    #expect(LocalShortcut(storageKey: "⇧t") == LocalShortcut(key: "t", shift: true))
    #expect(LocalShortcut(storageKey: "5") == LocalShortcut(key: "5"))
    #expect(LocalShortcut(key: "5", shift: true) == nil, "Shift changes digits on many layouts.")
    #expect(LocalShortcut(key: "ab") == nil)
    #expect(LocalShortcut(key: "-") == nil)
    #expect(LocalShortcut(storageKey: "T") == nil)
    #expect(ShortcutCommand(id: "tool.star") == .tool(.star))
    #expect(ShortcutCommand(id: "action.spotlight") == .action(.spotlight))
    #expect(ShortcutCommand(id: "action.unknown") == nil)
}

@Test func everyToolAndButtonCanHaveAKeyAndMovedKeysAreReported() throws {
    var preferences = WorkspacePreferences()
    let star = try #require(LocalShortcut(key: "s", shift: true))
    #expect(try preferences.bind(star, to: .tool(.star)) == nil)
    #expect(preferences.shortcut(for: .tool(.star)) == star)
    #expect(preferences.toolShortcuts["s"] == Tool.cover.rawValue, "Plain S keeps Effects.")
    #expect(preferences.command(forPressed: star) == .tool(.star))

    let e = try #require(LocalShortcut(key: "e"))
    #expect(try preferences.bind(e, to: .action(.spotlight)) == nil)
    #expect(preferences.command(forPressed: e) == .action(.spotlight))

    // Taking a key that a tool uses moves it and says which tool lost it.
    let two = try #require(LocalShortcut(key: "2"))
    #expect(try preferences.bind(two, to: .action(.properties)) == .tool(.rectangle))
    #expect(preferences.shortcut(for: .tool(.rectangle)) == nil)
    #expect(preferences.toolShortcuts["2"] == nil)
    #expect(preferences.command(forPressed: two) == .action(.properties))

    // A tool can take a plain key from a button; it returns to the compatible tool map.
    #expect(try preferences.bind(two, to: .tool(.rectangle)) == .action(.properties))
    #expect(preferences.toolShortcuts["2"] == Tool.rectangle.rawValue)
    #expect(preferences.shortcut(for: .action(.properties)) == nil)

    // Rebinding keeps one key per command, and clearing removes it.
    let j = try #require(LocalShortcut(key: "j"))
    try preferences.bind(j, to: .tool(.star))
    #expect(preferences.shortcut(for: .tool(.star)) == j)
    #expect(preferences.command(forPressed: star) == .tool(.cover), "Shift-S falls back to plain S when unassigned.")
    try preferences.bind(nil, to: .tool(.star))
    #expect(preferences.shortcut(for: .tool(.star)) == nil)
    #expect(try WorkspacePreferences.read(preferences.data()) == preferences)
}

@Test func inkColorKeysStayReservedWithAndWithoutShift() throws {
    var preferences = WorkspacePreferences()
    let before = preferences
    for key in InkShortcut.choices.keys {
        for shift in [false, true] {
            let shortcut = try #require(LocalShortcut(key: key, shift: shift))
            #expect(throws: CanvasError.self) { try preferences.bind(shortcut, to: .tool(.star)) }
            #expect(throws: CanvasError.self) { try preferences.bind(shortcut, to: .action(.spotlight)) }
        }
    }
    #expect(preferences == before)
}

@Test func commandShortcutValidationRejectsDuplicatesAndStaysOptional() throws {
    var preferences = WorkspacePreferences()
    preferences.commandShortcuts = ["1": ShortcutCommand.action(.spotlight).id]
    #expect(throws: CanvasError.self) { try preferences.validate() }
    preferences.commandShortcuts = ["⇧a": "tool.pen"]
    #expect(throws: CanvasError.self) { try preferences.validate() }
    preferences.commandShortcuts = ["j": "tool.star"]
    #expect(throws: CanvasError.self) { try preferences.validate() }
    preferences.commandShortcuts = ["⇧r": "action.spotlight"]
    #expect(throws: CanvasError.self) { try preferences.validate() }
    preferences.commandShortcuts = ["j": "action.nothing"]
    #expect(throws: CanvasError.self) { try preferences.validate() }
    preferences.commandShortcuts = ["j": "action.spotlight", "⇧j": "tool.star", "e": "action.board"]
    preferences.toolbarSize = .minimal
    preferences.shareToolStyles = true
    let restored = try WorkspacePreferences.read(preferences.data())
    #expect(restored == preferences)

    // Older profiles have none of the new fields; they read with no extra keys and the original toolbar.
    var object = try #require(JSONSerialization.jsonObject(with: WorkspacePreferences().data()) as? [String: Any])
    object.removeValue(forKey: "commandShortcuts")
    object.removeValue(forKey: "toolbarSize")
    object.removeValue(forKey: "shareToolStyles")
    let legacy = try WorkspacePreferences.read(JSONSerialization.data(withJSONObject: object))
    #expect(legacy.commandShortcuts == nil && legacy.toolbarSize == nil && legacy.shareToolStyles == nil)

    // The legacy single-key API still refuses a key a button uses.
    preferences = WorkspacePreferences()
    try preferences.bind(LocalShortcut(key: "j"), to: .action(.clearAll))
    #expect(throws: CanvasError.self) { try preferences.assignShortcut("j", to: .pen) }
    preferences.resetLocalShortcuts()
    #expect(preferences.toolShortcuts == WorkspacePreferences.defaultToolShortcuts)
    #expect(preferences.commandShortcuts == nil)
}

@Test func sharedStyleCopiesOnlyWhatChanged() {
    var pen = Style.defaults(for: .pen)
    pen.width = 6
    let highlighter = Style.defaults(for: .highlighter)
    var shared = highlighter
    var changed = pen
    changed.setPaintColor(.blue, fill: false)
    shared.adoptSharedChanges(from: pen, to: changed, supportsFill: false)
    #expect(shared.color == RGBA.blue)
    #expect(shared.width == highlighter.width, "Unchanged width stays with each tool.")

    var rectangle = Style.defaults(for: .rectangle)
    rectangle.fontSize = 40
    var filled = changed
    filled.setFillEnabled(true)
    filled.pattern = .dashed
    filled.roughness = 2
    filled.endHead = .filled
    filled.fontSize = 12
    rectangle.adoptSharedChanges(from: changed, to: filled, supportsFill: true)
    #expect(rectangle.fill == filled.fill)
    #expect(rectangle.pattern == .dashed && rectangle.roughness == 2)
    #expect(rectangle.fontSize == 40, "Text size is tool-specific.")
    #expect(rectangle.endHead == Style.defaults(for: .rectangle).endHead, "Arrowheads are tool-specific.")

    var line = Style.defaults(for: .line)
    line.adoptSharedChanges(from: changed, to: filled, supportsFill: false)
    #expect(line.fill == nil, "Tools without fill never gain one.")
    #expect(Tool.pen.sharesStyle && Tool.rectangle.sharesStyle && Tool.text.sharesStyle)
    #expect(!Tool.select.sharesStyle && !Tool.eraser.sharesStyle && !Tool.blur.sharesStyle && !Tool.cover.sharesStyle)
}

@Test func toolbarItemsAllowPresentingButtonsButKeepSelectAndFixedButtons() throws {
    var preferences = WorkspacePreferences()
    preferences.toolbarItems = ["tool.select", "tool.pen", "tool.blur", "action.spotlight", "action.smartDraw"]
    preferences.toolbarTools = ["select", "pen", "blur"]
    #expect(try WorkspacePreferences.read(preferences.data()) == preferences)
    for invalid in [["tool.pen", "action.spotlight"], ["tool.select", "action.properties"], ["tool.select", "action.board"],
                    ["tool.select", "tool.pen", "tool.pen"], ["tool.select", "action.nothing"], [String]()] {
        preferences.toolbarItems = invalid
        #expect(throws: CanvasError.self) { try preferences.validate() }
    }
    #expect(ShortcutAction.toolbarChoices == [.spotlight, .cursorHalo, .pointerLens, .smartDraw, .shareRegion])
    preferences.toolbarItems = ["tool.select", "action.shareRegion"]
    preferences.toolbarTools = ["select"]
    try preferences.bind(LocalShortcut(key: "j"), to: .action(.shareRegion))
    #expect(try WorkspacePreferences.read(preferences.data()) == preferences)
    #expect(!ShortcutAction.clearAll.canBeInToolbar)
}
