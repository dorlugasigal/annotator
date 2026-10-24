import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import MotionCore
@testable import AnnotatorApp

@MainActor
final class TutorialCoachTests: XCTestCase {
    private var suite = ""
    private var defaults: UserDefaults!
    private var model: WorkspaceModel!
    private var tutorial: TutorialController!
    private var toolbar: NSPanel?
    private var toolsShown = true

    override func setUp() async throws {
        suite = "tutorial-coach-\(UUID())"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        model = WorkspaceModel(defaults: defaults, zoomInputPermissions: ZoomInputPermissions(
            isGranted: { false }, request: { XCTFail("The tutorial must not request Accessibility."); return false }))
        tutorial = TutorialController(model: model, startDrawing: { [weak self] in self?.draw() })
        tutorial.reducesMotion = { true }
    }

    override func tearDown() async throws {
        tutorial.shutdown()
        tutorial = nil
        toolbar?.orderOut(nil)
        toolbar?.contentView = nil
        toolbar?.close()
        toolbar = nil
        model.closeAll()
        model = nil
        defaults.removePersistentDomain(forName: suite)
    }

    func testRealActionsMoveTheCoachForwardAndLeaveTheDrawingsAlone() async throws {
        let preferences = model.preferences
        try await startCoach()
        XCTAssertEqual(model.preferences, preferences, "Starting the tutorial changes no setting.")
        XCTAssertEqual(model.mode, .draw, "Start tutorial turns drawing on.")
        try await waitFor(.rectangle)
        XCTAssertTrue(try XCTUnwrap(tutorial.window).isVisible, "The coach stays while drawing.")
        XCTAssertFalse(try XCTUnwrap(tutorial.window).isKeyWindow)
        XCTAssertTrue(model.introductionVisible)
        model.choose(.rectangle)
        try await waitFor(.shape)
        let box = Element(tool: .rectangle, frame: Rect(x: 120, y: 120, width: 160, height: 90))
        model.perform { try $0.add(box) }
        try await waitFor(.color)
        model.applyStyle { $0.color = .blue }
        try await waitFor(.arrow)
        tutorial.skipStep()
        try await waitFor(.pen)
        tutorial.skipStep()
        try await waitFor(.move)
        model.choose(.select)
        try await waitUntil { self.tutorial.shownGoalsDone == 1 }
        model.perform { try $0.update(box.id) { $0.translate(by: Point(40, 10)) } }
        try await waitFor(.undo)
        model.undo()
        XCTAssertEqual(model.undoSerial, 1)
        try await waitFor(.useApps)
        model.interact()
        try await waitFor(.drawAgain)
        draw()
        try await waitFor(.hide)
        model.concealWorkspaceSurfaces()
        try await waitUntil { self.tutorial.shownGoalsDone == 1 }
        XCTAssertTrue(try XCTUnwrap(tutorial.window).isVisible, "The card stays while the drawings are hidden.")
        model.revealWorkspaceSurfaces()
        try await waitUntil { self.tutorial.surface == .finish }
        XCTAssertTrue(tutorial.preferences.completed)
        XCTAssertEqual(tutorial.progress?.completed,
                       [TutorialStep.rectangle, .shape, .color, .move, .undo, .useApps, .drawAgain, .hide])
        XCTAssertEqual(tutorial.progress?.skipped, [TutorialStep.arrow, .pen])
        tutorial.done()
        try await waitUntil { !self.tutorial.isVisible }
        XCTAssertFalse(model.introductionVisible)
        XCTAssertTrue(model.desktopSessions.contains { $0.document.elements.contains { $0.id == box.id } },
                      "The tutorial never deletes practice drawings.")
    }

    func testEndingKeepsTheTutorialUnfinished() async throws {
        try await startCoach()
        tutorial.skipStep()
        tutorial.end()
        try await waitUntil { !self.tutorial.isVisible }
        XCTAssertFalse(tutorial.preferences.completed)
        XCTAssertFalse(tutorial.preferences.suppressed)
        XCTAssertNil(tutorial.progress)
        XCTAssertFalse(model.introductionVisible)
    }

