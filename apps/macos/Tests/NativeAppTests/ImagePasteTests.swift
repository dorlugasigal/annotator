import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class ImagePasteTests: XCTestCase {
    func testNativePasteActionAndToolbarCommandVUseTheClipboard() throws {
        let clipboard = NSPasteboard.withUniqueName()
        let suite = "native-image-paste-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults, imagePasteboard: clipboard)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite); clipboard.releaseGlobally() }
        let image = try PNGExporter.data(elements: [], size: CGSize(width: 24, height: 16), background: .blue)
        XCTAssertTrue(clipboard.setData(image, forType: .png))
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        XCTAssertTrue(NSApp.sendAction(#selector(NSText.paste(_:)), to: session.canvas, from: nil))
        XCTAssertEqual(session.document.elements.count, 1)
        let toolbar = AnnotationPanel(contentRect: NSRect(x: 0, y: 0, width: 200, height: 80),
                                      styleMask: [.titled], backing: .buffered, defer: false)
        toolbar.imagePasteModel = model
        defer { toolbar.orderOut(nil) }
        let event = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: .command,
            timestamp: 0, windowNumber: toolbar.windowNumber, context: nil, characters: "v",
            charactersIgnoringModifiers: "v", isARepeat: false, keyCode: 9))
        XCTAssertTrue(toolbar.performKeyEquivalent(with: event))
        XCTAssertEqual(session.document.elements.count, 2)
        XCTAssertTrue(session.document.elements.allSatisfy { $0.tool == .image })
        model.undo()
        XCTAssertEqual(session.document.elements.count, 1)
        let editor = NSTextView(frame: NSRect(x: 0, y: 0, width: 100, height: 60))
        toolbar.contentView = editor
        XCTAssertTrue(toolbar.makeFirstResponder(editor))
        _ = toolbar.performKeyEquivalent(with: event)
        XCTAssertEqual(session.document.elements.count, 1, "Text-editing focus must not insert a canvas image.")
    }

    func testImageTypesAndMissingClipboardAreExplicit() throws {
        let clipboard = NSPasteboard.withUniqueName()
        defer { clipboard.releaseGlobally() }
        XCTAssertThrowsError(try ImageClipboard.read(clipboard))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 16, height: 16), background: .blue)
        let rep = NSBitmapImageRep(cgImage: image)
        let gif = try XCTUnwrap(rep.representation(using: .gif, properties: [:]))
        XCTAssertTrue(clipboard.setData(gif, forType: NSPasteboard.PasteboardType("com.compuserve.gif")))
        XCTAssertEqual(try RasterAsset.decode(ImageClipboard.read(clipboard)).width, 16)
    }
}
