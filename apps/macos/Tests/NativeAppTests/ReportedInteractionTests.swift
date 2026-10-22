import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class ReportedInteractionTests: XCTestCase {
    func testEraserDeletesEveryUnlockedCensorAndUndoRestoresIt() throws {
        let suite = "eraser-censors-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 500, height: 400), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        for tool in [Tool.cover, .blur, .pixelate, .distort] {
            let element = Element(tool: tool, frame: Rect(x: 100, y: 100, width: 100, height: 100))
            try session.document.add(element)
            editor.model.choose(.eraser)
            func event(_ type: NSEvent.EventType) throws -> NSEvent {
                try XCTUnwrap(NSEvent.mouseEvent(with: type,
                    location: session.canvas.convert(element.frame.center.cgPoint, to: nil), modifierFlags: [],
                    timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
                    eventNumber: 0, clickCount: 1, pressure: 1))
            }
            session.canvas.mouseDown(with: try event(.leftMouseDown))
            XCTAssertTrue(session.canvas.eraserPreviewIDs.contains(element.id))
            session.canvas.mouseUp(with: try event(.leftMouseUp))
            XCTAssertFalse(session.document.elements.contains { $0.id == element.id })
            editor.model.undo()
            XCTAssertTrue(session.document.elements.contains { $0.id == element.id })
            session.document.select([element.id])
            try session.document.deleteSelection()
        }
    }

    func testStartupPlacementRespectsDockAndMenuBarOnEveryEdge() {
        for available in [
            CGRect(x: 0, y: 90, width: 1512, height: 850),
            CGRect(x: 100, y: 0, width: 1412, height: 940),
            CGRect(x: 0, y: 0, width: 1412, height: 940),
            CGRect(x: -1920, y: 50, width: 1800, height: 1000)
        ] {
            for (orientation, edges) in [
                (ToolbarOrientation.horizontal, [ToolbarDockEdge.top, .bottom]),
                (.vertical, [.left, .right])
            ] {
                for edge in edges {
                    let size = orientation == .horizontal ? CGSize(width: 880, height: 119) : CGSize(width: 68, height: 520)
                    let frame = ToolbarPlacement.frame(size: size, visibleFrame: available, orientation: orientation, edge: edge)
                    XCTAssertTrue(available.contains(frame))
                    switch edge {
                    case .top: XCTAssertEqual(frame.maxY, available.maxY - 12)
                    case .bottom: XCTAssertEqual(frame.minY, available.minY + 12)
                    case .left: XCTAssertEqual(frame.minX, available.minX + 12)
                    case .right: XCTAssertEqual(frame.maxX, available.maxX - 12)
                    default: XCTFail("Unexpected edge")
                    }
                }
            }
        }
    }

    func testVisualLibraryHasEveryToolOnceAndFitsCompactly() throws {
        let suite = "visual-library-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let tools = ToolLibrarySection.allCases.flatMap(\.tools) + [.blur, .pixelate, .distort]
        XCTAssertEqual(Set(tools), Set(Tool.allCases))
        XCTAssertEqual(tools.count, Set(tools).count)
        let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        for section in ToolLibrarySection.allCases {
            let host = NSHostingController(rootView: ToolLibrary(model: model, section: .constant(section), dismiss: {})
                .padding(16).environment(\.colorScheme, .dark))
            let size = host.sizeThatFits(in: CGSize(width: 368, height: 400))
            XCTAssertLessThanOrEqual(size.height, section == .shapes ? 320 : 260,
                                    "The expanded Shapes category has three rows; other categories keep their prior budget.")
            XCTAssertEqual(size.width, 368, accuracy: 1)
            host.view.setFrameSize(size)
            host.view.layoutSubtreeIfNeeded()
            let bitmap = try XCTUnwrap(host.view.bitmapImageRepForCachingDisplay(in: host.view.bounds))
            host.view.cacheDisplay(in: host.view.bounds, to: bitmap)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: output.appendingPathComponent("library-\(section.rawValue.lowercased()).png"))
        }
    }
}