    func testReduceMotionSkipsTheGhostButStillPressesTheKeys() async throws {
        tutorial.demoDelay = 0
        try await startCoach()
        tutorial.skipStep()
        XCTAssertEqual(tutorial.shownGoal, .shapeDrawn)
        tutorial.showMe()
        try await waitUntil { self.tutorial.demonstratedGoals.last == .shapeDrawn }
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(tutorial.overlay.ghostRuns, 0)
        XCTAssertFalse(tutorial.overlay.isPlayingDemo)
        let presses = tutorial.keyPressToken
        tutorial.skipStep()
        XCTAssertEqual(tutorial.shownGoal, .colorChanged)
        try await waitUntil { self.tutorial.keyPressToken > presses }
        XCTAssertEqual(tutorial.overlay.ghostRuns, 0)
        model.applyStyle { $0.color = .yellow }
        try await waitUntil { self.tutorial.progress?.completed.contains(.color) == true }
        for _ in 0..<20 where tutorial.surface == .coach { tutorial.skipStep() }
        XCTAssertEqual(tutorial.surface, .finish)
        XCTAssertEqual(tutorial.overlay.celebrations, 0, "Reduce Motion leaves out the confetti.")
    }

    func testShowMePlaysTheGhostOnceAndANewGoalStopsIt() async throws {
        tutorial.reducesMotion = { false }
        tutorial.demoDelay = 60
        try await startCoach()
        tutorial.skipStep()
        XCTAssertEqual(tutorial.shownGoal, .shapeDrawn)
        XCTAssertEqual(tutorial.overlay.ghostRuns, 0, "The ghost waits for its delay.")
        let tool = model.tool
        tutorial.showMe()
        try await waitUntil { self.tutorial.overlay.isPlayingDemo }
        XCTAssertEqual(tutorial.overlay.ghostRuns, 1)
        XCTAssertFalse(tutorial.overlay.windows.isEmpty)
        XCTAssertTrue(tutorial.overlay.windows.allSatisfy { $0.ignoresMouseEvents && !$0.canBecomeKey },
                      "The ghost never takes a click or the keyboard.")
        XCTAssertEqual(model.tool, tool, "The ghost's click is only a picture.")
        tutorial.skipStep()
        try await waitUntil { !self.tutorial.overlay.isPlayingDemo }
        try await Task.sleep(for: .milliseconds(400))
        XCTAssertEqual(tutorial.overlay.ghostRuns, 1, "Nothing loops.")
        XCTAssertEqual(model.mode, .draw)
        XCTAssertTrue(model.desktopSessions.allSatisfy { $0.document.elements.isEmpty }, "The ghost draws nothing.")
    }

    func testFinishingCelebratesOnceAndOpensShortcutSettings() async throws {
        tutorial.reducesMotion = { false }
        tutorial.demoDelay = 60
        try await startCoach()
        for _ in 0..<20 where tutorial.surface == .coach { tutorial.skipStep() }
        XCTAssertEqual(tutorial.surface, .finish)
        XCTAssertTrue(tutorial.preferences.completed)
        XCTAssertEqual(tutorial.progress?.completed, [TutorialStep.drawAgain], "Drawing is on, so Draw again counts at once.")
        XCTAssertEqual(tutorial.overlay.celebrations, 1, "Only the finished tutorial pops confetti.")
        XCTAssertTrue(tutorial.overlay.isCelebrating)
        tutorial.openSettings()
        XCTAssertEqual(model.settingsTab, .toolbar)
        tutorial.done()
        try await waitUntil { !self.tutorial.isVisible }
        try await waitUntil { !self.tutorial.overlay.isCelebrating }
        XCTAssertTrue(tutorial.overlay.windows.allSatisfy { !$0.isVisible }, "Nothing waits on screen after the burst.")
    }

    func testFinishedStepsDoNotPopConfetti() async throws {
        tutorial.reducesMotion = { false }
        tutorial.demoDelay = 60
        try await startCoach()
        model.choose(.rectangle)
        try await waitFor(.shape)
        model.perform { try $0.add(Element(tool: .rectangle, frame: Rect(x: 120, y: 120, width: 160, height: 90))) }
        try await waitFor(.color)
        XCTAssertEqual(tutorial.progress?.completed, [TutorialStep.rectangle, .shape])
        XCTAssertEqual(tutorial.overlay.celebrations, 0, "Only the finished tutorial celebrates.")
        XCTAssertFalse(tutorial.overlay.isCelebrating)
    }

