import AnnotationCore
import AppKit
import Foundation
@testable import PresentationRendering

/// The laser pointer's trail: scripted presses, moves and releases, the trail as drawn at set times and pictures of it.
@MainActor
enum LaserExport {
    enum Step {
        case press(Point, Double, RGBA, Double)
        case move(Point, Double)
        case release(Point?, Double)
        case prune(Double)
    }

    struct Scenario {
        let name: String
        let note: String
        let steps: [Step]
        let times: [Double]
    }

    static func run(_ corpus: Corpus) throws {
        corpus.describe("laser", "The laser pointer's trail from LaserTrail: scripted presses, moves, releases and prunes, the strokes they leave, and every stroke's dots, opacity, bounds and per-layer bands at set times. pictures.jsonl lists pictures drawn by LaserTrailRenderer into an sRGB premultiplied RGBA bitmap (transparent background) at 1x and 2x.")
        var records: [J] = []
        for scenario in scenarios() {
            let trail = play(scenario.steps)
            records.append([
                "name": .string(scenario.name), "note": .string(scenario.note),
                "steps": .array(scenario.steps.map(record)),
                "strokes": .array(trail.strokes.map { stroke in
                    ["samples": .array(stroke.samples.map { ["point": $0.point.json, "time": .double($0.time)] }),
                     "color": .value(stroke.color), "width": .double(stroke.width), "live": .bool(stroke.live)]
                }),
                "isDrawing": .bool(trail.isDrawing), "isResting": .bool(trail.isResting),
                "frames": .array(scenario.times.map { now in frame(trail, at: now) })
            ])
        }
        try corpus.lines("laser/trails.jsonl", records)
        try corpus.json("laser/constants.json", [
            "defaultColor": .value(LaserTrail.defaultColor), "defaultWidth": .double(LaserTrail.defaultWidth),
            "lifetime": .double(LaserTrail.lifetime), "minimumStep": .double(LaserTrail.minimumStep),
            "tailTaper": .double(LaserTrail.tailTaper), "restInterval": .double(LaserTrail.restInterval),
            "maximumSamples": .int(LaserTrail.maximumSamples),
            "passes": .array(LaserTrail.passes.map { pass in
                ["scale": .double(pass.scale), "extra": .double(pass.extra), "alpha": .double(pass.alpha), "white": .bool(pass.white)]
            }),
            "defaultStyle": .value(Style.defaults(for: .laser))
        ])
        try pictures(corpus)
    }

    static func play(_ steps: [Step]) -> LaserTrail {
        var trail = LaserTrail()
        for step in steps {
            switch step {
            case .press(let point, let time, let color, let width): trail.press(at: point, time: time, color: color, width: width)
            case .move(let point, let time): trail.move(to: point, time: time)
            case .release(let point, let time): trail.release(at: point, time: time)
            case .prune(let time): trail.prune(at: time)
            }
        }
        return trail
    }

    private static func record(_ step: Step) -> J {
        switch step {
        case .press(let point, let time, let color, let width):
            ["press": ["point": point.json, "time": .double(time), "color": .value(color), "width": .double(width)]]
        case .move(let point, let time): ["move": ["point": point.json, "time": .double(time)]]
        case .release(let point, let time): ["release": ["point": point.json, "time": .double(time)]]
        case .prune(let time): ["prune": ["time": .double(time)]]
        }
    }

    private static func frame(_ trail: LaserTrail, at now: Double) -> J {
        var pruned = trail
        pruned.prune(at: now)
        return [
            "time": .double(now),
            "shapes": .array(trail.shapes(at: now).map { shape in
                ["color": .value(shape.color), "opacity": .double(shape.opacity),
                 "dots": .array(shape.dots.map { ["center": $0.center.json, "radius": .double($0.radius)] }),
                 "bands": .array(LaserTrail.passes.map { pass in
                     .array(zip(shape.dots, shape.dots.dropFirst()).map { LaserTrail.band(from: $0.0, to: $0.1, pass: pass)?.json ?? .null })
                 })]
            }),
            "bounds": trail.bounds(at: now)?.json ?? .null,
            "afterPrune": ["strokeSampleCounts": .array(pruned.strokes.map { .int($0.samples.count) }), "isEmpty": .bool(pruned.isEmpty)]
        ]
    }

