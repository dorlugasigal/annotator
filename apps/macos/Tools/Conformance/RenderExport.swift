import AnnotationCore
import AppKit
import Foundation
@testable import PresentationRendering

/// Scenes drawn by PNGExporter at 1x and 2x on a transparent background, with the board that holds them.
@MainActor
enum RenderExport {
    struct Scene {
        let name: String
        let note: String
        let size: CGSize
        let elements: [Element]
        var fontDependent = false
        var interpolationDependent = false
        var darkVariant = false
    }

    static func run(_ corpus: Corpus) throws {
        corpus.describe("render", "Scenes rendered with PNGExporter (SceneRenderer into an sRGB premultiplied RGBA bitmap, transparent background) at 1x and 2x in the light appearance, plus the board file with the same elements. Scenes marked fontDependent use macOS system fonts; interpolationDependent ones scale a bitmap.")
        var records: [J] = []
        for scene in try scenes() {
            let board = try DrawingFile(document: CanvasDocument(elements: scene.elements)).data()
            corpus.data("render/\(scene.name).board.json", board)
            var images: [String: J] = [:]
            for (appearanceName, appearance) in [("light", NSAppearance.Name.aqua), ("dark", .darkAqua)] where appearanceName == "light" || scene.darkVariant {
                for scale in [1.0, 2.0] {
                    var rendered: Result<CGImage, Error> = .failure(ExportFailure("Not rendered."))
                    NSAppearance(named: appearance)!.performAsCurrentDrawingAppearance {
                        rendered = Result { try PNGExporter.image(elements: scene.elements, size: scene.size, scale: scale) }
                    }
                    let file = "\(scene.name)\(appearanceName == "dark" ? "-dark" : "")@\(Int(scale))x.png"
                    corpus.data("render/\(file)", try PNGExporter.encode(try rendered.get()))
                    images["\(appearanceName) \(Int(scale))x"] = .string(file)
                }
            }
            records.append(["scene": .string(scene.name), "note": .string(scene.note), "board": .string("\(scene.name).board.json"),
                            "size": scene.size.json, "images": .object(images), "fontDependent": .bool(scene.fontDependent),
                            "interpolationDependent": .bool(scene.interpolationDependent)])
        }
        try corpus.lines("render/index.jsonl", records)
    }

    private static func grid(_ tools: [Tool], columns: Int, cell: CGSize, start: Int, inset: Double = 10,
                             _ style: (Tool, Int) -> Style = { tool, _ in Style.defaults(for: tool) }) -> [Element] {
        tools.enumerated().map { index, tool in
            let frame = Rect(x: Double(index % columns) * cell.width + inset, y: Double(index / columns) * cell.height + inset,
                             width: cell.width - inset * 2, height: cell.height - inset * 2)
            return Fixture.element(start + index, tool, frame: frame, style: style(tool, index))
        }
    }

