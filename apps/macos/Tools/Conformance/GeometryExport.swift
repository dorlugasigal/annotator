import AnnotationCore
import CoreGraphics
import Foundation
@testable import PresentationRendering

/// Pure geometry: shape and stroke paths from the renderer, arrowheads, polygons, connectors, anchors,
/// drag constraints, viewports, transforms, axis ticks, pointer shapes and font-dependent text sizes.
@MainActor
enum GeometryExport {
    static func run(_ corpus: Corpus) throws {
        corpus.describe("geometry", "Path elements from SceneRenderer for every shape and stroke (rough ones from the element seed), arrowheads, polygons, rounded diamonds, connector curves, anchors, drag constraints, viewports, sharing regions, transforms, axis ticks and pointer shapes. text.jsonl depends on macOS fonts.")
        try shapes(corpus)
        try strokes(corpus)
        try arrowheads(corpus)
        try polygons(corpus)
        try connectors(corpus)
        try anchors(corpus)
        try snapping(corpus)
        try viewports(corpus)
        try transforms(corpus)
        try axisTicks(corpus)
        try pointer(corpus)
        try text(corpus)
    }

    private static func pathRecord(_ element: Element, _ renderer: SceneRenderer, _ variant: String) -> J {
        ["variant": .string(variant), "element": .value(element), "path": PathRecord.summary(renderer.path(for: element))]
    }

    private static func shapes(_ corpus: Corpus) throws {
        let renderer = SceneRenderer()
        let frame = Rect(x: 10, y: 20, width: 100, height: 70)
        var records: [J] = []
        var number = 1_000
        for tool in Tool.allCases where tool.isShape {
            var variants: [(String, (inout Style) -> Void)] = [("plain", { _ in })]
            if tool == .rectangle || tool == .diamond {
                variants += [("corner radius 12", { $0.cornerRadius = 12 }), ("corner radius 500", { $0.cornerRadius = 500 })]
            }
            if tool == .grid { variants.append(("3 rows and 5 columns", { $0.gridRows = 3; $0.gridColumns = 5 })) }
            if tool == .axes {
                variants += [("4 ticks", { $0.axisTickCount = 4 }), ("no ticks", { $0.axisTickCount = 0 }),
                             ("negative axes with 3 ticks", { $0.negativeAxes = true; $0.axisTickCount = 3 })]
            }
            let system = Tool.systemDesignShapes.contains(tool)
            for (variant, change) in variants {
                for roughness in system ? [0, 2.5] : [0, 1, 2.5, 6] {
                    number += 1
                    var style = Style.defaults(for: tool)
                    change(&style)
                    style.roughness = roughness
                    records.append(pathRecord(Fixture.element(number, tool, frame: frame, style: style), renderer, "\(variant), roughness \(roughness)"))
                }
            }
        }
        number += 1
        records.append(pathRecord(Fixture.element(number, .ellipse, frame: Rect(x: 0, y: 0, width: 300, height: 4),
                                                  style: Fixture.style(.ellipse) { $0.roughness = 12 }), renderer, "thin ellipse, roughness 12"))
        number += 1
        records.append(pathRecord(Fixture.element(number, .rectangle, frame: Rect(x: 5, y: 5, width: 0, height: 0),
                                                  style: Fixture.style(.rectangle) { $0.roughness = 3 }), renderer, "empty rectangle, roughness 3"))
        try corpus.lines("geometry/shapes.jsonl", records)
    }

    private static func strokes(_ corpus: Corpus) throws {
        let renderer = SceneRenderer()
        let wave = Samples.wave(from: Point(0, 50), count: 20, step: 10, amplitude: 30)
        let corner = [Point(0, 0), Point(60, 80), Point(140, -20), Point(200, 40)]
        let cases: [(String, Element)] = [
            ("pen", Fixture.element(2_001, .pen, points: wave)),
            ("pen, roughness 2", Fixture.element(2_002, .pen, points: wave, style: Fixture.style(.pen) { $0.roughness = 2 })),
            ("pen, roughness 12", Fixture.element(2_003, .pen, points: wave, style: Fixture.style(.pen) { $0.roughness = 12 })),
            ("highlighter, roughness 1", Fixture.element(2_004, .highlighter, points: wave, style: Fixture.style(.highlighter) { $0.roughness = 1 })),
            ("single point", Fixture.element(2_005, .pen, points: [Point(5, 5)])),
            ("line", Fixture.element(2_006, .line, points: [Point(0, 0), Point(200, 50)])),
            ("line through four points", Fixture.element(2_007, .line, points: corner)),
            ("curved line", Fixture.element(2_008, .line, points: corner, style: Fixture.style(.line) { $0.curved = true })),
            ("curved line through three points", Fixture.element(2_009, .line, points: Array(corner.prefix(3)), style: Fixture.style(.line) { $0.curved = true })),
            ("curved line through two points", Fixture.element(2_010, .line, points: Array(corner.prefix(2)), style: Fixture.style(.line) { $0.curved = true })),
            ("line, roughness 2", Fixture.element(2_011, .line, points: [Point(0, 0), Point(200, 50)], style: Fixture.style(.line) { $0.roughness = 2 })),
            ("arrow, roughness 6", Fixture.element(2_012, .arrow, points: [Point(0, 0), Point(200, 50)], style: Fixture.style(.arrow) { $0.roughness = 6 })),
            ("curved arrow, roughness 2", Fixture.element(2_013, .arrow, points: corner, style: Fixture.style(.arrow) { $0.curved = true; $0.roughness = 2 }))
        ]
        try corpus.lines("geometry/strokes.jsonl", cases.map { pathRecord($0.1, renderer, $0.0) })
    }

