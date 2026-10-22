import AnnotationCore
import Foundation

extension BoardsExport {
    private static func broken(_ number: Int, _ tool: Tool = .rectangle, _ change: (inout Element) -> Void) -> Element {
        var element = Fixture.element(number, tool, frame: Rect(x: 10, y: 10, width: 80, height: 60))
        if tool.isFreehand || tool.isConnector || tool == .ruler {
            element.points = [Point(10, 10), Point(90, 70)]
        }
        change(&element)
        return element
    }

    private static func styled(_ number: Int, _ tool: Tool = .rectangle, _ change: (inout Style) -> Void) -> Element {
        broken(number, tool) { change(&$0.style) }
    }

    static func invalid(_ corpus: Corpus) throws {
        let rectangle = Fixture.element(1, .rectangle, frame: Rect(x: 10, y: 10, width: 80, height: 60))
        var cases: [(String, Data)] = [
            ("schema-0", try RawBoard.bytes([rectangle], schema: 0)),
            ("schema-4", try RawBoard.bytes([rectangle], schema: 4)),
            ("label-in-schema-1", try RawBoard.bytes([broken(2) { $0.label = ShapeLabel(text: "A") }], schema: 1)),
            ("gradient-in-schema-2", try RawBoard.bytes([styled(3, .pen) { $0.strokeGradient = GradientPreset.dusk.gradient }], schema: 2)),
            ("matching-label-in-schema-2", try RawBoard.bytes([broken(4) { $0.label = ShapeLabel(text: "A", followsStroke: true) }], schema: 2)),
            ("label-on-text", try RawBoard.bytes([broken(5, .text) { $0.label = ShapeLabel(text: "A") }], schema: 2)),
            ("image-without-data", try RawBoard.bytes([broken(6, .image) { _ in }], schema: 1)),
            ("image-not-png", try RawBoard.bytes([broken(7, .image) { $0.imagePNG = Data("GIF89a not a png".utf8) }], schema: 1)),
            ("image-data-on-rectangle", try RawBoard.bytes([broken(8) { $0.imagePNG = try? Fixture.smallPNG() }], schema: 1)),
            ("rotated-cover", try RawBoard.bytes([broken(9, .cover) { $0.rotation = 0.1 }], schema: 1)),
            ("rotated-blur", try RawBoard.bytes([broken(10, .blur) { $0.rotation = -0.1 }], schema: 1)),
            ("negative-width", try RawBoard.bytes([broken(11) { $0.frame.width = -1 }], schema: 1)),
            ("frame-too-far", try RawBoard.bytes([broken(12) { $0.frame.x = 10_000_000.5 }], schema: 1)),
            ("frame-too-wide", try RawBoard.bytes([broken(13) { $0.frame.width = 1_000_000.5 }], schema: 1)),
            ("point-too-far", try RawBoard.bytes([broken(14, .pen) { $0.points[1].y = -10_000_001 }], schema: 1)),
            ("pressure-count", try RawBoard.bytes([broken(15, .pen) { $0.pressures = [1] }], schema: 1)),
            ("pressure-above-one", try RawBoard.bytes([broken(16, .pen) { $0.pressures = [1, 1.01] }], schema: 1)),
            ("select-tool", try RawBoard.bytes([broken(17, .select) { _ in }], schema: 1)),
            ("eraser-tool", try RawBoard.bytes([broken(18, .eraser) { _ in }], schema: 1)),
            ("anchor-on-rectangle", try RawBoard.bytes([rectangle, broken(19) {
                $0.startAnchor = Anchor(elementID: Fixture.id(1), relative: Point(0.5, 0)) }], schema: 1)),
            ("anchor-outside-shape", try RawBoard.bytes([rectangle, broken(20, .arrow) {
                $0.endAnchor = Anchor(elementID: Fixture.id(1), relative: Point(1.5, 0.5)) }], schema: 1)),
            ("duplicate-ids", try RawBoard.bytes([rectangle, rectangle], schema: 1)),
            ("width-too-small", try RawBoard.bytes([styled(21) { $0.width = 0.2 }], schema: 1)),
            ("width-too-large", try RawBoard.bytes([styled(22) { $0.width = 256.5 }], schema: 1)),
            ("opacity-above-one", try RawBoard.bytes([styled(23) { $0.opacity = 1.5 }], schema: 1)),
            ("opacity-negative", try RawBoard.bytes([styled(24) { $0.opacity = -0.1 }], schema: 1)),
            ("roughness-too-large", try RawBoard.bytes([styled(25) { $0.roughness = 12.5 }], schema: 1)),
            ("corner-radius-too-large", try RawBoard.bytes([styled(26) { $0.cornerRadius = 1_000.5 }], schema: 1)),
            ("font-size-too-small", try RawBoard.bytes([styled(27, .text) { $0.fontSize = 3.9 }], schema: 1)),
            ("font-size-too-large", try RawBoard.bytes([styled(28, .text) { $0.fontSize = 1_025 }], schema: 1)),
            ("font-name-too-long", try RawBoard.bytes([styled(29, .text) { $0.fontName = String(repeating: "é", count: 129) }], schema: 1)),
            ("color-above-one", try RawBoard.bytes([styled(30) { $0.color.red = 1.1 }], schema: 1)),
            ("color-alpha-negative", try RawBoard.bytes([styled(31) { $0.color.alpha = -0.01 }], schema: 1)),
            ("fill-above-one", try RawBoard.bytes([styled(32) { $0.fill = RGBA(0, 2, 0) }], schema: 1)),
            ("grid-rows-zero", try RawBoard.bytes([styled(33, .grid) { $0.gridRows = 0 }], schema: 1)),
            ("grid-columns-25", try RawBoard.bytes([styled(34, .grid) { $0.gridColumns = 25 }], schema: 1)),
            ("axis-ticks-negative", try RawBoard.bytes([styled(35, .axes) { $0.axisTickCount = -1 }], schema: 1)),
            ("axis-ticks-25", try RawBoard.bytes([styled(36, .axes) { $0.axisTickCount = 25 }], schema: 1)),
            ("head-scale-too-small", try RawBoard.bytes([styled(37, .arrow) { $0.headScale = 0.49 }], schema: 1)),
            ("head-scale-too-large", try RawBoard.bytes([styled(38, .arrow) { $0.headScale = 2.01 }], schema: 1)),
            ("effect-negative", try RawBoard.bytes([styled(39, .blur) { $0.effectAmount = -1 }], schema: 1)),
            ("effect-above-100", try RawBoard.bytes([styled(40, .pixelate) { $0.effectAmount = 100.5 }], schema: 1)),
            ("fill-gradient-without-fill", try RawBoard.bytes([styled(41) { $0.fillGradient = GradientPreset.citrus.gradient }], schema: 3)),
            ("gradient-one-color", try RawBoard.bytes([styled(42) { $0.strokeGradient = ColorGradient(colors: [RGBA(1, 0, 0)]) }], schema: 3)),
            ("gradient-nine-colors", try RawBoard.bytes([styled(43) {
                $0.strokeGradient = ColorGradient(colors: Array(repeating: RGBA(1, 0, 0), count: 9)) }], schema: 3)),
            ("gradient-translucent", try RawBoard.bytes([styled(44) {
                $0.strokeGradient = ColorGradient(colors: [RGBA(1, 0, 0), RGBA(0, 0, 1, 0.99)]) }], schema: 3)),
            ("gradient-angle-361", try RawBoard.bytes([styled(45) {
                $0.strokeGradient = ColorGradient(colors: [RGBA(1, 0, 0), RGBA(0, 0, 1)], angle: 360.5) }], schema: 3)),
            ("gradient-angle-negative", try RawBoard.bytes([styled(46) {
                $0.strokeGradient = ColorGradient(colors: [RGBA(1, 0, 0), RGBA(0, 0, 1)], angle: -1) }], schema: 3)),
            ("label-style-invalid", try RawBoard.bytes([broken(47) {
                var style = Style(); style.fontSize = 2
                $0.label = ShapeLabel(text: "A", style: style) }], schema: 2))
        ]
        cases.sort { $0.0 < $1.0 }
        var records: [J] = []
        for (name, input) in cases {
            corpus.data("boards/invalid/\(name).json", input)
            let result: J = outcome { _ = try DrawingFile.read(input); return .null }
            guard case .object(let fields) = result, let error = fields["error"] else {
                throw ExportFailure("The invalid board \(name) opened without an error.")
            }
            records.append(["name": .string(name), "file": .string("\(name).json"), "error": error])
        }

        // Limits too large for a committed file. The recipe says how to build the input.
        let recipes: [(String, String, () throws -> Void)] = [
            ("file-over-128-mb", "134,217,729 zero bytes, read with DrawingFile.read.", {
                _ = try DrawingFile.read(Data(count: 128 * 1_024 * 1_024 + 1)) }),
            ("text-over-1-mb", "One text element whose text is 1,000,001 letters a, opened as a document.", {
                _ = try CanvasDocument(elements: [Fixture.element(90, .text, frame: Rect(x: 0, y: 0, width: 40, height: 20),
                                                                  text: String(repeating: "a", count: 1_000_001))]) }),
            ("label-over-1-mb", "One rectangle whose label text is 1,000,001 letters a, opened as a document.", {
                _ = try CanvasDocument(elements: [broken(91) { $0.label = ShapeLabel(text: String(repeating: "a", count: 1_000_001)) }]) }),
            ("image-over-32-mb", "One image element whose data is the 8-byte PNG signature followed by 33,554,425 zero bytes.", {
                _ = try CanvasDocument(elements: [broken(92, .image) {
                    $0.imagePNG = Data([137, 80, 78, 71, 13, 10, 26, 10]) + Data(count: 32 * 1_024 * 1_024 - 7) }]) })
        ]
        for (name, recipe, body) in recipes {
            let result: J = outcome { try body(); return .null }
            guard case .object(let fields) = result, let error = fields["error"] else {
                throw ExportFailure("The limit case \(name) passed.")
            }
            records.append(["name": .string(name), "recipe": .string(recipe), "error": error])
        }
        try corpus.lines("boards/invalid/index.jsonl", records)
    }
}
