import Foundation

public enum ShapeRecognition {
    public static func previewInput(_ element: Element) -> Element {
        let count = min(256, element.points.count)
        let points: [Point]
        if count < 2 { points = element.points }
        else { points = (0..<count).map { element.points[$0 * (element.points.count - 1) / (count - 1)] } }
        var preview = Element(id: element.id, tool: element.tool, points: points, style: element.style)
        preview.seed = element.seed
        return preview
    }

    public static func recognize(_ element: Element) -> Element? {
        guard element.tool == .pen, element.points.count >= 5 else { return nil }
        let bounded = previewInput(element).points
        guard bounded.allSatisfy(\.isFinite) else { return nil }
        let samples = resample(bounded, count: 96)
        guard samples.count >= 6 else { return nil }
        if let arrow = recognizeArrow(samples, source: element) { return arrow }
        return recognizeLoop(samples, source: element)
    }

    private struct Fit {
        let tool: Tool
        let frame: Rect
        let angle: Double
        let score: Double
    }

    private static func recognizeLoop(_ samples: [Point], source: Element) -> Element? {
        let box = Rect.enclosing(samples)
        guard box.width >= 16, box.height >= 16,
              let first = samples.first, let last = samples.last else { return nil }
        let diagonal = hypot(box.width, box.height)
        guard first.distance(to: last) <= diagonal * 0.28 else { return nil }
        let center = samples.reduce(Point.zero, +) * (1 / Double(samples.count))
        let loop = samples + [first]
        var winding = 0.0, turning = 0.0
        for index in 1..<loop.count {
            let a = loop[index - 1] - center, b = loop[index] - center
            let angle = atan2(a.x * b.y - a.y * b.x, dot(a, b))
            winding += angle
            turning += abs(angle)
        }
        guard abs(winding) >= .pi * 1.5, abs(winding) <= .pi * 2.6,
              turning <= abs(winding) * 1.45 + 0.4 else { return nil }

        var xx = 0.0, yy = 0.0, xy = 0.0
        for p in samples {
            let d = p - center
            xx += d.x * d.x; yy += d.y * d.y; xy += d.x * d.y
        }
        var principal = atan2(2 * xy, xx - yy) / 2
        while principal > .pi / 4 { principal -= .pi / 2 }
        while principal < -.pi / 4 { principal += .pi / 2 }
        let angles: [Double] = [-Double.pi / 6, -.pi / 12, 0, .pi / 12, .pi / 6, principal]
        var best: Fit?
        for angle in angles {
            let rotated = samples.map { ($0 - center).rotated(around: .zero, by: -angle) }
            let bounds = Rect.enclosing(rotated)
            guard bounds.width >= 16, bounds.height >= 16 else { continue }
            let closed = rotated + [rotated[0]]
            let size = hypot(bounds.width, bounds.height)
            for tool in [Tool.rectangle, .diamond, .ellipse] {
                let template = outline(tool, in: bounds)
                let expectedLength = length(template)
                let observedLength = length(closed)
                guard observedLength >= expectedLength * 0.70,
                      observedLength <= expectedLength * 1.45 else { continue }
                // Check both directions so a partial outline cannot pass just
                // because its few drawn edges happen to match the template.
                let errors = rotated.map { distance($0, to: template) } +
                    resample(template, count: 48).map { distance($0, to: closed) }
                let mean = errors.reduce(0, +) / Double(errors.count) / size
                let sorted = errors.sorted()
                guard mean <= 0.055, sorted[sorted.count * 9 / 10] / size <= 0.12 else { continue }
                let score = mean + abs(angle) / (.pi / 4) * 0.003
                if score < (best?.score ?? .infinity) {
                    let fittedCenter = bounds.center.rotated(around: Point.zero, by: angle) + center
                    best = Fit(tool: tool,
                        frame: Rect(x: fittedCenter.x - bounds.width / 2, y: fittedCenter.y - bounds.height / 2,
                                    width: bounds.width, height: bounds.height),
                        angle: angle, score: score)
                }
            }
        }
        guard let best else { return nil }
        var result = Element(id: source.id, tool: best.tool, frame: best.frame, style: source.style)
        result.rotation = best.angle
        return preservingIdentity(result, from: source)
    }