    func testTheColorStepAsksForGreenWhenTheDrawingIsBlueAlready() async throws {
        let preferences = model.preferences
        XCTAssertEqual(TutorialText.step(.color, preferences: preferences, color: .coral).title, "Make it blue")
        XCTAssertEqual(TutorialText.keys(for: .colorChanged, preferences: preferences, color: .coral), ["B"])
        let blue = TutorialText.step(.color, preferences: preferences, color: .blue)
        XCTAssertEqual(blue.title, "Make it green")
        XCTAssertEqual(blue.detail, "Press {G}, or pick any color in the toolbar.")
        XCTAssertEqual(TutorialText.keys(for: .colorChanged, preferences: preferences, color: .blue), ["G"])
        XCTAssertEqual(TutorialText.step(.color, preferences: preferences).title, "Make it blue")

        model.applyStyle { $0.color = .blue }
        try await startCoach()
        tutorial.skipStep()
        tutorial.skipStep()
        XCTAssertEqual(tutorial.shownStep, .color)
        XCTAssertEqual(tutorial.shownColor, .blue)
        XCTAssertEqual(tutorial.lastAnnouncement,
                       "Step 3 of 10. Make it green. Press G, or pick any color in the toolbar.")
        model.applyStyle { $0.color = RGBA(0.28, 0.69, 0.4) }
        try await waitUntil { self.tutorial.shownStepDone }
        XCTAssertEqual(tutorial.shownColor, .blue, "The words stay put while the color changes.")
    }

    func testHintAppearsAfterAQuietSpellAndPointsAtShowMe() async throws {
        tutorial.hintDelay = 0.2
        try await startCoach()
        XCTAssertFalse(tutorial.hintVisible)
        try await waitUntil { self.tutorial.hintVisible }
        XCTAssertEqual(tutorial.showMePulseToken, 1)
        tutorial.skipStep()
        XCTAssertFalse(tutorial.hintVisible, "A new step starts without its hint.")
    }

