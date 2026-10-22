import AppKit
import ImageIO
import SwiftUI
import UniformTypeIdentifiers
import XCTest
@testable import AnnotatorApp

/// Renders the macOS UX reference used by the Windows edition (specification 013).
///
/// Each view is hosted in a panel that is fully transparent, ignores the mouse and never becomes key,
/// then drawn into an sRGB bitmap at twice its size in points. Nothing is shown on screen and no screen
/// pixels are read. Every image gets a JSON sidecar with the accessibility tree: roles, labels,
/// identifiers, values and frames in points from the image's top-left corner.
@MainActor
final class UXReferenceRecorder {
    static var isEnabled: Bool { ProcessInfo.processInfo.environment["ANNOTATOR_CAPTURE_UX_REFERENCE"] == "1" }
    static let scale: CGFloat = 2
    static let root = RepositoryRoot.url
        .appendingPathComponent("build/ux-reference/raw")

    enum Background: String {
        /// The view paints its own background, like the toolbar. Outside pixels stay transparent.
        case none
        /// The window background color, for Settings and other windows.
        case window
        /// Popovers use a system material; the window background color stands in for it.
        case popover
    }

    final class Panel: NSPanel {
        override var backingScaleFactor: CGFloat { UXReferenceRecorder.scale }
        // Controls draw their active appearance, as in a focused window.
        override var isKeyWindow: Bool { true }
        override var isMainWindow: Bool { true }
        override var canBecomeKey: Bool { false }
        override var canBecomeMain: Bool { false }
    }

    let surface: String
    private let directory: URL

