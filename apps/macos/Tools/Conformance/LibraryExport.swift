import AnnotationCore
import Foundation

/// My shapes: saved-shape files, creation and naming rules, inserting copies, and a scripted store session.
@MainActor
enum LibraryExport {
    static func run(_ corpus: Corpus) async throws {
        corpus.describe("library", "Saved-shape files with their read result and the document after inserting them, creation and naming refusals, invalid files, and a scripted ShapeLibraryStore session.")
        let files = try validFiles(corpus)
        try invalidFiles(corpus, template: files[0].1)
        try creation(corpus)
        try await store(corpus, files: files.map(\.1))
    }

    /// A saved-shape file written by the app, with the random shape ID replaced by a fixture ID.
    static func file(_ number: Int, name: String, elements: [Element], selecting: [Int]) throws -> Data {
        var document = try CanvasDocument(elements: elements)
        document.select(Set(selecting.map(Fixture.id)))
        let shape = try SavedShape(name: name, document: document)
        let text = String(decoding: try shape.data(), as: UTF8.self)
        return Data(text.replacingOccurrences(of: shape.id.uuidString, with: Fixture.id(number).uuidString).utf8)
    }

    private static func validFiles(_ corpus: Corpus) throws -> [(String, Data)] {
        let basic = Samples.basic()
        let gradientPen = Fixture.element(20, .pen, points: Samples.wave(from: Point(0, 0), count: 16, step: 6, amplitude: 9),
                                          style: Fixture.style(.pen) { $0.setGradient(GradientPreset.orchid.gradient, fill: false) })
        let label = Fixture.element(21, .rectangle, frame: Rect(x: 0, y: 0, width: 120, height: 60)) {
            $0.label = ShapeLabel(text: "Cache"); $0.groupID = Fixture.id(950)
        }
        let caption = Fixture.element(22, .text, frame: Rect(x: 0, y: 70, width: 120, height: 30), text: "Note") { $0.groupID = Fixture.id(950) }
        let files: [(String, Data)] = [
            ("arrow-between-boxes", try file(301, name: "Arrow between boxes", elements: basic, selecting: [1, 2, 3])),
            ("detached-connector", try file(302, name: "Detached connector", elements: basic, selecting: [1, 3])),
            ("grouped-label", try file(303, name: "Grouped label", elements: [label, caption], selecting: [21, 22])),
            ("gradient-pen", try file(304, name: "Gradient pen", elements: [gradientPen], selecting: [20])),
            ("trimmed-name", try file(305, name: "  Spaced name\n", elements: [basic[3]], selecting: [4])),
            ("single-rectangle", try file(306, name: "Ünïcödé 名前 ✓", elements: [basic[0]], selecting: [1]))
        ]
        let base = try CanvasDocument(elements: [Fixture.element(500, .ellipse, frame: Rect(x: 0, y: 0, width: 40, height: 40))])
        var records: [J] = []
        for (name, data) in files {
            corpus.data("library/valid/\(name).json", data)
            let shape = try SavedShape.read(data)
            let fresh = FreshIDs()
            let added = outcome { .object(documentRecord(try shape.adding(to: base, centeredAt: Point(300, 200)), fresh)) }
            records.append([
                "name": .string(name), "file": .string("\(name).json"), "shapeName": .string(shape.name),
                "id": .string(shape.id.uuidString), "elementCount": .int(shape.elements.count), "bounds": shape.bounds.json,
                "estimatedBytes": .int(shape.estimatedBytes), "drawingSchema": .int(shape.drawing.schemaVersion),
                "writtenEqualsInput": .bool(try shape.data() == data),
                "insertedAt300x200": added
            ])
        }
        try corpus.lines("library/valid/index.jsonl", records)
        return files
    }

