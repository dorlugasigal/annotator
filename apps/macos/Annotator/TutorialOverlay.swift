import AppKit
import QuartzCore
import AnnotationCore
import MotionCore

/// One ghost demonstration in screen points (AppKit coordinates, origin at the bottom left of the main display).
struct TutorialGhostRun: Equatable {
    enum Ink: Equatable { case line, rectangle }

    enum Step: Equatable {
        /// Moves to a point and clicks once.
        case click(CGPoint)
        /// Presses at the path's first point, follows it and lets go; the ink, if any, follows the ghost.
        case drag([CGPoint], ink: Ink?)
        /// Clicks each point in turn while a smooth line grows through them: the curved arrow. With `double`, the last
        /// click is a double-click, which finishes the arrow with its head.
        case clicks([CGPoint], double: Bool = false)
    }

    /// The full frame of the display the ghost plays on.
    var display: CGRect
    var start: CGPoint
    var steps: [Step]
    /// Keys pressed after the steps; after a curved arrow they finish it, which adds its head. 0 for none.
    var finishKeys = 0
}

/// The ghost's keyframes for one run, sampled from the shared tutorial motion so both apps move alike.
/// Times are seconds from the start of the run.
struct TutorialGhostTimeline {
    struct Ripple {
        let center: CGPoint
        let time: Double
    }

    struct Ink {
        let style: TutorialGhostRun.Ink
        /// The line the ink draws, revealed by `reveal` (line style).
        var line: [CGPoint] = []
        var reveal: [(time: Double, value: Double)] = []
        /// The rectangle at each moment (rectangle style).
        var boxes: [(time: Double, rect: CGRect)] = []
        var start = 0.0
        var release = 0.0
        var end: Double { release + TutorialMotion.inkFadeDelay + TutorialMotion.inkFadeDuration }
    }

    private(set) var positions: [(time: Double, point: CGPoint)] = []
    private(set) var scales: [(time: Double, value: Double)] = []
    private(set) var opacities: [(time: Double, value: Double)] = []
    private(set) var ripples: [Ripple] = []
    private(set) var inks: [Ink] = []
    /// When the last click or drag has let go.
    private(set) var actionsEnd = 0.0
    /// When the keys after the steps go down, if the run has any.
    private(set) var keyCue: Double?
    private(set) var duration = 0.0
    /// The curved arrow waiting for its head: its ink and its last segment.
    private var arrow: (ink: Int, from: CGPoint, tip: CGPoint)?

