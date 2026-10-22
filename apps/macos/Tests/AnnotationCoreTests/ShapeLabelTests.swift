import Foundation
import Testing
@testable import AnnotationCore

@Test func shapeLabelsStayInsideTheirOwnersAcrossHistoryAndCopies() throws {
    var shape = Element(tool: .database, frame: Rect(x: 40, y: 50, width: 160, height: 120))
    shape.label = ShapeLabel(text: "Orders")
    var document = try CanvasDocument(elements: [shape])
    document.select([shape.id])
    try document.duplicateSelection()
    let copy = try #require(document.elements.last)
    #expect(copy.id != shape.id)
    #expect(copy.label == shape.label)
    try document.update(copy.id) { $0.label = ShapeLabel(text: "Archive") }
    #expect(document.elements.first?.label?.text == "Orders")
    document.undo()
    #expect(document.elements.last?.label?.text == "Orders")
    try document.update(shape.id) { $0.translate(by: Point(25, 30)) }
    #expect(document.elements.first?.label?.text == "Orders")
    let oldFont = try #require(document.elements.first?.label?.style.fontSize)
    try document.update(shape.id) { $0.resize(to: Rect(x: 65, y: 80, width: 320, height: 240)) }
    #expect(document.elements.first?.label?.style.fontSize == oldFont*2)
    #expect(shape.estimatedBytes >= MemoryLayout<Element>.stride + shape.label!.estimatedBytes)
}

@Test func labeledBoardsUseVersionTwoAndOldBoardsStillLoad() throws {
    let plain = Element(tool: .database, frame: Rect(x: 0, y: 0, width: 160, height: 120))
    let old = DrawingFile(document: try CanvasDocument(elements: [plain]))
    #expect(old.schemaVersion == 1)
    #expect(try DrawingFile.read(old.data()).elements.first?.label == nil)
    var labeled = plain
    labeled.label = ShapeLabel(text: "Customer records")
    let file = DrawingFile(document: try CanvasDocument(elements: [labeled]))
    #expect(file.schemaVersion == 2)
    #expect(try DrawingFile.read(file.data()).elements == [labeled])
    var object = try #require(JSONSerialization.jsonObject(with: file.data()) as? [String: Any])
    object["schemaVersion"] = 1
    let malformed = try JSONSerialization.data(withJSONObject: object)
    #expect(throws: CanvasError.self) { try DrawingFile.read(malformed) }
}

@Test func savedShapesKeepLabelsAndLegacyCylinderIdentity() throws {
    var shape = Element(tool: .database, frame: Rect(x: 40, y: 50, width: 160, height: 120))
    shape.label = ShapeLabel(text: "Orders")
    var document = try CanvasDocument(elements: [shape])
    document.select([shape.id])
    let saved = try SavedShape(name: "Labeled cylinder", document: document)
    let loaded = try SavedShape.read(saved.data())
    let inserted = try loaded.adding(to: .empty(), centeredAt: Point(300, 200))
    #expect(inserted.elements.first?.label == shape.label)
    #expect(inserted.elements.first?.frame.center == Point(300, 200))
    #expect(Tool.database.rawValue == "database")
    #expect(Tool.database.label == "Cylinder")
    #expect(Tool.databaseStack.label == "Database")
}

@Test func invalidLabelsAreRejectedAndDefaultTextHasContrast() throws {
    var cover = Element(tool: .cover, frame: Rect(x: 0, y: 0, width: 50, height: 50))
    cover.label = ShapeLabel(text: "Not a shape")
    #expect(throws: CanvasError.self) { try cover.validate() }
    #expect(ShapeLabel.defaultColor(on: RGBA(0.1, 0.1, 0.1)) == .white)
    #expect(ShapeLabel.defaultColor(on: .white) == RGBA(0, 0, 0))
}
