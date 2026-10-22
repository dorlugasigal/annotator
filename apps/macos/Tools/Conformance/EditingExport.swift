import AnnotationCore
import Foundation

/// Scripted editing sessions: every step's result and the document state after it.
@MainActor
enum EditingExport {
    static func run(_ corpus: Corpus) throws {
        corpus.describe("editing", "Scripted CanvasDocument sessions. Each line is one step: its result or error, then the elements, selection, generation, undo state and history size after it.")
        try corpus.lines("editing/session.jsonl", try session())
        try corpus.lines("editing/history-limits.jsonl", try historyLimits())
        try corpus.lines("editing/refusals.jsonl", try refusals())
    }

    @MainActor private final class Script {
        var document: CanvasDocument
        let ids = FreshIDs()
        var records: [J] = []
        init(_ document: CanvasDocument) { self.document = document }

        @MainActor func step(_ name: String, _ body: (inout CanvasDocument) throws -> J = { _ in .null }) {
            var record: [String: J] = ["step": .string(name)]
            do { record["result"] = try body(&document) } catch { record["error"] = ErrorRecord.describe(error) }
            records.append(.object(record.merging(documentRecord(document, ids)) { $1 }))
        }
    }

    private static let rectangle = Fixture.element(1, .rectangle, frame: Rect(x: 40, y: 40, width: 120, height: 80))
    private static let ellipse = Fixture.element(2, .ellipse, frame: Rect(x: 300, y: 60, width: 100, height: 60),
                                                 style: Fixture.style(.ellipse) { $0.fill = RGBA(1, 0.82, 0, 0.4) })
    private static let arrow = Fixture.element(3, .arrow, points: [Point(160, 80), Point(300, 90)]) {
        $0.startAnchor = Anchor(elementID: Fixture.id(1), relative: Point(1, 0.5))
        $0.endAnchor = Anchor(elementID: Fixture.id(2), relative: Point(0, 0.5))
    }

    private static func session() throws -> [J] {
        let script = Script(try CanvasDocument())
        let pen = Fixture.element(4, .pen, points: [Point(60, 200)])
        let cover = Fixture.element(5, .cover, frame: Rect(x: 400, y: 200, width: 60, height: 40))
        script.step("start with an empty document")
        script.step("add a rectangle") { try $0.add(rectangle); return .null }
        script.step("add an ellipse") { try $0.add(ellipse); return .null }
        script.step("add an arrow bound to both") { try $0.add(arrow); return .null }
        script.step("move the rectangle by (10, 20); the arrow start follows") { try $0.update(Fixture.id(1)) { $0.translate(by: Point(10, 20)) }; return .null }
        script.step("resize the ellipse; the arrow end follows") {
            try $0.update(Fixture.id(2)) { $0.resize(to: Rect(x: 320, y: 40, width: 140, height: 100)) }; return .null
        }
        script.step("select the rectangle and the ellipse") { $0.select([Fixture.id(1), Fixture.id(2)]); return .null }
        script.step("group them") { try $0.groupSelection(); return .null }
        script.step("select nothing by clicking empty space") { $0.select(at: Point(700, 500)); return .null }
        script.step("click the ellipse, which selects its whole group") { $0.select(at: Point(390, 90)); return .null }
        script.step("shift-click the arrow to add it") { $0.select(at: Point(240, 95), extending: true); return .null }
        script.step("duplicate the selection") { try $0.duplicateSelection(); return .null }
        script.step("send the copies to the back") { try $0.arrangeSelection(toFront: false); return .null }
        script.step("lock the copies") { try $0.setSelectionLocked(true); return .null }
        script.step("delete the selection; locked objects stay") { try $0.deleteSelection(); return .null }
        script.step("unlock and delete the copies") { try $0.setSelectionLocked(false); try $0.deleteSelection(); return .null }
        script.step("undo") { $0.undo(); return .null }
        script.step("undo") { $0.undo(); return .null }
        script.step("redo") { $0.redo(); return .null }
        script.step("ungroup the selection") { try $0.ungroupSelection(); return .null }
        script.step("begin an edit and move the arrow") {
            $0.begin(); try $0.update(Fixture.id(3)) { $0.translate(by: Point(0, 50)) }; return .null
        }
        script.step("cancel the edit") { $0.cancel(); return .null }
        script.step("begin an edit, recolor the selection and commit with an operation ID") {
            $0.begin(); try $0.editSelection { $0.style.color = .blue }; try $0.commit(operationID: Fixture.id(800)); return .null
        }
        script.step("undo the operation") { $0.undo(); return .null }
        script.step("add a pen stroke with one point") { try $0.add(pen); return .null }
        script.step("append three pen samples with pressure") {
            for (index, point) in [Point(70, 210), Point(85, 205), Point(100, 220)].enumerated() {
                try $0.appendSample(to: Fixture.id(4), point: point, pressure: 0.25 * Double(index + 1))
            }
            return .null
        }
        script.step("undo the last sample") { $0.undo(); return .null }
        script.step("append a sample after the undo") { try $0.appendSample(to: Fixture.id(4), point: Point(120, 230)); return .null }
        script.step("nearest anchor near the rectangle's right edge") {
            anchor($0.nearestAnchor(to: Point(168, 112), excluding: Fixture.id(3)), script.ids)
        }
        script.step("nearest anchor just outside 18 points") {
            anchor($0.nearestAnchor(to: Point(170 + 18.5, 120), excluding: Fixture.id(3)), script.ids)
        }
        script.step("nearest anchor between two candidates") {
            anchor($0.nearestAnchor(to: Point(330, 85), excluding: Fixture.id(3), threshold: 40), script.ids)
        }
        script.step("add a cover") { try $0.add(cover); return .null }
        script.step("select everything and delete without covers") {
            $0.select(Set($0.elements.map(\.id))); try $0.deleteSelection(includingProtection: false); return .null
        }
        script.step("undo") { $0.undo(); return .null }
        script.step("clear annotations; the cover stays") { try $0.clearAnnotations(); return .null }
        script.step("undo") { $0.undo(); return .null }
        script.step("clear covers and effects") { try $0.clearProtection(); return .null }
        script.step("show the lens at 3x") { try $0.setPresentation(PresentationSettings(mode: .lens, zoom: 3)); return .null }
        script.step("stop the lens without a history step") { $0.stopPresentationWithoutHistory(); return .null }
        script.step("copy the rectangle and the arrow 40 points right; the copy keeps only its binding to the copied rectangle") {
            try $0.insertCopies(of: $0.elements.filter { [Fixture.id(1), Fixture.id(3)].contains($0.id) }, offset: Point(40, 0)); return .null
        }
        script.step("clear all") { try $0.clearAll(); return .null }
        script.step("undo") { $0.undo(); return .null }
        recognition(script)
        return script.records
    }