    init(_ run: TutorialGhostRun) {
        var at = run.start
        positions.append((0, at))
        scales.append((0, TutorialMotion.ghostAppearScale))
        scales.append((TutorialMotion.ghostAppearDuration, 1))
        opacities.append((0, 0))
        opacities.append((TutorialMotion.ghostAppearDuration, 1))
        var time = TutorialMotion.ghostAppearDuration + TutorialMotion.ghostPause
        actionsEnd = time
        for step in run.steps {
            switch step {
            case .click(let point):
                travel(to: point, at: &at, time: &time)
                click(at: point, time: &time)
            case .drag(let path, let style):
                guard let first = path.first else { continue }
                travel(to: first, at: &at, time: &time)
                press(time: &time)
                let start = time
                let samples = follow(path, at: &at, time: &time)
                release(time: &time)
                if let style {
                    var ink = Ink(style: style)
                    ink.start = start
                    ink.release = time
                    switch style {
                    case .line:
                        ink.line = path
                        ink.reveal = samples.map { ($0.time, $0.fraction) }
                    case .rectangle:
                        ink.boxes = samples.map { ($0.time, Self.rect(first, $0.point)) }
                    }
                    inks.append(ink)
                }
            case .clicks(let points, let double):
                guard let first = points.first else { continue }
                travel(to: first, at: &at, time: &time)
                let curve = Self.curve(through: points)
                let lengths = Self.cumulative(curve.points)
                let total = lengths.last ?? 0
                var ink = Ink(style: .line)
                ink.line = curve.points
                ink.start = time
                ink.reveal = [(time, 0)]
                click(at: first, time: &time)
                for index in 1..<max(1, points.count) {
                    time += TutorialMotion.ghostPause
                    let from = curve.anchors[index - 1], to = curve.anchors[index]
                    let base = lengths[from], span = lengths[to] - lengths[from]
                    ink.reveal.append((time, total > 0 ? base / total : 0))
                    for sample in follow(Array(curve.points[from...to]), at: &at, time: &time) {
                        ink.reveal.append((sample.time, total > 0 ? (base + sample.fraction * span) / total : 1))
                    }
                    click(at: points[index], time: &time)
                }
                ink.release = time
                if points.count >= 2 && curve.points.count >= 2 {
                    arrow = (ink: inks.count, from: curve.points[curve.points.count - 2],
                             tip: curve.points[curve.points.count - 1])
                }
                inks.append(ink)
                if double, let last = points.last {
                    time += TutorialMotion.doubleClickGap
                    ripples.append(Ripple(center: last, time: time))
                    press(time: &time)
                    head(at: time)
                    release(time: &time)
                }
            }
            actionsEnd = time
            time += TutorialMotion.ghostPause
        }
        if run.finishKeys > 0 {
            // The keys go down; after a curved arrow they finish it.
            keyCue = time
            head(at: time + TutorialMotion.keyDownDuration)
            time += Double(run.finishKeys - 1) * TutorialMotion.keyStagger + TutorialMotion.keyDownDuration +
                TutorialMotion.keyUpDuration + 0.35
        }
        let vanished = time + TutorialMotion.ghostVanishDuration
        positions.append((vanished, at))
        scales.append((vanished, 1))
        opacities.append((time, 1))
        opacities.append((vanished, 0))
        duration = max(vanished, inks.map(\.end).max() ?? 0,
                       ripples.map { $0.time + TutorialMotion.rippleDuration }.max() ?? 0)
    }

    func position(at time: Double) -> CGPoint {
        guard let first = positions.first else { return .zero }
        if time <= first.time { return first.point }
        for index in 1..<positions.count where positions[index].time >= time {
            let a = positions[index - 1], b = positions[index]
            let span = b.time - a.time
            let t = CGFloat(span > 0 ? (time - a.time) / span : 1)
            return CGPoint(x: a.point.x + (b.point.x - a.point.x) * t, y: a.point.y + (b.point.y - a.point.y) * t)
        }
        return positions[positions.count - 1].point
    }

    func scale(at time: Double) -> Double { Self.value(scales, at: time) }

    func opacity(at time: Double) -> Double { Self.value(opacities, at: time) }

    static func value(_ track: [(time: Double, value: Double)], at time: Double) -> Double {
        guard let first = track.first else { return 0 }
        if time <= first.time { return first.value }
        for index in 1..<track.count where track[index].time >= time {
            let a = track[index - 1], b = track[index]
            let span = b.time - a.time
            let t = span > 0 ? (time - a.time) / span : 1
            return a.value + (b.value - a.value) * t
        }
        return track[track.count - 1].value
    }

    /// Travels on the shared arc. The arc bends right of travel with y pointing down; screen points have y pointing
    /// up, so y is mirrored on the way in and out.
    private mutating func travel(to target: CGPoint, at: inout CGPoint, time: inout Double) {
        positions.append((time, at))
        let distance = Double(hypot(target.x - at.x, target.y - at.y))
        guard distance > 0.5 else {
            at = target
            return
        }
        let duration = TutorialMotion.travelDuration(distance)
        let count = max(2, Int((duration * 60).rounded()))
        let from = Point(Double(at.x), -Double(at.y))
        let to = Point(Double(target.x), -Double(target.y))
        for index in 1...count {
            let t = Double(index) / Double(count)
            let point = TutorialMotion.travelPoint(from: from, to: to, at: t)
            positions.append((time + duration * t, CGPoint(x: point.x, y: -point.y)))
        }
        time += duration
        at = target
    }

