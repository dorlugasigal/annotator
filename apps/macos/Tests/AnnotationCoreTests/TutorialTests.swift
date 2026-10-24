import Foundation
import Testing
@testable import AnnotationCore

// Windows ports these tests in apps/windows/tests/Annotator.Core.Tests/Tutorial/TutorialTests.cs.

private func look(_ mode: TutorialMode = .useApps, presented: Bool = true, tool: Tool = .pen, color: RGBA = .coral,
                  curved: Bool = false, elements: [TutorialElement] = [], undo: UInt64 = 0,
                  editing: Bool = false) -> TutorialObservation {
    TutorialObservation(mode: mode, presented: presented, tool: tool, strokeColor: color, curved: curved,
                        elements: elements, undoSerial: undo, editing: editing)
}

private func drawing(_ tool: Tool, x: Double = 10, curved: Bool = false, points: Int = 2,
                     id: UUID = UUID()) -> TutorialElement {
    TutorialElement(id: id, tool: tool, curved: curved, pointCount: points,
                    frame: Rect(x: x, y: 10, width: 40, height: 30), rotation: 0)
}

/// Holds the progress so expectations can call its mutating methods.
private final class Run {
    var progress: TutorialProgress
    init(_ start: TutorialObservation) { progress = TutorialProgress(start) }
    var step: TutorialStep { progress.step }
    var currentGoal: TutorialGoal? { progress.currentGoal }
    func observe(_ observation: TutorialObservation) -> TutorialChange { progress.observe(observation) }
    @discardableResult func skip() -> TutorialChange { progress.skip() }
}

@Test func tutorialStepsAndGoalsKeepTheirOrderAndRawValues() {
    #expect(TutorialStep.all.map(\.rawValue) ==
        ["rectangle", "shape", "color", "arrow", "pen", "move", "undo", "useApps", "drawAgain", "hide"])
    #expect(TutorialStep.arrow.goals == [.arrowTool, .curvedStyle, .curvedArrowDrawn])
    #expect(TutorialStep.hide.goals == [.hidden, .shown])
    #expect(TutorialGoal.allCases.allSatisfy { !$0.rawValue.isEmpty })
    #expect(TutorialStep.all.enumerated().allSatisfy { $0.element.index == $0.offset })
}

@Test func aWholeTutorialAcceptsKeysOrClicksBecauseOnlyTheResultCounts() {
    // Start tutorial turns drawing on; the first step picks a tool.
    let run = Run(look(.draw))
    #expect(run.step == .rectangle)
    #expect(run.observe(look(.draw, tool: .rectangle)) == TutorialChange.step)
    #expect(run.step == .shape)

    let box = drawing(.rectangle)
    #expect(run.observe(look(.draw, tool: .rectangle, elements: [box], editing: true)) == TutorialChange.none)
    #expect(run.observe(look(.draw, tool: .rectangle, elements: [box])) == TutorialChange.step)
    #expect(run.step == .color)
    #expect(run.observe(look(.draw, tool: .rectangle, color: .blue, elements: [box])) == TutorialChange.step)
    #expect(run.step == .arrow)

    #expect(run.observe(look(.draw, tool: .arrow, elements: [box])) == TutorialChange.goal)
    #expect(run.currentGoal == .curvedStyle)
    #expect(run.progress.isGoalDone(0))
    #expect(!run.progress.isGoalDone(1))
    #expect(run.observe(look(.draw, tool: .arrow, curved: true, elements: [box])) == TutorialChange.goal)
    let straight = drawing(.arrow, curved: true, points: 2)
    #expect(run.observe(look(.draw, tool: .arrow, curved: true, elements: [box, straight])) == TutorialChange.none)
    let bent = drawing(.arrow, curved: true, points: 3)
    #expect(run.observe(look(.draw, tool: .arrow, curved: true, elements: [box, straight, bent])) ==
        TutorialChange.step)
    #expect(run.step == .pen)

    var drawn = [box, straight, bent]
    #expect(run.observe(look(.draw, tool: .pen, elements: drawn)) == TutorialChange.goal)
    drawn.append(drawing(.pen))
    #expect(run.observe(look(.draw, tool: .pen, elements: drawn)) == TutorialChange.step)
    #expect(run.step == .move)

    #expect(run.observe(look(.draw, tool: .select, elements: drawn)) == TutorialChange.goal)
    var moved = drawn
    moved[0].frame = box.frame.translated(by: Point(30, 5))
    #expect(run.observe(look(.draw, tool: .select, elements: moved, editing: true)) == TutorialChange.none)
    #expect(run.observe(look(.draw, tool: .select, elements: moved)) == TutorialChange.step)
    #expect(run.step == .undo)

    #expect(run.observe(look(.draw, tool: .select, elements: drawn, undo: 1)) == TutorialChange.step)
    #expect(run.step == .useApps)
    #expect(run.observe(look(.useApps, tool: .select, elements: drawn, undo: 1)) == TutorialChange.step)
    #expect(run.step == .drawAgain)
    #expect(run.observe(look(.draw, tool: .select, elements: drawn, undo: 1)) == TutorialChange.step)
    #expect(run.step == .hide)
    #expect(run.observe(look(.useApps, presented: false, elements: drawn, undo: 1)) == TutorialChange.goal)
    #expect(run.observe(look(.draw, elements: drawn, undo: 1)) == TutorialChange.finished)
    #expect(run.progress.isFinished)
    #expect(run.currentGoal == nil)
    #expect(run.progress.completed == TutorialStep.all)
    #expect(run.progress.skipped.isEmpty)
}

