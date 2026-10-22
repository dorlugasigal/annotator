// Compile with -swift-version 6 and Annotator/MenuBarIcon.swift; run from the repository root.
// Original Annotator mark and tile. No third-party artwork or fonts.
import AppKit

let directory = URL(fileURLWithPath: "apps/macos/Annotator/Assets.xcassets/AppIcon.appiconset")
try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
var entries: [[String: String]] = []
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = points * scale
        let size = CGFloat(pixels)
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                     isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        NSColor(calibratedRed: 0.145, green: 0.17, blue: 0.155, alpha: 1).setFill()
        NSBezierPath(roundedRect: NSRect(x: size * 0.06, y: size * 0.06, width: size * 0.88, height: size * 0.88),
                     xRadius: size * 0.20, yRadius: size * 0.20).fill()
        let mark = MenuBarIcon.makeImage(color: NSColor(calibratedWhite: 0.96, alpha: 1), size: size * 0.64)
        mark.isTemplate = false
        mark.draw(in: NSRect(x: size * 0.18, y: size * 0.18, width: size * 0.64, height: size * 0.64))
        NSColor(calibratedRed: 0.93, green: 0.45, blue: 0.31, alpha: 1).setFill()
        NSBezierPath(ovalIn: NSRect(x: size * 0.70, y: size * 0.70, width: size * 0.10, height: size * 0.10)).fill()
        NSGraphicsContext.restoreGraphicsState()
        let filename = "icon_\(points)x\(points)@\(scale)x.png"
        try bitmap.representation(using: .png, properties: [:])!.write(to: directory.appendingPathComponent(filename))
        entries.append(["idiom": "mac", "size": "\(points)x\(points)", "scale": "\(scale)x", "filename": filename])
    }
}
let catalog: [String: Any] = ["images": entries, "info": ["author": "xcode", "version": 1]]
try JSONSerialization.data(withJSONObject: catalog, options: [.prettyPrinted, .sortedKeys])
    .write(to: directory.appendingPathComponent("Contents.json"))