    /// Follows `path` at an eased pace and returns where the ghost is at each sample, with the share of the path done.
    private mutating func follow(_ path: [CGPoint], at: inout CGPoint,
                                 time: inout Double) -> [(time: Double, fraction: Double, point: CGPoint)] {
        guard let first = path.first, let last = path.last else { return [] }
        positions.append((time, first))
        let lengths = Self.cumulative(path)
        let total = lengths.last ?? 0
        guard path.count > 1, total > 0.5 else {
            at = last
            return [(time, 1, last)]
        }
        let duration = TutorialMotion.dragDuration(Double(total))
        let count = max(2, Int((duration * 60).rounded()))
        var samples: [(time: Double, fraction: Double, point: CGPoint)] = [(time, 0, first)]
        for index in 1...count {
            let u = Double(index) / Double(count)
            let fraction = MotionCurves.standard.progress(at: u)
            let point = Self.point(along: path, lengths: lengths, distance: fraction * total)
            positions.append((time + duration * u, point))
            samples.append((time + duration * u, fraction, point))
        }
        time += duration
        at = last
        return samples
    }

    /// The curved arrow is finished at `at`: its head appears at the last point, and the arrow and its head fade together.
    private mutating func head(at time: Double) {
        guard let arrow else { return }
        self.arrow = nil
        let barbs = TutorialMotion.arrowhead(from: Point(Double(arrow.from.x), Double(arrow.from.y)),
                                             tip: Point(Double(arrow.tip.x), Double(arrow.tip.y)))
        var ink = Ink(style: .line)
        ink.line = [CGPoint(x: barbs.left.x, y: barbs.left.y), arrow.tip, CGPoint(x: barbs.right.x, y: barbs.right.y)]
        ink.reveal = [(time, 1)]
        ink.start = time
        ink.release = time
        inks[arrow.ink].release = time
        inks.append(ink)
    }

    private mutating func press(time: inout Double) {
        scales.append((time, 1))
        scales.append((time + TutorialMotion.ghostPressDuration, TutorialMotion.ghostPressScale))
        time += TutorialMotion.ghostPressDuration
    }

    private mutating func release(time: inout Double) {
        scales.append((time, TutorialMotion.ghostPressScale))
        scales.append((time + TutorialMotion.ghostReleaseDuration, 1))
        time += TutorialMotion.ghostReleaseDuration
    }

    private mutating func click(at point: CGPoint, time: inout Double) {
        ripples.append(Ripple(center: point, time: time))
        press(time: &time)
        release(time: &time)
    }

    static func cumulative(_ path: [CGPoint]) -> [Double] {
        var lengths = [0.0]
        for index in path.indices.dropFirst() {
            let step = hypot(path[index].x - path[index - 1].x, path[index].y - path[index - 1].y)
            lengths.append(lengths[index - 1] + Double(step))
        }
        return lengths
    }

    static func point(along path: [CGPoint], lengths: [Double], distance: Double) -> CGPoint {
        guard let last = path.last else { return .zero }
        for index in 1..<path.count where lengths[index] >= distance {
            let span = lengths[index] - lengths[index - 1]
            let t = CGFloat(span > 0 ? (distance - lengths[index - 1]) / span : 1)
            let a = path[index - 1], b = path[index]
            return CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t)
        }
        return last
    }

    /// The smooth line through the clicked points and where each point falls on it.
    static func curve(through points: [CGPoint]) -> (points: [CGPoint], anchors: [Int]) {
        let segments = 12
        guard points.count >= 3 else { return (points, Array(points.indices)) }
        let smooth = TutorialMotion.smooth(points.map { Point(Double($0.x), Double($0.y)) }, segments: segments)
        return (smooth.map { CGPoint(x: $0.x, y: $0.y) }, points.indices.map { $0 * segments })
    }

    static func rect(_ a: CGPoint, _ b: CGPoint) -> CGRect {
        CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
    }
}

