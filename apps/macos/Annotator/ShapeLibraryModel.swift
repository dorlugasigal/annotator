import AppKit
import SwiftUI
import AnnotationCore
import PresentationRendering

@MainActor
final class ShapeLibraryModel: ObservableObject {
    private static let scopes = NSMapTable<UserDefaults, ShapeLibraryModel>(
        keyOptions: [.weakMemory, .objectPointerPersonality], valueOptions: .strongMemory)

    static func scoped(to defaults: UserDefaults) -> ShapeLibraryModel {
        if let library = scopes.object(forKey: defaults) { return library }
        let library = ShapeLibraryModel(defaults: defaults)
        scopes.setObject(library, forKey: defaults)
        return library
    }
    @Published private(set) var shapes: [SavedShape] = []
    @Published private(set) var isBusy = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var isLoaded = false
    private(set) var store: ShapeLibraryStore?

    init(defaults: UserDefaults) {
        store = defaults === UserDefaults.standard ? nil : ShapeLibraryStore()
    }

    init(directory: URL) {
        store = ShapeLibraryStore(directory: directory)
    }

    private func storage() throws -> ShapeLibraryStore {
        if let store { return store }
        let directory = try FileManager.default.url(
            for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: false)
            .appendingPathComponent("Annotator/My Shapes", isDirectory: true)
        let resolved = ShapeLibraryStore(directory: directory)
        store = resolved
        return resolved
    }

    func reload() async {
        guard !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        do {
            shapes = try await storage().load()
            isLoaded = true
            errorMessage = nil
        } catch {
            isLoaded = false
            errorMessage = "My shapes could not load. \(error.localizedDescription)"
        }
    }

    func save(name: String, document: CanvasDocument) async -> Bool {
        guard !isBusy else { return false }
        isBusy = true
        defer { isBusy = false }
        do {
            shapes = try await storage().saveSelection(name: name, document: document)
            isLoaded = true
            errorMessage = nil
            return true
        } catch {
            errorMessage = "Shape not saved. \(error.localizedDescription)"
            return false
        }
    }

    func rename(_ shape: SavedShape, to name: String) async {
        guard !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        do {
            shapes = try await storage().rename(shape.id, to: name)
            errorMessage = nil
        } catch { errorMessage = "Shape not renamed. \(error.localizedDescription)" }
    }

    func delete(_ shape: SavedShape) async {
        guard !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        do {
            shapes = try await storage().delete(shape.id)
            errorMessage = nil
        } catch {
            errorMessage = "Shape not deleted. \(error.localizedDescription)"
        }
    }
}

extension WorkspaceModel {
    func insertSavedShape(_ shape: SavedShape) throws {
        guard let active else { throw ShapeLibraryError.invalidFile("Open a drawing canvas first.") }
        guard !active.canvas.hasActiveEdit else { throw ShapeLibraryError.unfinishedEdit }
        let center = active.presentation?.screenViewport?.source.center
            ?? Point(active.canvas.visibleRect.midX, active.canvas.visibleRect.midY)
        let updated = try shape.adding(to: active.document, centeredAt: center)
        useLocalSelection(in: active)
        try activateCanvasForEditing(active)
        active.document = updated
        usageMetrics.record(.reusableShape)
        tool = .select
        synchronize()
        status = "Inserted \(shape.name) at the canvas center. Drag it to move it; Undo removes this copy."
    }
}

struct SavedShapeThumbnail: NSViewRepresentable {
    let shape: SavedShape

    func makeNSView(context: Context) -> ShapeThumbnailView { ShapeThumbnailView(shape: shape) }
    func updateNSView(_ view: ShapeThumbnailView, context: Context) {
        view.shape = shape
        view.needsDisplay = true
    }
}

@MainActor
final class ShapeThumbnailView: NSView {
    var shape: SavedShape
    private let renderer = SceneRenderer()
    override var isFlipped: Bool { true }

    init(shape: SavedShape) {
        self.shape = shape
        super.init(frame: .zero)
        setAccessibilityElement(false)
    }

    required init?(coder: NSCoder) { nil }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext, bounds.width > 0, bounds.height > 0 else { return }
        let padding = shape.elements.map {
            max(12, $0.style.width * 8 * ($0.style.headScale ?? 1) + $0.style.roughness * 3)
        }.max() ?? 12
        let content = shape.bounds
        let scale = min(bounds.width / max(1, content.width + padding * 2),
                        bounds.height / max(1, content.height + padding * 2))
        context.saveGState()
        context.translateBy(x: bounds.midX, y: bounds.midY)
        context.scaleBy(x: scale, y: scale)
        context.translateBy(x: -content.center.x, y: -content.center.y)
        renderer.draw(shape.elements, in: context)
        context.restoreGState()
    }
}
