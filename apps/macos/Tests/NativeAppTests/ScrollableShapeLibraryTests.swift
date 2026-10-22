import AppKit
import SwiftUI
import XCTest
@testable import AnnotatorApp

@MainActor
final class ScrollableShapeLibraryTests: XCTestCase {
    static func scrollView(in view: NSView) -> NSScrollView? {
        if let scroll = view as? NSScrollView,
           (scroll.documentView?.frame.height ?? 0) > scroll.contentSize.height + 10 { return scroll }
        return view.subviews.compactMap { scrollView(in: $0) }.first
    }

    static func scrollToBottom(_ scroll: NSScrollView) {
        guard let document = scroll.documentView else { return }
        let y = document.isFlipped ? max(0,document.frame.height-scroll.contentSize.height) : 0
        scroll.contentView.scroll(to: CGPoint(x: 0, y: y))
        scroll.reflectScrolledClipView(scroll.contentView)
    }

    func testLastRowIsReachableAndFooterStaysOutsideTheGrid() async throws {
        let suite = "scrolling-shapes-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let host = NSHostingController(rootView: ToolLibraryPanel(
            model: model, section: .constant(.shapes), dismiss: {}, maximumHeight: 310))
        let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 368, height: 310),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.ignoresMouseEvents = true
        panel.contentViewController = host
        defer {
            panel.orderOut(nil); panel.contentViewController = nil; panel.close()
            model.closeAll(); defaults.removePersistentDomain(forName: suite)
        }
        panel.setContentSize(host.view.fittingSize)
        panel.orderFrontRegardless()
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(150))
        host.view.layoutSubtreeIfNeeded()
        let scroll = try XCTUnwrap(Self.scrollView(in: host.view))
        XCTAssertGreaterThan(scroll.documentView!.frame.height, scroll.contentSize.height*2)
        let nodes = NativeAccessibilityNode.nodes(in: host.view)
        let footer = try XCTUnwrap(nodes.first { $0.accessibilityIdentifier() == "tool-library-footer" })
        let scrollFrame = scroll.window!.convertToScreen(scroll.convert(scroll.bounds, to: nil))
        let footerFrame = footer.accessibilityFrame()
        XCTAssertLessThanOrEqual(footerFrame.maxY, scrollFrame.minY + 1)
        Self.scrollToBottom(scroll)
        try await Task.sleep(for: .milliseconds(150))
        let updated = NativeAccessibilityNode.nodes(in: host.view)
        let axes = try XCTUnwrap(updated.first { $0.accessibilityLabel() == "Axes" && $0.accessibilityRole() == .button })
        XCTAssertTrue(scrollFrame.insetBy(dx: -1, dy: -1).contains(axes.accessibilityFrame()))
        let nextFooter = try XCTUnwrap(updated.first { $0.accessibilityIdentifier() == "tool-library-footer" })
        XCTAssertEqual(nextFooter.accessibilityFrame(), footerFrame)
        XCTAssertLessThanOrEqual(panel.frame.height, 310)
    }
}
