import AppKit
import XCTest
@testable import AnnotatorApp

@MainActor
final class MenuBarIconTests: XCTestCase {
    private func render(scale: Int, background: NSColor? = nil) throws -> NSBitmapImageRep {
        let pixels = 18 * scale
        let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: pixels * 4, bitsPerPixel: 32))
        let context = try XCTUnwrap(NSGraphicsContext(bitmapImageRep: bitmap))
        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        NSGraphicsContext.current = context
        context.cgContext.scaleBy(x: CGFloat(scale), y: CGFloat(scale))
        if let background {
            context.cgContext.setFillColor(background.cgColor)
            context.cgContext.fill(CGRect(x: 0, y: 0, width: 18, height: 18))
        }
        MenuBarIcon.makeImage().draw(in: CGRect(x: 0, y: 0, width: 18, height: 18))
        return bitmap
    }

    func testBrandGlyphIsTemplateSizedAccessibleAndHasNoTileOrAccent() throws {
        let image = MenuBarIcon.makeImage()
        XCTAssertEqual(image.size, NSSize(width: 18, height: 18))
        XCTAssertTrue(image.isTemplate)
        XCTAssertEqual(image.accessibilityDescription, "Annotator")
        for scale in [1, 2, 3] {
            let bitmap = try render(scale: scale)
            var ink = 0
            var hasCounter = false
            for y in 0..<bitmap.pixelsHigh {
                var solid: [Int] = []
                for x in 0..<bitmap.pixelsWide {
                    let color = try XCTUnwrap(bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB))
                    if color.alphaComponent > 0.6 { solid.append(x); ink += 1 }
                    if color.alphaComponent > 0.2 {
                        XCTAssertLessThan(color.redComponent + color.greenComponent + color.blueComponent, 0.02)
                    }
                }
                if let first = solid.first, let last = solid.last, last > first {
                    hasCounter = hasCounter || (first..<last).contains {
                        (bitmap.colorAt(x: $0, y: y)?.alphaComponent ?? 1) < 0.1
                    }
                }
            }
            XCTAssertGreaterThan(ink, 40 * scale * scale)
            XCTAssertLessThan(ink, 230 * scale * scale)
            XCTAssertTrue(hasCounter, "The A's inner opening must remain clear at menu-bar size.")
            for (x, y) in [(0, 0), (0, bitmap.pixelsHigh - 1), (bitmap.pixelsWide - 1, 0),
                           (bitmap.pixelsWide - 1, bitmap.pixelsHigh - 1)] {
                XCTAssertLessThan(try XCTUnwrap(bitmap.colorAt(x: x, y: y)).alphaComponent, 0.02)
            }
        }
    }

    func testWriteNativeGlyphPreview() throws {
        let bitmap = try render(scale: 8, background: .white)
        let root = RepositoryRoot.url
        let directory = root.appendingPathComponent("build")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            .write(to: directory.appendingPathComponent("menu-bar-icon-preview.png"))
    }

    func testSharingDotIsRedNoninteractiveAccessibleThroughButtonAndClearsOnStop() throws {
        let button = NSButton(frame: CGRect(x: 0, y: 0, width: 24, height: 24))
        AppDelegate.configureMenuBarButton(button)
        let dot = try XCTUnwrap(button.subviews.compactMap { $0 as? SharingStatusDot }.first)
        XCTAssertTrue(dot.isHidden)
        XCTAssertEqual(button.accessibilityValue() as? String, "Region sharing off")
        for name in [NSAppearance.Name.aqua, .darkAqua] {
            button.appearance = NSAppearance(named: name)
            AppDelegate.configureMenuBarButton(button, sharingActive: true)
            XCTAssertTrue(try XCTUnwrap(button.image).isTemplate)
            XCTAssertEqual(button.subviews.compactMap { $0 as? SharingStatusDot }.count, 1)
            XCTAssertFalse(dot.isHidden)
            XCTAssertFalse(dot.isAccessibilityElement())
            XCTAssertNil(dot.hitTest(CGPoint(x: 3, y: 3)))
            XCTAssertTrue(button.bounds.contains(dot.frame))
            XCTAssertEqual(button.accessibilityValue() as? String, "Region sharing on")
            let bitmap = try XCTUnwrap(dot.bitmapImageRepForCachingDisplay(in: dot.bounds))
            dot.cacheDisplay(in: dot.bounds, to: bitmap)
            let center = try XCTUnwrap(bitmap.colorAt(x: bitmap.pixelsWide / 2, y: bitmap.pixelsHigh / 2)?.usingColorSpace(.sRGB))
            XCTAssertGreaterThan(center.redComponent, center.greenComponent + 0.3)
            XCTAssertGreaterThan(center.redComponent, center.blueComponent + 0.3)
            XCTAssertGreaterThan(center.alphaComponent, 0.9)
        }
        AppDelegate.configureMenuBarButton(button, sharingActive: false)
        XCTAssertTrue(dot.isHidden)
        XCTAssertEqual(button.accessibilityValue() as? String, "Region sharing off")
        XCTAssertEqual(button.toolTip, "Annotator")
    }
}
