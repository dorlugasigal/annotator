import AnnotationCore
import Foundation

/// Smart Draw: recorded strokes and what ShapeRecognition makes of them.
@MainActor
enum RecognitionExport {
    static func run(_ corpus: Corpus) throws {
        corpus.describe("recognition", "Pen strokes from a seeded generator and the element ShapeRecognition.recognize returns, or null. The recognized element keeps the stroke's ID; its new geometry token and append epoch use stand-in IDs.")
        var random = SeededRandom(seed: 0x5348_4150_45)
        func jitter(_ points: [Point], _ amount: Double) -> [Point] {
            points.map { $0 + Point(random.range(-amount, amount), random.range(-amount, amount)) }
        }
        func outline(_ corners: [Point], perSide: Int) -> [Point] {
            var points: [Point] = []
            for index in corners.indices {
                let a = corners[index], b = corners[(index + 1) % corners.count]
                for step in 0..<perSide { points.append(a + (b - a) * (Double(step) / Double(perSide))) }
            }
            return points
        }
        func rotated(_ points: [Point], by angle: Double) -> [Point] {
            let center = Rect.enclosing(points).center
            return points.map { $0.rotated(around: center, by: angle) }
        }
        func ellipse(center: Point, rx: Double, ry: Double, count: Int, turns: Double = 1, start: Double = 0) -> [Point] {
            (0...count).map {
                let angle = start + Double($0) / Double(count) * turns * 2 * .pi
                return Point(center.x + rx * cos(angle), center.y + ry * sin(angle))
            }
        }
        func arrow(from tail: Point, to tip: Point, barb: Double, spread: Double, count: Int) -> [Point] {
            let axis = (tip - tail) * (1 / tail.distance(to: tip)), normal = Point(-axis.y, axis.x)
            let shaft = (0...count).map { tail + (tip - tail) * (Double($0) / Double(count)) }
            let left = tip - axis * barb + normal * spread, right = tip - axis * barb - normal * spread
            func segment(_ a: Point, _ b: Point) -> [Point] { (1...6).map { a + (b - a) * (Double($0) / 6) } }
            let toLeft: [Point] = segment(tip, left), back: [Point] = segment(left, tip), toRight: [Point] = segment(tip, right)
            return shaft + toLeft + back + toRight
        }
        let box = [Point(100, 100), Point(260, 100), Point(260, 200), Point(100, 200)]
        let diamond = [Point(180, 80), Point(280, 150), Point(180, 220), Point(80, 150)]
        let cases: [(String, [Point])] = [
            ("rectangle", jitter(outline(box, perSide: 16) + [box[0]], 1.5)),
            ("rectangle started at another corner", jitter(outline(Array(box[2...] + box[..<2]), perSide: 16) + [box[2]], 1.5)),
            ("rectangle rotated 10 degrees", rotated(jitter(outline(box, perSide: 16) + [box[0]], 1), by: 10 * .pi / 180)),
            ("rectangle rotated 30 degrees", rotated(jitter(outline(box, perSide: 16) + [box[0]], 1), by: 30 * .pi / 180)),
            ("rectangle that overshoots its start", jitter(outline(box, perSide: 16) + [box[0], Point(140, 100), Point(170, 101)], 1.5)),
            ("rectangle with a 30% gap", Array(jitter(outline(box, perSide: 16), 1).prefix(46))),
            ("square, small", jitter(outline([Point(10, 10), Point(30, 10), Point(30, 30), Point(10, 30)], perSide: 8) + [Point(10, 10)], 0.5)),
            ("square, too small", jitter(outline([Point(10, 10), Point(24, 10), Point(24, 24), Point(10, 24)], perSide: 8) + [Point(10, 10)], 0.3)),
            ("diamond", jitter(outline(diamond, perSide: 14) + [diamond[0]], 1.5)),
            ("circle", jitter(ellipse(center: Point(200, 200), rx: 80, ry: 80, count: 72), 1.5)),
            ("wide ellipse", jitter(ellipse(center: Point(200, 200), rx: 150, ry: 60, count: 80), 1.5)),
            ("ellipse rotated 20 degrees", rotated(jitter(ellipse(center: Point(200, 200), rx: 120, ry: 50, count: 80), 1), by: 20 * .pi / 180)),
            ("ellipse drawn counterclockwise", jitter(Array(ellipse(center: Point(200, 200), rx: 100, ry: 70, count: 72).reversed()), 1.5)),
            ("spiral of two turns", ellipse(center: Point(200, 200), rx: 90, ry: 90, count: 140, turns: 2)),
            ("three-quarter arc", ellipse(center: Point(200, 200), rx: 90, ry: 90, count: 60, turns: 0.75)),
            ("triangle", jitter(outline([Point(200, 60), Point(300, 240), Point(100, 240)], perSide: 18) + [Point(200, 60)], 1.5)),
            ("arrow to the right", jitter(arrow(from: Point(100, 200), to: Point(300, 200), barb: 30, spread: 20, count: 40), 1)),
            ("arrow up and left", jitter(arrow(from: Point(300, 300), to: Point(150, 120), barb: 28, spread: 18, count: 40), 1)),
            ("arrow drawn head first", jitter(Array(arrow(from: Point(100, 100), to: Point(260, 160), barb: 25, spread: 16, count: 40).reversed()), 1)),
            ("arrow with a lopsided head", jitter(arrow(from: Point(100, 200), to: Point(300, 200), barb: 30, spread: 4, count: 40), 0.5)),
            ("arrow too short", jitter(arrow(from: Point(100, 200), to: Point(125, 200), barb: 8, spread: 6, count: 20), 0.3)),
            ("curved shaft with a head", jitter(ellipse(center: Point(200, 300), rx: 100, ry: 100, count: 30, turns: 0.3, start: .pi)
                + [Point(240, 225), Point(245, 255), Point(270, 250)], 0.5)),
            ("straight line", jitter((0...40).map { Point(100 + Double($0) * 5, 150) }, 0.8)),
            ("zigzag", (0...40).map { Point(100 + Double($0) * 6, 150 + ($0 % 2 == 0 ? -20 : 20)) }),
            ("four points", [Point(0, 0), Point(100, 0), Point(100, 100), Point(0, 100)]),
            ("five points in a square", [Point(0, 0), Point(100, 0), Point(100, 100), Point(0, 100), Point(0, 1)])
        ]
        let ids = FreshIDs()
        var records: [J] = []
        for (index, (name, points)) in cases.enumerated() {
            let stroke = Fixture.element(7_000 + index, .pen, points: points, style: Fixture.style(.pen) { $0.color = .blue; $0.width = 4 })
            let result = ShapeRecognition.recognize(stroke).map { J.value(ids.element($0)) } ?? .null
            records.append(["name": .string(name), "stroke": .value(stroke), "result": result])
        }
        try corpus.lines("recognition/strokes.jsonl", records)

        let long = Fixture.element(7_100, .pen, points: (0..<1_000).map { Point(Double($0) * 0.5, 100 + 40 * sin(Double($0) / 40)) })
        let preview = ShapeRecognition.previewInput(long)
        try corpus.json("recognition/preview-input.json", [
            "note": "previewInput keeps at most 256 points. With n stroke points and m = min(256, n), result point i is stroke point i × (n − 1) / (m − 1) with integer division.",
            "strokePointCount": .int(long.points.count), "strokeFirst": long.points.first!.json, "strokeLast": long.points.last!.json,
            "previewPoints": preview.points.json, "previewSeed": .uint(preview.seed)
        ])
    }
}

