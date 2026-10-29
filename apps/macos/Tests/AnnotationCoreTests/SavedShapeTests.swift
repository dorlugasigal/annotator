import Foundation
import Testing
@testable import AnnotationCore

@Suite struct SavedShapeTests {
    private func selection(_ elements: [Element]) throws -> CanvasDocument {
        var document = try CanvasDocument(elements: elements)
        document.select(Set(elements.map(\.id)))
        return document
    }

    private func rectangle() -> Element {
        Element(tool: .rectangle, frame: Rect(x: 40, y: 60, width: 100, height: 80))
    }

    @Test func savingPreservesSourceAndRoundTripsAllDrawingKinds() throws {
        let tools = Tool.allCases.filter { !$0.isProtection && ![.select, .eraser, .laser, .image].contains($0) }
        let elements = tools.map { tool in
            var item = Element(tool: tool, frame: Rect(x: 10, y: 20, width: 80, height: 70),
                               points: tool.isFreehand || tool.isConnector ? [Point(10, 20), Point(90, 90)] : [],
                               text: tool == .text ? "Two\nlines" : "")
            if tool.isFreehand { item.pressures = [0.25, 0.75] }
            item.rotation = 0.25
            item.style.roughness = 2
            return item
        }
        var document = try selection(elements)
        try document.add(rectangle())
        document.undo()
        let original = document.elements
        let selected = document.selection
        let shape = try SavedShape(name: "  Diagram  ", document: document)
        let restored = try SavedShape.read(shape.data())
        #expect(restored.name == "Diagram")
        #expect(restored.id == shape.id)
        #expect(restored.elements == original)
        #expect(document.elements == original)
        #expect(document.selection == selected)
        #expect(document.canRedo)
        #expect(!document.canUndo)
    }

