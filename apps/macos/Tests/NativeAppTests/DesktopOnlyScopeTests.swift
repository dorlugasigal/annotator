import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class DesktopOnlyScopeTests: XCTestCase {
    func testImageEditorIsNotPartOfTheApplicationModule() {
        XCTAssertNil(NSClassFromString("AnnotatorApp.SnapshotWorkspace"))
        XCTAssertNil(NSClassFromString("AnnotatorApp.NativeCanvasFixture"))
        XCTAssertTrue(String(reflecting: NativeCanvasFixture.self).hasPrefix("NativeAppTests."))
        XCTAssertTrue(Bundle(for: NativeCanvasFixture.self).bundlePath.hasSuffix(".xctest"))
    }

    func testExtrasHasDesktopLayersButNoOpenImageEditor() throws {
        let suite = "desktop-only-library-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let host = NSHostingView(rootView: ToolLibrary(model: model, section: .constant(.images), dismiss: {}))
        let panel = NSPanel(contentRect: CGRect(x: 80, y: 80, width: 360, height: 300),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close(); model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        NativeAccessibilityNode.enable()
        RunLoop.current.run(until: Date().addingTimeInterval(0.08))
        let labels = NativeAccessibilityNode.nodes(in: host).compactMap { $0.accessibilityLabel() }
        XCTAssertTrue(labels.contains("Paste image"))
        XCTAssertTrue(labels.contains("Insert image"))
        XCTAssertFalse(labels.contains("Open image"))
        XCTAssertFalse(labels.contains("Edit image separately…"))
        XCTAssertEqual(ToolLibrarySection.images.rawValue, "Extras")
    }

    func testNormalActivationCreatesOnlyLiveDesktopSessions() throws {
        let suite = "desktop-only-activation-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let app = AppDelegate(model: model)
        defer { app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification)); defaults.removePersistentDomain(forName: suite) }
        model.setVisibilityAnimation(false)
        app.resumeDrawing()
        let session = try XCTUnwrap(model.active)
        session.panel.ignoresMouseEvents = true
        XCTAssertNil(session.sourceImage)
        XCTAssertFalse(model.hasReferenceImage)
        XCTAssertEqual(session.panel.title, "Annotator drawing canvas")
        XCTAssertTrue(app.toolbarIsVisible)
        app.hideTools()
    }
}