/// Hit testing: which points each element contains, and the frontmost element in overlapping scenes.
@MainActor
enum HitTestExport {
    static func run(_ corpus: Corpus) throws {
        corpus.describe("hittest", "contains.jsonl: per element, a grid of HitTesting.contains results ('#' hit, '.' miss). frontmost.jsonl: per scene, a grid of ElementStacking.frontmost results by element letter. Grid points are origin + (column, row) × step.")
        try contains(corpus)
        try frontmost(corpus)
    }

    private static func grid(origin: Point, step: Double, columns: Int, rows: Int, _ symbol: (Point) -> Character) -> J {
        .array((0..<rows).map { row in
            .string(String((0..<columns).map { column in symbol(origin + Point(Double(column) * step, Double(row) * step)) }))
        })
    }

    private static func contains(_ corpus: Corpus) throws {
        var elements: [(String, Element)] = []
        var number = 8_000
        func add(_ name: String, _ tool: Tool, frame: Rect = Rect(x: 0, y: 0, width: 100, height: 70), points: [Point] = [],
                 text: String = "", _ change: (inout Element) -> Void = { _ in }) {
            number += 1
            elements.append((name, Fixture.element(number, tool, frame: frame, points: points, text: text, change)))
        }
        add("rectangle", .rectangle)
        add("rectangle rotated 30 degrees", .rectangle) { $0.rotation = .pi / 6 }
        add("ellipse", .ellipse)
        add("counter", .counter, frame: Rect(x: 0, y: 0, width: 36, height: 36), text: "1")
        add("web", .web)
        add("diamond", .diamond)
        add("rounded diamond", .diamond) { $0.style.cornerRadius = 14 }
        add("triangle", .triangle)
        add("hexagon", .hexagon)
        add("star", .star)
        add("database", .database)
        add("messaging", .messaging, frame: Rect(x: 0, y: 0, width: 120, height: 60))
        add("text", .text, frame: Rect(x: 0, y: 0, width: 90, height: 30), text: "Hi")
        add("cover", .cover)
        add("pen zigzag", .pen, points: [Point(0, 0), Point(30, 60), Point(60, 0), Point(90, 60)])
        add("single-point pen", .pen, points: [Point(20, 20)])
        add("wide highlighter", .highlighter, points: [Point(0, 30), Point(100, 40)])
        add("line", .line, points: [Point(0, 0), Point(100, 70)])
        add("curved line", .line, points: [Point(0, 60), Point(30, 0), Point(70, 70), Point(100, 10)]) { $0.style.curved = true }
        add("ruler", .ruler, points: [Point(0, 60), Point(120, 60)], text: "120 px")
        for kind in ArrowHead.allCases where kind != .none {
            add("arrow with \(kind.rawValue) heads", .arrow, points: [Point(0, 70), Point(100, 10)]) {
                $0.style.startHead = kind; $0.style.endHead = kind; $0.style.headScale = 1.5; $0.style.width = 4
            }
        }
        let step = 2.0
        let records: [J] = elements.map { name, element in
            let origin = Point(-24 + 5.0 / 16, -24 + 5.0 / 16)
            let columns = Int(((max(element.frame.maxX, 120) + 24) - origin.x) / step) + 1
            let rows = Int(((max(element.frame.maxY, 90) + 24) - origin.y) / step) + 1
            return ["name": .string(name), "element": .value(element), "tolerance": 6.0, "origin": origin.json, "step": .double(step),
                    "columns": .int(columns), "rows": .int(rows),
                    "grid": grid(origin: origin, step: step, columns: columns, rows: rows) {
                        HitTesting.contains($0, element: element) ? "#" : "."
                    }]
        }
        try corpus.lines("hittest/contains.jsonl", records)
    }

