// MIT-derived geometry from Rough.js renderer.ts and math.ts.
// Copyright (c) 2019 Preet Shihn. See THIRD_PARTY_NOTICES.md.
import CoreGraphics
import Foundation
import AnnotationCore

enum RoughGeometry {
    struct Random {
        var state: UInt32
        init(seed: UInt64) { state = UInt32(truncatingIfNeeded: seed); if state == 0 { state = 1 } }
        mutating func next() -> Double {
            state = state &* 48_271
            return Double(state & 0x7fff_ffff) / 2_147_483_648
        }
    }
    private struct Generator {
        var random: Random
        let roughness: Double
        mutating func offset(_ low: Double, _ high: Double, gain: Double = 1) -> Double {
            roughness * gain * (random.next() * (high - low) + low)
        }
        mutating func offset(_ amount: Double, gain: Double = 1) -> Double { offset(-amount, amount, gain: gain) }

        mutating func line(_ a: Point, _ b: Point, preserve: Bool, into path: CGMutablePath) {
            for overlay in [false, true] {
                let length = a.distance(to: b)
                let gain = length < 200 ? 1 : length > 500 ? 0.4 : -0.0016668 * length + 1.233334
                let amount = min(2, length / 10) * (overlay ? 0.5 : 1)
                let divergence = 0.2 + random.next() * 0.2
                let displacementX = offset(2 * (b.y - a.y) / 200, gain: gain)
                let displacementY = offset(2 * (a.x - b.x) / 200, gain: gain)
                let start = Point(a.x + (preserve ? 0 : offset(amount, gain: gain)),
                                  a.y + (preserve ? 0 : offset(amount, gain: gain)))
                let first = Point(displacementX + a.x + (b.x - a.x) * divergence + offset(amount, gain: gain),
                                  displacementY + a.y + (b.y - a.y) * divergence + offset(amount, gain: gain))
                let second = Point(displacementX + a.x + 2 * (b.x - a.x) * divergence + offset(amount, gain: gain),
                                   displacementY + a.y + 2 * (b.y - a.y) * divergence + offset(amount, gain: gain))
                let end = Point(b.x + (preserve ? 0 : offset(amount, gain: gain)),
                                b.y + (preserve ? 0 : offset(amount, gain: gain)))
                path.move(to: start.cgPoint)
                path.addCurve(to: end.cgPoint, control1: first.cgPoint, control2: second.cgPoint)
            }
        }
        mutating func bezier(start: Point, first: Point, second: Point, end: Point, preserve: Bool, into path: CGMutablePath) {
            for pass in 0..<2 {
                let amount = pass == 0 ? 2.0 : 2.3
                let beginning = pass == 0 || preserve ? start : Point(start.x + offset(2), start.y + offset(2))
                let finish = preserve ? end : Point(end.x + offset(amount), end.y + offset(amount))
                let one = Point(first.x + offset(amount), first.y + offset(amount))
                let two = Point(second.x + offset(amount), second.y + offset(amount))
                path.move(to: beginning.cgPoint)
                path.addCurve(to: finish.cgPoint, control1: one.cgPoint, control2: two.cgPoint)
            }
        }
        mutating func ellipse(_ frame: Rect, into path: CGMutablePath) {
            let rx = frame.width / 2, ry = frame.height / 2
            let estimate = sqrt(Double.pi * 2 * sqrt((rx * rx + ry * ry) / 2))
            let steps = ceil(max(9, 9 / sqrt(200) * estimate))
            let increment = .pi * 2 / max(steps, 4)
            _ = offset(0); _ = offset(0) // curveFitting=1 still advances Rough.js's seeded sequence.
            let upper = offset(0.4, 1)
            let overlap = increment * offset(0.1, upper)
            for pass in 0..<2 {
                let amount = pass == 0 ? 1.0 : 1.5
                let extra = pass == 0 ? overlap : 0
                let angle = offset(0.5) - .pi / 2
                func position(_ theta: Double, factor: Double = 1) -> Point {
                    Point(frame.center.x + factor * rx * cos(theta), frame.center.y + factor * ry * sin(theta))
                }
                func jittered(_ point: Point, dx: Double, dy: Double) -> Point { Point(point.x + dx, point.y + dy) }
                var points = [jittered(position(angle - increment, factor: 0.9), dx: offset(amount), dy: offset(amount))]
                var theta = angle
                while theta < .pi * 2 + angle - 0.01 {
                    points.append(jittered(position(theta), dx: offset(amount), dy: offset(amount)))
                    theta += increment
                }
                let overlapStartAngle = angle + .pi * 2 + extra * 0.5
                let overlapEndAngle = angle + .pi * 2 + extra
                let overlapStart = jittered(position(overlapStartAngle), dx: offset(amount), dy: offset(amount))
                let overlapEnd = jittered(position(angle + extra, factor: 0.98), dx: offset(amount), dy: offset(amount))
                let guideOffset = Point(offset(amount), offset(amount))
                if extra > increment {
                    // Large seeded overlaps need perimeter samples, not a single cubic chord.
                    func appendArc(from start: Point, angle startAngle: Double, factor startFactor: Double,
                                   to end: Point, angle endAngle: Double, factor endFactor: Double) {
                        let segments = max(1, Int(ceil((endAngle - startAngle) / increment)))
                        let startOffset = start - position(startAngle, factor: startFactor)
                        let endOffset = end - position(endAngle, factor: endFactor)
                        for index in 1..<segments {
                            let t = Double(index) / Double(segments)
                            points.append(position(startAngle + (endAngle - startAngle) * t,
                                                   factor: startFactor + (endFactor - startFactor) * t)
                                          + startOffset * (1 - t) + endOffset * t)
                        }
                        points.append(end)
                    }
                    let last = points[points.count - 1]
                    appendArc(from: last, angle: theta - increment, factor: 1,
                              to: overlapStart, angle: overlapStartAngle, factor: 1)
                    appendArc(from: overlapStart, angle: overlapStartAngle, factor: 1,
                              to: overlapEnd, angle: overlapEndAngle, factor: 0.98)
                    let guideAngle = overlapEndAngle - min(extra * 0.5, increment)
                    points.append(position(guideAngle, factor: 0.9) + guideOffset)
                } else {
                    points.append(overlapStart)
                    points.append(overlapEnd)
                    points.append(position(angle + extra * 0.5, factor: 0.9) + guideOffset)
                }
                guard points.count > 3 else { continue }
                path.move(to: points[1].cgPoint)
                for index in 1..<(points.count - 2) {
                    let a = points[index], b = points[index + 1]
                    path.addCurve(to: b.cgPoint,
                                  control1: (a + (b - points[index - 1]) * (1 / 6.0)).cgPoint,
                                  control2: (b + (a - points[index + 2]) * (1 / 6.0)).cgPoint)
                }
            }
        }
    }