    static func scenes() throws -> [Scene] {
        var scenes: [Scene] = []
        let wave = { (y: Double) in Samples.wave(from: Point(20, y), count: 28, step: 8, amplitude: 12) }
        scenes.append(Scene(name: "strokes", note: "Pen with and without pressure, highlighter, rough pen, dashed and dotted lines, single points.",
                            size: CGSize(width: 360, height: 240), elements: [
            Fixture.element(9_001, .pen, points: wave(30)),
            Fixture.element(9_002, .pen, points: wave(75), style: Fixture.style(.pen) { $0.width = 8; $0.pressure = true; $0.color = .blue }) {
                $0.pressures = (0..<28).map { 0.1 + 0.9 * Double($0) / 27 }
            },
            Fixture.element(9_003, .highlighter, points: wave(120)),
            Fixture.element(9_004, .pen, points: wave(165), style: Fixture.style(.pen) { $0.roughness = 2; $0.color = .ink }),
            Fixture.element(9_005, .line, points: [Point(260, 20), Point(340, 60)], style: Fixture.style(.line) { $0.pattern = .dashed }),
            Fixture.element(9_006, .line, points: [Point(260, 80), Point(340, 120)], style: Fixture.style(.line) { $0.pattern = .dotted; $0.width = 4 }),
            Fixture.element(9_007, .pen, points: [Point(280, 170)], style: Fixture.style(.pen) { $0.width = 10 }),
            Fixture.element(9_008, .highlighter, points: [Point(320, 170)]),
            Fixture.element(9_009, .pen, points: wave(210), style: Fixture.style(.pen) { $0.opacity = 0.5; $0.width = 6 })
        ]))
        let heads = ArrowHead.allCases.filter { $0 != .none }
        var arrows = heads.enumerated().map { index, kind in
            let x = Double(index % 3) * 130 + 20, y = Double(index / 3) * 46 + 24
            return Fixture.element(9_100 + index, .arrow, points: [Point(x, y), Point(x + 96, y + 8)], style: Fixture.style(.arrow) {
                $0.startHead = kind; $0.endHead = kind; $0.headScale = 1; $0.color = .ink
            })
        }
        arrows.append(Fixture.element(9_120, .arrow, points: [Point(20, 260), Point(90, 300), Point(170, 240), Point(240, 290)],
                                      style: Fixture.style(.arrow) { $0.curved = true; $0.startHead = .circle; $0.width = 3 }))
        arrows.append(Fixture.element(9_121, .arrow, points: [Point(280, 300), Point(380, 250)],
                                      style: Fixture.style(.arrow) { $0.width = 8; $0.headScale = 2; $0.endHead = .filled }))
        scenes.append(Scene(name: "arrowheads", note: "Every arrowhead at both ends, a curved arrow and a wide arrow.",
                            size: CGSize(width: 400, height: 320), elements: arrows))
        scenes.append(Scene(name: "shapes", note: "Every basic and diagram shape with default style; rectangle and diamond also rounded.",
                            size: CGSize(width: 420, height: 240), elements: grid(
            [.rectangle, .rectangle, .ellipse, .diamond, .diamond, .triangle, .hexagon, .star, .database, .queue, .person, .grid, .axes, .axes],
            columns: 5, cell: CGSize(width: 84, height: 80), start: 9_200) { tool, index in
                var style = Style.defaults(for: tool)
                if index == 1 { style.cornerRadius = 12 }
                if index == 4 { style.cornerRadius = 10 }
                if tool == .grid { style.gridRows = 3; style.gridColumns = 4 }
                if index == 12 { style.axisTickCount = 4 }
                if index == 13 { style.axisTickCount = 3; style.negativeAxes = true }
                return style
            }))
        scenes.append(Scene(name: "fills", note: "Solid, hatch and crosshatch fills, gradient fills and gradient strokes.",
                            size: CGSize(width: 420, height: 240), elements: grid(
            [.rectangle, .rectangle, .rectangle, .ellipse, .diamond, .hexagon, .star, .rectangle, .ellipse, .database],
            columns: 5, cell: CGSize(width: 84, height: 90), start: 9_300) { tool, index in
                var style = Style.defaults(for: tool)
                style.fill = RGBA(0.17, 0.45, 0.88, index == 5 ? 1 : 0.4)
                style.fillPattern = [1, 3, 6].contains(index) ? .hatch : [2, 4].contains(index) ? .crosshatch : .solid
                if index == 6 { style.width = 1 }
                if index == 7 { style.setGradient(GradientPreset.lagoon.gradient, fill: true) }
                if index == 8 { style.setGradient(GradientPreset.sunset.gradient, fill: true); style.setGradient(GradientPreset.dusk.gradient, fill: false) }
                if index == 9 { style.width = 5; style.setGradient(GradientPreset.spectrum.gradient, fill: false) }
                return style
            } + [Fixture.element(9_320, .pen, points: Samples.wave(from: Point(20, 200), count: 46, step: 8, amplitude: 14),
                                 style: Fixture.style(.pen) { $0.width = 8; $0.setGradient(GradientPreset.citrus.gradient, fill: false) })]))
        scenes.append(Scene(name: "system-shapes", note: "The 14 system-design shapes.", size: CGSize(width: 448, height: 128),
                            elements: grid(Tool.systemDesignShapes, columns: 7, cell: CGSize(width: 64, height: 64), start: 9_400) { tool, _ in
            var style = Style.defaults(for: tool); style.color = .ink; style.width = 2
            return style
        }))
        let roughTools: [Tool] = [.rectangle, .ellipse, .diamond, .hexagon, .rectangle]
        var rough: [Element] = []
        for (row, roughness) in [1.0, 3, 6].enumerated() {
            rough += grid(roughTools, columns: 5, cell: CGSize(width: 84, height: 80), start: 9_500 + row * 10) { tool, index in
                var style = Style.defaults(for: tool)
                style.roughness = roughness
                if index == 4 { style.cornerRadius = 14; style.fill = RGBA(0.96, 0.89, 0.22, 0.6); style.fillPattern = .hatch }
                return style
            }.map { element in
                var moved = element
                moved.translate(by: Point(0, Double(row) * 80))
                moved.revision = 0; moved.geometryToken = element.geometryToken; moved.appendEpoch = element.appendEpoch
                return moved
            }
            rough.append(Fixture.element(9_540 + row, .arrow, points: [Point(430, 20 + Double(row) * 80), Point(520, 60 + Double(row) * 80)],
                                         style: Fixture.style(.arrow) { $0.roughness = roughness }))
        }
        scenes.append(Scene(name: "rough", note: "Shapes and an arrow at roughness 1, 3 and 6; rough geometry comes from each element's seed.",
                            size: CGSize(width: 540, height: 250), elements: rough))
        scenes.append(Scene(name: "opacity-rotation", note: "Overlapping translucent fills, object opacity and rotation.",
                            size: CGSize(width: 360, height: 240), elements: [
            Fixture.element(9_601, .rectangle, frame: Rect(x: 20, y: 20, width: 140, height: 100),
                            style: Fixture.style(.rectangle) { $0.fill = RGBA(0.94, 0.27, 0.22, 1); $0.opacity = 0.5 }),
            Fixture.element(9_602, .ellipse, frame: Rect(x: 90, y: 60, width: 140, height: 100),
                            style: Fixture.style(.ellipse) { $0.fill = RGBA(0.17, 0.45, 0.88, 0.7); $0.color = .blue }),
            Fixture.element(9_603, .rectangle, frame: Rect(x: 240, y: 40, width: 90, height: 50)) { $0.rotation = .pi / 6 },
            Fixture.element(9_604, .hexagon, frame: Rect(x: 240, y: 140, width: 90, height: 70),
                            style: Fixture.style(.hexagon) { $0.fill = RGBA(0.28, 0.69, 0.4, 0.5); $0.opacity = 0.8 }) { $0.rotation = -0.4 },
            Fixture.element(9_605, .star, frame: Rect(x: 40, y: 150, width: 70, height: 70)) { $0.rotation = 0.2 }
        ]))
        var images: [Element] = []
        for (index, (frame, rotation, opacity)) in [(Rect(x: 10, y: 10, width: 4, height: 3), 0.0, 1.0),
                                                   (Rect(x: 30, y: 20, width: 120, height: 90), 0.3, 0.9),
                                                   (Rect(x: 170, y: 40, width: 60, height: 60), 0, 0.5)].enumerated() {
            var image = Fixture.element(9_700 + index, .image, frame: frame, style: Fixture.style(.image) { $0.opacity = opacity })
            image.imagePNG = try Fixture.smallPNG()
            image.rotation = rotation
            images.append(image)
        }
        scenes.append(Scene(name: "image", note: "A 4 x 3 pixel PNG at its size, scaled up and rotated, and stretched at half opacity.",
                            size: CGSize(width: 240, height: 140), elements: images, interpolationDependent: true))
        scenes.append(Scene(name: "protection", note: "Without screen pixels, covers and pixel effects draw as black rectangles on the integral frame.",
                            size: CGSize(width: 300, height: 140), elements: [
            Fixture.element(9_801, .cover, frame: Rect(x: 10.3, y: 10.6, width: 60.2, height: 40.5)),
            Fixture.element(9_802, .blur, frame: Rect(x: 90, y: 10, width: 60, height: 40)),
            Fixture.element(9_803, .pixelate, frame: Rect(x: 170.5, y: 10.5, width: 60, height: 40)),
            Fixture.element(9_804, .distort, frame: Rect(x: 10, y: 80.25, width: 280, height: 40.5))
        ]))
        scenes.append(Scene(name: "text-and-measurements", note: "Text, counters, rulers and shape labels. Uses macOS system fonts; the dark variant changes the ruler label background.",
                            size: CGSize(width: 420, height: 300), elements: [
            Fixture.element(9_901, .text, frame: Rect(x: 10, y: 10, width: 200, height: 40), text: "System 28",
                            style: Fixture.style(.text) { $0.color = .ink }),
            Fixture.element(9_902, .text, frame: Rect(x: 220, y: 10, width: 190, height: 30), text: "Bold 20, right",
                            style: Fixture.style(.text) { $0.fontSize = 20; $0.bold = true; $0.alignment = .right; $0.color = .blue }),
            Fixture.element(9_903, .text, frame: Rect(x: 10, y: 60, width: 200, height: 50), text: "Mono 16\ntwo lines",
                            style: Fixture.style(.text) { $0.fontSize = 16; $0.fontName = "monospace"; $0.color = .ink }),
            Fixture.element(9_904, .text, frame: Rect(x: 220, y: 60, width: 190, height: 40), text: "Gradient centered",
                            style: Fixture.style(.text) { $0.fontSize = 18; $0.alignment = .center; $0.setGradient(GradientPreset.sunset.gradient, fill: false) }),
            Fixture.element(9_905, .counter, frame: Rect(x: 10, y: 120, width: 36, height: 36), text: "1"),
            Fixture.element(9_906, .counter, frame: Rect(x: 60, y: 120, width: 48, height: 48), text: "12",
                            style: Fixture.style(.counter) { $0.color = .blue; $0.opacity = 0.5 }),
            Fixture.element(9_907, .ruler, points: [Point(130, 150), Point(300, 150)], text: "170 px"),
            Fixture.element(9_908, .ruler, points: [Point(320, 120), Point(400, 200)], text: "113 px", style: Fixture.style(.ruler) { $0.opacity = 0.6 }),
            Fixture.element(9_909, .rectangle, frame: Rect(x: 10, y: 200, width: 150, height: 80)) { $0.label = ShapeLabel(text: "Service") },
            Fixture.element(9_910, .server, frame: Rect(x: 180, y: 200, width: 80, height: 80),
                            style: Fixture.style(.server) { $0.color = .ink; $0.setGradient(GradientPreset.ocean.gradient, fill: false) }) {
                $0.label = ShapeLabel(text: "API", followsStroke: true)
            },
            Fixture.element(9_911, .ellipse, frame: Rect(x: 280, y: 210, width: 120, height: 70),
                            style: Fixture.style(.ellipse) { $0.fill = RGBA(0.12, 0.15, 0.16, 0.9) }) {
                var style = Style(); style.alignment = .center; style.color = .white; style.fontSize = 16
                $0.label = ShapeLabel(text: "Dark fill", style: style)
            }
        ], fontDependent: true, darkVariant: true))
        return scenes
    }
}
