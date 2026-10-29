import Foundation

/// The laser pointer's trail. While the button is down, the pointer leaves a glowing line. Each point of it lasts
/// `lifetime` seconds after the pointer passed and thins out as it ages, so the line disappears behind the pointer; when
/// the button goes up, what is left fades out as well. People choose the lifetime with the laser pointer's Fade. The trail is never part of a drawing: it has no Undo step and is
/// never saved, copied or exported.
public struct LaserTrail: Equatable, Sendable {
    /// The laser pointer's color and width in a new profile.
    public static let defaultColor = RGBA(0.98, 0.16, 0.2)
    public static let defaultWidth = 6.0
    /// Seconds a point of the trail lasts after the pointer passed it, unless the person chose another Fade.
    public static let defaultLifetime = 0.8
    /// The Fade people can choose, in seconds.
    public static let lifetimeRange: ClosedRange<Double> = 0.5...5
    /// A new position closer than this to the point before the newest moves the newest point instead of adding one.
    public static let minimumStep = 1.0
    /// The tail narrows to nothing over this many line widths.
    public static let tailTaper = 4.0

    /// One layer of the trail's look. Layers are drawn in order: four glows in the trail's color that get stronger towards
    /// the line, so the glow fades out softly, then the line itself and a thin white core. Each layer is one filled
    /// outline, so where the trail crosses itself it is no darker.
    public struct Pass: Equatable, Sendable {
        /// The layer's radius at each dot: the dot's radius times `scale`, plus `extra` points.
        public let scale: Double
        public let extra: Double
        public let alpha: Double
        /// Draws white instead of the trail's color.
        public let white: Bool

        public init(scale: Double, extra: Double, alpha: Double, white: Bool) {
            self.scale = scale; self.extra = extra; self.alpha = alpha; self.white = white
        }

        public func radius(_ dot: Dot) -> Double { dot.radius > 0 ? dot.radius * scale + extra : 0 }
    }

    public static let passes: [Pass] = [
        Pass(scale: 2.8, extra: 3, alpha: 0.05, white: false),
        Pass(scale: 2.2, extra: 2, alpha: 0.08, white: false),
        Pass(scale: 1.7, extra: 1.5, alpha: 0.12, white: false),
        Pass(scale: 1.35, extra: 1, alpha: 0.18, white: false),
        Pass(scale: 1, extra: 0, alpha: 1, white: false),
        Pass(scale: 0.4, extra: 0, alpha: 0.85, white: true)
    ]

    public struct Sample: Equatable, Sendable {
        public var point: Point
        public var time: Double

        public init(point: Point, time: Double) { self.point = point; self.time = time }
    }

    /// One press of the button: the points the pointer passed, oldest first, with the color and width it had.
    public struct Stroke: Equatable, Sendable {
        public var samples: [Sample]
        public var color: RGBA
        public var width: Double
        /// The button is still down, so the newest point stays fresh.
        public var live: Bool
    }

    /// One round point of a stroke as drawn.
    public struct Dot: Equatable, Sendable {
        public var center: Point
        public var radius: Double

        public init(center: Point, radius: Double) { self.center = center; self.radius = radius }
    }

    /// A stroke as drawn at one moment: round dots from the tail to the head, each joined to the next by a band whose
    /// ends are the two dots' widths.
    public struct Shape: Equatable, Sendable {
        public var dots: [Dot]
        public var color: RGBA
        public var opacity: Double
    }

    /// A pause longer than this on one spot is kept as a second point there, at the time the pointer moved on.
    public static let restInterval = 0.05
    /// A stroke keeps at most this many points; the oldest go first.
    public static let maximumSamples = 2_048

    public private(set) var strokes: [Stroke] = []
    /// Seconds a point of the trail lasts after the pointer passed it. A change applies to the whole trail at once.
    public var lifetime: Double

    public init(lifetime: Double = LaserTrail.defaultLifetime) { self.lifetime = lifetime }

    public var isEmpty: Bool { strokes.isEmpty }
    /// The button is down and the newest stroke follows the pointer.
    public var isDrawing: Bool { strokes.last?.live == true }
    /// The button is down and only the dot under the pointer is left, so nothing changes until the pointer moves or lets go.
    public var isResting: Bool { strokes.count == 1 && strokes[0].live && strokes[0].samples.count == 1 }

    /// Starts a new stroke at `point`. A stroke that is still fading keeps fading on its own.
    public mutating func press(at point: Point, time: Double, color: RGBA, width: Double) {
        release(at: nil, time: time)
        strokes.append(Stroke(samples: [Sample(point: point, time: time)], color: color, width: width, live: true))
    }

    /// Follows the pointer to `point`.
    public mutating func move(to point: Point, time: Double) {
        guard let index = strokes.indices.last, strokes[index].live else { return }
        let samples = strokes[index].samples
        let sample = Sample(point: point, time: time)
        if samples.count >= 2, samples[samples.count - 2].point.distance(to: point) < Self.minimumStep {
            strokes[index].samples[samples.count - 1] = sample
            return
        }
        noteRest(in: index, until: time)
        strokes[index].samples.append(sample)
        let extra = strokes[index].samples.count - Self.maximumSamples
        if extra > 0 { strokes[index].samples.removeFirst(extra) }
    }