    private static func invalidFiles(_ corpus: Corpus, template: Data) throws {
        let text = String(decoding: template, as: UTF8.self)
        let id = Fixture.id(301).uuidString
        func edit(_ target: String, _ replacement: String) throws -> Data {
            guard text.contains(target) else { throw ExportFailure("The template saved shape has no \(target).") }
            return Data(text.replacingOccurrences(of: target, with: replacement).utf8)
        }
        let cover = try RawBoard.bytes([Fixture.element(40, .cover, frame: Rect(x: 0, y: 0, width: 10, height: 10))], schema: 1)
        let image = try RawBoard.bytes([Fixture.element(41, .image, frame: Rect(x: 0, y: 0, width: 4, height: 3)) {
            $0.imagePNG = try? Fixture.smallPNG() }], schema: 1)
        func wrap(_ drawing: Data, name: String = "Shape") -> Data {
            Data(("{\"drawing\":" + String(decoding: drawing, as: UTF8.self) + ",\"id\":\"\(id)\",\"name\":\"\(name)\",\"schemaVersion\":1}").utf8)
        }
        let outside = try RawBoard.bytes([Fixture.element(42, .arrow, points: [Point(0, 0), Point(50, 0)]) {
            $0.endAnchor = Anchor(elementID: Fixture.id(43), relative: Point(0.5, 0.5)) }], schema: 1)
        let cases: [(String, Data)] = [
            ("schema-2", try edit("\"schemaVersion\":1}", "\"schemaVersion\":2}")),
            ("empty-name", try edit("\"name\":\"Arrow between boxes\"", "\"name\":\"\"")),
            ("spaces-only-name", try edit("\"name\":\"Arrow between boxes\"", "\"name\":\"   \"")),
            ("padded-name", try edit("\"name\":\"Arrow between boxes\"", "\"name\":\" Padded\"")),
            ("name-with-line-break", try edit("\"name\":\"Arrow between boxes\"", "\"name\":\"Two\\nlines\"")),
            ("name-with-tab", try edit("\"name\":\"Arrow between boxes\"", "\"name\":\"Tab\\there\"")),
            ("name-with-direction-mark", try edit("\"name\":\"Arrow between boxes\"", "\"name\":\"Right\\u202eto left\"")),
            ("name-121-characters", try edit("\"name\":\"Arrow between boxes\"", "\"name\":\"\(String(repeating: "n", count: 121))\"")),
            ("name-120-decomposed-letters", try edit("\"name\":\"Arrow between boxes\"", "\"name\":\"\(String(repeating: "e\u{301}", count: 61))\"")),
            ("cover", wrap(cover)),
            ("image", wrap(image)),
            ("no-elements", wrap(try RawBoard.bytes([], schema: 1))),
            ("connector-outside", wrap(outside)),
            ("missing-id", try edit(",\"id\":\"\(id)\"", "")),
            ("drawing-schema-4", wrap(try RawBoard.bytes([Fixture.element(44, .rectangle, frame: Rect(x: 0, y: 0, width: 5, height: 5))], schema: 4)))
        ]
        var records: [J] = []
        for (name, data) in cases {
            corpus.data("library/invalid/\(name).json", data)
            let read = outcome { .string(try SavedShape.read(data).name) }
            records.append(["name": .string(name), "file": .string("\(name).json"), "read": read])
        }
        records.append(["name": "over-8-mb", "recipe": "8,388,609 bytes of spaces, read with SavedShape.read.",
                        "read": outcome { .string(try SavedShape.read(Data(repeating: 0x20, count: 8 * 1_024 * 1_024 + 1)).name) }])
        try corpus.lines("library/invalid/index.jsonl", records)
    }

    private static func creation(_ corpus: Corpus) throws {
        let basic = Samples.basic()
        func make(_ name: String, select: [Int], elements: [Element] = Samples.basic(), editing: Bool = false) -> J {
            outcome {
                var document = try CanvasDocument(elements: elements)
                document.select(Set(select.map(Fixture.id)))
                if editing { document.begin() }
                let shape = try SavedShape(name: name, document: document)
                return ["name": .string(shape.name), "elementCount": .int(shape.elements.count)]
            }
        }
        let cover = Fixture.element(60, .cover, frame: Rect(x: 0, y: 0, width: 10, height: 10))
        let names = ["Plain", "  Trimmed  ", "", " ", "\n", "Two\nlines", "Tab\tname", "Bell\u{7}", "Mark\u{200f}", "Isolate\u{2068}x",
                     "Arabic\u{61c}mark", String(repeating: "n", count: 120), String(repeating: "n", count: 121),
                     String(repeating: "é", count: 120), String(repeating: "e\u{301}", count: 60), String(repeating: "e\u{301}", count: 61),
                     String(repeating: "👍🏽", count: 60), String(repeating: "👍🏽", count: 61), "Line\u{2028}separator", "Zero\u{200b}width"]
        var records: [J] = names.map { ["case": "name", "input": .string($0), "result": make($0, select: [1])] }
        records.append(["case": "nothing selected", "result": make("Shape", select: [])])
        records.append(["case": "unfinished edit", "result": make("Shape", select: [1], editing: true)])
        records.append(["case": "cover selected", "result": make("Shape", select: [1, 60], elements: basic + [cover])])
        let shape = try SavedShape.read(try file(310, name: "Original", elements: basic, selecting: [1]))
        for name in ["Renamed", "  Renamed  ", "", String(repeating: "x", count: 121)] {
            records.append(["case": "rename", "input": .string(name), "result": outcome { .string(try shape.renamed(to: name).name) }])
        }
        var editing = try CanvasDocument(elements: basic)
        editing.begin()
        let small = try CanvasDocument(elements: basic, historyByteLimit: 6_000)
        let insertCases: [(String, CanvasDocument, Point)] = [
            ("insert into a document with an unfinished edit", editing, Point(0, 0)),
            ("insert at a center that is not finite", try CanvasDocument(), Point(.infinity, 0)),
            ("insert at a center beyond the coordinate limit", try CanvasDocument(), Point(10_000_001, 0)),
            ("insert when the undo history budget is too small", small, Point(0, 0))
        ]
        for (name, document, center) in insertCases {
            records.append(["case": .string(name), "result": outcome { .int(try shape.adding(to: document, centeredAt: center).elements.count) }])
        }
        try corpus.lines("library/creation.jsonl", records)
    }

