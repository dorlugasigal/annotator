import Foundation
import AnnotationCore

/// The tutorial's motion: durations in seconds, distances in points, and the curves both apps use. Nothing loops: every
/// animation plays once and ends in a still state. With Reduce Motion the ghost and the confetti stay away and the cards
/// cross-fade.
public enum TutorialMotion {
    /// A small overshoot for the check mark's circle and keycaps that pop in.
    public static let pop = CubicBezier(0.34, 1.4, 0.64, 1)

    /// The card enters: it rises from below, grows from 96% and fades in.
    public static let cardEnterDuration = 0.42
    public static let cardEnterRise = 16.0
    public static let cardEnterScale = 0.96
    public static let cardEnterFade = 0.24

    /// The card leaves: it sinks a little and fades out.
    public static let cardExitDuration = 0.2
    public static let cardExitSink = 8.0
    public static let cardExitScale = 0.98

    /// A step's words leave upward, then the next step's words rise into place.
    public static let stepOutDuration = 0.16
    public static let stepOutRise = 10.0
    public static let stepInDelay = 0.06
    public static let stepInDuration = 0.38
    public static let stepInRise = 14.0
    public static let stepInFade = 0.24

    /// The check mark draws itself while its circle pops in.
    public static let checkDuration = 0.34
    public static let checkStartScale = 0.6

    /// The pause after a step is done before the next step shows; shorter when the next is done already.
    public static let doneBeat = 0.85
    public static let quickDoneBeat = 0.45

    /// The progress track's leading edge moves first and the trailing edge follows (the old tour's step capsule).
    public static let trackLeadDuration = 0.32
    public static let trackTrailDuration = 0.5
    public static let trackTrailDelay = 0.07

    /// A keycap goes down and comes back up; keys of a chord go down one after another.
    public static let keyDownDuration = 0.08
    public static let keyUpDuration = 0.14
    public static let keyStagger = 0.07
    public static let keyTravel = 2.0

    /// The ghost starts after the step's words settle.
    public static let ghostStartDelay = 0.55
    public static let ghostAppearDuration = 0.22
    public static let ghostAppearScale = 0.8
    public static let ghostVanishDuration = 0.28
    public static let ghostPause = 0.25
    public static let ghostPressDuration = 0.08
    public static let ghostReleaseDuration = 0.14
    public static let ghostPressScale = 0.86

    /// A click sends out one ring.
    public static let rippleDuration = 0.5
    public static let rippleStartRadius = 4.0
    public static let rippleEndRadius = 24.0
    public static let rippleStartOpacity = 0.55

    /// The ghost's ink fades once the ghost lets go.
    public static let inkFadeDelay = 0.35
    public static let inkFadeDuration = 0.4
    public static let inkOpacity = 0.6
    public static let inkWidth = 3.0

    /// The ring around the toolbar control to use: one pulse in, then it stays until the goal is done.
    public static let targetPulseDuration = 0.6
    public static let targetPulseScale = 1.18
    public static let targetOutset = 3.0
    public static let targetCornerRadius = 10.0
    public static let targetWidth = 2.0
    public static let targetFadeDuration = 0.2

    /// With no progress for this long, the card adds a hint and points at Show me once.
    public static let hintDelay = 15.0

    /// How long the ghost travels `distance` points: quick for short hops, never slower than 1.1 seconds.
    public static func travelDuration(_ distance: Double) -> Double {
        min(1.1, max(0.45, 0.35 + abs(distance) / 1400))
    }

    /// How long the ghost takes to draw or drag along a path of `length` points: 480 points a second, between half a second
    /// and 1.6 seconds. It follows the path by length with the standard curve.
    public static func dragDuration(_ length: Double) -> Double {
        min(1.6, max(0.5, abs(length) / 480))
    }