    static func scenarios() -> [Scenario] {
        let red = LaserTrail.defaultColor, width = LaserTrail.defaultWidth
        var scenarios: [Scenario] = []
        scenarios.append(Scenario(name: "held", note: "A press that never moves keeps its dot, at full size and opacity.",
            steps: [.press(Point(100, 100), 0, red, width)], times: [0, 0.5, 2]))
        var line: [Step] = [.press(Point(20, 60), 0, red, width)]
        for index in 1...40 { line.append(.move(Point(20 + Double(index) * 5, 60), Double(index) * 0.01)) }
        line.append(.release(nil, 0.4))
        scenarios.append(Scenario(name: "line", note: "A fast straight line, released at 0.4 seconds: the tail retreats, then the rest fades.",
            steps: line, times: [0.05, 0.2, 0.4, 0.6, 0.9, 1.1, 1.25]))
        var circle: [Step] = [.press(Point(220, 100), 0, RGBA(0.1, 0.6, 1), 10)]
        for index in 1...72 {
            let angle = Double(index) * .pi / 30
            circle.append(.move(Point(160 + 60 * cos(angle), 100 + 60 * sin(angle)), Double(index) / 120))
        }
        scenarios.append(Scenario(name: "circle", note: "A blue circle and a bit more, so the trail crosses itself, still held.",
            steps: circle, times: [0.3, 0.6, 0.9]))
        scenarios.append(Scenario(name: "rest", note: "The pointer rests longer than the lifetime, then moves on: the old part stays gone.",
            steps: [.press(Point(0, 0), 0, red, width), .move(Point(50, 0), 0.1), .prune(2), .move(Point(60, 0), 2),
                    .move(Point(90, 10), 2.02), .release(Point(120, 0), 2.04)],
            times: [1.5, 2.0, 2.03, 2.5, 2.9]))
        scenarios.append(Scenario(name: "two strokes", note: "A second press starts a new stroke with its own color and width.",
            steps: [.press(Point(0, 0), 0, red, width), .move(Point(30, 0), 0.05), .release(nil, 0.05),
                    .press(Point(100, 100), 0.2, RGBA(0, 0, 1), 10), .move(Point(130, 100), 0.25)],
            times: [0.3, 0.86, 1.2]))
        scenarios.append(Scenario(name: "tiny steps", note: "Moves closer than minimumStep to the point before the newest move the newest point.",
            steps: [.press(Point(0, 0), 0, red, width), .move(Point(10, 0), 0.01), .move(Point(10.3, 0.2), 0.02),
                    .move(Point(10.6, 0.1), 0.03), .move(Point(12, 0), 0.04)],
            times: [0.05]))
        return scenarios
    }

    /// Pictures of two scenarios at a set time, for Windows rendering comparisons.
    private static func pictures(_ corpus: Corpus) throws {
        var records: [J] = []
        let size = CGSize(width: 320, height: 200)
        for (name, time) in [("circle", 0.6), ("line", 0.6)] {
            guard let scenario = scenarios().first(where: { $0.name == name }) else { continue }
            let trail = play(scenario.steps)
            var images: [String: J] = [:]
            for scale in [1.0, 2.0] {
                let width = Int(size.width * scale), height = Int(size.height * scale)
                guard let space = CGColorSpace(name: CGColorSpace.sRGB),
                      let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                          space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
                    throw ExportFailure("No bitmap for the laser picture.")
                }
                // Top-left origin, like the canvas.
                context.translateBy(x: 0, y: CGFloat(height))
                context.scaleBy(x: scale, y: -scale)
                LaserTrailRenderer.draw(trail, at: time, in: context)
                guard let image = context.makeImage() else { throw ExportFailure("The laser picture did not render.") }
                let file = "\(name.replacingOccurrences(of: " ", with: "-"))-\(time)@\(Int(scale))x.png"
                corpus.data("laser/\(file)", try PNGExporter.encode(image))
                images["\(Int(scale))x"] = .string(file)
            }
            records.append(["scenario": .string(name), "time": .double(time), "size": size.json, "images": .object(images)])
        }
        try corpus.lines("laser/pictures.jsonl", records)
    }
}