    private static func store(_ corpus: Corpus, files: [Data]) async throws {
        let manager = FileManager.default
        let root = manager.temporaryDirectory.appendingPathComponent("annotator-conformance-\(ProcessInfo.processInfo.processIdentifier)")
        try? manager.removeItem(at: root)
        defer { try? manager.removeItem(at: root) }
        let folder = root.appendingPathComponent("shapes")
        let store = ShapeLibraryStore(directory: folder)
        let shapes = try files.map { try SavedShape.read($0) }
        var records: [J] = []
        func list(_ result: [SavedShape]) -> J { .array(result.map { [.string($0.name), .string($0.id.uuidString)] }) }
        func step(_ name: String, _ body: () async throws -> [SavedShape]) async {
            do { records.append(["step": .string(name), "result": ["ok": list(try await body())]]) }
            catch { records.append(["step": .string(name), "result": ["error": ErrorRecord.describe(error)]]) }
        }
        await step("load a folder that does not exist") { try await store.load() }
        await step("save Arrow between boxes") { try await store.save(shapes[0]) }
        await step("save Grouped label") { try await store.save(shapes[2]) }
        await step("save Single rectangle") { try await store.save(shapes[5]) }
        await step("save Arrow between boxes again") { try await store.save(shapes[0]) }
        await step("rename Grouped label to Alpha") { try await store.rename(shapes[2].id, to: "Alpha") }
        await step("rename Single rectangle to an empty name") { try await store.rename(shapes[5].id, to: "") }
        await step("rename a shape that is not saved") { try await store.rename(Fixture.id(399), to: "Missing") }
        await step("delete Arrow between boxes") { try await store.delete(shapes[0].id) }
        await step("delete it again") { try await store.delete(shapes[0].id) }
        let lettered = "ABCDEF01-0000-4000-8000-000000000302"
        let extra: [(String, String, Data)] = [
            ("a file whose name is not an ID", "not-an-id.json", files[1]),
            ("a file whose name is a lowercase ID", lettered.lowercased() + ".json",
             Data(String(decoding: files[1], as: UTF8.self).replacingOccurrences(of: Fixture.id(302).uuidString, with: lettered).utf8)),
            ("a file whose name is another shape's ID", Fixture.id(377).uuidString + ".json", files[1]),
            ("a file that is not a saved shape", Fixture.id(378).uuidString + ".json", Data("{}".utf8))
        ]
        for (name, fileName, data) in extra {
            let url = folder.appendingPathComponent(fileName)
            try data.write(to: url)
            await step("load with \(name)") { try await store.load() }
            try manager.removeItem(at: url)
        }
        try files[3].write(to: folder.appendingPathComponent(".hidden.json"))
        try Data("notes".utf8).write(to: folder.appendingPathComponent("notes.txt"))
        await step("load with a hidden file and a text file, which are ignored") { try await store.load() }
        let outsideFile = root.appendingPathComponent("outside.json")
        try files[3].write(to: outsideFile)
        let link = folder.appendingPathComponent(Fixture.id(304).uuidString + ".json")
        try manager.createSymbolicLink(at: link, withDestinationURL: outsideFile)
        await step("load with a saved shape that is a link") { try await store.load() }
        try manager.removeItem(at: link)
        let linkedFolder = root.appendingPathComponent("linked")
        try manager.createSymbolicLink(at: linkedFolder, withDestinationURL: folder)
        await step("load a library folder that is a link") { try await ShapeLibraryStore(directory: linkedFolder).load() }

        let memory = ShapeLibraryStore()
        let template = String(decoding: files[5], as: UTF8.self)
        var full: [SavedShape] = []
        for index in 0..<ShapeLibraryStore.maximumEntries {
            let data = Data(template.replacingOccurrences(of: Fixture.id(306).uuidString, with: Fixture.id(10_000 + index).uuidString).utf8)
            full = try await memory.save(try SavedShape.read(data))
        }
        records.append(["step": "save 256 shapes in a library kept in memory", "result": ["ok": ["count": .int(full.count)]]])
        await step("save a 257th shape") { try await memory.save(shapes[3]) }
        try corpus.lines("library/store-script.jsonl", records)
        try corpus.json("library/limits.json", [
            "savedShapeMaximumBytes": .int(SavedShape.maximumBytes), "libraryMaximumBytes": .int(ShapeLibraryStore.maximumBytes),
            "libraryMaximumEntries": .int(ShapeLibraryStore.maximumEntries), "savedShapeSchema": .int(SavedShape.currentSchema),
            "sortOrder": "By name with Swift String <, then by ID string."
        ])
    }
}
