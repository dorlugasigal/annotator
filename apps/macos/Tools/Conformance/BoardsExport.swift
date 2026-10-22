import AnnotationCore
import Foundation

/// Board files: valid files with the bytes the app writes back after opening them, and invalid files
/// with the exact error.
@MainActor
enum BoardsExport {
    static func run(_ corpus: Corpus) throws {
        corpus.describe("boards", "Board files. valid: the input and the bytes DrawingFile writes after reading it. invalid: the input and the error DrawingFile.read throws.")
        try valid(corpus)
        try invalid(corpus)
    }

    private static func appBytes(_ elements: [Element]) throws -> Data {
        try DrawingFile(document: CanvasDocument(elements: elements)).data()
    }

    // MARK: Valid boards

    private static func allTools() -> [Element] {
        var elements: [Element] = []
        var number = 100
        let tools = Tool.allCases.filter { $0 != .select && $0 != .eraser }
        for (index, tool) in tools.enumerated() {
            number += 1
            let column = Double(index % 8), row = Double(index / 8)
            let frame = Rect(x: 20 + column * 130, y: 20 + row * 110, width: 100, height: 80)
            var style = Style.defaults(for: tool)
            var points: [Point] = []
            var text = ""
            switch tool {
            case .pen, .highlighter: points = Samples.wave(from: frame.origin + Point(0, 40), count: 12, step: 9, amplitude: 20)
            case .line, .ruler: points = [frame.origin, Point(frame.maxX, frame.maxY)]
            case .arrow:
                points = [frame.origin, Point(frame.x + 30, frame.maxY), Point(frame.x + 70, frame.y), Point(frame.maxX, frame.maxY)]
                style.curved = true; style.startHead = .circleOutline; style.endHead = .filled
            case .text: text = "Text ✓ é"
            case .counter: text = "12"
            case .grid: style.gridRows = 3; style.gridColumns = 4
            case .axes: style.negativeAxes = true; style.axisTickCount = 4
            case .blur, .pixelate, .distort: style.effectAmount = 30
            default: break
            }
            if tool == .ruler { text = "128 px" }
            if tool.isShape && index % 3 == 0 { style.roughness = 2 }
            if tool.supportsFill && index % 2 == 0 { style.fill = RGBA(0.28, 0.69, 0.4, 0.4); style.fillPattern = index % 4 == 0 ? .crosshatch : .solid }
            var element = Fixture.element(number, tool, frame: frame, points: points, text: text, style: style)
            if tool == .image { element.imagePNG = try? Fixture.smallPNG() }
            if !tool.isProtection && index % 5 == 1 { element.rotation = 0.25 }
            if tool == .rectangle || tool == .ellipse { element.groupID = Fixture.id(900) }
            if tool == .star { element.locked = true }
            if tool == .hexagon { element.label = ShapeLabel(text: "Label") }
            if tool == .server {
                var labelStyle = Style()
                labelStyle.alignment = .center
                element.label = ShapeLabel(text: "API", style: labelStyle, followsStroke: true)
                element.style.strokeGradient = GradientPreset.ocean.gradient
            }
            elements.append(element)
        }
        return elements
    }

