import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class IntroductionMotionTests: XCTestCase {
    private var suite = ""
    private var defaults: UserDefaults!
    private var model: WorkspaceModel!
    private var introduction: IntroductionController!

    override func setUp() async throws {
        suite = "introduction-motion-\(UUID())"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        model = WorkspaceModel(defaults: defaults, zoomInputPermissions: ZoomInputPermissions(
            isGranted: { false }, request: { XCTFail("The tour must not request Accessibility."); return false }))
        introduction = IntroductionController(model: model)
    }

    override func tearDown() async throws {
        introduction.close()
        introduction = nil
        model.closeAll()
        model = nil
        defaults.removePersistentDomain(forName: suite)
    }

    func testReduceMotionShowsEveryIllustrationInItsFinalStillState() async throws {
        XCTAssertEqual(IntroductionStory.initialProgress(reduceMotion: true, presented: true, review: nil), 1)
        XCTAssertEqual(IntroductionStory.initialProgress(reduceMotion: false, presented: true, review: nil), 0)
        XCTAssertEqual(IntroductionStory.initialProgress(reduceMotion: false, presented: false, review: nil), 1)
        introduction.navigator.reducesMotion = { true }
        introduction.showManually()
        let host = try XCTUnwrap(introduction.window?.contentView)
        for page in IntroductionPage.allCases {
            introduction.navigator.select(page)
            try await Task.sleep(for: .milliseconds(350))
            let settled = try pixels(of: host)
            try await Task.sleep(for: .milliseconds(700))
            XCTAssertEqual(try pixels(of: host), settled, "\(page) must stay still with Reduce Motion on.")
            XCTAssertEqual(introduction.navigator.page, page)
        }
        let playbacks = introduction.navigator.playbacks
        XCTAssertEqual(Set(playbacks.map(\.page)), Set(IntroductionPage.allCases))
        XCTAssertTrue(playbacks.allSatisfy { !$0.animated }, "Reduce Motion must skip every story animation.")
        introduction.navigator.select(.draw)
        try await Task.sleep(for: .milliseconds(350))
        let illustration = try XCTUnwrap(node("introduction-illustration", in: host))
        XCTAssertFalse(customActions(of: illustration).contains("Replay animation"),
                       "A still illustration has nothing to replay.")
    }

    func testIllustrationPlaysOnceAndNeverAdvancesThePage() async throws {
        introduction.navigator.reducesMotion = { false }
        introduction.navigator.storyTimeScale = 0.25
        introduction.showManually()
        let navigator = introduction.navigator
        let host = try XCTUnwrap(introduction.window?.contentView)
        try await Task.sleep(for: .milliseconds(260))
        let early = try pixels(of: host)
        try await Task.sleep(for: .milliseconds(300))
        XCTAssertNotEqual(try pixels(of: host), early, "The first illustration should play when the tour opens.")
        XCTAssertEqual(navigator.playbacks.last, IntroductionPlayback(page: .draw, animated: true))
        try await waitUntil { navigator.finishedPlaybacks.contains(.draw) }
        XCTAssertEqual(navigator.page, .draw, "Finishing an illustration must not change the page.")
        let finished = try pixels(of: host)
        try await Task.sleep(for: .milliseconds(600))
        XCTAssertEqual(try pixels(of: host), finished, "Illustrations must not loop after they finish.")
        XCTAssertEqual(navigator.page, .draw)

        NativeAccessibilityNode.enable()
        let illustration = try XCTUnwrap(node("introduction-illustration", in: host))
        XCTAssertEqual(illustration.accessibilityRole(), .image)
        XCTAssertTrue(illustration.accessibilityLabel()?.contains("A pen circles a bar on a chart") == true)
        let playbacks = navigator.playbacks.count
        XCTAssertTrue(performCustomAction("Replay animation", on: illustration))
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(navigator.playbacks.count, playbacks + 1)
        let finishedCount = navigator.finishedPlaybacks.count
        try await waitUntil { navigator.finishedPlaybacks.count > finishedCount }
        XCTAssertEqual(navigator.page, .draw, "Replaying must not change the page.")
        XCTAssertEqual(model.mode, .off)
    }

    func testArrowKeysAndButtonsMoveOnePageInTheirDirection() async throws {
        introduction.navigator.reducesMotion = { false }
        introduction.showManually()
        let panel = try XCTUnwrap(introduction.window as? IntroductionPanel)
        let navigator = introduction.navigator
        try press(123, in: panel)
        XCTAssertEqual(navigator.page, .draw, "Left on the first page stays put.")
        try press(124, in: panel)
        XCTAssertEqual(navigator.page, .color)
        XCTAssertEqual(navigator.direction, .forward)
        try press(124, in: panel)
        try press(124, in: panel)
        XCTAssertEqual(navigator.page, .setup)
        try press(124, in: panel)
        XCTAssertEqual(navigator.page, .setup, "Right on the last page stays put.")
        try press(123, in: panel)
        XCTAssertEqual(navigator.page, .focus)
        XCTAssertEqual(navigator.direction, .backward)
        try press(124, in: panel, modifiers: .command)
        try press(123, in: panel, modifiers: .option)
        XCTAssertEqual(navigator.page, .focus, "Arrow keys with modifiers are left to the system.")

        NativeAccessibilityNode.enable()
        let host = try XCTUnwrap(panel.contentView)
        try await Task.sleep(for: .milliseconds(120))
        XCTAssertTrue(try button("Next", in: host).accessibilityPerformPress())
        XCTAssertEqual(navigator.page, .setup)
        XCTAssertEqual(navigator.direction, .forward)
        try await Task.sleep(for: .milliseconds(120))
        XCTAssertTrue(try button("Back", in: host).accessibilityPerformPress())
        XCTAssertEqual(navigator.page, .focus)
        XCTAssertEqual(navigator.direction, .backward)
        try await Task.sleep(for: .milliseconds(120))
        let first = try button("Make a point", in: host)
        XCTAssertEqual(first.accessibilityValue() as? String, "Step 1 of 4")
        XCTAssertTrue(first.accessibilityPerformPress())
        XCTAssertEqual(navigator.page, .draw)
        XCTAssertEqual(navigator.direction, .backward)
        try await Task.sleep(for: .milliseconds(120))
        let last = try button("A little setup", in: host)
        XCTAssertEqual(last.accessibilityValue() as? String, "Step 4 of 4")
        XCTAssertTrue(last.accessibilityPerformPress())
        XCTAssertEqual(navigator.page, .setup)
        XCTAssertEqual(navigator.direction, .forward)
    }

    func testNavigationReplayAndReopeningNeverRequestPermissionsOrStartDrawing() async throws {
        let status = model.status
        introduction.navigator.storyTimeScale = 0.2
        introduction.showAutomatically(isLoginItem: false)
        let panel = try XCTUnwrap(introduction.window as? IntroductionPanel)
        XCTAssertFalse(panel.isKeyWindow)
        NativeAccessibilityNode.enable()
        let host = try XCTUnwrap(panel.contentView)
        for _ in IntroductionPage.allCases {
            try await Task.sleep(for: .milliseconds(90))
            if let illustration = node("introduction-illustration", in: host) {
                _ = performCustomAction("Replay animation", on: illustration)
            }
            try press(124, in: panel)
        }
        for page in IntroductionPage.allCases.reversed() {
            try await Task.sleep(for: .milliseconds(90))
            XCTAssertTrue(try button(page.title, in: host).accessibilityPerformPress())
        }
        introduction.close()
        XCTAssertFalse(introduction.navigator.isPresented)
        let playbacks = introduction.navigator.playbacks.count
        introduction.showManually()
        XCTAssertTrue(introduction.navigator.isPresented)
        try await Task.sleep(for: .milliseconds(120))
        XCTAssertGreaterThan(introduction.navigator.playbacks.count, playbacks, "Reopening plays the page again.")
        XCTAssertEqual(model.status, status, "No permission request or status change comes from the tour.")
        XCTAssertEqual(model.mode, .off)
        XCTAssertTrue(model.desktopSessions.isEmpty)
        XCTAssertEqual(model.zoomStartState, .idle)
        XCTAssertFalse(introduction.preferences.suppressed)
    }

    func testFirstPageUsesConfiguredShortcutsAndStaysShortWhileAnimating() async throws {
        model.setActivationShortcut(ActivationShortcut(keyCode: 20, character: "3", control: true, option: true))
        model.setResumeShortcut(ActivationShortcut(keyCode: 21, character: "4", control: true, option: true))
        XCTAssertNil(model.errorMessage)
        introduction.navigator.reducesMotion = { false }
        introduction.showManually()
        NativeAccessibilityNode.enable()
        let host = try XCTUnwrap(introduction.window?.contentView)
        for delay in [150, 900] {
            try await Task.sleep(for: .milliseconds(delay))
            let text = accessibilityText(in: host)
            XCTAssertTrue(text.contains("Control + Option + 3"), "Show / hide uses the configured shortcut.")
            XCTAssertTrue(text.contains("Control + Option + 4"), "Draw uses the configured shortcut.")
            XCTAssertTrue(text.contains("Escape"))
            XCTAssertFalse(text.contains("Control + Shift + 2"))
            let words = Set(NativeAccessibilityNode.nodes(in: host).filter { $0.accessibilityRole() == .staticText }
                .compactMap { $0.accessibilityValue() as? String ?? $0.accessibilityLabel() })
                .joined(separator: " ").split(whereSeparator: \.isWhitespace).count
            XCTAssertLessThanOrEqual(words, 120)
        }
        XCTAssertEqual(introduction.navigator.page, .draw)
    }

    func testStepIndicatorCapsuleCoversOnlyTheActiveStepWhenSettled() {
        let rect = CGRect(x: 0, y: 0, width: 400, height: 4)
        let settled = IntroductionStepCapsule(lower: 2, upper: 2, count: 4, spacing: 8).path(in: rect).boundingRect
        XCTAssertEqual(settled.minX, 2 * (94 + 8), accuracy: 0.01)
        XCTAssertEqual(settled.width, 94, accuracy: 0.01)
        let stretched = IntroductionStepCapsule(lower: 1, upper: 2, count: 4, spacing: 8).path(in: rect).boundingRect
        XCTAssertEqual(stretched.width, 94 * 2 + 8, accuracy: 0.01)
        XCTAssertEqual(IntroductionStory.shortcutFocus(1).opacity, 0, accuracy: 0.001)
        XCTAssertEqual(IntroductionStory.shortcutFocus(0.2).position, 0, accuracy: 0.001)
        XCTAssertEqual(IntroductionStory.shortcutFocus(0.55).position, 1, accuracy: 0.001)
        XCTAssertEqual(IntroductionStory.shortcutFocus(0.85).position, 2, accuracy: 0.001)
        for index in 0..<2 {
            let arrival = IntroductionStory.cardArrival(index: index, progress: 1)
            XCTAssertEqual(arrival.opacity, 1, accuracy: 0.001)
            XCTAssertEqual(arrival.offset, 0, accuracy: 0.001)
            XCTAssertGreaterThan(IntroductionStory.cardArrival(index: index, progress: 0).opacity, 0,
                                 "Cards stay readable to VoiceOver before they settle.")
        }
    }

    /// Writes review frames to build/IntroReview. Run with TEST_RUNNER_ANNOTATOR_CAPTURE_INTRO_REVIEW=1.
    func testRenderIntroductionReviewFrames() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_CAPTURE_INTRO_REVIEW"] == "1",
                          "Introduction review frames are a separate visual check.")
        let directory = RepositoryRoot.url.appendingPathComponent("build/IntroReview")
        try? FileManager.default.removeItem(at: directory)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let navigator = introduction.navigator
        navigator.reducesMotion = { true }
        introduction.showManually()
        let panel = try XCTUnwrap(introduction.window)
        let host = try XCTUnwrap(panel.contentView)
        let frames: [IntroductionPage: [Double]] = [
            .draw: [0, 0.08, 0.25, 0.36, 0.55, 0.64, 0.78, 1],
            .color: [0, 0.18, 0.4, 0.62, 0.8, 1],
            .focus: [0, 0.14, 0.32, 0.5, 0.7, 1],
            .setup: [0, 0.25, 0.5, 1]
        ]
        for (appearance, name) in [(NSAppearance.Name.aqua, "light"), (.darkAqua, "dark")] {
            panel.appearance = NSAppearance(named: appearance)
            for page in IntroductionPage.allCases {
                navigator.reviewProgress = frames[page]?.first
                navigator.select(page)
                try await Task.sleep(for: .milliseconds(300))
                for progress in frames[page] ?? [] {
                    navigator.reviewProgress = progress
                    navigator.replay()
                    try await Task.sleep(for: .milliseconds(60))
                    try write(host, to: directory.appendingPathComponent(
                        "\(name)-\(page.rawValue + 1)-\(page)-\(String(format: "%03d", Int(progress * 100))).png"))
                }
            }
        }

        // First-run setup page, before either permission is allowed.
        let firstRun = IntroductionController(model: model)
        firstRun.navigator.reducesMotion = { true }
        firstRun.navigator.reviewProgress = 1
        firstRun.navigator.reviewPermissions = (false, false)
        firstRun.showManually()
        firstRun.navigator.select(.setup)
        let firstRunPanel = try XCTUnwrap(firstRun.window)
        for (appearance, name) in [(NSAppearance.Name.aqua, "light"), (.darkAqua, "dark")] {
            firstRunPanel.appearance = NSAppearance(named: appearance)
            try await Task.sleep(for: .milliseconds(300))
            try write(try XCTUnwrap(firstRunPanel.contentView),
                      to: directory.appendingPathComponent("\(name)-4-setup-not-allowed.png"))
        }
        firstRun.close()

        // Illustration detail at 2x, one contact sheet per page and appearance.
        for dark in [false, true] {
            for page in IntroductionPage.allCases where page.illustrationDescription != nil {
                let images = try (frames[page] ?? []).map { progress -> CGImage in
                    let renderer = ImageRenderer(content: IntroductionIllustration(page: page, progress: progress)
                        .frame(width: 472, height: 120)
                        .environment(\.colorScheme, dark ? .dark : .light))
                    renderer.scale = 2
                    return try XCTUnwrap(renderer.cgImage)
                }
                try writeSheet(images, background: dark ? 0.12 : 0.97,
                               to: directory.appendingPathComponent("sheet-\(dark ? "dark" : "light")-\(page)@2x.png"))
            }
        }

        // Real playback and page motion, sampled on the wall clock.
        panel.appearance = NSAppearance(named: .darkAqua)
        navigator.reviewProgress = nil
        navigator.reducesMotion = { false }
        navigator.select(.draw)
        try await Task.sleep(for: .milliseconds(700))
        navigator.replay()
        let started = ContinuousClock.now
        for milliseconds in [700, 1500, 2400, 3300, 4300, 5600] {
            try await Task.sleep(until: started + .milliseconds(milliseconds), clock: .continuous)
            try write(host, to: directory.appendingPathComponent("live-draw-\(milliseconds)ms.png"))
        }
        for (target, label) in [(IntroductionPage.color, "forward"), (.draw, "back")] {
            navigator.select(target)
            let moved = ContinuousClock.now
            for milliseconds in [70, 160, 320] {
                try await Task.sleep(until: moved + .milliseconds(milliseconds), clock: .continuous)
                try write(host, to: directory.appendingPathComponent("page-\(label)-\(milliseconds)ms.png"))
            }
            try await Task.sleep(for: .milliseconds(500))
        }
    }

    // MARK: Helpers

    private func press(_ keyCode: UInt16, in panel: NSPanel, modifiers: NSEvent.ModifierFlags = []) throws {
        let scalar = keyCode == 123 ? "\u{F702}" : "\u{F703}"
        let event = try XCTUnwrap(NSEvent.keyEvent(
            with: .keyDown, location: .zero, modifierFlags: modifiers.union([.function, .numericPad]),
            timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber, context: nil,
            characters: scalar, charactersIgnoringModifiers: scalar, isARepeat: false, keyCode: keyCode))
        panel.sendEvent(event)
    }

    private func button(_ label: String, in host: NSView) throws -> NativeAccessibilityNode {
        try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .button && $0.accessibilityLabel() == label
        }, "Missing button \(label)")
    }

    private func node(_ identifier: String, in host: NSView) -> NativeAccessibilityNode? {
        NativeAccessibilityNode.enable()
        return NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == identifier }
    }

    private func customActions(of node: NativeAccessibilityNode) -> [String] {
        guard node.object.responds(to: NSSelectorFromString("accessibilityCustomActions")) else { return [] }
        return (node.object.value(forKey: "accessibilityCustomActions") as? [NSAccessibilityCustomAction] ?? []).map(\.name)
    }

    private func performCustomAction(_ name: String, on node: NativeAccessibilityNode) -> Bool {
        guard node.object.responds(to: NSSelectorFromString("accessibilityCustomActions")),
              let action = (node.object.value(forKey: "accessibilityCustomActions") as? [NSAccessibilityCustomAction])?
                .first(where: { $0.name == name }) else { return false }
        if let handler = action.handler { return handler() }
        guard let target = action.target as? NSObject, let selector = action.selector else { return false }
        _ = target.perform(selector, with: action)
        return true
    }

    private func accessibilityText(in host: NSView) -> String {
        NativeAccessibilityNode.nodes(in: host).flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }.joined(separator: "\n")
    }

    private func waitUntil(timeout: Duration = .seconds(5), _ condition: () -> Bool) async throws {
        let deadline = ContinuousClock.now + timeout
        while !condition() {
            guard ContinuousClock.now < deadline else { return XCTFail("Timed out waiting for the story to finish.") }
            try await Task.sleep(for: .milliseconds(40))
        }
    }

    private func bitmap(of view: NSView) throws -> NSBitmapImageRep {
        view.layoutSubtreeIfNeeded()
        let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
        view.cacheDisplay(in: view.bounds, to: bitmap)
        return bitmap
    }

    /// The step indicator, illustration and first lines of the page, without the scroll bar area.
    private func pixels(of view: NSView) throws -> Data {
        let bitmap = try bitmap(of: view)
        let scale = CGFloat(bitmap.pixelsWide) / view.bounds.width
        let image = try XCTUnwrap(bitmap.cgImage?.cropping(to: CGRect(
            x: 0, y: 0, width: (view.bounds.width - 24) * scale, height: 240 * scale)))
        return try XCTUnwrap(NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]))
    }

    private func writeSheet(_ images: [CGImage], background: CGFloat, to url: URL) throws {
        let gap = 16, width = images.map(\.width).max() ?? 0
        let height = images.map(\.height).reduce(0, +) + gap * (images.count + 1)
        let context = try XCTUnwrap(CGContext(
            data: nil, width: width + gap * 2, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(gray: background, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: width + gap * 2, height: height))
        var top = gap
        for image in images {
            context.draw(image, in: CGRect(x: gap, y: height - top - image.height, width: image.width, height: image.height))
            top += image.height + gap
        }
        let sheet = try XCTUnwrap(context.makeImage())
        try XCTUnwrap(NSBitmapImageRep(cgImage: sheet).representation(using: .png, properties: [:])).write(to: url)
    }

    private func write(_ view: NSView, to url: URL) throws {
        try XCTUnwrap(try bitmap(of: view).representation(using: .png, properties: [:])).write(to: url)
    }
}
