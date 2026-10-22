import AnnotationCore
import AppKit
import Foundation
@testable import PresentationRendering

extension GeometryExport {
    static func anchors(_ corpus: Corpus) throws {
        var records: [J] = []
        var number = 4_000
        for tool in Tool.allCases where tool.isShape {
            number += 1
            for (frame, rotation) in [(Rect(x: 10, y: 20, width: 100, height: 70), 0.0), (Rect(x: 10, y: 20, width: 160, height: 60), 0.4)] {
                var element = Fixture.element(number, tool, frame: frame)
                element.rotation = rotation
                records.append(["tool": .string(tool.rawValue), "frame": frame.json, "rotation": .double(rotation),
                                "symbolFrame": element.symbolFrame.json, "anchorLocations": element.anchorLocations.json,
                                "anchorPoints": element.anchorLocations.map { element.anchorPoint($0) }.json])
            }
        }
        try corpus.lines("geometry/anchors.jsonl", records)
    }

    static func snapping(_ corpus: Corpus) throws {
        var records: [J] = []
        let start = Point(100, 100)
        for step in 0..<144 {
            let angle = Double(step) * 2.5 * .pi / 180
            let point = start + Point(cos(angle), sin(angle)) * 80
            records.append(["constraint": "snappedLineEnd", "start": start.json, "point": point.json,
                            "result": DragConstraint.snappedLineEnd(from: start, to: point).json])
        }
        for point in [Point(100, 100), Point(180, 100), Point(100, 20), Point(141.4213562373095, 141.4213562373095)] {
            records.append(["constraint": "snappedLineEnd", "start": start.json, "point": point.json,
                            "result": DragConstraint.snappedLineEnd(from: start, to: point).json])
        }
        for point in [Point(150, 120), Point(60, 170), Point(30, 40), Point(130, 20), Point(100, 160), Point(100, 100)] {
            records.append(["constraint": "squareEnd", "start": start.json, "point": point.json,
                            "result": DragConstraint.squareEnd(from: start, to: point).json])
            for tool in Tool.systemDesignShapes {
                let ratio = tool.systemShapeAspectRatio!
                records.append(["constraint": "aspectEnd", "tool": .string(tool.rawValue), "ratio": .double(ratio), "start": start.json,
                                "point": point.json, "result": DragConstraint.aspectEnd(from: start, to: point, ratio: ratio).json])
            }
        }
        try corpus.lines("geometry/snapping.jsonl", records)
    }

    static func viewports(_ corpus: Corpus) throws {
        var records: [J] = []
        let displays = [Rect(x: 0, y: 0, width: 1512, height: 982), Rect(x: -1920, y: 0, width: 1920, height: 1080)]
        func viewport(_ value: Viewport) -> J {
            let probes = [value.output.origin, value.output.center, Point(value.output.maxX, value.output.maxY)]
            return ["output": value.output.json, "source": value.source.json,
                    "sourcePoints": probes.map { value.sourcePoint(for: $0) }.json,
                    "outputPoints": [value.source.origin, value.source.center].map { value.outputPoint(for: $0) }.json]
        }
        for display in displays {
            for factor in [1, 1.5, 2, 3.7, 16, 0.5, 16.5, .nan] {
                for focus in [display.center, display.origin, Point(display.maxX, display.maxY), Point(display.x + 10, display.maxY - 300)] {
                    records.append(["call": "zoom", "display": display.json, "factor": .double(factor), "focus": focus.json,
                                    "result": outcome { viewport(try Viewport.zoom(display: display, factor: factor, focus: focus)) }])
                    records.append(["call": "cursorAnchored", "display": display.json, "factor": .double(factor), "cursor": focus.json,
                                    "result": outcome { viewport(try Viewport.cursorAnchored(display: display, factor: factor, cursor: focus)) }])
                }
            }
        }
        let zoomed = try Viewport.zoom(display: displays[0], factor: 2, focus: Point(400, 300))
        for preset in ShareRegionPreset.allCases {
            for display in displays {
                records.append(["call": "sharingRegion", "preset": .string(preset.rawValue), "title": .string(preset.title), "display": display.json,
                                "result": outcome {
                                    let region = try SharingRegion(preset: preset, displayBounds: display)
                                    return ["bounds": region.bounds.json, "sourceBoundsAtZoom2": region.sourceBounds(through: zoomed).json,
                                            "aspectFitIn1280x720": try region.aspectFit(in: Rect(x: 0, y: 0, width: 1280, height: 720)).json]
                                }])
            }
        }
        for bounds in [Rect(x: 0, y: 0, width: 31.9, height: 100), Rect(x: 0, y: 0, width: 32, height: 32), Rect(x: 1500, y: 0, width: 32, height: 32)] {
            records.append(["call": "sharingRegion", "bounds": bounds.json, "display": displays[0].json,
                            "result": outcome { .value(try SharingRegion(bounds: bounds, displayBounds: displays[0]).bounds) }])
        }
        try corpus.lines("geometry/viewports.jsonl", records)
    }