    static func outline(_ element: Element, canonical: CGPath) -> CGPath {
        var generator = Generator(random: Random(seed: element.seed), roughness: element.style.roughness)
        let output = CGMutablePath()
        if element.tool == .ellipse {
            generator.ellipse(element.frame, into: output)
            return output
        }
        var current = Point.zero, first = Point.zero
        let preserve = element.style.cornerRadius > 0 || element.tool.isConnector
        canonical.applyWithBlock { item in
            let operation = item.pointee
            switch operation.type {
            case .moveToPoint:
                current = Point(operation.points[0]); first = current
            case .addLineToPoint:
                let next = Point(operation.points[0])
                generator.line(current, next, preserve: preserve, into: output); current = next
            case .addQuadCurveToPoint:
                let control = Point(operation.points[0]), end = Point(operation.points[1])
                generator.bezier(start: current, first: current + (control - current) * (2 / 3.0),
                                 second: end + (control - end) * (2 / 3.0), end: end, preserve: true, into: output)
                current = end
            case .addCurveToPoint:
                let end = Point(operation.points[2])
                generator.bezier(start: current, first: Point(operation.points[0]), second: Point(operation.points[1]),
                                 end: end, preserve: preserve, into: output)
                current = end
            case .closeSubpath:
                if current != first { generator.line(current, first, preserve: preserve, into: output) }
                current = first
            @unknown default: break
            }
        }
        return output
    }
}