    private static func valid(_ corpus: Corpus) throws {
        var cases: [(String, String, Data)] = []
        cases.append(("basic", "Common tools in board format 1, with an arrow bound to two shapes.", try appBytes(Samples.basic())))
        cases.append(("protection", "A cover and the three pixel effects with their strengths.", try appBytes([
            Fixture.element(20, .cover, frame: Rect(x: 20, y: 20, width: 120, height: 60)),
            Fixture.element(21, .blur, frame: Rect(x: 160, y: 20, width: 120, height: 60), style: Fixture.style(.blur) { $0.effectAmount = 30 }),
            Fixture.element(22, .pixelate, frame: Rect(x: 20, y: 100, width: 120, height: 60), style: Fixture.style(.pixelate) { $0.effectAmount = 12 }),
            Fixture.element(23, .distort, frame: Rect(x: 160, y: 100, width: 120, height: 60))
        ])))
        var image = Fixture.element(30, .image, frame: Rect(x: 40, y: 40, width: 120, height: 90), style: Fixture.style(.image) { $0.opacity = 0.9 })
        image.imagePNG = try Fixture.smallPNG()
        image.rotation = 0.3
        cases.append(("image", "A pasted PNG image, rotated and partly transparent.", try appBytes([image])))
        cases.append(("labels", "Shape labels need board format 2.", try appBytes([
            Fixture.element(40, .rectangle, frame: Rect(x: 20, y: 20, width: 160, height: 80)) { $0.label = ShapeLabel(text: "Service") },
            Fixture.element(41, .server, frame: Rect(x: 220, y: 20, width: 90, height: 90)) { $0.label = ShapeLabel(text: "DB\nprimary") },
            Fixture.element(42, .ellipse, frame: Rect(x: 20, y: 140, width: 120, height: 70),
                            style: Fixture.style(.ellipse) { $0.fill = RGBA(0.1, 0.1, 0.1, 0.9) }) {
                var style = Style(); style.alignment = .center; style.color = ShapeLabel.defaultColor(on: RGBA(0.1, 0.1, 0.1, 0.9))
                $0.label = ShapeLabel(text: "Dark fill", style: style)
            }
        ])))
        cases.append(("gradients", "Gradients and labels that follow the stroke need board format 3.", try appBytes([
            Fixture.element(50, .pen, points: Samples.wave(from: Point(20, 60), count: 30, step: 8, amplitude: 25),
                            style: Fixture.style(.pen) { $0.width = 8; $0.setGradient(GradientPreset.sunset.gradient, fill: false) }),
            Fixture.element(51, .rectangle, frame: Rect(x: 280, y: 20, width: 160, height: 100), style: Fixture.style(.rectangle) {
                $0.setFillEnabled(true); $0.setGradient(GradientPreset.lagoon.gradient, fill: true)
            }) { $0.label = ShapeLabel(text: "Matches", style: nil, followsStroke: true) }
        ])))
        cases.append(("all-tools", "Every drawing tool, with a group, a locked shape, rotation, roughness, fills and labels.", try appBytes(allTools())))

        // Files the app would not write itself.
        var stale = Samples.basic().filter { [1, 2, 3].contains(Int($0.id.uuidString.suffix(12))!) }
        stale[2].points = [Point(0, 0), Point(10, 10)]
        stale[2].frame = Rect.enclosing(stale[2].points)
        stale[2].endAnchor = Anchor(elementID: Fixture.id(999), relative: Point(0.5, 0.5))
        cases.append(("stale-bindings", "An arrow whose start is away from its bound shape and whose end is bound to a missing shape. Opening resolves the start and drops the end binding.",
                      try RawBoard.bytes(stale, schema: 1)))
        cases.append(("higher-schema-than-needed", "Board format 3 with no labels or gradients is written back as format 1.",
                      try RawBoard.bytes([Fixture.element(60, .rectangle, frame: Rect(x: 10, y: 10, width: 50, height: 40))], schema: 3)))
        let limits = try limitElements()
        cases.append(("limits", "Values at the edges of their allowed ranges.", try appBytes(limits)))
        let base = String(decoding: try appBytes([Fixture.element(61, .ellipse, frame: Rect(x: 5, y: 5, width: 40, height: 30))]), as: UTF8.self)
        cases.append(("unknown-keys", "Keys from a future version are ignored and not written back.",
                      Data(base.replacingOccurrences(of: #""schemaVersion":1"#, with: #""future":{"a":[1,{"b":null}]},"schemaVersion":1"#)
                        .replacingOccurrences(of: #""tool":"ellipse""#, with: #""tool":"ellipse","wobble":3"#).utf8)))
        cases.append(("utf16-little-endian", "Foundation also reads UTF-16 board files with a byte order mark; the app writes UTF-8.",
                      Data([0xFF, 0xFE]) + base.data(using: .utf16LittleEndian)!))
        cases.append(("pretty-printed", "Whitespace and key order do not matter when reading.", Data(prettyPrinted(base).utf8)))

        var records: [J] = []
        for (name, note, input) in cases {
            let text = String(data: input, encoding: .utf8) ?? String(data: input, encoding: .utf16) ?? ""
            let fresh = FreshIDs(keepingUUIDsIn: text)
            let declared = try JSONDecoder().decode(DrawingFile.self, from: input).schemaVersion
            let document = try DrawingFile.read(input)
            let output = try DrawingFile(document: CanvasDocument(elements: fresh.elements(document.elements))).data()
            let written = try JSONDecoder().decode(DrawingFile.self, from: output).schemaVersion
            corpus.data("boards/valid/\(name).json", input)
            corpus.data("boards/valid/\(name).written.json", output)
            records.append([
                "name": .string(name), "note": .string(note), "file": .string("\(name).json"),
                "written": .string("\(name).written.json"), "declaredSchema": .int(declared), "writtenSchema": .int(written),
                "elementCount": .int(document.elements.count), "writtenEqualsInput": .bool(output == input)
            ])
        }
        try corpus.lines("boards/valid/index.jsonl", records)
    }

    private static func prettyPrinted(_ compact: String) -> String {
        guard let object = try? JSONSerialization.jsonObject(with: Data(compact.utf8)),
              let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted]) else { return compact }
        return String(decoding: data, as: UTF8.self)
    }

    private static func limitElements() throws -> [Element] {
        var gradientEight = Style()
        gradientEight.strokeGradient = ColorGradient(colors: (0..<8).map { RGBA(Double($0) / 7, 0.5, 1 - Double($0) / 7) }, angle: 360)
        return [
            Fixture.element(70, .rectangle, frame: Rect(x: 10_000_000, y: -10_000_000, width: 1_000_000, height: 0),
                            style: Fixture.style(.rectangle) { $0.width = 0.25; $0.opacity = 0; $0.roughness = 12; $0.cornerRadius = 1_000 }),
            Fixture.element(71, .text, frame: Rect(x: 0, y: 0, width: 32, height: 20), text: "שלום 👩🏽‍💻 e\u{301}",
                            style: Fixture.style(.text) { $0.fontSize = 4; $0.fontName = String(repeating: "f", count: 256); $0.width = 256 }),
            Fixture.element(72, .text, frame: Rect(x: 0, y: 40, width: 32, height: 20), text: "",
                            style: Fixture.style(.text) { $0.fontSize = 1_024; $0.bold = true; $0.alignment = .right; $0.fontName = "monospace" }),
            Fixture.element(73, .grid, frame: Rect(x: 0, y: 0, width: 10, height: 10), style: Fixture.style(.grid) { $0.gridRows = 1; $0.gridColumns = 24 }),
            Fixture.element(74, .axes, frame: Rect(x: 0, y: 0, width: 10, height: 10), style: Fixture.style(.axes) { $0.axisTickCount = 0 }),
            Fixture.element(75, .axes, frame: Rect(x: 0, y: 0, width: 10, height: 10), style: Fixture.style(.axes) { $0.axisTickCount = 24; $0.negativeAxes = false }),
            Fixture.element(76, .arrow, points: [Point(0, 0), Point(100, 0)], style: Fixture.style(.arrow) { $0.headScale = 0.5; $0.startHead = .bar }),
            Fixture.element(77, .arrow, points: [Point(0, 10), Point(100, 10)], style: Fixture.style(.arrow) { $0.headScale = 2 }),
            Fixture.element(78, .blur, frame: Rect(x: 0, y: 0, width: 10, height: 10), style: Fixture.style(.blur) { $0.effectAmount = 0 }),
            Fixture.element(79, .distort, frame: Rect(x: 0, y: 0, width: 10, height: 10), style: Fixture.style(.distort) { $0.effectAmount = 100 }),
            Fixture.element(80, .pen, points: [Point(-10_000_000, 10_000_000), Point(-9_999_999.5, 10_000_000)]) {
                $0.pressures = [0, 1]; $0.revision = .max; $0.seed = .max
            },
            Fixture.element(81, .line, points: [Point(0, 0), Point(5, 5)], style: Gradients.two),
            Fixture.element(82, .pen, points: [Point(3, 3)], style: gradientEight) { $0.seed = 0 },
            Fixture.element(83, .rectangle, frame: Rect(x: 0, y: 0, width: 20, height: 20), style: Fixture.style(.rectangle) {
                $0.fill = RGBA(0, 0, 0, 0); $0.fillGradient = ColorGradient(colors: [RGBA(1, 1, 1), RGBA(0, 0, 0)], angle: 0)
            })
        ]
    }

    private enum Gradients {
        static var two: Style {
            var style = Style.defaults(for: .line)
            style.strokeGradient = ColorGradient(colors: [RGBA(1, 0, 0), RGBA(0, 0, 1)], angle: 0)
            return style
        }
    }
}