    init(_ surface: String) throws {
        self.surface = surface
        directory = Self.root.appendingPathComponent(surface)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    static func panel(size: CGSize, dark: Bool) -> Panel {
        let panel = Panel(contentRect: CGRect(origin: .zero, size: size),
                          styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "Annotator UX reference"
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.alphaValue = 0
        panel.ignoresMouseEvents = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.collectionBehavior = [.transient, .ignoresCycle]
        panel.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        return panel
    }

    static func backgroundColor(_ background: Background, dark: Bool) -> NSColor? {
        guard background != .none, let appearance = NSAppearance(named: dark ? .darkAqua : .aqua) else { return nil }
        var color: NSColor?
        appearance.performAsCurrentDrawingAppearance {
            color = NSColor.windowBackgroundColor.usingColorSpace(.sRGB)
        }
        return color
    }

    /// Hosts a SwiftUI view and records it. Width or height fixes that dimension; otherwise the view's
    /// ideal size is used.
    @discardableResult
    func view<V: View>(_ content: V, state: String, dark: Bool, background: Background = .none,
                       width: CGFloat? = nil, height: CGFloat? = nil, notes: String = "",
                       spec: [String] = [], settle: Duration = .milliseconds(180)) async throws -> CGSize {
        let fill = Self.backgroundColor(background, dark: dark).map { Color(nsColor: $0) } ?? Color.clear
        let host = NSHostingView(rootView: AnyView(content
            .frame(width: width, height: height)
            .background(fill)
            .environment(\.colorScheme, dark ? .dark : .light)
            .environment(\.controlActiveState, .key)))
        return try await record(host, state: state, dark: dark, background: background, notes: notes,
                                spec: spec, settle: settle, fitting: true)
    }

    /// Records an AppKit view at its current frame size.
    @discardableResult
    func nsView(_ view: NSView, state: String, dark: Bool, background: Background = .none, notes: String = "",
                spec: [String] = [], settle: Duration = .milliseconds(120)) async throws -> CGSize {
        try await record(view, state: state, dark: dark, background: background, notes: notes,
                         spec: spec, settle: settle, fitting: false)
    }

    private func record(_ view: NSView, state: String, dark: Bool, background: Background, notes: String,
                        spec: [String], settle: Duration, fitting: Bool) async throws -> CGSize {
        let panel = Self.panel(size: fitting ? CGSize(width: 900, height: 700) : view.frame.size, dark: dark)
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        view.appearance = panel.appearance
        if background != .none, let color = Self.backgroundColor(background, dark: dark) {
            view.wantsLayer = true
            view.layer?.backgroundColor = color.cgColor
        }
        panel.contentView = view
        panel.orderFrontRegardless()
        for _ in 0..<4 {
            if fitting {
                let size = view.fittingSize
                panel.setContentSize(size)
                view.setFrameSize(size)
            }
            view.layoutSubtreeIfNeeded()
            try await Task.sleep(for: .milliseconds(40))
        }
        try await Task.sleep(for: settle)
        view.layoutSubtreeIfNeeded()
        let image = try Self.render(view)
        let size = view.bounds.size
        try write(image, state: state, dark: dark, metadata: [
            "kind": "view", "background": background.rawValue, "notes": notes, "spec": spec,
            "size": [Self.rounded(size.width), Self.rounded(size.height)],
            "accessibility": Self.accessibility(of: view)
        ])
        return size
    }

    /// Records an image drawn directly, such as a cursor or a pointer halo. Pass `accessibility` to record
    /// the accessibility tree of the view the image shows; it is measured from that view's top-left corner.
    func image(_ image: CGImage, state: String, dark: Bool, notes: String = "", spec: [String] = [],
               extra: [String: Any] = [:], accessibility: NSView? = nil) throws {
        var metadata: [String: Any] = ["kind": "image", "notes": notes, "spec": spec,
            "size": [Self.rounded(CGFloat(image.width) / Self.scale), Self.rounded(CGFloat(image.height) / Self.scale)]]
        if let accessibility { metadata["accessibility"] = Self.accessibilityInWindow(of: accessibility) }
        metadata.merge(extra) { _, new in new }
        try write(image, state: state, dark: dark, metadata: metadata)
    }

    /// The accessibility tree of a view, hosting it in a transparent panel when it has no window.
    static func accessibilityInWindow(of view: NSView) -> [[String: Any]] {
        guard view.window == nil else { return accessibility(of: view) }
        let host = panel(size: view.frame.size, dark: false)
        defer { host.orderOut(nil); host.contentView = nil; host.close() }
        host.contentView = view
        host.orderFrontRegardless()
        view.layoutSubtreeIfNeeded()
        return accessibility(of: view)
    }

    /// Records frames of a motion at fixed times, for a contact sheet and video.
    func sequence(_ frames: [CGImage], name: String, dark: Bool, times: [Double], notes: String = "",
                  spec: [String] = []) throws {
        XCTAssertEqual(frames.count, times.count)
        let folder = directory.appendingPathComponent("\(name)-\(dark ? "dark" : "light")")
        try? FileManager.default.removeItem(at: folder)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for (index, frame) in frames.enumerated() {
            try Self.writePNG(frame, to: folder.appendingPathComponent(String(format: "frame-%04d.png", index)))
        }
        let size = frames.first.map { [Self.rounded(CGFloat($0.width) / Self.scale),
                                       Self.rounded(CGFloat($0.height) / Self.scale)] } ?? [0, 0]
        try Self.writeJSON(["surface": surface, "state": name, "appearance": dark ? "dark" : "light",
                            "kind": "sequence", "notes": notes, "spec": spec, "scale": Self.scale,
                            "size": size, "times": times.map { Self.rounded($0 * 1000) / 1000 }],
                           to: folder.appendingPathComponent("sequence.json"))
    }

    private func write(_ image: CGImage, state: String, dark: Bool, metadata: [String: Any]) throws {
        XCTAssertFalse(Self.isBlank(image), "\(surface)/\(state) rendered no visible pixels.")
        let name = "\(state)-\(dark ? "dark" : "light")"
        try Self.writePNG(image, to: directory.appendingPathComponent("\(name).png"))
        var sidecar = metadata
        sidecar["surface"] = surface
        sidecar["state"] = state
        sidecar["appearance"] = dark ? "dark" : "light"
        sidecar["scale"] = Self.scale
        try Self.writeJSON(sidecar, to: directory.appendingPathComponent("\(name).json"))
        print("UX REFERENCE \(surface)/\(name) \(image.width)x\(image.height)")
    }

    // MARK: Rendering

    static func render(_ view: NSView) throws -> CGImage {
        let size = view.bounds.size
        guard size.width >= 1, size.height >= 1, size.width < 6000, size.height < 6000 else {
            throw CocoaError(.coderInvalidValue, userInfo: [NSLocalizedDescriptionKey: "Unusable view size \(size)."])
        }
        let context = try makeContext(width: size.width, height: size.height)
        let graphics = NSGraphicsContext(cgContext: context, flipped: false)
        view.displayIgnoringOpacity(view.bounds, in: graphics)
        return try XCTUnwrap(context.makeImage())
    }


    /// An sRGB context sized in points, drawing at 2x.
    static func makeContext(width: CGFloat, height: CGFloat) throws -> CGContext {
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try XCTUnwrap(CGContext(data: nil, width: Int(ceil(width * scale)), height: Int(ceil(height * scale)),
            bitsPerComponent: 8, bytesPerRow: 0, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.scaleBy(x: scale, y: scale)
        return context
    }

    /// Draws with AppKit into a 2x sRGB context with a top-left origin.
    static func draw(width: CGFloat, height: CGFloat, _ body: (CGContext) throws -> Void) throws -> CGImage {
        let context = try makeContext(width: width, height: height)
        context.translateBy(x: 0, y: height)
        context.scaleBy(x: 1, y: -1)
        let previous = NSGraphicsContext.current
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
        defer { NSGraphicsContext.current = previous }
        try body(context)
        return try XCTUnwrap(context.makeImage())
    }

    static func isBlank(_ image: CGImage) -> Bool {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: 48, height: 48, bitsPerComponent: 8, bytesPerRow: 48 * 4,
                                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let data = context.data else { return true }
        context.interpolationQuality = .medium
        context.draw(image, in: CGRect(x: 0, y: 0, width: 48, height: 48))
        let pixels = data.bindMemory(to: UInt8.self, capacity: 48 * 48 * 4)
        var visible = 0, minimum = 255, maximum = 0
        for index in stride(from: 0, to: 48 * 48 * 4, by: 4) where pixels[index + 3] > 8 {
            visible += 1
            let luminance = Int(pixels[index]) + Int(pixels[index + 1]) + Int(pixels[index + 2])
            minimum = min(minimum, luminance / 3); maximum = max(maximum, luminance / 3)
        }
        return visible == 0 || (visible == 48 * 48 && maximum - minimum < 2)
    }

    static func writePNG(_ image: CGImage, to url: URL) throws {
        let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination), "Could not write \(url.lastPathComponent).")
    }

    static func writeJSON(_ object: Any, to url: URL) throws {
        let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: url, options: .atomic)
    }

    static func rounded(_ value: CGFloat) -> Double { (Double(value) * 2).rounded() / 2 }

    // MARK: Accessibility tree

    static func accessibility(of view: NSView) -> [[String: Any]] {
        NativeAccessibilityNode.enable()
        guard let window = view.window else { return [] }
        let origin = window.convertToScreen(view.convert(view.bounds, to: nil))
        var visited = Set<ObjectIdentifier>()
        var budget = 3000

        func value(_ object: NSObject, _ key: String) -> Any? {
            object.responds(to: NSSelectorFromString(key)) ? object.value(forKey: key) : nil
        }
        func text(_ object: NSObject, _ key: String) -> String? {
            guard let string = value(object, key) as? String, !string.isEmpty else { return nil }
            return string
        }
        func plain(_ raw: Any?) -> Any? {
            switch raw {
            case let string as String: string.isEmpty ? nil : string
            case let attributed as NSAttributedString: attributed.string.isEmpty ? nil : attributed.string
            case let number as NSNumber:
                CFGetTypeID(number) == CFBooleanGetTypeID() ? number.boolValue : (number.doubleValue * 1000).rounded() / 1000
            default: nil
            }
        }
        func children(of object: NSObject) -> [NSObject] {
            if let list = value(object, "accessibilityChildren") as? [NSObject], !list.isEmpty { return list }
            return (object as? NSView)?.subviews.filter { !$0.isHidden } ?? []
        }
        func node(_ object: NSObject, depth: Int) -> [[String: Any]] {
            guard depth < 40, budget > 0, visited.insert(ObjectIdentifier(object)).inserted else { return [] }
            budget -= 1
            let nested = children(of: object).flatMap { node($0, depth: depth + 1) }
            var entry: [String: Any] = [:]
            if let role = text(object, "accessibilityRole") { entry["role"] = role }
            if let subrole = text(object, "accessibilitySubrole") { entry["subrole"] = subrole }
            for (key, name) in [("accessibilityLabel", "label"), ("accessibilityTitle", "title"),
                                ("accessibilityIdentifier", "identifier"), ("accessibilityHelp", "help"),
                                ("accessibilityPlaceholderValue", "placeholder")] {
                if let string = text(object, key) { entry[name] = string }
            }
            if let current = plain(value(object, "accessibilityValue")) { entry["value"] = current }
            // AppKit and SwiftUI answer these as isAccessibilityEnabled and isAccessibilitySelected.
            if object.responds(to: NSSelectorFromString("isAccessibilityEnabled")),
               let enabled = object.value(forKey: "accessibilityEnabled") as? Bool { entry["enabled"] = enabled }
            if object.responds(to: NSSelectorFromString("isAccessibilitySelected")),
               let selected = object.value(forKey: "accessibilitySelected") as? Bool, selected { entry["selected"] = true }
            let named = ["label", "title", "identifier", "value", "help"].contains { entry[$0] != nil }
            let role = entry["role"] as? String ?? ""
            let interesting = named || !["AXGroup", "AXUnknown", "AXLayoutArea", "AXSplitGroup", ""].contains(role)
            guard interesting else { return nested }
            if let frame = (value(object, "accessibilityFrame") as? NSValue)?.rectValue, !frame.isEmpty {
                entry["frame"] = [rounded(frame.minX - origin.minX), rounded(origin.maxY - frame.maxY),
                                  rounded(frame.width), rounded(frame.height)]
            }
            if !nested.isEmpty { entry["children"] = nested }
            return [entry]
        }
        return node(view, depth: 0)
    }
}