    private static func anchor(_ anchor: Anchor?, _ ids: FreshIDs) -> J {
        guard let anchor else { return .null }
        return ["elementID": .string(ids.known(anchor.elementID).uuidString), "relative": anchor.relative.json]
    }

    private static func recognition(_ script: Script) {
        let stroke = Fixture.element(6, .pen, points: [Point(500, 300), Point(600, 300), Point(600, 380), Point(500, 380), Point(500, 302)])
        script.step("add a pen stroke to recognize") { try $0.add(stroke); return .null }
        let generation = script.document.generation
        let token = script.document.elements.first { $0.id == stroke.id }!.geometryToken
        var shape = Fixture.element(6, .rectangle, frame: Rect(x: 500, y: 300, width: 100, height: 80))
        shape.seed = stroke.seed
        script.step("apply a recognized rectangle with a stale generation") {
            .bool(try $0.applyRecognition(shape, expectedGeneration: generation &+ 1, expectedGeometryToken: token))
        }
        script.step("apply a recognized rectangle with a stale geometry token") {
            .bool(try $0.applyRecognition(shape, expectedGeneration: generation, expectedGeometryToken: Fixture.id(7)))
        }
        script.step("apply the recognized rectangle") {
            .bool(try $0.applyRecognition(shape, expectedGeneration: generation, expectedGeometryToken: token))
        }
        script.step("undo after recognition") { $0.undo(); return .null }
    }

    private static func historyLimits() throws -> [J] {
        let script = Script(try CanvasDocument(historyByteLimit: 3_000, historyCountLimit: 3))
        for number in 1...6 {
            script.step("add rectangle \(number)") {
                try $0.add(Fixture.element(number, .rectangle, frame: Rect(x: Double(number) * 20, y: 0, width: 10, height: 10))); return .null
            }
        }
        for _ in 0..<4 { script.step("undo") { $0.undo(); return .null } }
        for _ in 0..<4 { script.step("redo") { $0.redo(); return .null } }
        script.step("add a stroke with 200 points, larger than the history budget") {
            try $0.add(Fixture.element(7, .pen, points: (0..<200).map { Point(Double($0), 50) })); return .null
        }
        script.step("undo") { $0.undo(); return .null }
        return script.records
    }

    private static func refusals() throws -> [J] {
        let script = Script(try CanvasDocument(elements: [rectangle, ellipse, arrow]))
        script.step("start with three objects")
        script.step("commit without an edit") { try $0.commit(); return .null }
        script.step("update a missing object") { try $0.update(Fixture.id(99)) { $0.rotation = 1 }; return .null }
        script.step("update to a width of zero") { try $0.update(Fixture.id(1)) { $0.style.width = 0 }; return .null }
        script.step("change an object's ID") { try $0.update(Fixture.id(1)) { $0.id = Fixture.id(98) }; return .null }
        script.step("add an object with an existing ID") { try $0.add(rectangle); return .null }
        script.step("append a sample to a rectangle") { try $0.appendSample(to: Fixture.id(1), point: Point(0, 0)); return .null }
        script.step("append a sample beyond the coordinate limit") {
            try $0.add(Fixture.element(8, .pen, points: [Point(0, 0)]))
            try $0.appendSample(to: Fixture.id(8), point: Point(0, 10_000_001)); return .null
        }
        script.step("append a sample with pressure above one") { try $0.appendSample(to: Fixture.id(8), point: Point(1, 1), pressure: 1.5); return .null }
        script.step("zoom the lens to 17x") { try $0.setPresentation(PresentationSettings(mode: .lens, zoom: 17)); return .null }
        script.step("lock the rectangle, then try to move it") {
            $0.select([Fixture.id(1)]); try $0.setSelectionLocked(true)
            try $0.update(Fixture.id(1)) { $0.translate(by: Point(5, 5)) }; return .null
        }
        script.step("rotate a cover") {
            try $0.add(Fixture.element(9, .cover, frame: Rect(x: 0, y: 0, width: 10, height: 10)))
            try $0.update(Fixture.id(9)) { $0.rotation = 0.5 }; return .null
        }
        var records = script.records
        for (name, bytes, count) in [("a history byte limit of zero", 0, 100), ("a history count limit of zero", 1_000, 0)] {
            records.append(["step": .string("create a document with \(name)"),
                            "result": outcome { _ = try CanvasDocument(historyByteLimit: bytes, historyCountLimit: count); return .null }])
        }
        records.append(["step": "create a document with two objects that share an ID",
                        "result": outcome { _ = try CanvasDocument(elements: [rectangle, rectangle]); return .null }])
        return records
    }
}