    static func transforms(_ corpus: Corpus) throws {
        var records: [J] = []
        var labelled = Fixture.element(5_001, .rectangle, frame: Rect(x: 0, y: 0, width: 100, height: 50))
        var labelStyle = Style()
        labelStyle.fontSize = 20
        labelled.label = ShapeLabel(text: "Label", style: labelStyle)
        let pen = Fixture.element(5_002, .pen, points: [Point(0, 0), Point(10, 40), Point(30, 10)])
        let flat = Fixture.element(5_003, .line, points: [Point(0, 5), Point(80, 5)])
        for (element, target) in [(labelled, Rect(x: 10, y: 10, width: 300, height: 60)), (labelled, Rect(x: 0, y: 0, width: 4, height: 2)),
                                  (pen, Rect(x: 100, y: 100, width: 60, height: 20)), (flat, Rect(x: 0, y: 0, width: 160, height: 30))] {
            var resized = element
            resized.resize(to: target)
            records.append(["call": "resize", "element": .value(element), "to": target.json, "frame": resized.frame.json,
                            "points": resized.points.json, "labelFontSize": resized.label.map { .double($0.style.fontSize) } ?? .null,
                            "revision": .uint(resized.revision)])
        }
        var moved = pen
        moved.translate(by: Point(-5.5, 7.25))
        records.append(["call": "translate", "element": .value(pen), "by": Point(-5.5, 7.25).json, "frame": moved.frame.json, "points": moved.points.json])
        for tool in Tool.systemDesignShapes {
            for frame in [Rect(x: 0, y: 0, width: 100, height: 100), Rect(x: 0, y: 0, width: 200, height: 50), Rect(x: 0, y: 0, width: 30, height: 120)] {
                records.append(["call": "symbolFrame", "tool": .string(tool.rawValue), "frame": frame.json,
                                "result": Fixture.element(5_100, tool, frame: frame).symbolFrame.json])
            }
        }
        let a = Fixture.element(5_201, .rectangle, frame: Rect(x: 0, y: 0, width: 100, height: 50)) { $0.rotation = 0.5 }
        let b = Fixture.element(5_202, .ellipse, frame: Rect(x: 150, y: 80, width: 40, height: 40))
        let locked = Fixture.element(5_203, .rectangle, frame: Rect(x: -300, y: 0, width: 10, height: 10)) { $0.locked = true }
        let cover = Fixture.element(5_204, .cover, frame: Rect(x: 0, y: 200, width: 30, height: 30))
        for (name, elements) in [("rotated and plain", [a, b]), ("with a locked object", [a, b, locked]), ("with a cover", [a, cover]),
                                 ("one object", [a]), ("only locked objects", [locked, Fixture.element(5_205, .text, frame: .zero) { $0.locked = true }])] {
            let selection = SelectionGeometry(elements: elements, selection: Set(elements.map(\.id)))
            records.append(["call": "selectionGeometry", "case": .string(name), "elements": .value(elements),
                            "result": selection.map { ["bounds": $0.bounds.json, "canRotate": .bool($0.canRotate), "corners": $0.corners.json] } ?? .null])
        }
        try corpus.lines("geometry/transforms.jsonl", records)
    }

    static func axisTicks(_ corpus: Corpus) throws {
        var records: [J] = []
        for frame in [Rect(x: 10, y: 20, width: 100, height: 70), Rect(x: 0, y: 0, width: 333, height: 17)] {
            for count in [nil, 0, 1, 2, 5, 24] as [Int?] {
                for negative in [nil, false, true] as [Bool?] {
                    var style = Style.defaults(for: .axes)
                    style.axisTickCount = count
                    style.negativeAxes = negative
                    let positions = AxisTickGeometry.positions(in: frame, style: style)
                    records.append(["frame": frame.json, "axisTickCount": count.map { .int($0) } ?? .null,
                                    "negativeAxes": negative.map { .bool($0) } ?? .null,
                                    "horizontal": positions.horizontal.json, "vertical": positions.vertical.json])
                }
            }
        }
        try corpus.lines("geometry/axis-ticks.jsonl", records)
    }

