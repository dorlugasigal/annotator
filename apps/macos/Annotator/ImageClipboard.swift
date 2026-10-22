import AppKit
import UniformTypeIdentifiers
import AnnotationCore

@MainActor
enum ImageClipboard {
    static func read(_ pasteboard: NSPasteboard) throws -> Data {
        let preferred: [NSPasteboard.PasteboardType] = [.png, .tiff]
        let otherImages = (pasteboard.types ?? []).filter {
            !preferred.contains($0) && UTType($0.rawValue)?.conforms(to: .image) == true
        }
        for type in preferred + otherImages {
            if let data = pasteboard.data(forType: type) {
                guard !data.isEmpty, data.count <= 128 * 1_024 * 1_024 else {
                    throw CanvasError.invalidDocument("The copied image is empty or exceeds 128 MB.")
                }
                return data
            }
        }
        if let value = pasteboard.string(forType: .fileURL), let url = URL(string: value), url.isFileURL {
            let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize
            guard let size, size <= 128 * 1_024 * 1_024 else {
                throw CanvasError.invalidDocument("The copied image file exceeds 128 MB.")
            }
            return try Data(contentsOf: url, options: .mappedIfSafe)
        }
        throw CanvasError.invalidDocument("Copy an image or image file, then choose Paste image. A web link alone does not contain image pixels.")
    }
}

@MainActor
class AnnotationPanel: NSPanel {
    weak var imagePasteModel: WorkspaceModel?

    override func sendEvent(_ event: NSEvent) {
        if imagePasteModel?.handleLocalZoomScroll(event) == true { return }
        super.sendEvent(event)
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard event.type == .keyDown else { return super.performKeyEquivalent(with: event) }
        let flags = event.modifierFlags.intersection([.command, .control, .option, .shift])
        if let model = imagePasteModel {
            if let shortcut = model.toolLibraryShortcut, shortcut.matches(event), !(firstResponder is NSTextView) {
                model.openToolLibrary()
                return true
            }
            if (model.preferences.resumeShortcut ?? .resumeDrawing).matches(event) {
                model.resumeDrawing()
                return true
            }
            if (model.preferences.zoomShortcut ?? .liveZoom).matches(event) {
                model.toggleScreenZoomFromShortcut()
                return true
            }
            if model.preferences.activationShortcut.matches(event), model.performLocalActivationShortcut() { return true }
            if (model.preferences.hideShortcut ?? .hideTools).matches(event), let hide = model.onHideToolbar {
                hide()
                return true
            }
            if event.keyCode == 53, flags.isEmpty, !(firstResponder is NSTextView),
               model.screenZoomEnabled || model.magnifierEnabled {
                model.handleZoomEscape()
                return true
            }
        }
        if flags == .command, !(firstResponder is NSTextView), let model = imagePasteModel {
            switch event.charactersIgnoringModifiers?.lowercased() {
            case "v": model.paste(); return true
            case "c": if model.copySelection() { return true }
            case "x": if model.copySelection(cut: true) { return true }
            default: break
            }
        }

        return super.performKeyEquivalent(with: event)
    }

    @objc func paste(_ sender: Any?) {
        imagePasteModel?.paste()
    }
}

private extension ActivationShortcut {
    func matches(_ event: NSEvent) -> Bool {
        var expected: NSEvent.ModifierFlags = []
        if control { expected.insert(.control) }
        if option { expected.insert(.option) }
        if command { expected.insert(.command) }
        if shift { expected.insert(.shift) }
        return UInt32(event.keyCode) == keyCode &&
            event.modifierFlags.intersection([.command, .control, .option, .shift]) == expected
    }
}