    private static func recognizeArrow(_ samples: [Point], source: Element) -> Element? {
        for points in [samples, Array(samples.reversed())] {
            let tail = points[0]
            let distances = points.map { tail.distance(to: $0) }
            guard let farthest = distances.max() else { continue }
            // Resampling can put a retraced tip slightly farther out than its
            // first visit. Keep the head's return trip out of the shaft.
            guard let tipIndex = (1..<(points.count - 1)).first(where: {
                distances[$0] >= farthest * 0.97 &&
                    distances[$0] >= distances[$0 - 1] && distances[$0] >= distances[$0 + 1]
            }),
                  tipIndex >= 4, tipIndex < points.count - 6 else { continue }
            let tip = points[tipIndex], span = tail.distance(to: tip)
            guard span >= 32 else { continue }
            let axis = (tip - tail) * (1 / span)
            let normal = Point(-axis.y, axis.x)
            let shaft = Array(points[0...tipIndex])
            let shaftError = shaft.reduce(0.0) { $0 + abs(dot($1 - tail, normal)) } / Double(shaft.count)
            guard shaftError / span <= 0.035, length(shaft) <= span * 1.18 else { continue }
            let head = Array(points[tipIndex...])
            guard head.allSatisfy({
                let offset = $0 - tip
                return dot(offset, axis) >= -span * 0.5 && dot(offset, axis) <= span * 0.08 &&
                    abs(dot(offset, normal)) <= span * 0.4
            }), let left = head.max(by: { dot($0 - tip, normal) < dot($1 - tip, normal) }),
                let right = head.min(by: { dot($0 - tip, normal) < dot($1 - tip, normal) }) else { continue }
            let leftWidth = dot(left - tip, normal), rightWidth = -dot(right - tip, normal)
            let leftBack = -dot(left - tip, axis), rightBack = -dot(right - tip, axis)
            guard min(leftWidth, rightWidth) >= max(5, span * 0.04),
                  min(leftBack, rightBack) >= span * 0.035,
                  max(leftWidth, rightWidth) <= min(leftWidth, rightWidth) * 3,
                  max(leftBack, rightBack) <= min(leftBack, rightBack) * 3 else { continue }
            let headError = head.reduce(0.0) {
                $0 + min(segmentDistance($1, tip, left), segmentDistance($1, tip, right))
            } / Double(head.count)
            guard headError / span <= 0.04, length(head) <= (tip.distance(to: left) + tip.distance(to: right)) * 2.1 else { continue }
            var style = source.style
            style.startHead = .none
            style.endHead = .open
            style.curved = false
            style.headScale = min(2, max(0.5, (leftBack + rightBack) / 2 / (2 * max(12, style.width * 4))))
            let result = Element(id: source.id, tool: .arrow, points: [tail, tip], style: style)
            return preservingIdentity(result, from: source)
        }
        return nil
    }

    private static func preservingIdentity(_ element: Element, from source: Element) -> Element {
        var result = element
        result.groupID = source.groupID
        result.locked = source.locked
        result.seed = source.seed
        result.revision = source.revision &+ 1
        return result
    }

    private static func outline(_ tool: Tool, in box: Rect) -> [Point] {
        switch tool {
        case .rectangle:
            return [box.origin, Point(box.maxX, box.y), Point(box.maxX, box.maxY),
                    Point(box.x, box.maxY), box.origin]
        case .diamond:
            return [Point(box.center.x, box.y), Point(box.maxX, box.center.y),
                    Point(box.center.x, box.maxY), Point(box.x, box.center.y), Point(box.center.x, box.y)]
        default:
            return (0...48).map {
                let angle = Double($0) / 48 * 2 * Double.pi
                return Point(box.center.x + box.width / 2 * cos(angle), box.center.y + box.height / 2 * sin(angle))
            }
        }
    }

    private static func resample(_ points: [Point], count: Int) -> [Point] {
        guard let first = points.first, let last = points.last else { return [] }
        var distances = [0.0]
        for index in 1..<points.count { distances.append(distances[index - 1] + points[index - 1].distance(to: points[index])) }
        guard let total = distances.last, total > 0 else { return [first] }
        var index = 1
        var output = [first]
        for sample in 1..<(count - 1) {
            let position = total * Double(sample) / Double(count - 1)
            while index < points.count - 1 && distances[index] < position { index += 1 }
            let segment = distances[index] - distances[index - 1]
            let fraction = segment > 0 ? (position - distances[index - 1]) / segment : 0
            output.append(points[index - 1] + (points[index] - points[index - 1]) * fraction)
        }
        output.append(last)
        return output
    }

    private static func length(_ points: [Point]) -> Double {
        zip(points, points.dropFirst()).reduce(0) { $0 + $1.0.distance(to: $1.1) }
    }

    private static func distance(_ point: Point, to path: [Point]) -> Double {
        zip(path, path.dropFirst()).reduce(Double.infinity) { min($0, segmentDistance(point, $1.0, $1.1)) }
    }

    private static func segmentDistance(_ point: Point, _ start: Point, _ end: Point) -> Double {
        let vector = end - start
        let squared = dot(vector, vector)
        guard squared > 0 else { return point.distance(to: start) }
        let t = min(1, max(0, dot(point - start, vector) / squared))
        return point.distance(to: start + vector * t)
    }

    private static func dot(_ a: Point, _ b: Point) -> Double { a.x * b.x + a.y * b.y }
}