    static func headRecord(_ head: ArrowHeadGeometry?) -> J {
        guard let head else { return .null }
        return [
            "kind": .string(head.kind.rawValue), "tip": head.tip.json, "direction": head.direction.json, "length": .double(head.length),
            "side": head.side.json, "base": head.base.json, "left": head.left.json, "right": head.right.json,
            "polygon": head.polygon.json, "square": head.square.json, "hollow": .bool(head.hollow),
            "relationshipSegments": .array(head.relationshipSegments.map { [$0.0.json, $0.1.json] }),
            "relationshipCircle": head.relationshipCircle.map { ["center": $0.center.json, "radius": .double($0.radius)] } ?? .null
        ]
    }

    private static func arrowheads(_ corpus: Corpus) throws {
        let configs: [(String, [Point], Bool, Double, Double?)] = [
            ("straight", [Point(20, 30), Point(180, 90)], false, 3, 0.75),
            ("short, limited to 70% of the segment", [Point(20, 30), Point(40, 34)], false, 3, 1),
            ("wide stroke, large head", [Point(0, 0), Point(200, 0)], false, 8, 2),
            ("no head scale", [Point(0, 0), Point(0, 120)], false, 2, nil),
            ("curved", [Point(0, 0), Point(60, 80), Point(140, -20), Point(200, 40)], true, 3, 1),
            ("curved with a sharp last bend", [Point(0, 0), Point(100, 0), Point(104, 90)], true, 3, 1),
            ("repeated end points", [Point(0, 0), Point(100, 0), Point(100, 0.0005)], false, 3, 1),
            ("all points equal", [Point(5, 5), Point(5, 5)], false, 3, 1)
        ]
        var records: [J] = []
        var number = 3_000
        for kind in ArrowHead.allCases where kind != .none {
            for (name, points, curved, width, headScale) in configs {
                number += 1
                let element = Fixture.element(number, .arrow, points: points, style: Fixture.style(.arrow) {
                    $0.startHead = kind; $0.endHead = kind; $0.curved = curved; $0.width = width; $0.headScale = headScale
                })
                records.append(["kind": .string(kind.rawValue), "label": .string(kind.label), "config": .string(name),
                                "element": .value(element),
                                "start": headRecord(ArrowHeadGeometry.make(for: element, atStart: true)),
                                "end": headRecord(ArrowHeadGeometry.make(for: element, atStart: false))])
            }
        }
        try corpus.lines("geometry/arrowheads.jsonl", records)
        try corpus.json("geometry/arrowhead-choices.json", [
            "shapeChoices": .array(ArrowHead.shapeChoices.map { .string($0.rawValue) }),
            "relationshipChoices": .array(ArrowHead.relationshipChoices.map { .string($0.rawValue) })
        ])
    }

    private static func polygons(_ corpus: Corpus) throws {
        let frames = [Rect(x: 0, y: 0, width: 1, height: 1), Rect(x: 10, y: 20, width: 100, height: 70),
                      Rect(x: -5, y: -5, width: 0, height: 10), Rect(x: 0.5, y: 0.25, width: 33.3, height: 77.7)]
        var records: [J] = []
        for kind in [("triangle", PolygonShapeKind.triangle), ("hexagon", .hexagon), ("star", .star)] {
            for frame in frames {
                records.append(["polygon": .string(kind.0), "frame": frame.json, "vertices": PolygonShape.vertices(kind.1, in: frame).json])
            }
        }
        for frame in frames + [Rect(x: 0, y: 0, width: 0, height: 0), Rect(x: 0, y: 0, width: 200, height: 20)] {
            for radius in [0.0, 5, 12, 40, 1_000, -3] {
                let corners = RoundedDiamond.corners(frame: frame, radius: radius)
                records.append(["roundedDiamond": .double(radius), "frame": frame.json,
                                "corners": .array(corners.map { ["entry": $0.entry.json, "control": $0.control.json, "exit": $0.exit.json] }),
                                "path": PathRecord.elements(DiamondGeometry.path(frame: frame, cornerRadius: radius))])
            }
        }
        try corpus.lines("geometry/polygons.jsonl", records)
    }

    private static func connectors(_ corpus: Corpus) throws {
        let sets: [[Point]] = [
            [Point(0, 0), Point(100, 0)],
            [Point(0, 0), Point(50, 80), Point(100, 0)],
            [Point(0, 0), Point(60, 80), Point(140, -20), Point(200, 40)],
            [Point(0, 0), Point(0, 0), Point(50, 50), Point(50, 50), Point(100, 0)],
            [Point(10, 10), Point(30, 90), Point(70, 20), Point(110, 95), Point(160, 5), Point(220, 60)]
        ]
        let records: [J] = sets.map { points in
            func curve(_ value: ConnectorCurve) -> J {
                ["start": value.start.json, "firstControl": value.firstControl.json, "secondControl": value.secondControl.json,
                 "end": value.end.json, "bounds": value.bounds.json,
                 "samples": .array((0...8).map { value.point(at: Double($0) / 8).json })]
            }
            return ["points": points.json, "curves": .array(ConnectorGeometry.curves(through: points).map(curve)),
                    "startCurve": ConnectorGeometry.endpointCurve(through: points, atStart: true).map(curve) ?? .null,
                    "endCurve": ConnectorGeometry.endpointCurve(through: points, atStart: false).map(curve) ?? .null]
        }
        try corpus.lines("geometry/connectors.jsonl", records)
    }
}
