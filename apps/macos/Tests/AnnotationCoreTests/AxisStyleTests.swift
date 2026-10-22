import Foundation
import Testing
@testable import AnnotationCore

@Test func axisStyleValidatesCountAndReadsLegacyFiles() throws {
    let legacy = try JSONDecoder().decode(Style.self, from: JSONEncoder().encode(Style()))
    #expect(legacy.axisTickCount == nil)
    for count in [0, 1, 9, 24] {
        var style = legacy
        style.axisTickCount = count
        style.negativeAxes = true
        try style.validate()
        #expect(try JSONDecoder().decode(Style.self, from: JSONEncoder().encode(style)) == style)
    }
    for count in [-1, 25, Int.max] {
        var style = legacy
        style.axisTickCount = count
        #expect(throws: CanvasError.invalidStyle) { try style.validate() }
    }
}

@Test func axisSettingsStayPerObjectAndSurviveSaveAndUndo() throws {
    let first = Element(tool: .axes, frame: Rect(x: 20, y: 30, width: 200, height: 100))
    let second = Element(tool: .axes, frame: Rect(x: 250, y: 30, width: 200, height: 100))
    var document = try CanvasDocument(elements: [first, second])
    document.select([first.id])
    document.begin()
    try document.editSelection { $0.style.axisTickCount = 6; $0.style.negativeAxes = true }
    try document.commit()
    #expect(document.elements[0].style.axisTickCount == 6)
    #expect(document.elements[0].style.negativeAxes == true)
    #expect(document.elements[1] == second)
    let saved = try DrawingFile.read(DrawingFile(document: document).data())
    #expect(saved.elements == document.elements)
    document.undo()
    #expect(document.elements == [first, second])
    document.redo()
    #expect(document.elements == saved.elements)
}
