import AppKit
import SwiftUI
import XCTest
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class SnapshotToolbarSizingTests: XCTestCase {
    func testToolbarTracksWindowSizeAndRestoresMeasuredHeightWithoutResizingSource() async throws {
        let suite = "snapshot-toolbar-sizing-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 400, height: 240), background: .white)
        let workspace = NativeCanvasFixture(image: image, defaults: defaults)
        defer {
            workspace.close()
            defaults.removePersistentDomain(forName: suite)
        }
        let window = workspace.session.panel
        let container = try XCTUnwrap(window.contentView)
        let toolbar = try XCTUnwrap(container.subviews.compactMap { $0 as? NSHostingView<ToolbarView> }.first)

        func settleLayout() async throws {
            for _ in 0..<4 {
                container.layoutSubtreeIfNeeded()
                try await Task.sleep(for: .milliseconds(20))
            }
            container.layoutSubtreeIfNeeded()
        }

        func assertSourceUnchanged(file: StaticString = #filePath, line: UInt = #line) throws {
            let source = try XCTUnwrap(workspace.session.sourceImage, file: file, line: line)
            XCTAssertEqual(source.width, 400, file: file, line: line)
            XCTAssertEqual(source.height, 240, file: file, line: line)
        }

        func assertVisibleToolbarFits(file: StaticString = #filePath, line: UInt = #line) throws {
            let available = CGSize(width: container.bounds.width - 24, height: container.bounds.height)
            let reference = NSHostingController(rootView: ToolbarView(
                model: workspace.model, availableSize: available, showInspector: false))
            let measured = reference.sizeThatFits(in: available)
            XCTAssertTrue(workspace.toolsVisible, file: file, line: line)
            XCTAssertFalse(toolbar.isHidden, file: file, line: line)
            XCTAssertEqual(toolbar.rootView.availableSize.width, available.width, accuracy: 1, file: file, line: line)
            XCTAssertEqual(toolbar.rootView.availableSize.height, available.height, accuracy: 1, file: file, line: line)
            XCTAssertGreaterThan(measured.height, 0, file: file, line: line)
            XCTAssertEqual(toolbar.frame.width, ceil(measured.width), accuracy: 1, file: file, line: line)
            XCTAssertEqual(toolbar.frame.height, ceil(measured.height), accuracy: 1, file: file, line: line)
            XCTAssertGreaterThanOrEqual(toolbar.frame.minX, container.bounds.minX, file: file, line: line)
            XCTAssertLessThanOrEqual(toolbar.frame.maxX, container.bounds.maxX, file: file, line: line)
            XCTAssertEqual(toolbar.frame.midX, container.bounds.midX, accuracy: 1, file: file, line: line)
            XCTAssertGreaterThan(workspace.scroll.frame.height, 0, file: file, line: line)
            XCTAssertFalse(workspace.scroll.frame.intersects(toolbar.frame), file: file, line: line)
            try assertSourceUnchanged(file: file, line: line)
        }

        for size in [CGSize(width: 560, height: 540), CGSize(width: 1_100, height: 720),
                     CGSize(width: 440, height: 480)] {
            window.setContentSize(size)
            try await settleLayout()
            XCTAssertEqual(container.bounds.width, size.width, accuracy: 1)
            XCTAssertEqual(container.bounds.height, size.height, accuracy: 1)
            try assertVisibleToolbarFits()

            let visibleHeight = toolbar.frame.height
            let viewportHeight = workspace.scroll.frame.height
            workspace.setToolsVisible(false)
            try await settleLayout()
            XCTAssertFalse(workspace.toolsVisible)
            XCTAssertTrue(toolbar.isHidden)
            XCTAssertEqual(toolbar.frame.height, 0, accuracy: 1)
            XCTAssertEqual(workspace.scroll.frame.height, viewportHeight + visibleHeight, accuracy: 1)
            try assertSourceUnchanged()

            workspace.setToolsVisible(true)
            try await settleLayout()
            try assertVisibleToolbarFits()
            XCTAssertEqual(workspace.scroll.frame.height, viewportHeight, accuracy: 1)
        }

        workspace.setToolsVisible(false)
        window.setContentSize(CGSize(width: 1_100, height: 640))
        try await settleLayout()
        XCTAssertTrue(toolbar.isHidden)
        XCTAssertEqual(toolbar.frame.height, 0, accuracy: 1)
        try assertSourceUnchanged()
        workspace.setToolsVisible(true)
        try await settleLayout()
        try assertVisibleToolbarFits()
    }
}
