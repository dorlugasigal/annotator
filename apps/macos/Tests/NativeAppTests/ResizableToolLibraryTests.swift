import AppKit
import XCTest
@testable import AnnotatorApp

@MainActor
final class ResizableToolLibraryTests: XCTestCase {
    func testLibraryResizesToMoreColumnsReopensAndClosesWithWorkspace() async throws {
        let suite = "resizable-library-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.resumeDrawing()
        model.active?.panel.ignoresMouseEvents = true
        app.toolbarWindow?.ignoresMouseEvents = true
        model.openToolLibrary(.shapes)
        let window = try XCTUnwrap(app.toolLibraryWindow)
        window.ignoresMouseEvents = true
        XCTAssertTrue(window.styleMask.contains(.resizable))
        window.setContentSize(CGSize(width: 368, height: 310))
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(160))
        let host = try XCTUnwrap(window.contentView)
        let small = NativeAccessibilityNode.nodes(in: host)
        let smallRectangle = try XCTUnwrap(small.first { $0.accessibilityLabel() == "Rectangle" && $0.accessibilityRole() == .button })
        let smallDatabase = try XCTUnwrap(small.first { $0.accessibilityLabel() == "Database" && $0.accessibilityRole() == .button })
        XCTAssertGreaterThan(abs(smallRectangle.accessibilityFrame().midY-smallDatabase.accessibilityFrame().midY), 20)
        window.setContentSize(CGSize(width: 850, height: 660))
        try await Task.sleep(for: .milliseconds(160))
        let large = NativeAccessibilityNode.nodes(in: host)
        let rectangle = try XCTUnwrap(large.first { $0.accessibilityLabel() == "Rectangle" && $0.accessibilityRole() == .button })
        let database = try XCTUnwrap(large.first { $0.accessibilityLabel() == "Database" && $0.accessibilityRole() == .button })
        XCTAssertEqual(rectangle.accessibilityFrame().midY, database.accessibilityFrame().midY, accuracy: 1)
        let axes = try XCTUnwrap(large.first { $0.accessibilityLabel() == "Axes" && $0.accessibilityRole() == .button })
        XCTAssertTrue(window.frame.contains(axes.accessibilityFrame()))
        let size = window.frame.size
        window.close()
        XCTAssertFalse(window.isVisible)
        model.openToolLibrary(.shapes)
        XCTAssertTrue(app.toolLibraryWindow === window)
        XCTAssertEqual(window.frame.size, size)
        app.hideTools()
        XCTAssertFalse(window.isVisible)
        XCTAssertNotNil(window.contentViewController)
        app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
        XCTAssertNil(app.toolLibraryWindow)
        XCTAssertNil(window.contentViewController)
    }

    func testChooseLoadBalancerDrawThenReopenAndChooseAnotherShape() async throws {
        let suite = "library-reopen-after-draw-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.resumeDrawing()
        let session = try XCTUnwrap(model.active)
        session.panel.ignoresMouseEvents = true
        app.toolbarWindow?.ignoresMouseEvents = true
        NativeAccessibilityNode.enable()
        for name in ["Load balancer", "Server"] {
            let toolbar = try XCTUnwrap(app.toolbarWindow?.contentView)
            let more = try XCTUnwrap(NativeAccessibilityNode.nodes(in: toolbar).first {
                $0.accessibilityLabel() == "Tool library" && $0.accessibilityRole() == .button
            })
            XCTAssertTrue(more.accessibilityPerformPress())
            try await Task.sleep(for: .milliseconds(80))
            let window = try XCTUnwrap(app.toolLibraryWindow)
            window.setContentSize(CGSize(width: 850, height: 660))
            try await Task.sleep(for: .milliseconds(120))
            let host = try XCTUnwrap(window.contentView)
            let button = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                $0.accessibilityLabel() == name && $0.accessibilityRole() == .button
            })
            XCTAssertTrue(button.accessibilityPerformPress())
            try await Task.sleep(for: .milliseconds(80))
            XCTAssertEqual(model.tool.label, name)
            XCTAssertFalse(window.isVisible)
            func event(_ type: NSEvent.EventType, _ point: CGPoint) throws -> NSEvent {
                try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point, to: nil),
                    modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber,
                    context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
            }
            session.canvas.mouseDown(with: try event(.leftMouseDown, CGPoint(x: 300, y: 300)))
            session.canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 480, y: 430)))
            session.canvas.mouseUp(with: try event(.leftMouseUp, CGPoint(x: 480, y: 430)))
            XCTAssertEqual(session.document.elements.last?.tool.label, name)
        }
        XCTAssertEqual(session.document.elements.count, 2)
    }
}
