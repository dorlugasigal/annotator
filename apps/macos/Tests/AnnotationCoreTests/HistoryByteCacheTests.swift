import Testing
@testable import AnnotationCore

@Test func cachedSnapshotBytesRemainExactThroughUndoRedoAndBranching() throws {
    let first = Element(tool: .text, frame: Rect(x: 0, y: 0, width: 80, height: 30), text: "First")
    let second = Element(tool: .text, frame: Rect(x: 0, y: 50, width: 80, height: 30), text: "Second")
    var document = try CanvasDocument()
    try document.add(first)
    #expect(document.historyBytes == 64)
    try document.add(second)
    #expect(document.historyBytes == 128 + first.estimatedBytes)
    document.undo()
    #expect(document.historyBytes == 128 + first.estimatedBytes + second.estimatedBytes)
    document.redo()
    #expect(document.historyBytes == 128 + first.estimatedBytes)
    try document.update(second.id) { $0.text = String(repeating: "A", count: 8_000) }
    let beforeUndo = document.historyBytes
    document.undo()
    #expect(document.historyBytes > beforeUndo)
    document.redo()
    #expect(document.historyBytes == beforeUndo)
    document.undo()
    try document.update(first.id) { $0.text = "Branch" }
    #expect(!document.canRedo)
    #expect(document.historyBytes <= document.historyByteLimit)
}
