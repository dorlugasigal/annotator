import Foundation
import Testing
@testable import AnnotationCore

@Suite struct ShapeLibraryStoreTests {
    private func temporaryDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("annotator-shapes-\(UUID())")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    private func shape(_ name: String = "Saved") throws -> SavedShape {
        let element = Element(tool: .rectangle, frame: Rect(x: 20, y: 30, width: 60, height: 50))
        var document = try CanvasDocument(elements: [element])
        document.select([element.id])
        return try SavedShape(name: name, document: document)
    }

    @Test func saveReopenAndDeleteUseUUIDPaths() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("Library")
        let store = ShapeLibraryStore(directory: directory)
        #expect(try await store.load().isEmpty)
        #expect(!FileManager.default.fileExists(atPath: directory.path))
        let first = try shape("../../A / B")
        let second = try shape("../../A / B")
        #expect(try await store.save(first).count == 1)
        #expect(try await store.save(second).count == 2)
        let files = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        #expect(Set(files) == Set([first.id, second.id].map { "\($0.uuidString).json" }))
        let reopened = ShapeLibraryStore(directory: directory)
        let entries = try await reopened.load()
        #expect(Set(entries.map(\.id)) == [first.id, second.id])
        #expect(entries.allSatisfy { $0.name == first.name && $0.elements.count == 1 })
        #expect(try await reopened.delete(first.id).map(\.id) == [second.id])
        #expect(try await store.load().map(\.id) == [second.id])
        #expect(try await store.delete(second.id).isEmpty)
        #expect(try await reopened.load().isEmpty)
    }

    @Test func transientLibrariesNeverResolveDirectoriesAndRemainIndependent() async throws {
        let first = ShapeLibraryStore()
        let second = ShapeLibraryStore()
        #expect(first.directory == nil)
        #expect(second.directory == nil)
        #expect(try await first.load().isEmpty)
        let entry = try shape()
        #expect(try await first.save(entry).map(\.id) == [entry.id])
        #expect(try await second.load().isEmpty)
        #expect(try await first.load().first?.elements == entry.elements)
        #expect(try await first.delete(entry.id).isEmpty)
        #expect(first.directory == nil)
    }

    @Test func transientLibraryUsesTheSameValidationAndCountBudget() async throws {
        let store = ShapeLibraryStore()
        let first = try shape()
        _ = try await store.save(first)
        await #expect(throws: (any Error).self) { try await store.save(first) }
        await #expect(throws: ShapeLibraryError.noSelection) {
            try await store.saveSelection(name: "Empty", document: .empty())
        }
        for _ in 1..<ShapeLibraryStore.maximumEntries {
            _ = try await store.save(shape())
        }
        await #expect(throws: ShapeLibraryError.libraryFull) { try await store.save(shape()) }
        #expect(try await store.load().count == ShapeLibraryStore.maximumEntries)
        _ = try await store.delete(first.id)
        #expect(try await store.save(shape()).count == ShapeLibraryStore.maximumEntries)
    }

    @Test func corruptFileBlocksSaveWithoutReplacingAnything() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = ShapeLibraryStore(directory: directory)
        let first = try shape()
        _ = try await store.save(first)
        let corrupt = directory.appendingPathComponent("\(UUID().uuidString).json")
        let data = Data("not a shape".utf8)
        try data.write(to: corrupt)
        await #expect(throws: (any Error).self) { try await store.load() }
        await #expect(throws: (any Error).self) { try await store.save(shape("New")) }
        #expect(try Data(contentsOf: corrupt) == data)
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).count == 2)
        try FileManager.default.removeItem(at: corrupt)
        #expect(try await store.load().map(\.id) == [first.id])
    }

    @Test func duplicateMissingMismatchedAndSymlinkFilesFail() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("Library")
        let store = ShapeLibraryStore(directory: directory)
        let first = try shape()
        _ = try await store.save(first)
        await #expect(throws: (any Error).self) { try await store.save(first) }
        await #expect(throws: (any Error).self) { try await store.delete(UUID()) }
        let file = directory.appendingPathComponent("\(first.id.uuidString).json")
        let original = try Data(contentsOf: file)
        try shape("Wrong ID").data().write(to: file, options: .atomic)
        await #expect(throws: (any Error).self) { try await store.load() }
        try original.write(to: file, options: .atomic)
        let target = root.appendingPathComponent("outside.json")
        try original.write(to: target)
        try FileManager.default.removeItem(at: file)
        try FileManager.default.createSymbolicLink(at: file, withDestinationURL: target)
        await #expect(throws: (any Error).self) { try await store.load() }
        await #expect(throws: (any Error).self) { try await store.delete(first.id) }
        #expect(try Data(contentsOf: target) == original)
    }

    @Test func writeAndDeleteFailuresAreNotReportedAsSuccess() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("not-a-directory")
        try Data("keep".utf8).write(to: file)
        let store = ShapeLibraryStore(directory: file)
        await #expect(throws: (any Error).self) { try await store.save(shape()) }
        await #expect(throws: (any Error).self) { try await store.delete(UUID()) }
        #expect(try Data(contentsOf: file) == Data("keep".utf8))
    }

    @Test func exactEntryCountLimitAndOversizedFile() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var ids: [UUID] = []
        for _ in 0..<ShapeLibraryStore.maximumEntries {
            let entry = try shape()
            ids.append(entry.id)
            try entry.data().write(to: directory.appendingPathComponent("\(entry.id.uuidString).json"))
        }
        let store = ShapeLibraryStore(directory: directory)
        #expect(try await store.load().count == ShapeLibraryStore.maximumEntries)
        await #expect(throws: ShapeLibraryError.libraryFull) { try await store.save(shape()) }
        let victim = try #require(ids.first)
        #expect(try await store.delete(victim).count == ShapeLibraryStore.maximumEntries - 1)
        #expect(try await store.save(shape()).count == ShapeLibraryStore.maximumEntries)
        let oversized = directory.appendingPathComponent("\(try #require(ids.last).uuidString).json")
        try Data(repeating: 32, count: SavedShape.maximumBytes + 1).write(to: oversized)
        await #expect(throws: ShapeLibraryError.entryTooLarge) { try await store.load() }
    }

    @Test func exactAggregateFileBudgetAndDeletionRecovery() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var ids: [UUID] = []
        for _ in 0..<(ShapeLibraryStore.maximumBytes / SavedShape.maximumBytes) {
            let entry = try shape()
            var data = try entry.data()
            data.append(Data(repeating: 32, count: SavedShape.maximumBytes - data.count))
            ids.append(entry.id)
            try data.write(to: directory.appendingPathComponent("\(entry.id.uuidString).json"))
        }
        let store = ShapeLibraryStore(directory: directory)
        #expect(try await store.load().count == ids.count)
        await #expect(throws: ShapeLibraryError.libraryFull) { try await store.save(shape()) }
        let victim = try #require(ids.first)
        _ = try await store.delete(victim)
        #expect(try await store.save(shape()).count == ids.count)
    }
}