    private static func frontmost(_ corpus: Corpus) throws {
        let scenes: [(String, [Element])] = [
            ("text inside outline shapes stays clickable", [
                Fixture.element(8_101, .text, frame: Rect(x: 60, y: 50, width: 80, height: 30), text: "Inside"),
                Fixture.element(8_102, .rectangle, frame: Rect(x: 20, y: 20, width: 160, height: 100)),
                Fixture.element(8_103, .ellipse, frame: Rect(x: 40, y: 30, width: 120, height: 80),
                                style: Fixture.style(.ellipse) { $0.fill = RGBA(1, 0, 0, 0.3) }),
                Fixture.element(8_104, .text, frame: Rect(x: 200, y: 50, width: 80, height: 30), text: "Hidden"),
                Fixture.element(8_105, .rectangle, frame: Rect(x: 190, y: 20, width: 120, height: 100),
                                style: Fixture.style(.rectangle) { $0.fill = RGBA(0, 0, 1, 0.8) }),
                Fixture.element(8_106, .text, frame: Rect(x: 330, y: 50, width: 60, height: 30), text: "Hatch"),
                Fixture.element(8_107, .rectangle, frame: Rect(x: 320, y: 20, width: 90, height: 100),
                                style: Fixture.style(.rectangle) { $0.fill = RGBA(0, 0.5, 0, 1); $0.fillPattern = .hatch })
            ]),
            ("covers and effects stay on top", [
                Fixture.element(8_201, .blur, frame: Rect(x: 20, y: 20, width: 120, height: 80)),
                Fixture.element(8_202, .cover, frame: Rect(x: 80, y: 60, width: 120, height: 80)),
                Fixture.element(8_203, .rectangle, frame: Rect(x: 0, y: 0, width: 240, height: 160),
                                style: Fixture.style(.rectangle) { $0.fill = RGBA(1, 1, 0, 1) }),
                Fixture.element(8_204, .arrow, points: [Point(10, 150), Point(230, 10)])
            ]),
            ("strokes, groups and rotation", [
                Fixture.element(8_301, .pen, points: Samples.wave(from: Point(10, 60), count: 30, step: 8, amplitude: 30)),
                Fixture.element(8_302, .rectangle, frame: Rect(x: 60, y: 30, width: 100, height: 60)) { $0.rotation = 0.6; $0.groupID = Fixture.id(990) },
                Fixture.element(8_303, .star, frame: Rect(x: 150, y: 20, width: 90, height: 90)) { $0.groupID = Fixture.id(990) },
                Fixture.element(8_304, .highlighter, points: [Point(0, 100), Point(250, 110)]),
                Fixture.element(8_305, .counter, frame: Rect(x: 200, y: 100, width: 36, height: 36), text: "2")
            ])
        ]
        let letters = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        var records: [J] = []
        for (name, elements) in scenes {
            let bounds = Rect.enclosing(elements.flatMap { [$0.frame.origin, Point($0.frame.maxX, $0.frame.maxY)] })
            let origin = Point(bounds.x - 12 + 5.0 / 16, bounds.y - 12 + 5.0 / 16)
            let step = 3.0
            let columns = Int((bounds.width + 24) / step) + 1, rows = Int((bounds.height + 24) / step) + 1
            let legend = Dictionary(uniqueKeysWithValues: elements.enumerated().map { (String(letters[$0.offset]), J.string($0.element.id.uuidString)) })
            records.append(["scene": .string(name), "elements": .value(elements), "legend": .object(legend), "origin": origin.json,
                            "step": .double(step), "columns": .int(columns), "rows": .int(rows),
                            "grid": grid(origin: origin, step: step, columns: columns, rows: rows) { point in
                                guard let hit = ElementStacking.frontmost(at: point, in: elements),
                                      let index = elements.firstIndex(where: { $0.id == hit.id }) else { return "." }
                                return letters[index]
                            }])
        }
        try corpus.lines("hittest/frontmost.jsonl", records)
    }
}