@Test func aStateThatAlreadyHoldsCountsAtOnceButSomethingNewMustComeLater() {
    // The Rectangle already chosen finishes the first step at once; the drawing the next step waits for must be new.
    let old = drawing(.rectangle)
    let run = Run(look(.draw, tool: .rectangle, elements: [old]))
    #expect(run.step == .shape)
    #expect(run.progress.completed == [TutorialStep.rectangle])
    #expect(run.observe(look(.draw, tool: .rectangle, elements: [old])) == TutorialChange.none)
    // Drawing after Use apps is a step of its own later.
    #expect(TutorialStep.drawAgain.goals == [.drawing])
}

@Test func anotherToolsColorIsNotAColorChange() {
    let run = Run(look(.draw, tool: .rectangle))
    #expect(run.step == .shape)
    run.skip()
    #expect(run.step == .color)
    #expect(run.observe(look(.draw, tool: .arrow, color: .yellow)) == TutorialChange.none)
    #expect(run.observe(look(.draw, tool: .arrow, color: .yellow)) == TutorialChange.none)
    #expect(run.observe(look(.draw, tool: .arrow, color: .blue)) == TutorialChange.step)
}

@Test func smartDrawShapesCountForThePenAndMovesNeedSelect() {
    let run = Run(look(.draw, tool: .rectangle))
    while run.step != .pen { run.skip() }
    #expect(run.observe(look(.draw, tool: .pen)) == TutorialChange.goal)
    #expect(run.currentGoal == .penDrawn)
    let recognized = drawing(.ellipse)
    #expect(run.observe(look(.draw, tool: .pen, elements: [recognized])) == TutorialChange.step)
    #expect(run.step == .move)
    var erased = recognized
    erased.frame = Rect(x: 0, y: 0, width: 5, height: 5)
    #expect(run.observe(look(.draw, tool: .eraser, elements: [erased])) == TutorialChange.none)
    #expect(run.observe(look(.draw, tool: .select, elements: [erased])) == TutorialChange.goal)
    #expect(run.currentGoal == .moved)
    var turned = erased
    turned.rotation = 0.4
    #expect(run.observe(look(.draw, tool: .select, elements: [turned])) == TutorialChange.step)
}

@Test func hiddenToolsAreNotDrawingAndSkippingTheLastStepFinishes() {
    let run = Run(look(.draw, presented: false))
    #expect(run.step == .rectangle)
    while run.step != .hide {
        #expect(run.skip() == TutorialChange.step)
    }
    // Drawings and tools are hidden already, so the step's first goal counts at once.
    #expect(run.currentGoal == .shown)
    #expect(run.observe(look(presented: false)) == TutorialChange.none)
    #expect(run.skip() == TutorialChange.finished)
    #expect(run.progress.isFinished)
    #expect(run.observe(look()) == TutorialChange.finished)
    #expect(run.progress.skipped == TutorialStep.all)
}

@Test func theKeyboardLightsShortcutsWithModifiersAndEscape() {
    #expect(TutorialKeyboard.ids(["Ctrl", "Shift", "2"], mac: false) == ["ctrl", "shift", "2"])
    #expect(TutorialKeyboard.ids(["\u{2303}\u{21E7}2"], mac: true) == ["control", "shift", "2"])
    #expect(TutorialKeyboard.ids(["\u{2318}Z"], mac: true) == ["command", "z"])
    #expect(TutorialKeyboard.shows(["Ctrl", "2"], mac: false))
    #expect(TutorialKeyboard.shows(["Esc"], mac: false))
    #expect(TutorialKeyboard.shows(["esc"], mac: true))
    #expect(!TutorialKeyboard.shows(["7"], mac: false))
    #expect(!TutorialKeyboard.shows(["Enter"], mac: false))
    #expect(!TutorialKeyboard.shows(["Ctrl", "Alt", "9"], mac: false))
    #expect(TutorialKeyboard.ids(["Ctrl", "9"], mac: false) == nil)
    for mac in [false, true] {
        let keys = TutorialKeyboard.keys(mac: mac)
        #expect(Set(keys.map(\.id)).count == keys.count)
        #expect(keys.allSatisfy { (0...TutorialKeyboard.height).contains(TutorialKeyboard.frame($0).y) })
    }
}

@Test func tutorialElementsReadTheDrawingsRouteAndPlace() {
    var style = Style()
    style.curved = true
    var element = Element(tool: .arrow, points: [Point(0, 0), Point(20, 10), Point(40, 0)], style: style)
    element.rotation = 0.3
    let seen = TutorialElement(element)
    #expect(seen.id == element.id)
    #expect(seen.tool == .arrow)
    #expect(seen.curved)
    #expect(seen.pointCount == 3)
    #expect(seen.frame == element.frame)
    #expect(seen.rotation == 0.3)
    var nudged = seen
    nudged.frame = seen.frame.translated(by: Point(1, 0))
    #expect(seen.samePlace(seen))
    #expect(!seen.samePlace(nudged))
}
