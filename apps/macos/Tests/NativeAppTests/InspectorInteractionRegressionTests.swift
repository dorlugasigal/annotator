import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
import NativePlatform
@testable import AnnotatorApp

@MainActor
final class InspectorInteractionRegressionTests: XCTestCase {
    func testSettingsToolLabelsMatchToolbarSymbolsAndCanonicalCoverTitle() {
        for tool in Tool.allCases {
            let label = SettingsToolLabel(tool: tool)
            XCTAssertEqual(label.symbol, tool.toolbarSymbol)
            XCTAssertEqual(label.title, tool == .cover ? "Effects" : tool.label)
            XCTAssertNotNil(NSImage(systemSymbolName: label.symbol, accessibilityDescription: nil),
                            "The \(tool.rawValue) settings icon must exist on macOS.")
        }
    }

    func testEffectFieldCommitsReturnOnceWithoutRoundingAndEscapeCancels() throws {
        try withWorkspace { editor in
            var item = Element(tool: .blur, frame: Rect(x: 50, y: 50, width: 100, height: 100))
            item.style.effectAmount = 8.125
            editor.session.document = try CanvasDocument(elements: [item])
            select(item, in: editor)
            try withPanel(StyleValueControl(model: editor.model, parameter: .effect)) { panel, host in
                let field = try XCTUnwrap(fields(in: host).first)
                XCTAssertTrue(panel.makeFirstResponder(field))
                settle()
                XCTAssertTrue(panel.makeFirstResponder(nil))
                settle()
                XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 8.125,
                               "Merely focusing a rounded display must not change the stored value.")
                let text = try enter("45.125", in: field, panel: panel)
                text.insertNewline(nil)
                settle()
                XCTAssertTrue(panel.makeFirstResponder(nil))
                settle()
                XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 45.125)
                XCTAssertFalse(editor.session.document.isEditing)
                editor.model.undo()
                XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 8.125)
                XCTAssertFalse(editor.session.document.canUndo, "Return plus blur must be one edit.")
                _ = try enter("70", in: field, panel: panel)
                let escape = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
                    timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber,
                    context: nil, characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}",
                    isARepeat: false, keyCode: 53))
                panel.sendEvent(escape)
                settle()
                XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 8.125)
                XCTAssertFalse(editor.session.document.canUndo)
            }
        }
    }

    func testDraftCannotLeakAcrossSelectionOrToolTransitions() throws {
        try withWorkspace { editor in
            var first = Element(tool: .blur, frame: Rect(x: 50, y: 50, width: 100, height: 100))
            var second = Element(tool: .blur, frame: Rect(x: 200, y: 50, width: 100, height: 100))
            first.style.effectAmount = 8
            second.style.effectAmount = 22
            let axes = Element(tool: .axes, frame: Rect(x: 50, y: 200, width: 200, height: 100))
            editor.session.document = try CanvasDocument(elements: [first, second, axes])
            select(first, in: editor)
            try withPanel(ContextualInspector(model: editor.model).frame(width: 700)) { panel, host in
                let field = try XCTUnwrap(fields(in: host).first)
                _ = try enter("70", in: field, panel: panel)
                editor.session.document.select([second.id])
                editor.model.synchronize()
                settle()
                XCTAssertEqual(editor.model.effectiveTool, .blur)
                XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 8)
                XCTAssertEqual(editor.session.document.elements[1].style.effectAmount, 22)
                XCTAssertFalse(editor.session.document.isEditing)
                let next = try XCTUnwrap(fields(in: host).first)
                XCTAssertEqual(next.stringValue, "22")
                _ = try enter("34", in: next, panel: panel)
                XCTAssertTrue(panel.makeFirstResponder(nil))
                settle()
                XCTAssertEqual(editor.session.document.elements[1].style.effectAmount, 34)
                editor.model.undo()
                XCTAssertEqual(editor.session.document.elements[1].style.effectAmount, 22)
                editor.session.document.select([axes.id])
                editor.model.synchronize()
                settle()
                XCTAssertEqual(editor.model.tool, .select)
                XCTAssertEqual(editor.model.effectiveTool, .axes)
                let deadline = Date().addingTimeInterval(1)
                while fields(in: host).count != 1 && Date() < deadline {
                    host.layoutSubtreeIfNeeded()
                    settle()
                }
                XCTAssertEqual(fields(in: host).count, 1, "Axes expose the tick count; width is just a slider.")
                editor.session.document.select([])
                editor.model.synchronize()
                settle()
                XCTAssertTrue(fields(in: host).isEmpty, "Empty Select must not show stale effect/axes controls.")
            }
        }
    }

    func testUnfilledGridAndAxesFieldsEditOnlyTheSelectedObject() throws {
        try withWorkspace { editor in
            let grid = Element(tool: .grid, frame: Rect(x: 50, y: 50, width: 100, height: 100))
            let axes = Element(tool: .axes, frame: Rect(x: 200, y: 50, width: 100, height: 100))
            editor.session.document = try CanvasDocument(elements: [grid, axes])
            select(grid, in: editor)
            try withPanel(ContextualInspector(model: editor.model).frame(width: 700)) { panel, host in
                let gridFields = fields(in: host)
                XCTAssertEqual(gridFields.count, 2, "Unfilled grids expose rows and columns; width is just a slider.")
                _ = try enter("7", in: XCTUnwrap(gridFields.first), panel: panel)
                XCTAssertTrue(panel.makeFirstResponder(nil))
                settle()
                XCTAssertEqual(editor.session.document.elements[0].style.gridRows, 7)
                XCTAssertNil(editor.session.document.elements[1].style.gridRows)
                editor.session.document.select([axes.id])
                editor.model.synchronize()
                settle()
                let negative = try XCTUnwrap(accessibilityNodes(in: host).first {
                    $0.accessibilityLabel() == "Centered axes with negative directions"
                })
                XCTAssertTrue(negative.accessibilityPerformPress())
                settle()
                XCTAssertEqual(editor.session.document.elements[1].style.negativeAxes, true)
                XCTAssertNil(editor.session.document.elements[0].style.negativeAxes)
                let tickField = try XCTUnwrap(fields(in: host).last)
                _ = try enter("6", in: tickField, panel: panel)
                XCTAssertTrue(panel.makeFirstResponder(nil))
                settle()
                XCTAssertEqual(editor.session.document.elements[1].style.axisTickCount, 6)
                XCTAssertNil(editor.session.document.elements[0].style.axisTickCount)
                _ = try enter("2.5", in: tickField, panel: panel)
                XCTAssertTrue(panel.makeFirstResponder(nil))
                settle()
                XCTAssertEqual(editor.session.document.elements[1].style.axisTickCount, 6)
                XCTAssertEqual(tickField.stringValue, "2.5", "Invalid counts remain available for correction.")
                _ = try enter("0", in: tickField, panel: panel)
                XCTAssertTrue(panel.makeFirstResponder(nil))
                settle()
                XCTAssertEqual(editor.session.document.elements[1].style.axisTickCount, 0)
                editor.model.undo()
                XCTAssertEqual(editor.session.document.elements[1].style.axisTickCount, 6)
                let saved = try DrawingFile.read(DrawingFile(document: editor.session.document).data())
                XCTAssertEqual(saved.elements, editor.session.document.elements)
            }
        }
    }

    func testCompactCoverKeepsSliderAndModePickerWithSelectedValues() throws {
        try withWorkspace { editor in
            var item = Element(tool: .blur, frame: Rect(x: 50, y: 50, width: 100, height: 100))
            item.style.effectAmount = 8
            editor.session.document = try CanvasDocument(elements: [item])
            select(item, in: editor)
            try withPanel(CoverPropertiesView(model: editor.model, compact: true)) { panel, host in
                let slider = try XCTUnwrap(accessibilityNodes(in: host).first {
                    $0.accessibilityRole() == .slider && $0.accessibilityLabel() == "Blur"
                })
                XCTAssertTrue(slider.accessibilityPerformIncrement())
                settle()
                let increased = try XCTUnwrap(editor.session.document.elements[0].style.effectAmount)
                XCTAssertGreaterThan(increased, 8)
                let field = try XCTUnwrap(fields(in: host).first)
                XCTAssertEqual(try XCTUnwrap(Double(field.stringValue)), increased, accuracy: 0.1)
                _ = try enter("27", in: field, panel: panel)
                XCTAssertTrue(panel.makeFirstResponder(nil))
                settle()
                XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 27)
                let menuItems = try effectMenu(in: host, choosing: "Pixelate")
                XCTAssertTrue(menuItems.contains("Pixelate"))
                settle()
                XCTAssertEqual(editor.session.document.elements[0].tool, .pixelate)
                XCTAssertEqual(editor.session.document.elements[0].id, item.id)
                XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 27)
                XCTAssertEqual(editor.model.tool, .select, "Changing selected cover mode must not leave Select.")
                editor.model.undo()
                XCTAssertEqual(editor.session.document.elements[0].tool, .blur)
            }

        }
    }

    func testEffectSliderMaximumAndMidpointPreserveLegacyValuesUntilEdited() throws {
        try withWorkspace { editor in
            for (tool, maximum) in [(Tool.blur, 20.0), (.pixelate, 40.0), (.distort, 100.0)] {
                let minimum = tool == .distort ? 2.0 : 0.0
                var item = Element(tool: tool, frame: Rect(x: 50, y: 50, width: 100, height: 100))
                item.style.effectAmount = 80.125
                editor.session.document = try CanvasDocument(elements: [item])
                select(item, in: editor)
                try withPanel(CoverPropertiesView(model: editor.model, compact: true)) { _, host in
                    let slider = try XCTUnwrap(accessibilityNodes(in: host).first { $0.accessibilityRole() == .slider })
                    let range = try slider.accessibilityRange()
                    XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 80.125)
                    XCTAssertFalse(editor.session.document.canUndo, "Displaying an older strength must not rewrite it.")
                    XCTAssertTrue(slider.setAccessibilityNumber((range.lowerBound + range.upperBound) / 2))
                    settle()
                    XCTAssertEqual(try XCTUnwrap(editor.session.document.elements[0].style.effectAmount),
                                   (minimum + maximum) / 2, accuracy: 0.01)
                    XCTAssertTrue(slider.setAccessibilityNumber(range.upperBound))
                    settle()
                    XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, maximum)
                    editor.model.undo()
                    XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, (minimum + maximum) / 2)
                    editor.model.undo()
                    XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 80.125)
                    let restored = try DrawingFile.read(DrawingFile(document: editor.session.document).data())
                    XCTAssertEqual(restored.elements[0].style.effectAmount, 80.125)
                    XCTAssertTrue(slider.setAccessibilityNumber(range.lowerBound))
                    settle()
                    XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, minimum)
                    let atMinimum = try DrawingFile.read(DrawingFile(document: editor.session.document).data())
                    XCTAssertEqual(atMinimum.elements[0].style.effectAmount, minimum)
                }
            }
        }
    }

    func testEffectsMenuContainsOnlyPixelEffects() throws {
        try withWorkspace { editor in
            editor.model.chooseCover()
            XCTAssertEqual(editor.model.tool, .blur)
            for compact in [false, true] {
                try withPanel(CoverPropertiesView(model: editor.model, compact: compact)) { _, host in
                    let modes = try effectMenu(in: host)
                        .filter { ["Solid", "Blur", "Pixelate", "Distort"].contains($0) }
                    XCTAssertEqual(Set(modes), ["Blur", "Pixelate", "Distort"])
                }
            }
        }
    }

    func testSelectedStyleSurvivesChoosingSelectAgain() throws {
        try withWorkspace { editor in
            var item = Element(tool: .axes, frame: Rect(x: 50, y: 50, width: 100, height: 100))
            item.style.width = 11
            item.style.axisTickCount = 6
            editor.session.document = try CanvasDocument(elements: [item])
            select(item, in: editor)
            editor.model.choose(.select)
            XCTAssertEqual(editor.model.style, item.style,
                           "Choosing Select must not show Select defaults for a retained selection.")
        }
    }

    func testPropertiesCycleIsSharedAndQuickControlsExistInImageWorkspace() throws {
        try withWorkspace { editor in
            ToolbarPropertiesMode.toolsOnly.apply(to: editor.model)
            editor.model.choose(.pen)
            try withPanel(ToolbarView(model: editor.model,
                                      availableSize: CGSize(width: 600, height: 500), showInspector: false)) { _, host in
                for expected in [ToolbarPropertiesMode.quick, .full, .toolsOnly, .quick] {
                    let controls = accessibilityNodes(in: host).filter {
                        $0.accessibilityLabel() == "Properties" && $0.accessibilityRole() == .button
                    }
                    XCTAssertEqual(controls.count, 1, "Only one Properties control belongs on the rail.")
                    XCTAssertTrue(try XCTUnwrap(controls.first).accessibilityPerformPress())
                    settle()
                    XCTAssertEqual(ToolbarPropertiesMode(model: editor.model), expected)
                }
                XCTAssertEqual(fields(in: host).count, 1, "Image workspace must retain quick width control.")
            }
        }
    }

    func testCounterQuickControlsEditOpacityAndNeverOfferIneffectiveWidth() throws {
        try withWorkspace { editor in
            var counter = Element(tool: .counter, frame: Rect(x: 50, y: 50, width: 40, height: 40), text: "1")
            counter.style.width = 7
            counter.style.opacity = 0.75
            editor.session.document = try CanvasDocument(elements: [counter])
            select(counter, in: editor)
            ToolbarPropertiesMode.quick.apply(to: editor.model)
            for (orientation, width) in [(ToolbarOrientation.horizontal, 320.0), (.horizontal, 1000.0), (.vertical, 1000.0)] {
                editor.model.setToolbarOrientation(orientation)
                try withPanel(ToolbarView(model: editor.model,
                                          availableSize: CGSize(width: width, height: 900))) { panel, host in
                    XCTAssertFalse(accessibilityNodes(in: host).contains { $0.accessibilityLabel() == "Precise width" })
                    XCTAssertTrue(accessibilityNodes(in: host).contains { $0.accessibilityLabel() == "Stroke color" })
                    let opacity = try XCTUnwrap(fields(in: host).first)
                    XCTAssertEqual(fields(in: host).count, 1)
                    XCTAssertEqual(opacity.stringValue, "75")
                    _ = try enter("55", in: opacity, panel: panel)
                    XCTAssertTrue(panel.makeFirstResponder(nil))
                    settle()
                    XCTAssertEqual(editor.session.document.elements[0].style.opacity, 0.55, accuracy: 0.001)
                    XCTAssertEqual(editor.session.document.elements[0].style.width, 7)
                    editor.model.undo()
                    XCTAssertEqual(editor.session.document.elements[0].style.opacity, 0.75, accuracy: 0.001)
                }
            }
        }
    }

    func testArrowheadTriggersAlignWithRoutingAndStackInCompactProperties() throws {
        try withWorkspace { editor in
            editor.model.choose(.arrow)
            for compact in [false, true] {
                try withPanel(ContextualInspector(model: editor.model, compact: compact)
                    .frame(width: compact ? 276 : 900)) { panel, host in
                    panel.setContentSize(host.fittingSize)
                    settle()
                    let nodes = accessibilityNodes(in: host)
                    let start = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == "Start arrowhead" && $0.accessibilityRole() == .button })
                        .accessibilityFrame()
                    let end = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == "End arrowhead" && $0.accessibilityRole() == .button })
                        .accessibilityFrame()
                    let routing = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == "Curved arrow" })
                        .accessibilityFrame()
                    XCTAssertEqual(start.width, 68, accuracy: 1)
                    XCTAssertEqual(end.width, 68, accuracy: 1)
                    if compact {
                        XCTAssertEqual(start.minX, end.minX, accuracy: 1)
                        XCTAssertLessThan(end.maxY, start.minY)
                    } else {
                        XCTAssertEqual(start.maxY, end.maxY, accuracy: 1)
                        XCTAssertGreaterThan(end.minX, start.maxX)
                        XCTAssertLessThanOrEqual(end.maxX - start.minX, 500)
                        XCTAssertEqual(routing.maxY, start.maxY, accuracy: 1)
                    }
                }
            }
        }
    }

    /// A screen too narrow for the arrow's row keeps it on one row that scrolls sideways, without a scroll bar.
    func testNarrowHorizontalPropertiesStayOnOneRowAndScrollSideways() throws {
        try withWorkspace { editor in
            editor.model.choose(.arrow)
            editor.model.setToolbarOrientation(.horizontal)
            ToolbarPropertiesMode.full.apply(to: editor.model)
            try withPanel(ToolbarView(model: editor.model,
                                      availableSize: CGSize(width: 400, height: 900))) { panel, host in
                panel.setContentSize(host.fittingSize)
                settle()
                // The panel helper adds 12 points of padding on each side.
                XCTAssertLessThanOrEqual(host.fittingSize.width, 400 + 24 + 1, "The toolbar stays within the screen")
                let nodes = accessibilityNodes(in: host)
                let start = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == "Start arrowhead" && $0.accessibilityRole() == .button })
                    .accessibilityFrame()
                let end = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == "End arrowhead" && $0.accessibilityRole() == .button })
                    .accessibilityFrame()
                XCTAssertEqual(start.width, 68, accuracy: 1)
                XCTAssertEqual(end.width, 68, accuracy: 1)
                XCTAssertEqual(start.midY, end.midY, accuracy: 1, "One row, never two")
                XCTAssertGreaterThan(end.minX, start.maxX)
                let scroll = try XCTUnwrap(scrollViews(in: host).first { scroll in
                    (scroll.documentView?.frame.width ?? 0) > scroll.contentView.bounds.width + 1
                }, "The row scrolls sideways")
                XCTAssertFalse(scroll.hasHorizontalScroller && !(scroll.horizontalScroller?.isHidden ?? true),
                               "No scroll bar shows")
            }
        }
    }

    private func scrollViews(in view: NSView) -> [NSScrollView] {
        (view as? NSScrollView).map { [$0] } ?? view.subviews.flatMap { scrollViews(in: $0) }
    }

    func testActiveModesStayVisibleAndStopInEveryToolbarLayout() throws {
        try withWorkspace { editor in
            let configurations: [(ToolbarOrientation, ToolbarPropertiesMode, CGSize)] = [
                (.horizontal, .toolsOnly, CGSize(width: 320, height: 620)),
                (.horizontal, .quick, CGSize(width: 700, height: 620)),
                (.vertical, .toolsOnly, CGSize(width: 740, height: 620)),
                (.vertical, .full, CGSize(width: 740, height: 620))
            ]
            for (orientation, properties, available) in configurations {
                editor.model.setToolbarOrientation(orientation)
                properties.apply(to: editor.model)
                editor.model.choose(.pen)
                editor.model.spotlight = true
                editor.model.cursorHalo = true
                editor.model.board = .light
                try editor.session.document.setPresentation(PresentationSettings(mode: .screen, zoom: 2))
                editor.model.synchronize()
                try withPanel(ToolbarView(model: editor.model, availableSize: available)) { panel, host in
                    let size = host.fittingSize
                    XCTAssertLessThanOrEqual(size.width, available.width + 24)
                    XCTAssertLessThanOrEqual(size.height, available.height + 24)
                    panel.setContentSize(size)
                    settle()
                    let labels = ["Stop screen zoom", "Stop spotlight", "Stop cursor halo", "Stop whiteboard"]
                    for label in labels {
                        let button = try XCTUnwrap(accessibilityNodes(in: host).first {
                            $0.accessibilityRole() == .button && $0.accessibilityLabel() == label
                        })
                        XCTAssertEqual(button.accessibilityValue() as? String, "Active")
                        let bounds = panel.convertToScreen(host.convert(host.bounds, to: nil)).insetBy(dx: -1, dy: -1)
                        XCTAssertTrue(bounds.contains(button.accessibilityFrame()), "\(label) must not be clipped.")
                        XCTAssertTrue(button.accessibilityPerformPress())
                        settle()
                        XCTAssertFalse(accessibilityNodes(in: host).contains { $0.accessibilityLabel() == label })
                        XCTAssertEqual(editor.model.tool, .pen, "Stopping a mode must not change the drawing tool.")
                    }
                    XCTAssertFalse(editor.model.screenZoomEnabled)
                    XCTAssertFalse(editor.model.spotlight)
                    XCTAssertFalse(editor.model.cursorHalo)
                    XCTAssertEqual(editor.model.board, .transparent)
                    XCTAssertEqual(editor.session.document.presentation.mode, .off)
                    editor.model.undo()
                    XCTAssertTrue(editor.model.screenZoomEnabled, "Stopping zoom keeps its existing undo behavior.")
                    editor.model.stopZoom()
                }
            }
        }
    }

    func testLensAndBlackboardHaveIndependentOneClickStops() throws {
        try withWorkspace { editor in
            ToolbarPropertiesMode.toolsOnly.apply(to: editor.model)
            editor.model.board = .dark
            try editor.session.document.setPresentation(PresentationSettings(mode: .lens, zoom: 3))
            editor.model.synchronize()
            try withPanel(ToolbarView(model: editor.model,
                                      availableSize: CGSize(width: 320, height: 620))) { _, host in
                let lens = try XCTUnwrap(accessibilityNodes(in: host).first {
                    $0.accessibilityLabel() == "Stop pointer lens"
                })
                XCTAssertTrue(lens.accessibilityPerformPress())
                settle()
                XCTAssertFalse(editor.model.magnifierEnabled)
                XCTAssertEqual(editor.model.board, .dark, "Stopping the lens must not remove the board.")
                let board = try XCTUnwrap(accessibilityNodes(in: host).first {
                    $0.accessibilityLabel() == "Stop blackboard"
                })
                XCTAssertTrue(board.accessibilityPerformPress())
                settle()
                XCTAssertEqual(editor.model.board, .transparent)
            }
        }
    }

    func testPendingZoomShowsThePermissionStageAndCancelsWithoutStartingCapture() throws {
        let stages: [(ZoomStartState, String)] = [
            (.screenPermission, "Waiting for Screen Recording permission"),
            (.gesturePermission, "Waiting for Accessibility permission"),
            (.starting, "Starting live screen zoom")
        ]
        for (state, detail) in stages {
            let controller = ZoomStartController(
                screenGranted: { state != .screenPermission },
                gesturesGranted: { state != .gesturePermission },
                requestScreen: {}, requestGestures: {}, automaticChecks: false,
                start: {}, changed: { _ in })
            controller.request()
            XCTAssertEqual(controller.state, state)
            let presentation = try XCTUnwrap(PendingZoomPresentation(state: controller.state))
            XCTAssertEqual(presentation.detail, detail)
            for compact in [true, false] {
                controller.request()
                var cancellations = 0
                try withPanel(PendingZoomControl(presentation: presentation, compact: compact) {
                    cancellations += 1
                    controller.cancel()
                }) { _, host in
                    let cancel = try XCTUnwrap(accessibilityNodes(in: host).first {
                        $0.accessibilityLabel() == "Cancel pending zoom"
                    })
                    XCTAssertEqual(cancel.accessibilityValue() as? String, detail)
                    XCTAssertTrue(cancel.accessibilityPerformPress())
                    settle()
                    XCTAssertEqual(cancellations, 1)
                    XCTAssertEqual(controller.state, .idle)
                    XCTAssertNil(PendingZoomPresentation(state: controller.state))
                }
            }
        }
        XCTAssertNil(PendingZoomPresentation(state: .active))
        XCTAssertNil(PendingZoomPresentation(state: .failed("Permission timed out")))
    }

    func testStrokeAndFillPalettesExposeLabeledScreenPicker() throws {
        try withWorkspace { editor in
            for fill in [false, true] {
                try withPanel(InspectorColorPalette(model: editor.model, fill: fill, done: {})) { panel, host in
                    panel.setContentSize(host.fittingSize)
                    settle()
                    let button = try XCTUnwrap(accessibilityNodes(in: host).first {
                        $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Pick from screen"
                    })
                    let frame = button.accessibilityFrame()
                    XCTAssertGreaterThan(frame.width, 160)
                    XCTAssertGreaterThanOrEqual(frame.height, 30)
                    let bounds = panel.convertToScreen(host.convert(host.bounds, to: nil)).insetBy(dx: -1, dy: -1)
                    XCTAssertTrue(bounds.contains(frame))
                }
            }
        }
    }

    func testSettingsDoNotExposeTheRejectedAIFileWorkflow() throws {
        try withWorkspace { editor in
            try withPanel(CustomizationView(model: editor.model, initialTab: .general)) { panel, host in
                panel.setContentSize(host.fittingSize)
                settle()
                let nodes = accessibilityNodes(in: host)
                let text = nodes.compactMap {
                    ($0.accessibilityValue() as? String) ?? $0.accessibilityLabel()
                }.joined(separator: "\n")
                XCTAssertFalse(text.contains("Open drawings from AI tools"))
                XCTAssertFalse(text.contains("Open AI drawing..."))
                XCTAssertFalse(text.contains("AI tools"))
                XCTAssertFalse(text.contains("Allow agent drafts"))
                XCTAssertFalse(text.contains("Pro advanced editing"))
            }
        }
    }

    func testDiamondPropertiesShareOneRowWithoutClipping() throws {
        try withWorkspace { editor in
            editor.model.choose(.diamond)
            try withPanel(ContextualInspector(model: editor.model).fixedSize()) { panel, host in
                panel.setContentSize(host.fittingSize)
                settle()
                let nodes = accessibilityNodes(in: host)
                let edges = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == "Rounded corners" })
                let strokeWidth = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == "Width" && $0.accessibilityRole() == .slider })
                XCTAssertEqual(edges.accessibilityFrame().midY, strokeWidth.accessibilityFrame().midY, accuracy: 2,
                               "Width is just the slider, on the same row as Edges")
                XCTAssertLessThan(host.fittingSize.height, 100, "One row of properties, not two")
                XCTAssertLessThan(host.fittingSize.width, 900)
                let bounds = panel.convertToScreen(host.convert(host.bounds, to: nil)).insetBy(dx: -1, dy: -1)
                for node in nodes where node.accessibilityRole() == .button {
                    XCTAssertTrue(bounds.contains(node.accessibilityFrame()), node.accessibilityLabel() ?? "Button")
                }
            }
        }
    }

    func testFloatingZoomControlsContainOnlyZoomAdjustment() throws {
        try withWorkspace { editor in
            editor.model.noteZoomGestures(available: false)
            for compact in [true, false] {
                try withPanel(LiveZoomControls(model: editor.model, compact: compact)) { panel, host in
                    panel.setContentSize(host.fittingSize)
                    settle()
                    let labels = accessibilityNodes(in: host).filter { $0.accessibilityRole() == .button }
                        .compactMap { $0.accessibilityLabel() }
                    XCTAssertEqual(Set(labels), ["Decrease live zoom", "Increase live zoom"])
                    XCTAssertFalse(labels.contains { $0.contains("Allow") || $0.contains("Retry") || $0.contains("Permission") })
                }
            }
        }
    }

    func testSmartDrawIconTogglesInBothPreferenceLayoutsWithoutEditingObjects() throws {
        XCTAssertNotNil(NSImage(systemSymbolName: "wand.and.stars", accessibilityDescription: nil))
        try withWorkspace { editor in
            editor.model.choose(.pen)
            let shape = Element(tool: .rectangle, frame: Rect(x: 20, y: 20, width: 80, height: 60))
            try editor.session.document.add(shape)
            let generation = editor.session.document.generation
            for full in [true, false] {
                editor.model.smartDraw = false
                editor.model.setInspectorPinned(full)
                editor.model.setQuickPreferencesExpanded(true)
                let content = full
                    ? AnyView(ContextualInspector(model: editor.model).frame(width: 800))
                    : AnyView(ToolbarView(model: editor.model).frame(width: 1000))
                try withPanel(content) { panel, host in
                    panel.setContentSize(host.fittingSize)
                    settle()
                    for expected in ["Off", "On"] {
                        let button = try XCTUnwrap(accessibilityNodes(in: host).first {
                            $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Smart Draw"
                        })
                        XCTAssertEqual(button.accessibilityValue() as? String, expected)
                        XCTAssertGreaterThanOrEqual(button.accessibilityFrame().height, 32)
                        XCTAssertTrue(button.accessibilityPerformPress())
                        settle()
                        XCTAssertEqual(editor.model.smartDraw, expected == "Off")
                        let data = try XCTUnwrap(editor.model.preferenceDefaults.data(forKey: "workspace-preferences-v1"))
                        XCTAssertEqual(try WorkspacePreferences.read(data).smartDraw, editor.model.smartDraw)
                    }

                }
            }
            XCTAssertEqual(editor.session.document.elements, [shape])
            XCTAssertEqual(editor.session.document.generation, generation)
        }
    }

    func testToolbarDoesNotOfferPermissionOrRetryActions() throws {
        try withWorkspace { editor in
            editor.model.reportCaptureFailure(CaptureFailure.streamStopped("Controlled test failure"))
            for orientation in [ToolbarOrientation.horizontal, .vertical] {
                editor.model.setToolbarOrientation(orientation)
                try withPanel(ToolbarView(model: editor.model, availableSize: CGSize(width: 1000, height: 1000))) { _, host in
                    settle()
                    let buttons = accessibilityNodes(in: host).filter { $0.accessibilityRole() == .button }
                        .compactMap { $0.accessibilityLabel() }
                    XCTAssertFalse(buttons.contains {
                        $0.contains("Permission") || $0.contains("Allow Option-scroll") || $0.hasPrefix("Retry")
                    })
                    XCTAssertTrue(buttons.contains("Settings and shortcuts"))
                }
            }
        }
    }

    func testMyShapesOnlyShowsDeleteInsideEditMode() async throws {
        let suite = "saved-shapes-edit-mode-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName:suite))
        let image = try PNGExporter.image(elements:[],size:CGSize(width:500,height:350),background:.white)
        let editor = NativeCanvasFixture(image:image,defaults:defaults)
        let library = ShapeLibraryModel(defaults:defaults)
        defer { editor.close();defaults.removePersistentDomain(forName:suite) }
        var document = try CanvasDocument(elements:[Element(tool:.rectangle,frame:Rect(x:10,y:10,width:60,height:40))])
        document.select(Set(document.elements.map(\.id)))
        await library.reload()
        let saved = await library.save(name:"Saved box",document:document)
        XCTAssertTrue(saved)
        NativeAccessibilityNode.enable()
        let host = NSHostingView(rootView:MyShapesLibrary(model:editor.model,library:library).padding(12).frame(width:360))
        let panel = NSPanel(contentRect:CGRect(x:60,y:60,width:360,height:480),
                            styleMask:[.titled,.nonactivatingPanel],backing:.buffered,defer:false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil);panel.contentView=nil;panel.close() }
        try await Task.sleep(for:.milliseconds(100))
        panel.setContentSize(host.fittingSize)
        XCTAssertFalse(library.isBusy)
        XCTAssertFalse(accessibilityNodes(in:host).contains { $0.accessibilityLabel()?.hasPrefix("Delete") == true })
        let edit = try XCTUnwrap(accessibilityNodes(in:host).first { $0.accessibilityLabel()=="Edit saved shapes" })
        XCTAssertTrue(edit.accessibilityPerformPress())
        try await Task.sleep(for:.milliseconds(60))
        let entry = try XCTUnwrap(accessibilityNodes(in:host).first { $0.accessibilityLabel()=="Select saved shape Saved box" })
        XCTAssertTrue(entry.accessibilityPerformPress())
        try await Task.sleep(for:.milliseconds(60))
        XCTAssertTrue(editor.session.document.elements.isEmpty,"Selecting an entry in Edit mode must not insert it.")
        XCTAssertNotNil(accessibilityNodes(in:host).first { $0.accessibilityLabel()=="Rename..." })
        XCTAssertNotNil(accessibilityNodes(in:host).first { $0.accessibilityLabel()=="Delete..." })
        let done = try XCTUnwrap(accessibilityNodes(in:host).first { $0.accessibilityLabel()=="Done editing saved shapes" })
        XCTAssertTrue(done.accessibilityPerformPress())
        try await Task.sleep(for:.milliseconds(60))
        XCTAssertFalse(accessibilityNodes(in:host).contains { $0.accessibilityLabel()?.hasPrefix("Delete") == true })
    }

    private func withWorkspace(_ body: (NativeCanvasFixture) throws -> Void) throws {
        let suite = "inspector-regressions-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 500, height: 400), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        try body(editor)
    }

    private func select(_ element: Element, in editor: NativeCanvasFixture) {
        editor.model.choose(.select)
        editor.session.document.select([element.id])
        editor.model.synchronize()
    }

    private func withPanel<Content: View>(_ content: Content, _ body: (NSPanel, NSView) throws -> Void) throws {
        NativeAccessibilityNode.enable()
        let host = NSHostingController(rootView: content.padding(12))
        let panel = NSPanel(contentRect: NSRect(x: 50, y: 50, width: 760, height: 350),
                            styleMask: [.titled], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentViewController = host
        defer { panel.contentViewController = nil; panel.close() }
        panel.makeKeyAndOrderFront(nil)
        settle()
        host.view.layoutSubtreeIfNeeded()
        try body(panel, host.view)
    }

    private func fields(in view: NSView) -> [NSTextField] {
        if let field = view as? NSTextField { return field.isEditable ? [field] : [] }
        return view.subviews.flatMap { fields(in: $0) }
    }

    private func effectMenu(in host: NSView, choosing title: String? = nil) throws -> [String] {
        func popups(in view: NSView) -> [NSPopUpButton] {
            if let picker = view as? NSPopUpButton { return [picker] }
            return view.subviews.flatMap { popups(in: $0) }
        }
        let picker = try XCTUnwrap(popups(in: host).first)
        let menu = try XCTUnwrap(picker.menu)
        menu.delegate?.menuNeedsUpdate?(menu)
        if let title {
            let item = try XCTUnwrap(menu.item(withTitle: title))
            XCTAssertNotNil(item.action)
            menu.performActionForItem(at: menu.index(of: item))
        }
        return menu.items.map(\.title)
    }

    private func accessibilityNodes(in node: NSObject) -> [NativeAccessibilityNode] {
        NativeAccessibilityNode.nodes(in: node)
    }

    @MainActor
    struct NativeAccessibilityNode {
        let object: NSObject

        static func enable() {
            let attribute = NSAccessibility.Attribute(rawValue: "AXEnhancedUserInterface")
            XCTAssertTrue(NSApplication.shared.accessibilityIsAttributeSettable(attribute))
            NSApplication.shared.accessibilitySetValue(true, forAttribute: attribute)
        }

        static func nodes(in root: NSObject) -> [Self] {
            var visited = Set<ObjectIdentifier>()
            @MainActor func visit(_ object: NSObject) -> [Self] {
                guard visited.insert(ObjectIdentifier(object)).inserted else { return [] }
                let node = Self(object: object)
                let children = node.value("accessibilityChildren") as? [NSObject] ?? []
                if children.isEmpty, let view = object as? NSView {
                    return [node] + view.subviews.flatMap { visit($0) }
                }
                return [node] + children.flatMap(visit)
            }
            return visit(root)
        }

        func accessibilityLabel() -> String? { value("accessibilityLabel") as? String }
        func accessibilityTitle() -> String? { value("accessibilityTitle") as? String }
        func accessibilityIdentifier() -> String? { value("accessibilityIdentifier") as? String }
        func accessibilityValue() -> Any? { value("accessibilityValue") }
        func accessibilityRole() -> NSAccessibility.Role? {
            (value("accessibilityRole") as? String).map(NSAccessibility.Role.init(rawValue:))
        }
        func accessibilityFrame() -> NSRect {
            guard let frame = value("accessibilityFrame") as? NSValue else {
                XCTFail("Accessibility object has no frame: \(type(of: object))")
                return .zero
            }
            return frame.rectValue
        }
        func accessibilityPerformPress() -> Bool {
            if let native = object as? any NSAccessibilityProtocol { return native.accessibilityPerformPress() }
            return perform("accessibilityPerformPress")
        }
        func accessibilityPerformIncrement() -> Bool { perform("accessibilityPerformIncrement") }
        func accessibilityRange() throws -> ClosedRange<Double> {
            let lower = try XCTUnwrap(value("accessibilityMinValue") as? NSNumber).doubleValue
            let upper = try XCTUnwrap(value("accessibilityMaxValue") as? NSNumber).doubleValue
            return lower...upper
        }
        func setAccessibilityNumber(_ number: Double) -> Bool {
            guard number.isFinite, object.accessibilityIsAttributeSettable(.value) else { return false }
            guard object.responds(to: NSSelectorFromString("setAccessibilityValue:")) else { return false }
            object.setValue(NSNumber(value: number), forKey: "accessibilityValue")
            return true
        }

        private func value(_ key: String) -> Any? {
            guard object.responds(to: NSSelectorFromString(key)) else { return nil }
            return object.value(forKey: key)
        }

        private func perform(_ name: String) -> Bool {
            let selector = NSSelectorFromString(name)
            guard object.responds(to: selector) else {
                XCTFail("Accessibility action \(name) is unavailable on \(type(of: object))")
                return false
            }
            // SwiftUI exposes these Objective-C methods without declaring NSAccessibility protocol conformance.
            typealias Action = @convention(c) (AnyObject, Selector) -> Bool
            let action = unsafeBitCast(object.method(for: selector), to: Action.self)
            return action(object, selector)
        }
    }

    private func enter(_ value: String, in field: NSTextField, panel: NSPanel) throws -> NSTextView {
        XCTAssertTrue(panel.makeFirstResponder(field))
        settle()
        let editor = try XCTUnwrap(field.currentEditor() as? NSTextView)
        editor.selectAll(nil)
        editor.insertText(value, replacementRange: NSRange(location: NSNotFound, length: 0))
        settle()
        return editor
    }

    private func settle() { RunLoop.current.run(until: Date().addingTimeInterval(0.06)) }
}

typealias NativeAccessibilityNode = InspectorInteractionRegressionTests.NativeAccessibilityNode