    /// The ghost's position at linear time `t` (0...1) on its way from `from` to `to`: a gentle arc that bends 12% of
    /// the distance to the right of travel (y pointing down), eased with the standard curve.
    public static func travelPoint(from: Point, to: Point, at t: Double) -> Point {
        let eased = MotionCurves.standard.progress(at: min(1, max(0, t)))
        let delta = to - from
        let distance = (delta.x * delta.x + delta.y * delta.y).squareRoot()
        let middle = (from + to) * 0.5
        let control = distance < 1e-9 ? middle : middle + Point(-delta.y / distance, delta.x / distance) * (distance * 0.12)
        let inverse = 1 - eased
        var result = from * (inverse * inverse)
        result = result + control * (2 * inverse * eased)
        result = result + to * (eased * eased)
        return result
    }

    /// A smooth line through `points` (Catmull-Rom), `segments` samples between each pair: the ghost's curved arrow.
    public static func smooth(_ points: [Point], segments: Int = 12) -> [Point] {
        guard points.count >= 3 else { return points }
        var result = [points[0]]
        for index in 0..<(points.count - 1) {
            let p0 = points[max(0, index - 1)]
            let p1 = points[index]
            let p2 = points[index + 1]
            let p3 = points[min(points.count - 1, index + 2)]
            for step in 1...segments {
                let t = Double(step) / Double(segments)
                let t2 = t * t
                let t3 = t2 * t
                var square = p0 * 2
                square = square - p1 * 5
                square = square + p2 * 4
                square = square - p3
                var cube = p1 * 3
                cube = cube - p0
                cube = cube - p2 * 3
                cube = cube + p3
                var sum = p1 * 2
                sum = sum + (p2 - p0) * t
                sum = sum + square * t2
                sum = sum + cube * t3
                result.append(sum * 0.5)
            }
        }
        return result
    }

    /// A loop around `center` for the ghost's pen: a little wider than tall, closing past its start.
    public static func loop(center: Point, radius: Double, samples: Int = 48) -> [Point] {
        (0...samples).map { index in
            let angle = -Double.pi / 2 + Double(index) / Double(samples) * Double.pi * 2.15
            let wobble = 1 + 0.04 * sin(angle * 3)
            return Point(center.x + cos(angle) * radius * 1.25 * wobble, center.y + sin(angle) * radius * wobble)
        }
    }
}

/// One piece of confetti.
public struct ConfettiPiece: Equatable, Sendable {
    /// Where it leaves, in points from the display's top-left corner.
    public var start: Point
    /// Its launch speed in points per second; y grows downward.
    public var velocity: Point
    /// Seconds after the burst before it leaves.
    public var delay: Double
    /// Seconds it lives after it leaves; it fades out over the last `ConfettiBurst.fadeDuration`.
    public var lifetime: Double
    /// Its first turn in degrees.
    public var angle: Double
    /// How fast it turns, in degrees per second.
    public var spin: Double
    /// Where its flutter starts, in radians.
    public var phase: Double
    /// How fast it flutters (its width follows the cosine), in radians per second.
    public var flutter: Double
    /// 0 a rectangle, 1 a dot, 2 a ribbon.
    public var shape: Int
    /// Its index in `ConfettiBurst.palette`.
    public var color: Int

    public init(start: Point, velocity: Point, delay: Double, lifetime: Double, angle: Double, spin: Double,
                phase: Double, flutter: Double, shape: Int, color: Int) {
        self.start = start; self.velocity = velocity; self.delay = delay; self.lifetime = lifetime
        self.angle = angle; self.spin = spin; self.phase = phase; self.flutter = flutter
        self.shape = shape; self.color = color
    }
}

/// Where a piece of confetti is at one moment.
public struct ConfettiState: Equatable, Sendable {
    public var position: Point
    public var angle: Double
    public var widthScale: Double
    public var opacity: Double

    public init(position: Point, angle: Double, widthScale: Double, opacity: Double) {
        self.position = position; self.angle = angle; self.widthScale = widthScale; self.opacity = opacity
    }
}

/// The celebration at the end: confetti pops from both bottom corners of the display, rises, and falls back. The pieces
/// come from a fixed seed and their paths have a closed form, so both apps show the same burst.
public enum ConfettiBurst {
    public static let seed: UInt64 = 0x5EED_A110_7A7E_C0DE
    public static let piecesPerCorner = 60
    public static let drag = 1.15
    public static let fadeDuration = 0.45