/// The ghost pointer's look: a hollow arrow outline, dark under white, over a soft accent halo centered on its tip.
enum TutorialGhostArt {
    /// The outline in points with the tip at the origin and y pointing down.
    static let pointer: [CGPoint] = [
        CGPoint(x: 0, y: 0), CGPoint(x: 0, y: 16), CGPoint(x: 4, y: 12.5), CGPoint(x: 7, y: 18.5),
        CGPoint(x: 9.2, y: 17.5), CGPoint(x: 6.3, y: 11.6), CGPoint(x: 11.5, y: 11.6)
    ]
    static let haloRadius: CGFloat = 18
    static let haloOpacity: CGFloat = 0.22
    static let outlineWidth: CGFloat = 3.5
    static let outlineOpacity: CGFloat = 0.55
    static let lineWidth: CGFloat = 1.8

    /// The outline as a path. `yUp` mirrors it for a layer whose y points up.
    static func pointerPath(yUp: Bool) -> CGPath {
        let path = CGMutablePath()
        for (index, point) in pointer.enumerated() {
            let mapped = CGPoint(x: point.x, y: yUp ? -point.y : point.y)
            if index == 0 { path.move(to: mapped) } else { path.addLine(to: mapped) }
        }
        path.closeSubpath()
        return path
    }
}

final class TutorialOverlayPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
}

/// Decorative: it never takes a click and VoiceOver skips it.
final class TutorialOverlayView: NSView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

/// The tutorial's click-through layer above the toolbar: the ghost pointer, its clicks and ink, the ring around the
/// control to use, and the confetti. Every animation plays once, is owned here and ends or is removed.
@MainActor
final class TutorialOverlay {
    private var panels: [TutorialOverlayPanel] = []
    private var demo: CALayer?
    private var ring: CAShapeLayer?
    private var bursts: [(token: Int, layer: CALayer, end: Task<Void, Never>)] = []
    private var burstToken = 0
    private var fading: [(token: Int, layer: CALayer)] = []
    private var fadeToken = 0
    private var demoEnd: Task<Void, Never>?
    /// The ring's frame in screen points, outset included.
    private(set) var ringFrame: CGRect?
    private(set) var ghostRuns = 0
    /// Bursts started.
    private(set) var celebrations = 0
    var isPlayingDemo: Bool { demo != nil }
    var isRingVisible: Bool { ring != nil }
    var isCelebrating: Bool { !bursts.isEmpty }
    var windows: [NSPanel] { panels }

    // MARK: Ghost