    func testCoachFollowsAVisibleToolbarAndStaysWhileItHides() async throws {
        let area = try XCTUnwrap(NSScreen.main).visibleFrame
        let panel = NSPanel(contentRect: CGRect(x: area.midX - 300, y: area.maxY - 80, width: 600, height: 56),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.ignoresMouseEvents = true
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        toolbar = panel
        tutorial.shutdown()
        tutorial = TutorialController(model: model, presented: { [weak self] in self?.toolsShown ?? false },
                                      toolbarWindow: { [weak self] in self?.toolbar },
                                      startDrawing: { [weak self] in self?.draw() })
        tutorial.reducesMotion = { true }
        try await startCoach()
        try await Task.sleep(for: .milliseconds(150))
        var card = try cardFrame()
        XCTAssertEqual(card.maxX, panel.frame.maxX, accuracy: 1, "Right edges line up.")
        XCTAssertEqual(card.maxY, panel.frame.minY - TutorialPlacement.gap, accuracy: 1, "The card sits below the toolbar.")

        let placed = card
        toolsShown = false
        panel.orderOut(nil)
        panel.setFrameOrigin(CGPoint(x: area.minX + 40, y: area.maxY - 80))
        model.status = "Tools hidden"
        try await Task.sleep(for: .milliseconds(150))
        card = try cardFrame()
        XCTAssertEqual(card.maxX, placed.maxX, accuracy: 0.5, "A hidden toolbar doesn't move the card.")
        XCTAssertEqual(card.maxY, placed.maxY, accuracy: 0.5)

        toolsShown = true
        panel.orderFrontRegardless()
        panel.setFrameOrigin(CGPoint(x: area.minX + 400, y: area.maxY - 80))
        try await Task.sleep(for: .milliseconds(150))
        card = try cardFrame()
        XCTAssertEqual(card.maxX, panel.frame.maxX, accuracy: 1, "A visible toolbar that moves takes the card along.")
    }

    func testPlacementSitsNextToTheToolbarAndStaysOnTheDisplay() {
        let area = CGRect(x: 0, y: 0, width: 1440, height: 875)
        let size = CGSize(width: 340, height: 200)
        let top = CGRect(x: 400, y: 800, width: 640, height: 56)
        var card = TutorialPlacement.coach(size: size, toolbar: top, visibleFrame: area)
        XCTAssertEqual(card.maxX, top.maxX)
        XCTAssertEqual(card.maxY, top.minY - TutorialPlacement.gap)
        let bottom = CGRect(x: 400, y: 10, width: 640, height: 56)
        card = TutorialPlacement.coach(size: size, toolbar: bottom, visibleFrame: area)
        XCTAssertEqual(card.minY, bottom.maxY + TutorialPlacement.gap, "No room below: the card goes above.")
        let left = CGRect(x: 0, y: 300, width: 56, height: 500)
        card = TutorialPlacement.coach(size: size, toolbar: left, visibleFrame: area)
        XCTAssertEqual(card.minX, left.maxX + TutorialPlacement.gap)
        XCTAssertEqual(card.maxY, left.maxY, "Tops line up beside a vertical toolbar.")
        let right = CGRect(x: 1384, y: 300, width: 56, height: 500)
        card = TutorialPlacement.coach(size: size, toolbar: right, visibleFrame: area)
        XCTAssertEqual(card.maxX, right.minX - TutorialPlacement.gap, "No room on the right: the card goes left.")
        card = TutorialPlacement.coach(size: size, toolbar: nil, visibleFrame: area)
        XCTAssertEqual(card.midX, area.midX)
        XCTAssertEqual(card.minY, area.minY + TutorialPlacement.bottomInset)
        let corner = CGRect(x: 0, y: 815, width: 200, height: 50)
        card = TutorialPlacement.coach(size: size, toolbar: corner, visibleFrame: area)
        XCTAssertEqual(card.minX, area.minX, "The card stays in the work area.")
        let welcome = TutorialPlacement.welcome(size: size, visibleFrame: area)
        XCTAssertEqual(welcome.midX, area.midX)
        XCTAssertEqual(welcome.maxY, area.maxY - area.height * 0.28, accuracy: 0.001)
    }

    func testUndoCountsOnlyAnUndoThatChangesSomething() throws {
        draw()
        XCTAssertEqual(model.undoSerial, 0)
        model.undo()
        XCTAssertEqual(model.undoSerial, 0, "Nothing to undo.")
        model.perform { try $0.add(Element(tool: .rectangle, frame: Rect(x: 10, y: 10, width: 40, height: 30))) }
        model.undo()
        XCTAssertEqual(model.undoSerial, 1)
        XCTAssertTrue(try XCTUnwrap(model.active).document.elements.isEmpty)
        model.active?.document.begin()
        model.undo()
        XCTAssertEqual(model.undoSerial, 1, "Cancelling an edit in progress is not an Undo.")
    }

    func testAnchorsAreFoundByTheirAccessibilityNames() async throws {
        model.setInspectorPinned(false)
        model.choose(.arrow)
        let panel = NSPanel(contentRect: CGRect(x: 200, y: 200, width: 900, height: 140),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.ignoresMouseEvents = true
        panel.alphaValue = 0
        let host = NSHostingView(rootView: ToolbarView(model: model))
        panel.contentView = host
        panel.setContentSize(host.fittingSize)
        panel.orderFrontRegardless()
        toolbar = panel
        try await Task.sleep(for: .milliseconds(150))
        let pen = try XCTUnwrap(TutorialAnchors.frame(named: "Pen", in: panel))
        XCTAssertTrue(panel.frame.insetBy(dx: -1, dy: -1).contains(pen))
        XCTAssertNil(TutorialAnchors.frame(named: "No such control", in: panel))
        XCTAssertNil(TutorialAnchors.frame(named: "Curved arrow", in: panel), "Quick controls don't show Curved.")
        XCTAssertEqual(TutorialAnchors.anchor(named: "Curved arrow", in: panel),
                       TutorialAnchors.frame(named: "Properties", in: panel),
                       "Without Curved, the ring and the ghost point at the Properties button.")
        XCTAssertNotNil(TutorialAnchors.anchor(named: "Curved arrow", in: panel))
        panel.orderOut(nil)
        XCTAssertNil(TutorialAnchors.frame(named: "Pen", in: panel), "A hidden toolbar has no anchors.")
        XCTAssertEqual(TutorialAnchors.name(for: .curvedStyle), "Curved arrow")
        XCTAssertNil(TutorialAnchors.name(for: .shown))
    }

    func testGhostTimelineFollowsTheSharedMotion() throws {
        let display = CGRect(x: 0, y: 0, width: 1000, height: 800)
        let timeline = TutorialGhostTimeline(TutorialGhostRun(
            display: display, start: CGPoint(x: 100, y: 100), steps: [.click(CGPoint(x: 500, y: 100))]))
        let leave = TutorialMotion.ghostAppearDuration + TutorialMotion.ghostPause
        let travel = TutorialMotion.travelDuration(400)
        let arrive = leave + travel
        XCTAssertEqual(try XCTUnwrap(timeline.ripples.first).time, arrive, accuracy: 1e-9)
        XCTAssertEqual(timeline.position(at: arrive).x, 500, accuracy: 1e-6)
        XCTAssertLessThan(timeline.position(at: leave + travel / 2).y, 100,
                          "The arc bends to the right of travel as seen on screen.")
        XCTAssertEqual(timeline.scale(at: arrive + TutorialMotion.ghostPressDuration), TutorialMotion.ghostPressScale,
                       accuracy: 1e-9)
        XCTAssertEqual(timeline.actionsEnd,
                       arrive + TutorialMotion.ghostPressDuration + TutorialMotion.ghostReleaseDuration, accuracy: 1e-9)
        XCTAssertEqual(timeline.opacity(at: 0), 0)
        XCTAssertEqual(timeline.opacity(at: timeline.duration), 0)

        let arrow = TutorialGhostTimeline(TutorialGhostRun(
            display: display, start: .zero,
            steps: [.clicks([CGPoint(x: 300, y: 300), CGPoint(x: 400, y: 420), CGPoint(x: 520, y: 360)])]))
        XCTAssertEqual(arrow.ripples.count, 3)
        let ink = try XCTUnwrap(arrow.inks.first)
        XCTAssertEqual(try XCTUnwrap(ink.reveal.last).value, 1, accuracy: 1e-6)
        XCTAssertEqual(ink.line.count, 25)
        let drawn = TutorialGhostTimeline.value(ink.reveal, at: arrow.ripples[1].time)
        XCTAssertGreaterThan(drawn, 0.2, "The arrow grows along with the ghost.")
        XCTAssertLessThan(drawn, 0.8)
        XCTAssertGreaterThan(arrow.duration, arrow.actionsEnd)
        XCTAssertNil(arrow.keyCue)

        let doubled = TutorialGhostTimeline(TutorialGhostRun(
            display: display, start: .zero,
            steps: [.clicks([CGPoint(x: 300, y: 300), CGPoint(x: 400, y: 420), CGPoint(x: 520, y: 360)], double: true)]))
        XCTAssertNil(doubled.keyCue, "A double-click finishes the arrow; no key plays.")
        XCTAssertEqual(doubled.ripples.count, 4, "The last point is clicked twice.")
        XCTAssertEqual(doubled.ripples[3].time - doubled.ripples[2].time,
                       TutorialMotion.ghostPressDuration + TutorialMotion.ghostReleaseDuration + TutorialMotion.doubleClickGap,
                       accuracy: 1e-9)
        XCTAssertEqual(doubled.inks.count, 2, "The double-click adds the arrowhead.")
        let tip = try XCTUnwrap(doubled.inks.last)
        XCTAssertEqual(tip.start, doubled.ripples[3].time + TutorialMotion.ghostPressDuration, accuracy: 1e-9)
        XCTAssertEqual(doubled.inks[0].release, tip.start, accuracy: 1e-9)
        XCTAssertEqual(doubled.actionsEnd, tip.start + TutorialMotion.ghostReleaseDuration, accuracy: 1e-9)

        let finished = TutorialGhostTimeline(TutorialGhostRun(
            display: display, start: .zero,
            steps: [.clicks([CGPoint(x: 300, y: 300), CGPoint(x: 400, y: 420), CGPoint(x: 520, y: 360)])],
            finishKeys: 1))
        let cue = try XCTUnwrap(finished.keyCue)
        XCTAssertEqual(cue, finished.actionsEnd + TutorialMotion.ghostPause, accuracy: 1e-9)
        XCTAssertEqual(finished.inks.count, 2, "Return adds the arrowhead.")
        let head = try XCTUnwrap(finished.inks.last)
        XCTAssertEqual(head.start, cue + TutorialMotion.keyDownDuration, accuracy: 1e-9)
        XCTAssertEqual(head.line.count, 3)
        XCTAssertEqual(head.line[1].x, 520, accuracy: 1e-6)
        XCTAssertEqual(head.line[1].y, 360, accuracy: 1e-6)
        XCTAssertEqual(finished.inks[0].release, head.start, accuracy: 1e-9, "The arrow and its head fade together.")
        XCTAssertGreaterThan(finished.duration, head.end - 1e-9)
    }

    // MARK: Helpers

    /// Opens the welcome card the way a launch does, without taking key focus, and starts the tutorial.
    private func startCoach() async throws {
        tutorial.showAutomatically(isLoginItem: false)
        XCTAssertEqual(tutorial.surface, .welcome)
        tutorial.start()
        try await waitUntil { self.tutorial.surface == .coach }
    }

    /// Starts drawing with click-through canvases, so the test never covers the desktop's input.
    private func draw() {
        model.beginDrawing()
        for session in model.desktopSessions { session.panel.ignoresMouseEvents = true }
    }

    private func waitFor(_ step: TutorialStep) async throws {
        try await waitUntil { self.tutorial.progress?.step == step && self.tutorial.shownStep == step }
    }

    private func cardFrame() throws -> CGRect {
        try XCTUnwrap(tutorial.window).frame.insetBy(dx: TutorialLayout.margin, dy: TutorialLayout.margin)
    }

    private func waitUntil(timeout: Duration = .seconds(5), _ condition: () -> Bool) async throws {
        let deadline = ContinuousClock.now + timeout
        while !condition() {
            guard ContinuousClock.now < deadline else { return XCTFail("Timed out waiting for the tutorial.") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }
}
