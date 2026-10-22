import Foundation

public actor ShapeLibraryStore {
    public static let maximumBytes = 64 * 1_024 * 1_024
    public static let maximumEntries = 256
    public nonisolated let directory: URL?
    private var transientShapes: [SavedShape] = []

    public init(directory: URL? = nil) { self.directory = directory }

    public func load() throws -> [SavedShape] {
        try readLibrary().shapes
    }

    public func saveSelection(name: String, document: CanvasDocument) throws -> [SavedShape] {
        try save(SavedShape(name: name, document: document))
    }

    public func save(_ shape: SavedShape) throws -> [SavedShape] {
        let data = try shape.data()
        var library = try readLibrary()
        guard !library.shapes.contains(where: { $0.id == shape.id }) else {
            throw ShapeLibraryError.invalidFile("This shape is already saved.")
        }
        guard library.shapes.count < Self.maximumEntries,
              library.bytes + data.count <= Self.maximumBytes,
              library.memory + shape.estimatedBytes <= Self.maximumBytes else {
            throw ShapeLibraryError.libraryFull
        }
        if let directory {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try checkDirectory(directory)
            let destination = path(for: shape.id, in: directory)
            guard !FileManager.default.fileExists(atPath: destination.path) else {
                throw ShapeLibraryError.invalidFile("A file already uses this shape ID.")
            }
            try data.write(to: destination, options: .atomic)
        } else {
            transientShapes.append(shape)
        }
        library.shapes.append(shape)
        return sorted(library.shapes)
    }

    public func rename(_ id: UUID, to name: String) throws -> [SavedShape] {
        var library = try readLibrary()
        guard let index = library.shapes.firstIndex(where: { $0.id == id }) else {
            throw ShapeLibraryError.invalidFile("This shape is no longer saved. Reload My shapes.")
        }
        let previous = library.shapes[index]
        let updated = try previous.renamed(to: name)
        let data = try updated.data()
        let oldBytes: Int
        if let directory {
            guard let size = try path(for: id, in: directory).resourceValues(forKeys: [.fileSizeKey]).fileSize else {
                throw ShapeLibraryError.invalidFile("The saved shape size could not be read.")
            }
            oldBytes = size
        } else { oldBytes = try previous.data().count }
        guard library.bytes - oldBytes + data.count <= Self.maximumBytes else { throw ShapeLibraryError.libraryFull }
        if let directory {
            try data.write(to: path(for: id, in: directory), options: .atomic)
        } else {
            guard let transientIndex = transientShapes.firstIndex(where: { $0.id == id }) else {
                throw ShapeLibraryError.invalidFile("This shape is no longer saved.")
            }
            transientShapes[transientIndex] = updated
        }
        library.shapes[index] = updated
        return sorted(library.shapes)
    }

    public func delete(_ id: UUID) throws -> [SavedShape] {
        var library = try readLibrary()
        guard library.shapes.contains(where: { $0.id == id }) else {
            throw ShapeLibraryError.invalidFile("This shape is no longer saved. Reload My shapes.")
        }
        if let directory {
            try FileManager.default.removeItem(at: path(for: id, in: directory))
        } else {
            transientShapes.removeAll { $0.id == id }
        }
        library.shapes.removeAll { $0.id == id }
        return sorted(library.shapes)
    }

    private func path(for id: UUID, in directory: URL) -> URL {
        directory.appendingPathComponent(id.uuidString).appendingPathExtension("json")
    }

    private func checkDirectory(_ directory: URL) throws {
        let values = try directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard values.isDirectory == true, values.isSymbolicLink != true else {
            throw ShapeLibraryError.invalidFile("The library folder must be a local folder, not a link.")
        }
    }

    private func readLibrary() throws -> (shapes: [SavedShape], bytes: Int, memory: Int) {
        guard let directory else {
            let bytes = try transientShapes.reduce(0) { try $0 + $1.data().count }
            return (sorted(transientShapes), bytes, transientShapes.reduce(0) { $0 + $1.estimatedBytes })
        }
        guard FileManager.default.fileExists(atPath: directory.path) else { return ([], 0, 0) }
        try checkDirectory(directory)
        let files = try FileManager.default.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey],
            options: [.skipsHiddenFiles]).filter { $0.pathExtension == "json" }
        guard files.count <= Self.maximumEntries else { throw ShapeLibraryError.libraryFull }
        var shapes: [SavedShape] = []
        var bytes = 0
        var memory = 0
        for file in files {
            guard let id = UUID(uuidString: file.deletingPathExtension().lastPathComponent),
                  file.lastPathComponent == path(for: id, in: directory).lastPathComponent else {
                throw ShapeLibraryError.invalidFile("A saved file has an invalid ID.")
            }
            let values = try file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true, let size = values.fileSize else {
                throw ShapeLibraryError.invalidFile("A saved shape is not a regular file.")
            }
            guard size <= SavedShape.maximumBytes else { throw ShapeLibraryError.entryTooLarge }
            guard bytes + size <= Self.maximumBytes else { throw ShapeLibraryError.libraryFull }
            let handle = try FileHandle(forReadingFrom: file)
            let data: Data
            do {
                data = try handle.read(upToCount: SavedShape.maximumBytes + 1) ?? Data()
            } catch {
                try handle.close()
                throw error
            }
            try handle.close()
            let shape = try SavedShape.read(data)
            guard shape.id == id else { throw ShapeLibraryError.invalidFile("A saved file has the wrong shape ID.") }
            bytes += data.count
            memory += shape.estimatedBytes
            guard bytes <= Self.maximumBytes, memory <= Self.maximumBytes else { throw ShapeLibraryError.libraryFull }
            shapes.append(shape)
        }
        return (sorted(shapes), bytes, memory)
    }

    private func sorted(_ shapes: [SavedShape]) -> [SavedShape] {
        shapes.sorted {
            $0.name == $1.name ? $0.id.uuidString < $1.id.uuidString : $0.name < $1.name
        }
    }
}
