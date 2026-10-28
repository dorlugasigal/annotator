import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class InspectorViewportTransitionTests: XCTestCase {
    /// Beside a vertical toolbar, full properties are a column that scrolls, without a scroll bar, on a short screen.
    /// A horizontal toolbar keeps them on one row instead (ToolbarCustomizationTests).
    func testSwitchingScrolledFullPropertiesRevealsTheFirstHeadingAndControl() throws {
        NativeAccessibilityNode.enable()
        let suite = "inspector-viewport-transitions-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 500, height: 400), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let items = [Tool.arrow, .rectangle, .pen, .cover, .blur, .arrow].map {
            Element(tool: $0, frame: Rect(x: 40, y: 40, width: 100, height: 100))
        }
        editor.session.document = try CanvasDocument(elements: items)
        editor.model.choose(.select)
        editor.session.document.select([items[0].id])
        editor.model.synchronize()
        editor.model.setToolbarOrientation(.vertical)
        ToolbarPropertiesMode.full.apply(to: editor.model)
        let host = NSHostingController(rootView: ToolbarView(model: editor.model,
            availableSize: CGSize(width: 640, height: 280)))
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 640, height: 280),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentViewController = host
        defer { panel.contentViewController = nil; panel.close() }

        func settle() {
            for _ in 0..<4 {
                panel.setContentSize(host.view.fittingSize)
                host.view.layoutSubtreeIfNeeded()
                RunLoop.current.run(until: Date().addingTimeInterval(0.03))
            }
        }
        settle()
        let scroll = try XCTUnwrap(scrollViews(in: host.view).first)
        XCTAssertTrue(try XCTUnwrap(scroll.documentView).isFlipped)
        XCTAssertFalse(scroll.hasVerticalScroller, "The properties have no scroll bar")
        XCTAssertFalse(scroll.hasHorizontalScroller)
        let sequence = [items[1], items[2], items[3], items[4], items[0], items[5]]
        for item in sequence {
            let oldDocument = try XCTUnwrap(scroll.documentView)
            let bottom = max(0, oldDocument.frame.height - scroll.contentView.bounds.height)
            scroll.contentView.scroll(to: NSPoint(x: 0, y: bottom))
            scroll.reflectScrolledClipView(scroll.contentView)
            if editor.model.effectiveTool == .arrow {
                XCTAssertGreaterThan(bottom, 0, "The long arrow inspector must actually be scrollable.")
            }
            editor.session.document.select([item.id])
            editor.model.synchronize()
            settle()
            XCTAssertEqual(scroll.contentView.bounds.minY, 0, accuracy: 1, item.tool.rawValue)
            let document = try XCTUnwrap(scroll.documentView)
            XCTAssertEqual(document.frame.width, scroll.contentView.bounds.width, accuracy: 1)
            let hostDocument = try XCTUnwrap(document.subviews.first)
            XCTAssertEqual(hostDocument.frame, document.bounds)
            let viewport = panel.convertToScreen(scroll.contentView.convert(scroll.contentView.bounds, to: nil))
            let nodes = accessibilityNodes(in: document)
            let heading = try XCTUnwrap(nodes.first {
                $0.accessibilityLabel() == "Editing \(item.tool.label)" ||
                    $0.accessibilityValue() as? String == "Editing \(item.tool.label)"
            })
            XCTAssertTrue(viewport.insetBy(dx: -1, dy: -1).contains(heading.accessibilityFrame()),
                          "The first heading must be wholly visible after switching to \(item.tool.rawValue).")
            XCTAssertLessThanOrEqual(viewport.maxY - heading.accessibilityFrame().maxY, 6)
            let firstControl = try XCTUnwrap(nodes.first {
                if item.tool == .cover {
                    return $0.accessibilityLabel() == "Solid cover · 100% opaque" ||
                        $0.accessibilityValue() as? String == "Solid cover · 100% opaque"
                }
                return item.tool.isPixelEffect ? $0.accessibilityRole() == .popUpButton :
                    $0.accessibilityLabel() == "Stroke color"
            })
            XCTAssertTrue(viewport.insetBy(dx: -1, dy: -1).contains(firstControl.accessibilityFrame()),
                          "The first swatch/picker must not be clipped for \(item.tool.rawValue).")
        }

        let document = try XCTUnwrap(scroll.documentView)
        let bottom = max(0, document.frame.height - scroll.contentView.bounds.height)
        scroll.contentView.scroll(to: NSPoint(x: 0, y: bottom))
        scroll.reflectScrolledClipView(scroll.contentView)
        XCTAssertGreaterThan(scroll.contentView.bounds.minY, 0)
        let offset = scroll.contentView.bounds.minY
        editor.model.applyStyle { $0.roughness = 2 }
        settle()
        XCTAssertEqual(scroll.contentView.bounds.minY, offset, accuracy: 1,
                       "Changing a value on the same object must not jump back to the top.")
    }

    private func scrollViews(in view: NSView) -> [NSScrollView] {
        if let scroll = view as? NSScrollView { return [scroll] }
        return view.subviews.flatMap { scrollViews(in: $0) }
    }

    private func accessibilityNodes(in node: NSObject) -> [NativeAccessibilityNode] {
        NativeAccessibilityNode.nodes(in: node)
    }
}
