import Foundation
import Testing
@testable import AnnotationCore

@Test func undoCancelsDraftBeforeTouchingCompletedHistory() throws {
    var document = CanvasDocument.empty()
    let completed = Element(tool: .rectangle, frame: Rect(x: 10, y: 10, width: 80, height: 50))
    let future = Element(tool: .ellipse, frame: Rect(x: 100, y: 10, width: 40, height: 40))
    try document.add(completed)
    document.select([completed.id])
    try document.add(future)
    document.undo()
    let generation = document.generation
    let historyBytes = document.historyBytes
    let restored = document.elements

    document.begin()
    try document.add(Element(tool: .pen, points: [Point(20, 20), Point(30, 30)]))
    try document.setPresentation(PresentationSettings(mode: .lens, zoom: 3))
    document.select([])
    document.undo()

    #expect(!document.isEditing)
    #expect(document.elements == restored)
    #expect(document.selection == [completed.id])
    #expect(document.presentation.mode == .off)
    #expect(document.generation == generation)
    #expect(document.historyBytes == historyBytes)
    #expect(document.canUndo)
    #expect(document.canRedo)
    document.redo()
    #expect(document.elements.map(\.id) == [completed.id, future.id])
    document.undo()
    document.undo()
    #expect(document.elements.isEmpty)
}