    /// The longest a burst can last, delay included.
    public static let duration = 2.8

    /// Sizes in points: a rectangle, a dot and a ribbon.
    public static let shapeSizes: [(width: Double, height: Double)] = [(8, 13), (7, 7), (4, 16)]

    /// The ink colors people know from the palette, with the accent blue.
    public static let palette: [RGBA] = [
        .coral, .blue, .yellow, RGBA(0.28, 0.69, 0.4), RGBA(0.89, 0.26, 0.4), RGBA(0, 0.6, 0.7), RGBA(1, 0.66, 0.23)
    ]

    /// Gravity for a display `height` points tall: taller displays get a stronger pull and launch.
    public static func gravity(height: Double) -> Double { 2000 * scale(height) }

    /// The pieces for a display of `width` by `height` points, left corner first.
    public static func pieces(width: Double, height: Double) -> [ConfettiPiece] {
        var random = SplitMix64(seed: seed)
        let factor = scale(height)
        var result: [ConfettiPiece] = []
        result.reserveCapacity(piecesPerCorner * 2)
        for corner in 0..<2 {
            for _ in 0..<piecesPerCorner {
                let angle = (52 + random.nextDouble() * 26) * Double.pi / 180
                let speed = (1100 + random.nextDouble() * 650) * factor
                let direction: Double = corner == 0 ? 1 : -1
                let x = corner == 0 ? random.nextDouble() * 24 : width - random.nextDouble() * 24
                let y = height + 6 - random.nextDouble() * 12
                let velocity = Point(cos(angle) * speed * direction, -sin(angle) * speed)
                let delay = random.nextDouble() * 0.18
                let lifetime = 1.9 + random.nextDouble() * 0.7
                let turn = random.nextDouble() * 360
                let spin = (random.nextDouble() * 2 - 1) * 540
                let phase = random.nextDouble() * Double.pi * 2
                let flutter = 6 + random.nextDouble() * 8
                let roll = random.nextDouble()
                let shape = roll < 0.6 ? 0 : roll < 0.8 ? 1 : 2
                let color = Int(random.nextDouble() * Double(palette.count)) % palette.count
                result.append(ConfettiPiece(start: Point(x, y), velocity: velocity, delay: delay, lifetime: lifetime,
                                            angle: turn, spin: spin, phase: phase, flutter: flutter,
                                            shape: shape, color: color))
            }
        }
        return result
    }

    /// Where `piece` is `time` seconds after the burst on a display `height` points tall. Air slows it in proportion to
    /// its speed and gravity pulls it down, so v(t) = g/k + (v0 − g/k)e^(−kt) vertically and v0·e^(−kt) sideways.
    /// Nil before it leaves and after it ends.
    public static func state(_ piece: ConfettiPiece, at time: Double, height: Double) -> ConfettiState? {
        var t = time - piece.delay
        guard t >= 0, t <= piece.lifetime + 1e-9 else { return nil }
        t = min(t, piece.lifetime)
        let g = gravity(height: height)
        let decay = (1 - exp(-drag * t)) / drag
        let x = piece.start.x + piece.velocity.x * decay
        let y = piece.start.y + g / drag * t + (piece.velocity.y - g / drag) * decay
        let opacity = min(1, max(0, (piece.lifetime - t) / fadeDuration))
        return ConfettiState(position: Point(x, y), angle: piece.angle + piece.spin * t,
                             widthScale: cos(piece.phase + piece.flutter * t), opacity: opacity)
    }

    private static func scale(_ height: Double) -> Double { min(1.4, max(0.75, height / 900)) }
}

/// SplitMix64: a small random number generator that gives the same numbers on every platform.
public struct SplitMix64: Sendable {
    private var state: UInt64

    public init(seed: UInt64) { state = seed }

    public mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }

    /// A number in 0..<1 from the top 53 bits.
    public mutating func nextDouble() -> Double {
        Double(next() >> 11) * (1.0 / Double(UInt64(1) << 53))
    }
}
