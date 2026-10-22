import Foundation
import Testing
@testable import AnnotationCore

@Test func groupedHistoryRetainsOperationIdentityAcrossEmptyParticipants() throws {
    let operation = UUID()
    var document = CanvasDocument.empty()
    document.begin()
    try document.clearAll()
    try document.commit(operationID: operation)
    #expect(document.canUndo)
    #expect(document.undoOperationID == operation)
    document.undo()
    #expect(document.redoOperationID == operation)
    document.redo()
    #expect(document.undoOperationID == operation)
    try document.add(Element(tool: .rectangle))
    #expect(document.undoOperationID == nil)
    document.undo()
    #expect(document.undoOperationID == operation)
}

@Test func transactionCancellationPreservesRedo() throws {
    var document = CanvasDocument.empty()
    let element = Element(tool: .rectangle, frame: Rect(x: 10, y: 10, width: 80, height: 50))
    try document.add(element)
    document.undo()
    #expect(document.canRedo)
    document.begin()
    try document.add(Element(tool: .ellipse))
    document.cancel()
    #expect(document.elements.isEmpty)
    #expect(document.canRedo)
    document.redo()
    #expect(document.elements.map(\.id) == [element.id])
}

@Test func cancellingNewTextRestoresSelectionHistoryAndSavedContent() throws {
    let original = Element(tool: .text, frame: Rect(x: 10, y: 10, width: 80, height: 50), text: "Keep")
    let undone = Element(tool: .ellipse)
    var document = try CanvasDocument(elements: [original])
    try document.add(undone)
    document.undo()
    document.select([original.id])
    let generation = document.generation
    let historyBytes = document.historyBytes
    document.begin()
    let draft = Element(tool: .text, text: " \n")
    try document.add(draft)
    document.select([draft.id])
    document.cancel()
    #expect(document.elements == [original])
    #expect(document.selection == [original.id])
    #expect(document.generation == generation)
    #expect(document.historyBytes == historyBytes)
    #expect(!document.canUndo)
    #expect(document.canRedo)
    #expect(!document.isEditing)
    #expect(try DrawingFile.read(DrawingFile(document: document).data()).elements == [original])
    #expect(try SavedShape(name: "Text", document: document).elements.map(\.text) == ["Keep"])
    document.redo()
    #expect(document.elements == [original, undone])
}

@Test func longStrokeIsOneTransactionWithoutTruncation() throws {
    var document = CanvasDocument.empty()
    let stroke = Element(tool: .pen)
    document.begin()
    try document.add(stroke)
    for index in 0..<20_000 {
        try document.appendSample(to: stroke.id, point: Point(Double(index), sin(Double(index))))
    }
    try document.commit()
    #expect(document.elements[0].points.count == 20_000)
    #expect(document.elements[0].pressures.count == 20_000)
    document.undo()
    #expect(document.elements.isEmpty)
    document.redo()
    #expect(document.elements[0].points.count == 20_000)
}

@Test func protectionSurvivesUntargetedCleanup() throws {
    var document = CanvasDocument.empty()
    let cover = Element(tool: .cover, frame: Rect(x: 0, y: 0, width: 100, height: 40))
    try document.add(cover)
    document.select([cover.id])
    try document.deleteSelection(includingProtection: false)
    try document.clearAnnotations()
    #expect(document.hasProtection)
    try document.clearProtection()
    #expect(!document.hasProtection)
}

@Test func bindingsFollowAndDuplicateTargets() throws {
    let target = Element(tool: .rectangle, frame: Rect(x: 0, y: 0, width: 100, height: 100))
    var arrow = Element(tool: .arrow, points: [Point(100, 50), Point(200, 50)])
    arrow.startAnchor = Anchor(elementID: target.id, relative: Point(1, 0.5))
    var document = try CanvasDocument(elements: [target, arrow])
    document.select([target.id])
    try document.editSelection { $0.translate(by: Point(30, 40)) }
    #expect(document.elements[1].points[0] == Point(130, 90))
    document.select([target.id, arrow.id])
    try document.duplicateSelection()
    let copies = document.elements.filter { document.selection.contains($0.id) }
    let copiedTarget = try #require(copies.first { $0.tool == .rectangle })
    let copiedArrow = try #require(copies.first { $0.tool == .arrow })
    #expect(copiedArrow.startAnchor?.elementID == copiedTarget.id)
    #expect(copiedArrow.points[0] == Point(148, 108))
}

@Test func locksAndHistoryBudgetPreserveVisibleElements() throws {
    var document = try CanvasDocument(historyByteLimit: 1_500, historyCountLimit: 3)
    let element = Element(tool: .rectangle)
    try document.add(element)
    document.select([element.id])
    try document.setSelectionLocked(true)
    try document.editSelection { $0.text = "must not apply" }
    try document.deleteSelection()
    #expect(document.elements.count == 1)
    #expect(document.elements[0].text.isEmpty)
    for _ in 0..<20 { try document.add(Element(tool: .ellipse)) }
    #expect(document.elements.count == 21)
    #expect(document.historyBytes <= 1_500)
}

@Test func fileRoundTripAndInvalidInput() throws {
    var document = CanvasDocument.empty()
    try document.add(Element(tool: .text, frame: Rect(x: -200, y: 20, width: 100, height: 90), text: "Hello\nworld"))
    let decoded = try DrawingFile.read(DrawingFile(document: document).data())
    #expect(decoded.elements == document.elements)
    #expect(throws: CanvasError.unsupportedSchema(99)) {
        try DrawingFile.read(Data(#"{"schemaVersion":99,"elements":[]}"#.utf8))
    }
    var invalid = Element(tool: .pen)
    invalid.points = [Point(.infinity, 0)]
    #expect(throws: CanvasError.invalidGeometry) { try document.add(invalid) }
}

@Test func viewportRoundTripWithNegativeOrigin() throws {
    let display = Rect(x: -1_440, y: -900, width: 1_440, height: 900)
    let viewport = try Viewport.zoom(display: display, factor: 4, focus: Point(-50, -50))
    for point in [display.origin, display.center, Point(display.maxX, display.maxY)] {
        let roundTrip = viewport.outputPoint(for: viewport.sourcePoint(for: point))
        #expect(roundTrip.distance(to: point) < 0.000_001)
    }
    #expect(display.contains(viewport.source))
    #expect(throws: CanvasError.invalidGeometry) {
        try Viewport.zoom(display: display, factor: .nan, focus: .zero)
    }
}