    @Test func freshCopiesRemapGroupsAnchorsAndSupportOneUndoRedo() throws {
        var target = rectangle()
        var text = Element(tool: .text, frame: Rect(x: 50, y: 70, width: 60, height: 30), text: "Label")
        let external = Element(tool: .ellipse, frame: Rect(x: 260, y: 60, width: 80, height: 80))
        var arrow = Element(tool: .arrow, points: [Point(140, 100), Point(260, 100)])
        arrow.startAnchor = Anchor(elementID: target.id, relative: Point(1, 0.5))
        arrow.endAnchor = Anchor(elementID: external.id, relative: Point(0, 0.5))
        target.groupID = UUID()
        text.groupID = target.groupID
        arrow.groupID = UUID()
        target.locked = true
        var document = try selection([target, text, arrow, external])
        document.select([target.id, text.id, arrow.id])
        let original = document.elements
        let originalSelection = document.selection
        let shape = try SavedShape(name: "Connected group", document: document)
        #expect(shape.elements[2].endAnchor == nil)
        #expect(shape.elements[2].points.last == original[2].points.last)
        #expect(document.elements == original)
        let center = Point(700, 500)
        var result = try shape.adding(to: document, centeredAt: center)
        let inserted = Array(result.elements.dropFirst(original.count))
        #expect(inserted.count == 3)
        #expect(result.selection == Set(inserted.map(\.id)))
        #expect(Array(result.elements.prefix(original.count)) == original)
        #expect(Set(inserted.map(\.id)).isDisjoint(with: Set(original.map(\.id))))
        #expect(inserted.allSatisfy { !$0.locked })
        #expect(inserted[0].groupID == inserted[1].groupID)
        #expect(inserted[0].groupID != target.groupID)
        #expect(inserted[2].groupID != arrow.groupID)
        #expect(inserted[2].groupID == inserted[0].groupID)
        #expect(inserted[2].startAnchor?.elementID == inserted[0].id)
        #expect(inserted[2].endAnchor == nil)
        #expect(inserted[0].seed == target.seed)
        #expect(inserted[0].geometryToken != target.geometryToken)
        #expect(inserted[0].appendEpoch != target.appendEpoch)
        let insertedShape = try SavedShape(name: "Copy", document: result)
        #expect(insertedShape.bounds.center == center)
        let second = try shape.adding(to: result, centeredAt: center)
        #expect(second.selection.isDisjoint(with: result.selection))
        #expect(Set(second.elements.suffix(3).compactMap(\.groupID))
            .isDisjoint(with: Set(inserted.compactMap(\.groupID))))
        result.undo()
        #expect(result.elements == original)
        #expect(result.selection == originalSelection)
        #expect(!result.canUndo)
        result.redo()
        #expect(Array(result.elements.suffix(3)) == inserted)
        #expect(result.selection == Set(inserted.map(\.id)))
        result.select([inserted[0].id])
        try result.editSelection { $0.translate(by: Point(12, 18)) }
        #expect(result.elements[6].points[0] == inserted[2].points[0] + Point(12, 18))
    }

    @Test func unsupportedSelectionFailsAsAWhole() throws {
        for tool in Tool.allCases.filter({ $0.isProtection || $0 == .image }) {
            var item = Element(tool: tool, frame: Rect(x: 0, y: 0, width: 20, height: 20))
            if tool == .image { item.imagePNG = Data([137, 80, 78, 71, 13, 10, 26, 10]) }
            let document = try selection([rectangle(), item])
            #expect(throws: ShapeLibraryError.unsupportedContent) {
                try SavedShape(name: "Private", document: document)
            }
            #expect(document.elements.count == 2)
        }
    }

    @Test func emptyNamesControlsAndPendingEditsAreRejected() throws {
        let document = try selection([rectangle()])
        for name in ["", "   ", "Line\nbreak", "A\u{0000}B", "\u{202e}reversed", String(repeating: "A", count: 121)] {
            #expect(throws: ShapeLibraryError.invalidName) { try SavedShape(name: name, document: document) }
        }
        #expect(try SavedShape(name: String(repeating: "A", count: 120), document: document).name.count == 120)
        #expect(throws: ShapeLibraryError.noSelection) { try SavedShape(name: "Empty", document: .empty()) }
        var pending = document
        pending.begin()
        #expect(throws: ShapeLibraryError.unfinishedEdit) { try SavedShape(name: "Draft", document: pending) }
        let shape = try SavedShape(name: "Ready", document: document)
        #expect(throws: ShapeLibraryError.unfinishedEdit) { try shape.adding(to: pending, centeredAt: .zero) }
        #expect(pending.isEditing)
        #expect(pending.elements == document.elements)
    }

    @Test func malformedSchemaIdentityGeometryAndHiddenImageDataAreRejected() throws {
        let shape = try SavedShape(name: "Valid", document: selection([rectangle()]))
        let data = try shape.data()
        #expect(throws: (any Error).self) { try SavedShape.read(Data("broken".utf8)) }
        let object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        for mutation in 0..<6 {
            var changed = object
            var drawing = try #require(changed["drawing"] as? [String: Any])
            var elements = try #require(drawing["elements"] as? [[String: Any]])
            switch mutation {
            case 0: changed["schemaVersion"] = 99
            case 1: drawing["schemaVersion"] = 99
            case 2: elements.append(elements[0])
            case 3: elements[0]["frame"] = ["x": 0, "y": 0, "width": -1, "height": 5]
            case 4: elements[0]["imagePNG"] = Data("private pixels".utf8).base64EncodedString()
            default: elements[0]["tool"] = "cover"
            }
            drawing["elements"] = elements
            changed["drawing"] = drawing
            let invalid = try JSONSerialization.data(withJSONObject: changed)
            #expect(throws: (any Error).self) { try SavedShape.read(invalid) }
        }
    }

    @Test func rotatedBoundsAndInvalidPlacement() throws {
        var item = rectangle()
        item.rotation = .pi / 2
        let document = try selection([item])
        let shape = try SavedShape(name: "Rotated", document: document)
        let result = try shape.adding(to: .empty(), centeredAt: Point(-300, 400))
        #expect(abs(result.elements[0].frame.center.x + 300) < 0.0001)
        #expect(abs(result.elements[0].frame.center.y - 400) < 0.0001)
        for center in [Point(.infinity, 0), Point(10_000_001, 0)] {
            #expect(throws: CanvasError.invalidGeometry) { try shape.adding(to: document, centeredAt: center) }
        }
        #expect(document.elements == [item])
    }

    @Test func inputAndUndoBudgetsRejectWithoutTruncation() throws {
        let shape = try SavedShape(name: "Box", document: selection([rectangle()]))
        #expect(throws: ShapeLibraryError.entryTooLarge) {
            try SavedShape.read(Data(repeating: 32, count: SavedShape.maximumBytes + 1))
        }
        let pointCount = SavedShape.maximumBytes / 16
        let longStroke = Element(tool: .pen, points: Array(repeating: Point(1, 2), count: pointCount))
        let document = try selection([longStroke])
        #expect(throws: ShapeLibraryError.entryTooLarge) { try SavedShape(name: "Long", document: document) }
        #expect(document.elements[0].points.count == pointCount)
        let limited = try CanvasDocument(elements: [rectangle()], historyByteLimit: 1_000)
        #expect(throws: ShapeLibraryError.undoBudget) { try shape.adding(to: limited, centeredAt: .zero) }
        #expect(limited.elements.count == 1)
        #expect(!limited.canUndo)
    }

    @Test func fullStrokeAndTextSurviveSavingAndInsertion() throws {
        let points = (0..<20_000).map { Point(Double($0) / 100, sin(Double($0))) }
        var stroke = Element(tool: .pen, points: points)
        stroke.pressures = Array(repeating: 0.75, count: points.count)
        let text = Element(tool: .text, frame: Rect(x: 10, y: 20, width: 50, height: 40), text: "A\nB")
        let shape = try SavedShape.read(SavedShape(name: "Ink", document: selection([stroke, text])).data())
        let copy = try shape.adding(to: .empty(), centeredAt: shape.bounds.center)
        #expect(copy.elements[0].points == points)
        #expect(copy.elements[0].pressures == stroke.pressures)
        #expect(copy.elements[1].text == text.text)
    }
}