    static func pointer(_ corpus: Corpus) throws {
        var records: [J] = []
        for shape in CursorHighlightSettings.Shape.allCases {
            for frame in [CGRect(x: 0, y: 0, width: 36, height: 36), CGRect(x: 10, y: 20, width: 50, height: 40)] {
                records.append(["shape": .string(shape.rawValue), "label": .string(shape.label), "frame": frame.json,
                                "path": PathRecord.summary(CursorHighlightRenderer.path(for: shape, in: frame))])
            }
        }
        var visibility: [J] = []
        for mode in CursorHighlightSettings.Visibility.allCases {
            var settings = CursorHighlightSettings()
            settings.visibility = mode
            for (now, movement, click) in [(10.0, nil, nil), (10.0, 9.3, nil), (10.0, 9.25, nil), (10.0, nil, 9.6), (10.0, nil, 9.55),
                                          (10.0, 10.5, nil), (10.0, 9.9, 9.0)] as [(Double, Double?, Double?)] {
                visibility.append(["visibility": .string(mode.rawValue), "now": .double(now),
                                   "lastMovement": movement.map { .double($0) } ?? .null, "lastClick": click.map { .double($0) } ?? .null,
                                   "visible": .bool(settings.isVisible(now: now, lastMovement: movement, lastClick: click))])
            }
        }
        var effects: [J] = []
        for show in [true, false] {
            for effect in [nil] + CursorHighlightSettings.ClickEffect.allCases.map(Optional.some) {
                var settings = CursorHighlightSettings()
                settings.showClicks = show
                settings.clickEffect = effect
                effects.append(["showClicks": .bool(show), "clickEffect": effect.map { .string($0.rawValue) } ?? .null,
                                "resolved": .string(settings.resolvedClickEffect.rawValue)])
            }
        }
        try corpus.lines("geometry/pointer-shapes.jsonl", records)
        try corpus.json("geometry/pointer-rules.json", [
            "defaults": .value(CursorHighlightSettings()), "clickDuration": .double(CursorHighlightSettings.clickDuration),
            "visibility": .array(visibility), "clickEffects": .array(effects),
            "spotlightFeather": .array([50.0, 100, 130, 200, 256, 350].map { radius in
                ["radius": .double(radius), "feather": .double(min(64, max(12, radius * 0.25)))]
            })
        ])
    }

    static func text(_ corpus: Corpus) throws {
        var records: [J] = []
        let strings = ["", "A", "Hello, board", "Two\nlines", "Ends with a line break\n", "שלום", "👩🏽‍💻 ✓", String(repeating: "W", count: 40)]
        for text in strings {
            for (size, bold, name) in [(28.0, false, "system"), (13, false, "system"), (64, true, "system"), (20, false, "monospace")] {
                var style = Style()
                style.fontSize = size; style.bold = bold; style.fontName = name
                let font = NativeTextFont.resolve(style)
                records.append(["text": .string(text), "fontSize": .double(size), "bold": .bool(bold), "fontName": .string(name),
                                "fontPostScriptName": .string(font.fontName), "ascender": .double(font.ascender),
                                "descender": .double(font.descender), "leading": .double(font.leading),
                                "contentSize": NativeTextFont.contentSize(text, font: font).json])
            }
        }
        for (tool, frame, label) in [(Tool.rectangle, Rect(x: 0, y: 0, width: 200, height: 100), "Service"),
                                     (.rectangle, Rect(x: 0, y: 0, width: 40, height: 20), "A much longer label"),
                                     (.server, Rect(x: 0, y: 0, width: 60, height: 60), "API\nGateway")] {
            let owner = Fixture.element(6_000, tool, frame: frame) { $0.label = ShapeLabel(text: label) }
            records.append(["owner": .value(owner), "labelFrame": ShapeLabelLayout.textElement(for: owner)?.frame.json ?? .null,
                            "fittedOwnerFrame": ShapeLabelLayout.fittedOwnerFrame(owner, label: owner.label!).json])
        }
        try corpus.lines("geometry/text.jsonl", records)
    }
}
