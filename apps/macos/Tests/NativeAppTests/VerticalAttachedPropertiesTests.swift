import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class VerticalAttachedPropertiesTests: XCTestCase {
    func testEveryToolHasAnIntrinsicAttachedSurfaceAndTransparentVoid() throws {
        NativeAccessibilityNode.enable()
        let suite = "vertical-attached-properties-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 400, height: 300), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let model = editor.model
        model.setToolbarOrientation(.vertical)
        let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview/VerticalAttached")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        for dark in [false, true] {
            for mode in [ToolbarPropertiesMode.quick, .full] {
                mode.apply(to: model)
                for tool in Tool.allCases {
                    model.tool = tool
                    model.style = .defaults(for: tool)
                    let host = NSHostingController(rootView: ToolbarView(model: model,
                        availableSize: CGSize(width: 1000, height: 900))
                        .environment(\.colorScheme, dark ? .dark : .light))
                    let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 400, height: 900),
                                        styleMask: [.borderless], backing: .buffered, defer: false)
                    panel.isReleasedWhenClosed = false
                    panel.isOpaque = false
                    panel.backgroundColor = .clear
                    panel.contentViewController = host
                    defer { panel.contentViewController = nil; panel.close() }
                    for _ in 0..<4 {
                        panel.setContentSize(host.view.fittingSize)
                        host.view.layoutSubtreeIfNeeded()
                        RunLoop.current.run(until: Date().addingTimeInterval(0.02))
                    }
                    let nodes = accessibilityNodes(in: host.view)
                    let rail = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == "Drawing tool rail" })
                    let properties = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == "Attached tool properties" })
                    let railFrame = rail.accessibilityFrame()
                    let surfaceFrame = properties.accessibilityFrame()
                    XCTAssertEqual(railFrame.width, 68, accuracy: 1, tool.rawValue)
                    XCTAssertEqual(surfaceFrame.width, 300, accuracy: 1, tool.rawValue)
                    XCTAssertEqual(surfaceFrame.maxY, railFrame.maxY, accuracy: 1, tool.rawValue)
                    XCTAssertGreaterThanOrEqual(surfaceFrame.minX - railFrame.maxX, 7)
                    XCTAssertLessThanOrEqual(host.view.fittingSize.width, 376)
                    XCTAssertLessThanOrEqual(host.view.fittingSize.height, 900)
                    XCTAssertEqual(nodes.filter {
                        $0.accessibilityLabel() == "Properties" && $0.accessibilityRole() == .button
                    }.count, 1)
                    let controlRoles: [NSAccessibility.Role] = [.button, .popUpButton, .slider, .textField, .checkBox]
                    for control in accessibilityNodes(in: properties.object) {
                        guard let role = control.accessibilityRole(), controlRoles.contains(role) else { continue }
                        let frame = control.accessibilityFrame()
                        guard frame.width > 0, frame.intersects(surfaceFrame) else { continue }
                        XCTAssertGreaterThanOrEqual(frame.minX, surfaceFrame.minX - 1, tool.rawValue)
                        XCTAssertLessThanOrEqual(frame.maxX, surfaceFrame.maxX + 1,
                                                 "A real \(tool.rawValue) control overflows the attached surface.")
                    }
                    if mode == .quick {
                        XCTAssertLessThanOrEqual(surfaceFrame.height, 90, tool.rawValue)
                    } else {
                        let scroll = try XCTUnwrap(scrollViews(in: host.view).first)
                        let content = try XCTUnwrap(scroll.documentView)
                        XCTAssertLessThanOrEqual(surfaceFrame.height, min(850, content.frame.height) + 60,
                                                 "The surface must hug its controls, not the rail: \(tool.rawValue)")
                        XCTAssertLessThanOrEqual(content.frame.width, 276)
                    }
                    let bitmap = try XCTUnwrap(host.view.bitmapImageRepForCachingDisplay(in: host.view.bounds))
                    host.view.cacheDisplay(in: host.view.bounds, to: bitmap)
                    if surfaceFrame.minY > railFrame.minY + 24 {
                        let screenPoint = NSPoint(x: surfaceFrame.midX, y: surfaceFrame.minY - 16)
                        let point = host.view.convert(panel.convertPoint(fromScreen: screenPoint), from: nil)
                        let scaleX = CGFloat(bitmap.pixelsWide) / host.view.bounds.width
                        let scaleY = CGFloat(bitmap.pixelsHigh) / host.view.bounds.height
                        let x = Int(point.x * scaleX)
                        let y = Int((host.view.isFlipped ? point.y : host.view.bounds.height - point.y) * scaleY)
                        XCTAssertGreaterThanOrEqual(x, 0)
                        XCTAssertGreaterThanOrEqual(y, 0)
                        XCTAssertLessThan(x, bitmap.pixelsWide)
                        XCTAssertLessThan(y, bitmap.pixelsHigh)
                        if x >= 0 && y >= 0 && x < bitmap.pixelsWide && y < bitmap.pixelsHigh {
                            let pixel = try XCTUnwrap(bitmap.colorAt(x: x, y: y))
                            XCTAssertLessThan(pixel.alphaComponent, 0.02,
                                              "The void below \(tool.rawValue) properties must stay transparent.")
                        }
                    }
                    let name = "\(tool.rawValue)-\(mode == .quick ? "quick" : "full")-\(dark ? "dark" : "light").png"
                    try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                        .write(to: output.appendingPathComponent(name))
                }
            }
        }
    }

    private func scrollViews(in view: NSView) -> [NSScrollView] {
        if let scroll = view as? NSScrollView { return [scroll] }
        return view.subviews.flatMap { scrollViews(in: $0) }
    }

    private func accessibilityNodes(in node: NSObject) -> [NativeAccessibilityNode] {
        NativeAccessibilityNode.nodes(in: node)
    }
}
