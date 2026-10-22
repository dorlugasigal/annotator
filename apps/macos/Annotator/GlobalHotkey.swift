import AppKit
import Carbon.HIToolbox
import AnnotationCore

@MainActor
final class GlobalHotkey {
    private var hotkey: EventHotKeyRef?
    private let identifier: UInt32
    private static var nextIdentifier: UInt32 = 1
    private static var handler: EventHandlerRef?
    private static var actions: [UInt32: () -> Void] = [:]
    var eventID: EventHotKeyID { EventHotKeyID(signature: 0x414E4E4F, id: identifier) }

    init(shortcut: ActivationShortcut = .standard, action: @escaping () -> Void) throws {
        try shortcut.validate()
        identifier = Self.nextIdentifier
        Self.nextIdentifier &+= 1
        try Self.installDispatcherIfNeeded()
        let id = EventHotKeyID(signature: 0x414E4E4F, id: identifier)
        let modifiers: UInt32 = (shortcut.control ? UInt32(controlKey) : 0)
            | (shortcut.option ? UInt32(optionKey) : 0) | (shortcut.command ? UInt32(cmdKey) : 0)
            | (shortcut.shift ? UInt32(shiftKey) : 0)
        let registered = RegisterEventHotKey(shortcut.keyCode, modifiers,
                                            id, GetApplicationEventTarget(), 0, &hotkey)
        guard registered == noErr else {
            Self.removeDispatcherIfIdle()
            throw HotkeyError.registration(registered, shortcut: shortcut.label)
        }
        Self.actions[identifier] = action
    }

    func unregister() {
        if let hotkey { UnregisterEventHotKey(hotkey) }
        Self.actions.removeValue(forKey: identifier)
        hotkey = nil
        Self.removeDispatcherIfIdle()
    }

    private static func installDispatcherIfNeeded() throws {
        guard handler == nil else { return }
        var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            guard let event else { return OSStatus(eventNotHandledErr) }
            var received = EventHotKeyID()
            guard GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                    nil, MemoryLayout<EventHotKeyID>.size, nil, &received) == noErr else {
                return OSStatus(eventNotHandledErr)
            }
            return MainActor.assumeIsolated {
                guard received.signature == 0x414E4E4F, let action = GlobalHotkey.actions[received.id] else {
                    return OSStatus(eventNotHandledErr)
                }
                action()
                return noErr
            }
        }, 1, &type, nil, &handler)
        guard status == noErr else { throw HotkeyError.registration(status) }
    }

    private static func removeDispatcherIfIdle() {
        if actions.isEmpty, let handler {
            RemoveEventHandler(handler)
            Self.handler = nil
        }
    }
}

enum HotkeyError: LocalizedError {
    case registration(OSStatus, shortcut: String? = nil)
    var errorDescription: String? {
        switch self {
        case .registration(let status, let shortcut):
            "The \(shortcut ?? "global keyboard") shortcut could not be registered (error \(status)). Another app or macOS may be using it. Change it in Settings > Toolbar & Shortcuts, or use the menu-bar command."
        }
    }
}
