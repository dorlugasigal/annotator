import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class WorkspaceVisibilityTests: XCTestCase {
    func testEachShapeHasItsOwnDelayAndReversalKeepsTheSameShuffle() async throws {
        let (model, defaults, suite) = try model()
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let shapes = (0..<3).map { Element(tool: .rectangle,
            frame: Rect(x: 100 + Double($0) * 150, y: 200, width: 90, height: 60)) }
        for shape in shapes { try session.document.add(shape) }
        let generation = session.document.generation
        var shuffles = 0
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false }, shuffled: {
            shuffles += 1
            return Array($0.reversed())
        })
        let toolbar = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 600, height: 120),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        defer {
            animator.cancel(); toolbar.orderOut(nil); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        model.setAnimationStyle(.dissolve)
        animator.hide(toolbar: toolbar, model: model) {}
        let hiding = animator.transitionObjectDelays
        XCTAssertEqual(hiding.count, 3)
        XCTAssertEqual(Set(hiding.values).count, 3)
        XCTAssertLessThan(try XCTUnwrap(hiding[shapes[2].id]), try XCTUnwrap(hiding[shapes[0].id]))
        XCTAssertEqual(animator.transientPanelCount, 4)
        animator.show(toolbar: toolbar, model: model) {}
        let showing = animator.transitionObjectDelays
        XCTAssertTrue(showing.values.allSatisfy { $0 == 0 }, "An interrupted motion must reverse immediately.")
        XCTAssertEqual(shuffles, 1)
        try await Task.sleep(for: .seconds(1.1))
        XCTAssertFalse(animator.hasActiveTimeline)
        XCTAssertEqual(session.document.elements, shapes)
        XCTAssertEqual(session.document.generation, generation)
        XCTAssertEqual(model.mode, .interact)
        animator.hide(toolbar: toolbar, model: model) {}
        try await Task.sleep(for: .seconds(1))
        animator.show(toolbar: toolbar, model: model) {}
        let freshShow = animator.transitionObjectDelays
        XCTAssertLessThan(try XCTUnwrap(freshShow[shapes[0].id]), try XCTUnwrap(freshShow[shapes[2].id]))
        XCTAssertEqual(shuffles, 2, "The completed hide and show share one shuffled order.")
    }

    func testAnimatingDoesNotDimTheToolbarBeforeItsFinalFrame() async throws {
        let (model, defaults, suite) = try model()
        let host = NSHostingView(rootView: ToolbarView(model: model).environment(\.colorScheme, .light))
        let panel = NSPanel(contentRect: CGRect(x: 80, y: 80, width: 1000, height: 200),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.contentView = host
        panel.setContentSize(host.fittingSize)
        panel.orderFrontRegardless()
        defer {
            model.visibilityAnimating = false
            panel.orderOut(nil); panel.contentView = nil; model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        func image() throws -> Data {
            host.layoutSubtreeIfNeeded()
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            return try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
        }
        try await Task.sleep(for: .milliseconds(80))
        let before = try image()
        model.visibilityAnimating = true
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(try image(), before, "Input blocking must not gray out controls and then flash them back.")
    }

    func testLargeBoardsSkipCosmeticWindowsWithoutDroppingShapes() throws {
        let (model, defaults, suite) = try model()
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        for index in 0..<65 {
            try session.document.add(Element(tool: .rectangle,
                frame: Rect(x: 100 + Double(index), y: 100, width: 20, height: 20)))
        }
        let original = session.document.elements
        model.setAnimationStyle(.dissolve)
        let toolbar = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 600, height: 120),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        defer {
            animator.cancel(); toolbar.orderOut(nil); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        animator.hide(toolbar: toolbar, model: model) {}
        XCTAssertTrue(model.workspaceHidden)
        XCTAssertEqual(animator.transientPanelCount, 0)
        XCTAssertFalse(animator.hasActiveTimeline)
        animator.show(toolbar: toolbar, model: model) {}
        XCTAssertEqual(session.document.elements, original)
        XCTAssertTrue(model.status.contains("64"))
        XCTAssertEqual(model.mode, .interact)
    }

    func testShowRendersLiveCanvasBeforeCompletingTheSnapshotHandoff() async throws {
        let (model, defaults, suite) = try model()
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        try session.document.add(Element(tool: .rectangle, frame: Rect(x: 100, y: 100, width: 100, height: 80)))
        let toolbar = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 600, height: 120),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        defer {
            animator.cancel(); toolbar.orderOut(nil); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        model.setAnimationStyle(.fade)
        var hidden = false
        animator.hide(toolbar: toolbar, model: model) { hidden = true }
        for _ in 0..<100 {
            if hidden { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(hidden)
        let before = session.canvas.renderPassCount
        var completed = false
        animator.show(toolbar: toolbar, model: model) {
            XCTAssertGreaterThan(session.canvas.renderPassCount, before)
            XCTAssertFalse(session.canvas.needsDisplay)
            XCTAssertEqual(animator.transientPanelCount, 0)
            completed = true
            model.interact()
        }
        // Check handoff ordering at completion, not a fixed scheduling delay.
        for _ in 0..<100 {
            if completed { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(completed)
    }

    func testSystemSuspensionCancelsPendingShowWithoutReactivatingInput() async throws {
        for cancelPending in [true, false] {
            let (model, defaults, suite) = try model()
            let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
            let app = AppDelegate(model: model, visibility: animator)
            defer {
                model.setVisibilityAnimation(false)
                app.hideTools()
                app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
                defaults.removePersistentDomain(forName: suite)
            }
            model.setVisibilityAnimation(false)
            app.activateTools()
            model.interact()
            let session = try XCTUnwrap(model.active)
            try session.document.add(Element(tool: .rectangle, frame: Rect(x: 40, y: 40, width: 80, height: 60)))
            let original = session.document.elements
            app.hideTools()
            model.setAnimationStyle(.curtain)
            app.activateTools()
            XCTAssertTrue(model.visibilityAnimating)
            model.suspendPresentation(cancelPendingRequests: cancelPending)
            XCTAssertFalse(animator.hasActiveTimeline)
            XCTAssertEqual(animator.transientPanelCount, 0)
            try await Task.sleep(for: .milliseconds(400))
            XCTAssertTrue(model.workspaceHidden)
            XCTAssertEqual(model.mode, .interact)
            XCTAssertTrue(session.panel.ignoresMouseEvents)
            XCTAssertFalse(session.panel.isVisible)
            XCTAssertEqual(session.document.elements, original)
        }
    }

    private func model() throws -> (WorkspaceModel, UserDefaults, String) {
        let suite = "visibility-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        return (WorkspaceModel(defaults: defaults), defaults, suite)
    }

    func testHiddenWorkspacePreservesEveryObjectAndReleasesSurfaces() throws {
        let (model, defaults, suite) = try model()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        for tool in Tool.allCases where tool != .image && tool != .select && tool != .eraser {
            try session.document.add(Element(tool: tool, frame: Rect(x: 150, y: 150, width: 80, height: 60)))
        }
        let elements = session.document.elements
        // Off shows and hides at once; Reduce Motion now crossfades instead.
        model.setVisibilityAnimation(false)
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { true })
        let toolbar = NSPanel(contentRect: NSRect(x: 200, y: 200, width: 880, height: 140),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        defer { animator.cancel(); toolbar.orderOut(nil) }
        animator.hide(toolbar: toolbar, model: model) {}
        XCTAssertTrue(model.workspaceHidden)
        XCTAssertFalse(session.panel.isVisible)
        XCTAssertTrue(session.panel.ignoresMouseEvents)
        XCTAssertFalse(toolbar.isVisible)
        XCTAssertFalse(model.visibilityAnimating)
        XCTAssertEqual(animator.transientPanelCount, 0)
        model.synchronize()
        session.presentation?.refresh()
        XCTAssertNil(session.presentation?.selectionOverlayLevel)
        XCTAssertFalse(session.panel.isVisible)
        XCTAssertEqual(session.document.elements, elements)
        // Avoid requesting pixel access in this visibility-only test.
        session.document.select(Set(elements.filter(\.tool.isPixelEffect).map(\.id)))
        try session.document.deleteSelection()
        let remaining = session.document.elements
        animator.show(toolbar: toolbar, model: model) {}
        XCTAssertFalse(model.workspaceHidden)
        XCTAssertTrue(session.panel.isVisible)
        XCTAssertTrue(session.panel.ignoresMouseEvents)
        XCTAssertEqual(model.mode, .interact)
        XCTAssertTrue(toolbar.isVisible)
        XCTAssertEqual(session.document.elements, remaining)
        session.document.undo()
        XCTAssertEqual(session.document.elements, elements)
    }

    func testAnimationRapidReversalDisposesTransientWindows() async throws {
        let (model, defaults, suite) = try model()
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let cover = Element(tool: .cover, frame: Rect(x: 180, y: 180, width: 100, height: 90))
        try session.document.add(cover)
        let toolbar = NSPanel(contentRect: NSRect(x: 200, y: 200, width: 880, height: 140),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        defer {
            animator.cancel(); toolbar.orderOut(nil)
            model.closeAll(); defaults.removePersistentDomain(forName: suite)
        }

        animator.hide(toolbar: toolbar, model: model) {}
        XCTAssertTrue(model.workspaceHidden)
        XCTAssertTrue(model.visibilityAnimating)
        XCTAssertEqual(animator.transientPanelCount, 2)
        animator.show(toolbar: toolbar, model: model) {}
        animator.hide(toolbar: toolbar, model: model) {}
        animator.show(toolbar: toolbar, model: model) {}
        try await Task.sleep(for: .seconds(ToolbarSlideMotion.showDuration + 0.12))
        XCTAssertTrue(animator.targetVisible)
        XCTAssertFalse(model.workspaceHidden)
        XCTAssertFalse(model.visibilityAnimating)
        XCTAssertEqual(animator.transientPanelCount, 0)
        XCTAssertEqual(toolbar.alphaValue, 1, accuracy: 0.01)
        XCTAssertTrue(session.panel.isVisible)
        XCTAssertEqual(session.document.elements, [cover])
        animator.hide(toolbar: toolbar, model: model) {}
        try await Task.sleep(for: .seconds(WorkspaceMotionTiming(style: .slide, appearing: false, hasDrawings: true).totalDuration + 0.15))
        XCTAssertFalse(toolbar.isVisible)
        XCTAssertFalse(session.panel.isVisible)
        XCTAssertEqual(animator.transientPanelCount, 0)
        XCTAssertEqual(session.document.elements, [cover])
    }

    func testThemePersistenceSharingAndIsolatedModelsDoNotChangeAppAppearance() throws {
        let (owner, defaults, suite) = try model()
        let (child, childDefaults, childSuite) = try model()
        let originalAppearance = NSApp.appearance
        defer {
            NSApp.appearance = originalAppearance
            child.closeAll(); owner.closeAll()
            defaults.removePersistentDomain(forName: suite)
            childDefaults.removePersistentDomain(forName: childSuite)
        }
        child.connectSettingsOwner(owner)
        let originalStyle = owner.style
        var callbacks: [AppTheme] = []
        owner.onThemeChanged = { callbacks.append($0) }
        for theme in AppTheme.allCases {
            child.setTheme(theme)
            XCTAssertEqual(owner.preferences.theme, theme)
            XCTAssertEqual(child.preferences.theme, theme)
            XCTAssertEqual(owner.style, originalStyle)
            XCTAssertTrue(NSApp.appearance === originalAppearance)
            let saved = try WorkspacePreferences.read(try XCTUnwrap(defaults.data(forKey: "workspace-preferences-v1")))
            XCTAssertEqual(saved.theme, theme)
        }
        XCTAssertEqual(callbacks, AppTheme.allCases)
        let restored = WorkspaceModel(defaults: defaults)
        let app = AppDelegate(model: restored)
        defer { restored.closeAll() }
        XCTAssertEqual(app.model.preferences.theme, .dark)
        XCTAssertTrue(NSApp.appearance === originalAppearance)
    }

    func testProductionThemeAppliesToExistingAndFutureNativeWindows() {
        let originalAppearance = NSApp.appearance
        let existing = NSPanel(contentRect: .zero, styleMask: [.borderless], backing: .buffered, defer: false)
        defer { NSApp.appearance = originalAppearance; existing.orderOut(nil) }
        for (theme, name) in [(AppTheme.light, NSAppearance.Name.aqua), (.dark, .darkAqua)] {
            AppDelegate.applyTheme(theme)
            XCTAssertEqual(existing.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]), name)
            let future = NSPanel(contentRect: .zero, styleMask: [.borderless], backing: .buffered, defer: false)
            XCTAssertEqual(future.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]), name)
            future.orderOut(nil)
        }
        AppDelegate.applyTheme(.system)
        XCTAssertNil(NSApp.appearance)
    }

    func testOffNeverCreatesTimelinesAndReduceMotionOnlyCrossfades() async throws {
        let (model, defaults, suite) = try model()
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        try session.document.add(Element(tool: .cover, frame: Rect(x: 80, y: 80, width: 60, height: 60)))
        let toolbar = NSPanel(contentRect: CGRect(x: 0, y: 0, width: 600, height: 140),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        toolbar.contentView = NSView(frame: CGRect(x: 0, y: 0, width: 600, height: 140))
        var reduced = false
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { reduced })
        defer {
            animator.cancel(); toolbar.orderOut(nil); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        for style in VisibilityAnimationStyle.available {
            model.setAnimationStyle(style)
            model.setVisibilityAnimation(false)
            reduced = false
            animator.hide(toolbar: toolbar, model: model) {}
            XCTAssertFalse(animator.hasActiveTimeline, "\(style)")
            XCTAssertEqual(animator.transientPanelCount, 0)
            XCTAssertNil(toolbar.contentView?.layer?.animationKeys())
            XCTAssertFalse(model.visibilityAnimating)
            XCTAssertFalse(session.panel.isVisible)
            XCTAssertTrue(session.panel.ignoresMouseEvents)
            animator.show(toolbar: toolbar, model: model) { model.interact() }
            XCTAssertFalse(animator.hasActiveTimeline)
            XCTAssertEqual(animator.transientPanelCount, 0)
            XCTAssertNil(toolbar.contentView?.layer?.mask)
        }
        model.setAnimationStyle(.magic)
        reduced = true
        animator.hide(toolbar: toolbar, model: model) {}
        XCTAssertTrue(animator.hasActiveTimeline, "Reduce Motion keeps a short crossfade.")
        XCTAssertEqual(animator.magicEffectLayerCount, 0, "Reduce Motion never starts movement effects.")
        XCTAssertEqual(animator.particleCount, 0)
        XCTAssertLessThanOrEqual(animator.transitionImageScreenFrames.count, 1, "Only the cover snapshot and the live toolbar fade.")
        let layer = try XCTUnwrap(toolbar.contentView?.layer)
        let group = try XCTUnwrap(layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
        let movement = try XCTUnwrap(group.animations?.first as? CABasicAnimation)
        XCTAssertEqual(movement.fromValue as? NSValue, movement.toValue as? NSValue, "The fade must not move the toolbar.")
        try await Task.sleep(for: .seconds(WorkspaceMotionTiming(style: .fade, appearing: false, hasDrawings: true).totalDuration + 0.2))
        XCTAssertFalse(animator.hasActiveTimeline)
        XCTAssertEqual(animator.transientPanelCount, 0)
        XCTAssertFalse(toolbar.isVisible)
    }

    func testEveryStyleRapidReversalCancelsStaleCompletionAndRestoresControls() async throws {
        let (model, defaults, suite) = try model()
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let cover = Element(tool: .cover, frame: Rect(x: 80, y: 80, width: 60, height: 60))
        try session.document.add(cover)
        let frame = CGRect(x: 100, y: 100, width: 600, height: 140)
        let toolbar = NSPanel(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
        toolbar.contentView = NSView(frame: CGRect(origin: .zero, size: frame.size))
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        defer {
            animator.cancel(); toolbar.orderOut(nil); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        for style in VisibilityAnimationStyle.available where style != .none {
            model.setAnimationStyle(style)
            var staleCompletions = 0
            var completions = 0
            animator.hide(toolbar: toolbar, model: model) { staleCompletions += 1 }
            animator.show(toolbar: toolbar, model: model) { staleCompletions += 1 }
            animator.hide(toolbar: toolbar, model: model) { staleCompletions += 1 }
            animator.show(toolbar: toolbar, model: model) { completions += 1; model.interact() }
            try await Task.sleep(for: .seconds(WorkspaceMotionTiming(style: style, appearing: true, hasDrawings: true).totalDuration + 0.2))
            XCTAssertEqual(staleCompletions, 0, "\(style)")
            XCTAssertEqual(completions, 1, "\(style)")
            XCTAssertFalse(animator.hasActiveTimeline)
            XCTAssertFalse(model.visibilityAnimating)
            XCTAssertEqual(animator.transientPanelCount, 0)
            XCTAssertNil(toolbar.contentView?.layer?.animationKeys())
            XCTAssertNil(toolbar.contentView?.layer?.mask)
            XCTAssertEqual(toolbar.frame, frame)
            XCTAssertEqual(toolbar.alphaValue, 1)
            XCTAssertTrue(toolbar.isVisible)
            XCTAssertEqual(session.document.elements, [cover])
        }
        session.document.undo()
        XCTAssertTrue(session.document.elements.isEmpty)
        session.document.redo()
        XCTAssertEqual(session.document.elements, [cover])
    }

    func testToolsOnlyLayoutAndPinnedInspectorArePersistent() throws {
        let (model, defaults, suite) = try model()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.tool = .pen
        model.setInspectorPinned(true)
        let pinned = NSHostingController(rootView: ToolbarView(model: model))
        let fullHeight = pinned.sizeThatFits(in: CGSize(width: 880, height: 900)).height
        model.setQuickPreferencesExpanded(false)
        let collapsed = NSHostingController(rootView: ToolbarView(model: model))
        let size = collapsed.sizeThatFits(in: CGSize(width: 880, height: 900))
        XCTAssertEqual(size.width, ToolbarToolLayout.horizontalWidth(tools: model.toolbarTools,
            available: 1000, propertiesVisible: false), accuracy: 1)
        XCTAssertLessThanOrEqual(size.height, 72)
        XCTAssertGreaterThan(fullHeight, size.height + 60)
        let saved = try WorkspacePreferences.read(try XCTUnwrap(defaults.data(forKey: "workspace-preferences-v1")))
        XCTAssertEqual(saved.quickPreferencesExpanded, false)
        XCTAssertEqual(saved.inspectorPinned, true)
    }

    func testQuitAndRemoveCensorsAreImmediateAndSettingsUseOneRoute() throws {
        let (model, defaults, suite) = try model()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        try session.document.add(Element(tool: .cover, frame: Rect(x: 100, y: 100, width: 80, height: 80)))
        let app = AppDelegate(model: model)
        XCTAssertEqual(app.applicationShouldTerminate(NSApp), .terminateNow)
        model.clearProtection()
        XCTAssertFalse(session.document.hasProtection)
        session.document.undo()
        XCTAssertTrue(session.document.hasProtection)
        var openings = 0
        model.onOpenPreferences = { openings += 1 }
        model.openPreferences()
        model.openPreferences()
        XCTAssertEqual(openings, 2)
        XCTAssertTrue(session.panel.ignoresMouseEvents)
    }

    func testEveryAnimationStyleCompletesAndReleasesItsWindows() async throws {
        let (model, defaults, suite) = try model()
        model.beginDrawing()
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        let toolbar = NSPanel(contentRect: NSRect(x: 100, y: 100, width: 880, height: 120),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        defer {
            animator.cancel(); toolbar.orderOut(nil)
            model.closeAll(); defaults.removePersistentDomain(forName: suite)
        }
        let session = try XCTUnwrap(model.active)
        try session.document.add(Element(tool: .rectangle, frame: Rect(x: 120, y: 120, width: 100, height: 60)))
        let original = session.document.elements
        for style in VisibilityAnimationStyle.available {
            model.setAnimationStyle(style)
            animator.hide(toolbar: toolbar, model: model) {}
            if style == .dissolve {
                XCTAssertGreaterThan(animator.particleCount, 0)
                XCTAssertLessThanOrEqual(animator.particleCount, 128_000)
            }
            try await Task.sleep(for: .seconds(WorkspaceMotionTiming(style: style, appearing: false, hasDrawings: true).totalDuration + 0.2))
            XCTAssertFalse(toolbar.isVisible)
            XCTAssertEqual(animator.transientPanelCount, 0)
            XCTAssertEqual(animator.particleCount, 0)
            // The visible synthetic canvas must not retain the user's keyboard between assertions.
            animator.show(toolbar: toolbar, model: model) { model.interact() }
            try await Task.sleep(for: .seconds(WorkspaceMotionTiming(style: style, appearing: true, hasDrawings: true).totalDuration + 0.2))
            XCTAssertTrue(toolbar.isVisible)
            XCTAssertFalse(model.visibilityAnimating)
            XCTAssertEqual(animator.transientPanelCount, 0)
            XCTAssertEqual(session.document.elements, original)
        }
    }

    func testGlobalToggleUsesVisibilityRatherThanDrawingMode() async throws {
        let (model, defaults, suite) = try model()
        let app = AppDelegate(model: model)
        defer {
            model.setVisibilityAnimation(false); app.hideTools()
            model.closeAll(); defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        model.interact()
        XCTAssertTrue(app.toolbarIsVisible)
        app.activateTools()
        XCTAssertTrue(model.workspaceHidden)
        app.activateTools()
        app.activateTools()
        try await Task.sleep(for: .milliseconds(400))
        XCTAssertTrue(model.workspaceHidden)
        XCTAssertFalse(app.toolbarIsVisible)
        app.activateTools()
        try await Task.sleep(for: .seconds(ToolbarSlideMotion.showDuration + 0.12))
        XCTAssertFalse(model.workspaceHidden)
        XCTAssertTrue(app.toolbarIsVisible)
        XCTAssertFalse(try XCTUnwrap(model.active).panel.ignoresMouseEvents)
        XCTAssertEqual(model.mode, .draw)
    }

    func testNumericColorAndBoardKeysDoNotConsumeModifiedToolChords() throws {
        let (model, defaults, suite) = try model()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        func key(_ text: String, _ modifiers: NSEvent.ModifierFlags = []) throws {
            let event = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: modifiers,
                timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
                characters: text, charactersIgnoringModifiers: text, isARepeat: false, keyCode: 0))
            session.canvas.keyDown(with: event)
        }
        for (index, tool) in [Tool.select, .rectangle, .diamond, .ellipse, .arrow, .line, .pen, .text, .highlighter, .eraser].enumerated() {
            try key(String((index + 1) % 10))
            XCTAssertEqual(model.tool, tool)
        }
        try key("7")
        for (letter, ink) in InkShortcut.choices {
            try key(letter)
            XCTAssertEqual(model.style.color, ink.color)
            XCTAssertEqual(model.tool, .pen)
        }
        try key("w", .control); XCTAssertEqual(model.board, .light)
        try key("w", .control); XCTAssertEqual(model.board, .transparent)
        try key("b", .control); XCTAssertEqual(model.board, .dark)
        try key("b", .control); XCTAssertEqual(model.board, .transparent)
        try key("2", .option)
        XCTAssertEqual(model.tool, .pen)
        try key("8")
        let click = try XCTUnwrap(NSEvent.mouseEvent(with: .leftMouseDown,
            location: session.canvas.convert(CGPoint(x: 200, y: 200), to: nil), modifierFlags: [],
            timestamp: 0, windowNumber: session.panel.windowNumber, context: nil, eventNumber: 0,
            clickCount: 1, pressure: 1))
        session.canvas.mouseDown(with: click)
        let editor = try XCTUnwrap(session.panel.firstResponder as? NSTextView)
        let color = model.style.color
        editor.insertText("b2w", replacementRange: NSRange(location: NSNotFound, length: 0))
        XCTAssertEqual(editor.string, "b2w")
        XCTAssertEqual(model.style.color, color)
        XCTAssertEqual(model.tool, .text)
    }
}
