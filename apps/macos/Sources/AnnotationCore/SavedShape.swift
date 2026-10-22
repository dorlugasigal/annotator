import Foundation

public enum ShapeLibraryError: Error, LocalizedError, Equatable, Sendable {
    case noSelection, unfinishedEdit, invalidName, unsupportedContent
    case invalidFile(String), entryTooLarge, libraryFull, undoBudget

    public var errorDescription: String? {
        switch self {
        case .noSelection: "Select one or more drawings to save."
        case .unfinishedEdit: "Finish or cancel the current edit, then try again."
        case .invalidName: "Enter a name with 1 to 120 characters and no line breaks or control characters."
        case .unsupportedContent: "My shapes saves drawings and text only. Deselect covers, pixel effects and images, then try again."
        case .invalidFile(let detail): "My shapes could not read a saved shape. \(detail)"
        case .entryTooLarge: "This shape exceeds the 8 MB limit. Select fewer drawings; nothing was saved."
        case .libraryFull: "My shapes is full (256 shapes or 64 MB). Delete a saved shape, then try again."
        case .undoBudget: "This canvas is too large to insert a shape with Undo. Save the board and use a smaller canvas."
        }
    }
}

public struct SavedShape: Codable, Identifiable, Sendable {
    public static let currentSchema = 1
    public static let maximumBytes = 8 * 1_024 * 1_024
    public let schemaVersion: Int
    public let id: UUID
    public let name: String
    public let drawing: DrawingFile

    public var elements: [Element] { drawing.elements }
    public var estimatedBytes: Int { elements.reduce(0) { $0 + $1.estimatedBytes } }
    public var bounds: Rect {
        elements.map { element in
            let frame = element.frame
            return Rect.enclosing([
                frame.origin, Point(frame.maxX, frame.y), Point(frame.x, frame.maxY),
                Point(frame.maxX, frame.maxY)
            ].map { $0.rotated(around: frame.center, by: element.rotation) })
        }.reduce(nil as Rect?) { $0?.union($1) ?? $1 } ?? .zero
    }

    public init(name: String, document: CanvasDocument) throws {
        guard !document.isEditing else { throw ShapeLibraryError.unfinishedEdit }
        let selected = document.elements.filter { document.selection.contains($0.id) }
        guard !selected.isEmpty else { throw ShapeLibraryError.noSelection }
        try Self.checkContent(selected)
        let targets = Set(selected.filter { $0.tool.isShape }.map(\.id))
        let detached = selected.map { original in
            var element = original
            if let anchor = element.startAnchor, !targets.contains(anchor.elementID) { element.startAnchor = nil }
            if let anchor = element.endAnchor, !targets.contains(anchor.elementID) { element.endAnchor = nil }
            return element
        }
        schemaVersion = Self.currentSchema
        id = UUID()
        self.name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        drawing = DrawingFile(document: try CanvasDocument(elements: detached))
        try validate()
        _ = try data()
    }

    private init(id: UUID, name: String, drawing: DrawingFile) throws {
        schemaVersion = Self.currentSchema
        self.id = id
        self.name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        self.drawing = drawing
        try validate()
    }

    public func renamed(to name: String) throws -> SavedShape {
        let result = try SavedShape(id: id, name: name, drawing: drawing)
        _ = try result.data()
        return result
    }

    public func validate() throws {
        guard schemaVersion == Self.currentSchema else {
            throw ShapeLibraryError.invalidFile("This format needs a newer version of Annotator.")
        }
        guard !name.isEmpty, name.unicodeScalars.count <= 120,
              name == name.trimmingCharacters(in: .whitespacesAndNewlines),
              name.unicodeScalars.allSatisfy({
                  !CharacterSet.controlCharacters.contains($0)
                      && !CharacterSet.newlines.contains($0)
                      && !CharacterSet(charactersIn: "\u{061c}\u{200e}\u{200f}\u{202a}\u{202b}\u{202c}\u{202d}\u{202e}\u{2066}\u{2067}\u{2068}\u{2069}").contains($0)
              }) else { throw ShapeLibraryError.invalidName }
        try Self.checkContent(elements)
        guard !elements.isEmpty else { throw ShapeLibraryError.noSelection }
        guard estimatedBytes <= Self.maximumBytes else { throw ShapeLibraryError.entryTooLarge }
        _ = try drawing.document()
        let targets = Set(elements.filter { $0.tool.isShape }.map(\.id))
        for element in elements {
            for anchor in [element.startAnchor, element.endAnchor].compactMap({ $0 }) {
                guard targets.contains(anchor.elementID) else {
                    throw ShapeLibraryError.invalidFile("A connector refers to a drawing outside this shape.")
                }
            }
        }
    }

    public func data() throws -> Data {
        try validate()
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let data = try encoder.encode(self)
        guard data.count <= Self.maximumBytes else { throw ShapeLibraryError.entryTooLarge }
        return data
    }

    public static func read(_ data: Data) throws -> SavedShape {
        guard data.count <= maximumBytes else { throw ShapeLibraryError.entryTooLarge }
        let shape = try JSONDecoder().decode(Self.self, from: data)
        try shape.validate()
        return shape
    }

    public func adding(to document: CanvasDocument, centeredAt center: Point) throws -> CanvasDocument {
        guard !document.isEditing else { throw ShapeLibraryError.unfinishedEdit }
        guard center.isFinite else { throw CanvasError.invalidGeometry }
        _ = try data()
        let originalBytes = 64 + document.elements.reduce(0) { $0 + $1.estimatedBytes }
        guard originalBytes + estimatedBytes <= min(document.historyByteLimit, ShapeLibraryStore.maximumBytes) else {
            throw ShapeLibraryError.undoBudget
        }
        var usedIDs = Set((document.elements + elements).flatMap { [$0.id] + [$0.groupID].compactMap { $0 } })
        func freshID() -> UUID {
            var id = UUID()
            while !usedIDs.insert(id).inserted { id = UUID() }
            return id
        }
        let ids = Dictionary(uniqueKeysWithValues: elements.map { ($0.id, freshID()) })
        let groups = Dictionary(uniqueKeysWithValues: Set(elements.compactMap(\.groupID)).map { ($0, freshID()) })
        let reusableGroup = elements.count > 1 ? freshID() : nil
        let offset = center - bounds.center
        let copies = try elements.map { original in
            var copy = original
            guard let id = ids[original.id] else { throw ShapeLibraryError.invalidFile("An object ID is missing.") }
            copy.id = id
            copy.groupID = reusableGroup ?? original.groupID.flatMap { groups[$0] }
            copy.locked = false
            copy.startAnchor = original.startAnchor.flatMap { anchor in
                ids[anchor.elementID].map { Anchor(elementID: $0, relative: anchor.relative) }
            }
            copy.endAnchor = original.endAnchor.flatMap { anchor in
                ids[anchor.elementID].map { Anchor(elementID: $0, relative: anchor.relative) }
            }
            copy.translate(by: offset)
            try copy.validate()
            return copy
        }
        var result = document
        result.begin()
        for copy in copies { try result.add(copy) }
        result.select(Set(copies.map(\.id)))
        try result.commit()
        return result
    }

    private static func checkContent(_ elements: [Element]) throws {
        guard elements.allSatisfy({ !$0.tool.isProtection && $0.tool != .image && $0.imagePNG == nil }) else {
            throw ShapeLibraryError.unsupportedContent
        }
    }
}
