import AppKit
import Carbon.HIToolbox
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class GlobalHotkeyTests: XCTestCase {
    func testSeparateRegisteredHotkeysDispatchOnlyTheirOwnActions() throws {
        var firstCount = 0, secondCount = 0
        let first = try GlobalHotkey(shortcut: ActivationShortcut(keyCode: 83, character: "1",
                                                                 control: true, option: true, command: true, shift: true)) {
            firstCount += 1
        }
        defer { first.unregister() }
        let second = try GlobalHotkey(shortcut: ActivationShortcut(keyCode: 84, character: "2",
                                                                  control: true, option: true, command: true, shift: true)) {
            secondCount += 1
        }
        defer { second.unregister() }
        for id in [first.eventID, second.eventID] {
            var event: EventRef?
            XCTAssertEqual(CreateEvent(nil, OSType(kEventClassKeyboard), UInt32(kEventHotKeyPressed),
                                       GetCurrentEventTime(), 0, &event), noErr)
            let dispatched = try XCTUnwrap(event)
            defer { ReleaseEvent(dispatched) }
            var value = id
            XCTAssertEqual(SetEventParameter(dispatched, EventParamName(kEventParamDirectObject),
                                             EventParamType(typeEventHotKeyID), MemoryLayout<EventHotKeyID>.size, &value), noErr)
            XCTAssertEqual(SendEventToEventTarget(dispatched, GetApplicationEventTarget()), noErr)
        }
        XCTAssertEqual(firstCount, 1)
        XCTAssertEqual(secondCount, 1)
    }
}