    /// The button is up: the stroke ends at `point`, if given, and its newest point starts to fade too.
    public mutating func release(at point: Point?, time: Double) {
        guard let index = strokes.indices.last, strokes[index].live else { return }
        if let point { move(to: point, time: time) }
        noteRest(in: index, until: time)
        strokes[index].live = false
    }

    /// The pointer stayed on the newest point from when it got there until `time`.
    private mutating func noteRest(in index: Int, until time: Double) {
        guard let newest = strokes[index].samples.last, time - newest.time > Self.restInterval else { return }
        strokes[index].samples.append(Sample(point: newest.point, time: time))
    }

    /// Drops what has faded by `now`, keeping the last faded point of each stroke so its tail can end between points.
    public mutating func prune(at now: Double) {
        let horizon = now - lifetime
        for index in strokes.indices {
            let samples = strokes[index].samples
            var faded = 0
            // Times only grow along a stroke, so faded points come first.
            while faded + 1 < samples.count, samples[faded + 1].time <= horizon { faded += 1 }
            if faded > 0 { strokes[index].samples.removeFirst(faded) }
        }
        strokes.removeAll { !$0.live && ($0.samples.last?.time ?? -.infinity) <= horizon }
    }

    /// Every stroke as drawn at `now`, oldest first. Strokes that have faded are left out.
    public func shapes(at now: Double) -> [Shape] {
        strokes.compactMap { Self.shape(of: $0, at: now, lifetime: lifetime) }
    }

    /// The area the trail covers at `now`, glow included, or `nil` when nothing shows. Every dot counts, even one with no
    /// width: the band from the tail's point to the next dot is drawn too.
    public func bounds(at now: Double) -> Rect? {
        var area: Rect?
        for shape in shapes(at: now) {
            for dot in shape.dots {
                let reach = Self.passes.map { $0.radius(dot) }.max() ?? 0
                let rect = Rect(x: dot.center.x - reach, y: dot.center.y - reach, width: reach * 2, height: reach * 2)
                area = area.map { $0.union(rect) } ?? rect
            }
        }
        return area
    }

    static func shape(of stroke: Stroke, at now: Double, lifetime: Double) -> Shape? {
        let horizon = now - lifetime
        let samples = stroke.samples
        guard let head = samples.last, stroke.live || head.time > horizon else { return nil }
        // The tail ends where the pointer was `lifetime` seconds ago, between the last faded point and the first fresh one.
        var visible: [Sample] = []
        for index in samples.indices where samples[index].time > horizon {
            if visible.isEmpty, index > 0 {
                let previous = samples[index - 1], next = samples[index]
                let span = next.time - previous.time
                let share = span > 0 ? min(1, max(0, (horizon - previous.time) / span)) : 1
                visible.append(Sample(point: previous.point + (next.point - previous.point) * share, time: horizon))
            }
            visible.append(samples[index])
        }
        // While the button is down, the dot under the pointer stays, even after resting longer than the lifetime.
        if visible.isEmpty { visible = [head] }
        let radius = stroke.width / 2
        let taper = max(stroke.width * tailTaper, 1)
        var travelled = 0.0
        var dots: [Dot] = []
        for (index, sample) in visible.enumerated() {
            if index > 0 { travelled += visible[index - 1].point.distance(to: sample.point) }
            let isHead = index == visible.count - 1
            let life = isHead && stroke.live ? 1 : fraction(sample.time - horizon, of: lifetime)
            let thickness = life * (2 - life)
            let tail = isHead ? 1 : min(1, travelled / taper)
            dots.append(Dot(center: sample.point, radius: radius * thickness * tail))
        }
        return Shape(dots: dots, color: stroke.color, opacity: stroke.live ? 1 : fraction(head.time - horizon, of: lifetime))
    }

    private static func fraction(_ remaining: Double, of lifetime: Double) -> Double {
        lifetime > 0 ? min(1, max(0, remaining / lifetime)) : 0
    }

    /// The band that joins two neighboring dots in one layer: four corners, each end as wide as its dot's circle, wound
    /// like the circles (counterclockwise with y up), or `nil` when the dots meet or both are empty.
    public static func band(from start: Dot, to end: Dot, pass: Pass) -> [Point]? {
        let length = start.center.distance(to: end.center)
        let first = pass.radius(start), last = pass.radius(end)
        guard length > 0, first > 0 || last > 0 else { return nil }
        let normal = Point(-(end.center.y - start.center.y) / length, (end.center.x - start.center.x) / length)
        let corners = [start.center - normal * first, end.center - normal * last,
                       end.center + normal * last, start.center + normal * first]
        return signedArea(corners) < 0 ? corners.reversed() : corners
    }

    static func signedArea(_ points: [Point]) -> Double {
        zip(points, points.dropFirst() + points.prefix(1)).reduce(0) { $0 + $1.0.x * $1.1.y - $1.1.x * $1.0.y } / 2
    }
}