    /// Plays `run` once and returns its timeline.
    @discardableResult
    func play(_ run: TutorialGhostRun, level: NSWindow.Level) -> TutorialGhostTimeline {
        stopDemo(animated: false)
        let timeline = TutorialGhostTimeline(run)
        guard let view = surface(on: run.display, level: level), let root = view.layer else { return timeline }
        ghostRuns += 1
        let origin = run.display.origin
        func local(_ point: CGPoint) -> CGPoint { CGPoint(x: point.x - origin.x, y: point.y - origin.y) }
        let scale = view.window?.backingScaleFactor ?? 2
        let accent = Self.accent(in: view)
        let duration = max(0.01, timeline.duration)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        let container = CALayer()
        container.frame = root.bounds
        root.addSublayer(container)
        let begin = container.convertTime(CACurrentMediaTime(), from: nil)
        func keyframes(_ keyPath: String, _ times: [Double], _ values: [Any]) -> CAKeyframeAnimation {
            Self.keyframes(keyPath, times: times, values: values, duration: duration, begin: begin)
        }

        for ink in timeline.inks {
            let layer = Self.strokeLayer(color: accent, width: CGFloat(TutorialMotion.inkWidth), scale: scale)
            layer.opacity = 0
            switch ink.style {
            case .line:
                layer.path = Self.polyline(ink.line.map(local))
                layer.add(keyframes("strokeEnd", ink.reveal.map { $0.time }, ink.reveal.map { NSNumber(value: $0.value) }),
                          forKey: "tutorial.reveal")
            case .rectangle:
                let boxes = ink.boxes.map { box in
                    CGPath(rect: CGRect(origin: local(box.rect.origin), size: box.rect.size), transform: nil)
                }
                layer.path = boxes.last
                layer.add(keyframes("path", ink.boxes.map { $0.time }, boxes), forKey: "tutorial.box")
            }
            let fade = ink.release + TutorialMotion.inkFadeDelay
            layer.add(keyframes("opacity", [0, ink.start, ink.start, fade, ink.end],
                                [0, 0, TutorialMotion.inkOpacity, TutorialMotion.inkOpacity, 0].map { NSNumber(value: $0) }),
                      forKey: "tutorial.opacity")
            container.addSublayer(layer)
        }

        for ripple in timeline.ripples {
            let layer = Self.strokeLayer(color: accent, width: 2, scale: scale)
            let center = local(ripple.center)
            var times: [Double] = [0, ripple.time]
            var paths: [Any] = [Self.circle(center, radius: TutorialMotion.rippleStartRadius)]
            paths.append(paths[0])
            var opacities: [Any] = [NSNumber(value: 0), NSNumber(value: 0)]
            for index in 0...10 {
                let progress = Double(index) / 10
                let grow = MotionCurves.enter.progress(at: progress)
                let radius = TutorialMotion.rippleStartRadius + (TutorialMotion.rippleEndRadius - TutorialMotion.rippleStartRadius) * grow
                times.append(ripple.time + TutorialMotion.rippleDuration * progress)
                paths.append(Self.circle(center, radius: radius))
                opacities.append(NSNumber(value: TutorialMotion.rippleStartOpacity * (1 - MotionCurves.fade.progress(at: progress))))
            }
            layer.path = Self.circle(center, radius: TutorialMotion.rippleEndRadius)
            layer.opacity = 0
            layer.add(keyframes("path", times, paths), forKey: "tutorial.path")
            layer.add(keyframes("opacity", times, opacities), forKey: "tutorial.opacity")
            container.addSublayer(layer)
        }

        let ghost = Self.ghostLayer(accent: accent, scale: scale)
        ghost.position = local(run.start)
        ghost.opacity = 0
        ghost.add(keyframes("position", timeline.positions.map { $0.time },
                            timeline.positions.map { NSValue(point: local($0.point)) }), forKey: "tutorial.position")
        ghost.add(keyframes("transform.scale", timeline.scales.map { $0.time },
                            timeline.scales.map { NSNumber(value: $0.value) }), forKey: "tutorial.scale")
        ghost.add(keyframes("opacity", timeline.opacities.map { $0.time },
                            timeline.opacities.map { NSNumber(value: $0.value) }), forKey: "tutorial.opacity")
        container.addSublayer(ghost)
        CATransaction.commit()

        demo = container
        demoEnd = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(duration + 0.05))
            guard let self, !Task.isCancelled else { return }
            self.demoEnd = nil
            self.stopDemo(animated: false)
        }
        return timeline
    }

    /// Stops the ghost; it fades out quickly unless `animated` is false.
    func stopDemo(animated: Bool = true) {
        demoEnd?.cancel()
        demoEnd = nil
        guard let demo else { return }
        self.demo = nil
        fadeOut(demo, animated: animated, duration: TutorialMotion.targetFadeDuration)
    }

    // MARK: Ring

    /// Rings the control at `anchor` (screen points). A new ring pulses in once; a ring that is already there moves.
    func showRing(around anchor: CGRect, level: NSWindow.Level, pulse: Bool) {
        let outset = CGFloat(TutorialMotion.targetOutset)
        let rect = anchor.insetBy(dx: -outset, dy: -outset)
        guard rect.width > 0, rect.height > 0, let screen = TutorialController.screen(for: rect),
              let view = surface(on: screen.frame, level: level), let root = view.layer else { return }
        let local = CGRect(x: rect.minX - screen.frame.minX, y: rect.minY - screen.frame.minY,
                           width: rect.width, height: rect.height)
        if let ring, ring.superlayer === root {
            guard ringFrame != rect else { return }
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            Self.place(ring, in: local)
            CATransaction.commit()
            ringFrame = rect
            return
        }
        hideRing(animated: false)
        let layer = Self.strokeLayer(color: Self.accent(in: view), width: CGFloat(TutorialMotion.targetWidth),
                                     scale: view.window?.backingScaleFactor ?? 2)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        Self.place(layer, in: local)
        root.addSublayer(layer)
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = 0
        fade.toValue = 1
        fade.timingFunction = Motion.fade
        if pulse {
            let grow = CABasicAnimation(keyPath: "transform.scale")
            grow.fromValue = TutorialMotion.targetPulseScale
            grow.toValue = 1
            grow.duration = TutorialMotion.targetPulseDuration
            grow.timingFunction = Motion.enter
            layer.add(grow, forKey: "tutorial.pulse")
            fade.duration = TutorialMotion.targetPulseDuration / 2
        } else {
            fade.duration = TutorialMotion.targetFadeDuration
        }
        layer.add(fade, forKey: "tutorial.fade")
        CATransaction.commit()
        ring = layer
        ringFrame = rect
    }

    func hideRing(animated: Bool) {
        guard let ring else { return }
        self.ring = nil
        ringFrame = nil
        fadeOut(ring, animated: animated, duration: TutorialMotion.targetFadeDuration)
    }

    // MARK: Confetti

    /// Pops confetti from both bottom corners of `display` (screen points). Each burst ends on its own when its last piece
    /// lands; a new one may start while another falls.
    func celebrate(on display: CGRect, level: NSWindow.Level) {
        guard let view = surface(on: display, level: level), let root = view.layer else { return }
        celebrations += 1
        let height = Double(display.height)
        let pieces = ConfettiBurst.pieces(width: Double(display.width), height: height)
        let colors = ConfettiBurst.palette.map { CGColor(srgbRed: $0.red, green: $0.green, blue: $0.blue, alpha: $0.alpha) }
        let scale = view.window?.backingScaleFactor ?? 2
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        let container = CALayer()
        container.frame = root.bounds
        root.addSublayer(container)
        let begin = container.convertTime(CACurrentMediaTime(), from: nil)
        for piece in pieces {
            let size = ConfettiBurst.shapeSizes[piece.shape]
            let layer = CALayer()
            layer.bounds = CGRect(x: 0, y: 0, width: size.width, height: size.height)
            layer.cornerRadius = piece.shape == 1 ? CGFloat(size.width / 2) : 1
            layer.backgroundColor = colors[piece.color]
            layer.contentsScale = scale
            layer.opacity = 0
            layer.position = CGPoint(x: piece.start.x, y: height - piece.start.y)
            let steps = max(2, Int((piece.lifetime * 30).rounded()))
            var times: [Double] = []
            var positions: [Any] = []
            var transforms: [Any] = []
            var opacities: [Any] = []
            for index in 0...steps {
                let fraction = Double(index) / Double(steps)
                guard let state = ConfettiBurst.state(piece, at: piece.delay + piece.lifetime * fraction,
                                                      height: height) else { continue }
                times.append(fraction * piece.lifetime)
                positions.append(NSValue(point: CGPoint(x: state.position.x, y: height - state.position.y)))
                // Turns are clockwise with y pointing down, so they reverse with y pointing up.
                let turn = CATransform3DMakeRotation(CGFloat(-state.angle * Double.pi / 180), 0, 0, 1)
                transforms.append(NSValue(caTransform3D: CATransform3DScale(turn, CGFloat(state.widthScale), 1, 1)))
                opacities.append(NSNumber(value: state.opacity))
            }
            guard times.count >= 2 else { continue }
            let group = CAAnimationGroup()
            group.animations = [
                Self.keyframes("position", times: times, values: positions, duration: piece.lifetime, begin: 0),
                Self.keyframes("transform", times: times, values: transforms, duration: piece.lifetime, begin: 0),
                Self.keyframes("opacity", times: times, values: opacities, duration: piece.lifetime, begin: 0)
            ]
            group.duration = piece.lifetime
            group.beginTime = begin + piece.delay
            group.fillMode = .forwards
            group.isRemovedOnCompletion = false
            layer.add(group, forKey: "tutorial.confetti")
            container.addSublayer(layer)
        }
        CATransaction.commit()
        burstToken &+= 1
        let token = burstToken
        let landed = pieces.map { $0.delay + $0.lifetime }.max() ?? 0
        // Holds the overlay until the burst ends, even when the tutorial closes first.
        let end = Task { @MainActor [self] in
            try? await Task.sleep(for: .seconds(landed + 0.1))
            guard !Task.isCancelled else { return }
            self.endBurst(token)
        }
        bursts.append((token: token, layer: container, end: end))
    }

    /// Removes every burst at once.
    func stopCelebration() {
        guard !bursts.isEmpty else { return }
        let ended = bursts
        bursts.removeAll()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for burst in ended {
            burst.end.cancel()
            burst.layer.removeFromSuperlayer()
        }
        CATransaction.commit()
        tidy()
    }

    private func endBurst(_ token: Int) {
        guard let index = bursts.firstIndex(where: { $0.token == token }) else { return }
        let burst = bursts.remove(at: index)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        burst.layer.removeFromSuperlayer()
        CATransaction.commit()
        tidy()
    }

    /// Removes everything and closes the windows.
    func stop() {
        stopDemo(animated: false)
        hideRing(animated: false)
        stopCelebration()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for entry in fading { entry.layer.removeFromSuperlayer() }
        CATransaction.commit()
        fading.removeAll()
        for panel in panels {
            panel.orderOut(nil)
            panel.contentView = nil
            panel.close()
        }
        panels.removeAll()
    }

    // MARK: Windows and layers

    /// The overlay window that covers `display`, created on first use and shown.
    private func surface(on display: CGRect, level: NSWindow.Level) -> NSView? {
        guard display.width > 0, display.height > 0 else { return nil }
        let panel: TutorialOverlayPanel
        if let existing = panels.first(where: { $0.frame == display }) {
            panel = existing
        } else {
            panel = TutorialOverlayPanel(contentRect: display, styleMask: [.borderless, .nonactivatingPanel],
                                         backing: .buffered, defer: false)
            panel.setFrame(display, display: false)
            panel.title = "Annotator tutorial guide"
            panel.isReleasedWhenClosed = false
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.hasShadow = false
            panel.ignoresMouseEvents = true
            panel.hidesOnDeactivate = false
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
            let view = TutorialOverlayView(frame: CGRect(origin: .zero, size: display.size))
            view.setAccessibilityElement(false)
            let root = CALayer()
            root.contentsScale = panel.backingScaleFactor
            view.layer = root
            view.wantsLayer = true
            panel.contentView = view
            root.frame = view.bounds
            panels.append(panel)
        }
        panel.level = level
        if !panel.isVisible { panel.orderFrontRegardless() }
        return panel.contentView
    }

    /// Fades `layer` out and removes it, or removes it at once.
    private func fadeOut(_ layer: CALayer, animated: Bool, duration: Double) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }
        guard animated, layer.superlayer != nil else {
            layer.removeFromSuperlayer()
            tidy()
            return
        }
        let current = layer.presentation()?.opacity ?? layer.opacity
        layer.opacity = 0
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = current
        fade.toValue = 0
        fade.duration = duration
        fade.timingFunction = Motion.fade
        layer.add(fade, forKey: "tutorial.fadeOut")
        fadeToken &+= 1
        let token = fadeToken
        fading.append((token, layer))
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(duration + 0.02))
            self?.finishFade(token)
        }
    }

    private func finishFade(_ token: Int) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for entry in fading where entry.token == token { entry.layer.removeFromSuperlayer() }
        CATransaction.commit()
        fading.removeAll { $0.token == token }
        tidy()
    }

    /// Hides overlay windows with nothing left to show, so no window waits on screen.
    private func tidy() {
        for panel in panels where panel.isVisible && (panel.contentView?.layer?.sublayers ?? []).isEmpty {
            panel.orderOut(nil)
        }
    }

    private static func keyframes(_ keyPath: String, times: [Double], values: [Any], duration: Double,
                                  begin: CFTimeInterval) -> CAKeyframeAnimation {
        var keyTimes = times.map { min(1, max(0, $0 / duration)) }
        var keyValues = values
        if let first = keyTimes.first, first > 0, let value = keyValues.first {
            keyTimes.insert(0, at: 0)
            keyValues.insert(value, at: 0)
        }
        if let last = keyTimes.last, last < 1, let value = keyValues.last {
            keyTimes.append(1)
            keyValues.append(value)
        }
        let animation = CAKeyframeAnimation(keyPath: keyPath)
        animation.values = keyValues
        animation.keyTimes = keyTimes.map { NSNumber(value: $0) }
        animation.duration = duration
        animation.beginTime = begin
        animation.calculationMode = .linear
        animation.fillMode = .both
        animation.isRemovedOnCompletion = false
        return animation
    }

    private static func ghostLayer(accent: CGColor, scale: CGFloat) -> CALayer {
        let ghost = CALayer()
        ghost.bounds = .zero
        ghost.contentsScale = scale
        let halo = CAShapeLayer()
        let radius = TutorialGhostArt.haloRadius
        halo.path = CGPath(ellipseIn: CGRect(x: -radius, y: -radius, width: radius * 2, height: radius * 2), transform: nil)
        halo.fillColor = accent.copy(alpha: TutorialGhostArt.haloOpacity)
        halo.contentsScale = scale
        let path = TutorialGhostArt.pointerPath(yUp: true)
        let outline = strokeLayer(color: CGColor(gray: 0, alpha: TutorialGhostArt.outlineOpacity),
                                  width: TutorialGhostArt.outlineWidth, scale: scale)
        outline.path = path
        let line = strokeLayer(color: CGColor(gray: 1, alpha: 1), width: TutorialGhostArt.lineWidth, scale: scale)
        line.path = path
        for layer in [halo, outline, line] { ghost.addSublayer(layer) }
        return ghost
    }

    private static func strokeLayer(color: CGColor, width: CGFloat, scale: CGFloat) -> CAShapeLayer {
        let layer = CAShapeLayer()
        layer.fillColor = nil
        layer.strokeColor = color
        layer.lineWidth = width
        layer.lineCap = .round
        layer.lineJoin = .round
        layer.contentsScale = scale
        return layer
    }

    private static func place(_ ring: CAShapeLayer, in rect: CGRect) {
        let radius = min(CGFloat(TutorialMotion.targetCornerRadius), rect.width / 2, rect.height / 2)
        ring.bounds = CGRect(origin: .zero, size: rect.size)
        ring.position = CGPoint(x: rect.midX, y: rect.midY)
        ring.path = CGPath(roundedRect: ring.bounds, cornerWidth: radius, cornerHeight: radius, transform: nil)
    }

    private static func polyline(_ points: [CGPoint]) -> CGPath {
        let path = CGMutablePath()
        if let first = points.first {
            path.move(to: first)
            for point in points.dropFirst() { path.addLine(to: point) }
        }
        return path
    }

    private static func circle(_ center: CGPoint, radius: Double) -> CGPath {
        let r = CGFloat(radius)
        return CGPath(ellipseIn: CGRect(x: center.x - r, y: center.y - r, width: r * 2, height: r * 2), transform: nil)
    }

    private static func accent(in view: NSView) -> CGColor {
        var color = NSColor.controlAccentColor.cgColor
        view.effectiveAppearance.performAsCurrentDrawingAppearance {
            color = NSColor.controlAccentColor.cgColor
        }
        return color
    }
}
