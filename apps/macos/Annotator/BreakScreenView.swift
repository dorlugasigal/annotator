import AppKit
import QuartzCore
import SwiftUI
import AnnotationCore

// MARK: Colors

struct BreakPaletteColors {
    let top: NSColor
    let bottom: NSColor
    let fields: [NSColor]
    let accent: NSColor

    static let warning = NSColor(srgbRed: 1.0, green: 0.71, blue: 0.28, alpha: 1)
}

extension BreakTimerSettings.Palette {
    var colors: BreakPaletteColors {
        func color(_ hex: UInt32) -> NSColor {
            NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                    blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
        }
        switch self {
        case .forest:
            return BreakPaletteColors(top: color(0x16241C), bottom: color(0x0A110D),
                                      fields: [color(0x2F6B4F), color(0x7FAF73), color(0x1F4D5C)], accent: color(0x9BE3B4))
        case .ocean:
            return BreakPaletteColors(top: color(0x0D1D33), bottom: color(0x06101D),
                                      fields: [color(0x1F5FA8), color(0x3FB6C9), color(0x4B3F9E)], accent: color(0x8FD3FF))
        case .sunset:
            return BreakPaletteColors(top: color(0x2C1521), bottom: color(0x170A12),
                                      fields: [color(0xD9604A), color(0xF0A458), color(0x8E3B7A)], accent: color(0xFFC995))
        case .berry:
            return BreakPaletteColors(top: color(0x24132F), bottom: color(0x12091B),
                                      fields: [color(0x7D3FC4), color(0xC9558F), color(0x3D4FC0)], accent: color(0xF0B0FF))
        case .graphite:
            return BreakPaletteColors(top: color(0x1C1E21), bottom: color(0x0E0F11),
                                      fields: [color(0x3C4450), color(0x5D6B7A), color(0x2B3138)], accent: color(0xE8ECEF))
        }
    }
}

// MARK: What every break screen shows

struct BreakFaceState: Equatable {
    enum Phase: Equatable { case running, lastMinute, paused, timeUp }

    var settings: BreakTimerSettings
    var label: String
    var fraction: Double
    /// Seconds until the ring is empty; `nil` while it stands still.
    var secondsToEmpty: TimeInterval?
    var ringGeneration: Int
    var phase: Phase
    var endTime: String?
    var lock: BreakTimerController.LockState
    var controlsVisible: Bool
    var rippleGeneration: Int
    var badge: BreakTimerController.Badge?
    var closing: Bool
    var notice: String?
    var spokenValue: String

    var title: String { phase == .timeUp ? "Time's up" : settings.title }

    static func preview(_ settings: BreakTimerSettings, now: Date = Date()) -> BreakFaceState {
        BreakFaceState(settings: settings, label: BreakCountdown.clock(seconds: settings.minutes * 60),
                       fraction: 0.72, secondsToEmpty: nil, ringGeneration: 0, phase: .running,
                       endTime: settings.showEndTime ? BreakTimerController.endTimeText(now.addingTimeInterval(Double(settings.minutes * 60))) : nil,
                       lock: settings.lockWhenTouched ? .armed : .off, controlsVisible: false, rippleGeneration: 0,
                       badge: nil, closing: false, notice: nil,
                       spokenValue: "\(BreakCountdown.spoken(seconds: settings.minutes * 60)) left")
    }
}

struct BreakFaceActions {
    var removeMinute: () -> Void = {}
    var togglePause: () -> Void = {}
    var addMinute: () -> Void = {}
    var end: () -> Void = {}
    var hoverControls: (Bool) -> Void = { _ in }
}

// MARK: Face

struct BreakScreenFace: View {
    let state: BreakFaceState
    var preview = false
    var actions = BreakFaceActions()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var appeared = false

    private var palette: BreakPaletteColors { state.settings.palette.colors }
    private var ringColor: NSColor {
        switch state.phase {
        case .lastMinute, .timeUp: BreakPaletteColors.warning
        case .paused: palette.accent.withAlphaComponent(0.55)
        case .running: palette.accent
        }
    }

    var body: some View {
        GeometryReader { proxy in
            let unit = min(proxy.size.width, proxy.size.height)
            ZStack {
                BreakBackdrop(background: state.settings.background, palette: state.settings.palette,
                              moves: !reduceMotion, blending: preview ? .withinWindow : .behindWindow)
                    .opacity(state.closing ? 0 : 1)
                    .accessibilityHidden(true)
                clockGroup(unit: unit)
                    .scaleEffect(state.closing ? 0.96 : appeared ? 1 : (reduceMotion ? 1 : 0.94))
                    .offset(y: appeared || reduceMotion ? 0 : 24 * unit / 900)
                    .opacity(appeared && !state.closing ? 1 : 0)
                VStack {
                    noticeBar(unit: unit).padding(.top, unit * 0.05)
                    Spacer(minLength: 0)
                    bottomBar(unit: unit).padding(.bottom, unit * 0.06)
                }
                .opacity(state.closing ? 0 : 1)
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .environment(\.colorScheme, .dark)
        .animation(reduceMotion ? Motion.reduced : .easeIn(duration: 0.28), value: state.closing)
        .onAppear {
            if preview { appeared = true; return }
            withAnimation(reduceMotion ? Motion.reduced : Motion.smooth) { appeared = true }
        }
    }

    private func clockGroup(unit: CGFloat) -> some View {
        let ring = unit * 0.6
        let line = max(1.5, unit * 0.016)
        return VStack(spacing: unit * 0.04) {
            if !state.title.isEmpty {
                Text(state.title)
                    .font(.system(size: unit * 0.052, weight: .semibold, design: .rounded))
                    .foregroundStyle(.white.opacity(0.88))
                    .lineLimit(1).minimumScaleFactor(0.5)
                    .frame(maxWidth: unit * 1.3)
                    .id(state.title)
                    .transition(.opacity.combined(with: .scale(scale: reduceMotion ? 1 : 0.96)))
            }
            ZStack {
                BreakRipples(generation: state.rippleGeneration, color: Color(nsColor: BreakPaletteColors.warning),
                             lineWidth: line, reduceMotion: reduceMotion)
                BreakRingView(fraction: state.fraction, secondsToEmpty: state.secondsToEmpty,
                              generation: state.ringGeneration, color: ringColor, lineWidth: line,
                              breathing: !reduceMotion && (state.phase == .lastMinute || state.phase == .timeUp),
                              reduceMotion: reduceMotion)
                VStack(spacing: unit * 0.014) {
                    Text(state.label)
                        .font(.system(size: unit * 0.17, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .foregroundStyle(.white)
                        .lineLimit(1).minimumScaleFactor(0.4)
                        .contentTransition(reduceMotion ? .opacity : .numericText(countsDown: state.phase != .timeUp))
                        .animation(reduceMotion ? Motion.reduced : .spring(response: 0.42, dampingFraction: 0.86), value: state.label)
                        .opacity(state.phase == .paused ? 0.45 : 1)
                        .frame(maxWidth: ring * 0.82)
                    subline(unit: unit)
                }
                if let badge = state.badge {
                    Text(badge.text)
                        .font(.system(size: unit * 0.034, weight: .semibold, design: .rounded))
                        .foregroundStyle(Color(nsColor: palette.accent))
                        .offset(x: ring * 0.36, y: -ring * 0.38)
                        .id(badge.id)
                        .transition(.asymmetric(
                            insertion: .scale(scale: reduceMotion ? 1 : 0.7).combined(with: .opacity),
                            removal: reduceMotion ? .opacity : .offset(y: -unit * 0.05).combined(with: .opacity)))
                        .accessibilityHidden(true)
                }
            }
            .frame(width: ring, height: ring)
            .animation(reduceMotion ? Motion.reduced : Motion.playful, value: state.badge)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Break timer")
            .accessibilityValue(state.spokenValue)
            .accessibilityIdentifier("break-countdown")
        }
        .animation(reduceMotion ? Motion.reduced : Motion.smooth, value: state.title)
        .animation(reduceMotion ? Motion.reduced : Motion.snappy, value: state.phase)
    }

    @ViewBuilder private func subline(unit: CGFloat) -> some View {
        if state.phase == .paused {
            Text("Paused")
                .font(.system(size: unit * 0.03, weight: .semibold, design: .rounded))
                .foregroundStyle(.white)
                .padding(.horizontal, unit * 0.022).padding(.vertical, unit * 0.008)
                .background(Capsule().fill(.white.opacity(0.16)))
                .transition(.scale(scale: reduceMotion ? 1 : 0.6).combined(with: .opacity))
        } else if state.phase == .timeUp, state.settings.countOver, state.label != "0:00" {
            Text("over time")
                .font(.system(size: unit * 0.03, weight: .medium, design: .rounded))
                .foregroundStyle(.white.opacity(0.7))
                .transition(.opacity)
        } else if let endTime = state.endTime, state.phase != .timeUp {
            Text(endTime)
                .font(.system(size: unit * 0.032, weight: .medium, design: .rounded))
                .foregroundStyle(.white.opacity(0.7))
                .transition(.opacity)
        }
    }

    @ViewBuilder private func bottomBar(unit: CGFloat) -> some View {
        let text = unit * 0.021
        VStack(spacing: unit * 0.02) {
            switch state.lock {
            case .arming(let seconds):
                lockChip("lock.open.fill", "Locking in \(seconds) s. Press Esc to cancel.", size: text)
                    .contentTransition(.numericText(countsDown: true))
            case .armed, .locking:
                lockChip("lock.fill", "This Mac locks if anyone touches it.", size: text)
            case .off:
                if state.controlsVisible && !preview {
                    controls(size: text)
                        .transition(reduceMotion ? .opacity : .move(edge: .bottom).combined(with: .opacity))
                }
            }
        }
        .animation(reduceMotion ? Motion.reduced : Motion.snappy, value: state.controlsVisible)
        .animation(reduceMotion ? Motion.reduced : Motion.snappy, value: state.lock)
    }

    @ViewBuilder private func noticeBar(unit: CGFloat) -> some View {
        let text = unit * 0.021
        if let notice = state.notice {
            Label(notice, systemImage: "exclamationmark.triangle.fill")
                .font(.system(size: text, weight: .medium, design: .rounded))
                .foregroundStyle(.white)
                .multilineTextAlignment(.leading)
                .padding(.horizontal, text * 1.1).padding(.vertical, text * 0.7)
                .background(RoundedRectangle(cornerRadius: text * 1.2).fill(Color.orange.opacity(0.32)))
                .frame(maxWidth: unit * 1.1)
                .transition(reduceMotion ? .opacity : .move(edge: .top).combined(with: .opacity))
                .accessibilityIdentifier("break-notice")
        }
    }

    private func lockChip(_ symbol: String, _ text: String, size: CGFloat) -> some View {
        HStack(spacing: size * 0.5) {
            Image(systemName: symbol).font(.system(size: size * 0.9, weight: .semibold))
            Text(text).font(.system(size: size, weight: .medium, design: .rounded))
        }
        .foregroundStyle(.white.opacity(0.85))
        .padding(.horizontal, size * 1.1).padding(.vertical, size * 0.6)
        .background(Capsule().fill(.white.opacity(0.1)))
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("break-lock-status")
    }

    private func controls(size: CGFloat) -> some View {
        let paused = state.phase == .paused
        return HStack(spacing: size * 0.5) {
            BreakControlButton(symbol: "minus", title: "1 min", label: "Remove a minute", size: size,
                               identifier: "break-remove-minute", action: actions.removeMinute)
                .disabled(state.phase == .timeUp)
            BreakControlButton(symbol: paused ? "play.fill" : "pause.fill", title: paused ? "Resume" : "Pause",
                               label: paused ? "Resume" : "Pause", size: size, identifier: "break-pause",
                               action: actions.togglePause)
                .disabled(state.phase == .timeUp)
            BreakControlButton(symbol: "plus", title: "1 min", label: "Add a minute", size: size,
                               identifier: "break-add-minute", action: actions.addMinute)
            Rectangle().fill(.white.opacity(0.18)).frame(width: 1, height: size * 1.6).accessibilityHidden(true)
            BreakControlButton(symbol: "xmark", title: "End break", label: "End break", size: size,
                               identifier: "break-end", prominent: true, action: actions.end)
        }
        .padding(size * 0.45)
        .background(Capsule().fill(.ultraThinMaterial))
        .overlay(Capsule().strokeBorder(.white.opacity(0.12)))
        .onHover { actions.hoverControls($0) }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Break controls")
    }
}

private struct BreakControlButton: View {
    let symbol: String
    let title: String
    let label: String
    let size: CGFloat
    let identifier: String
    var prominent = false
    let action: () -> Void
    @State private var hovered = false
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        Button(action: action) {
            HStack(spacing: size * 0.35) {
                Image(systemName: symbol).font(.system(size: size * 0.85, weight: .semibold))
                Text(title).font(.system(size: size, weight: .semibold, design: .rounded))
            }
            .foregroundStyle(prominent ? Color.black.opacity(0.85) : .white)
            .padding(.horizontal, size * 0.95).padding(.vertical, size * 0.55)
            .background(Capsule().fill(prominent ? Color.white.opacity(hovered ? 1 : 0.88) : .white.opacity(hovered ? 0.18 : 0.08)))
            .opacity(enabled ? 1 : 0.4)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .onHover { hovered = $0 }
        .accessibilityLabel(label)
        .accessibilityIdentifier(identifier)
    }
}

// MARK: Ripples at time up

private struct BreakRipples: View {
    let generation: Int
    let color: Color
    let lineWidth: CGFloat
    let reduceMotion: Bool

    var body: some View {
        ZStack {
            ForEach(0..<3, id: \.self) { index in
                Circle()
                    .stroke(color, lineWidth: lineWidth * 0.6)
                    .keyframeAnimator(initialValue: Ripple(), trigger: generation) { content, value in
                        content.scaleEffect(value.scale).opacity(reduceMotion ? 0 : value.opacity)
                    } keyframes: { _ in
                        KeyframeTrack(\.scale) {
                            LinearKeyframe(1, duration: 0.22 * Double(index))
                            CubicKeyframe(1.85, duration: 1.6)
                        }
                        KeyframeTrack(\.opacity) {
                            LinearKeyframe(0, duration: 0.22 * Double(index))
                            LinearKeyframe(0.75, duration: 0.05)
                            CubicKeyframe(0, duration: 1.55)
                        }
                    }
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private struct Ripple { var scale = 1.0; var opacity = 0.0 }
}

// MARK: Ring (Core Animation)

/// The ring empties with one Core Animation animation, so a long break costs no main-thread frames.
struct BreakRingView: NSViewRepresentable {
    var fraction: Double
    var secondsToEmpty: TimeInterval?
    var generation: Int
    var color: NSColor
    var lineWidth: CGFloat
    var breathing: Bool
    var reduceMotion: Bool

    func makeNSView(context: Context) -> BreakRingLayerView { BreakRingLayerView() }
    func updateNSView(_ view: BreakRingLayerView, context: Context) { view.apply(self) }
}

final class BreakRingLayerView: NSView {
    private let container = CALayer()
    private let track = CAShapeLayer()
    private let glow = CAShapeLayer()
    private let ring = CAShapeLayer()
    private var current: BreakRingView?
    private var appliedGeneration: Int?
    private var appliedSize = CGSize.zero
    private var appliedBreathing = false

    init() {
        super.init(frame: .zero)
        wantsLayer = true
        layer?.addSublayer(container)
        for shape in [track, glow, ring] {
            shape.fillColor = nil
            shape.lineCap = .round
            shape.strokeEnd = 1
            container.addSublayer(shape)
        }
        glow.opacity = 0.22
        ring.strokeEnd = 0
        glow.strokeEnd = 0
        track.strokeColor = NSColor.white.withAlphaComponent(0.1).cgColor
    }

    @available(*, unavailable) required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    /// The ring's visible fill right now, for tests.
    var displayedFraction: Double { Double(ring.presentation()?.strokeEnd ?? ring.strokeEnd) }
    var isDraining: Bool { ring.animation(forKey: "drain") != nil }

    override func layout() {
        super.layout()
        guard bounds.size != appliedSize, bounds.width > 0, bounds.height > 0 else { return }
        appliedSize = bounds.size
        CATransaction.begin(); CATransaction.setDisableActions(true)
        container.frame = bounds
        let side = min(bounds.width, bounds.height)
        let width = current?.lineWidth ?? 8
        let radius = max(1, side / 2 - width * 1.4)
        let center = CGPoint(x: bounds.midX, y: bounds.midY)
        let path = CGMutablePath()
        path.addArc(center: center, radius: radius, startAngle: .pi / 2, endAngle: .pi / 2 - 2 * .pi, clockwise: true)
        for shape in [track, glow, ring] { shape.path = path; shape.frame = bounds }
        container.anchorPoint = CGPoint(x: 0.5, y: 0.5)
        container.position = center
        CATransaction.commit()
        if let current {
            let first = appliedGeneration != current.generation
            restart(current, settle: first ? (current.reduceMotion ? 0.2 : 0.6) : 0)
        }
    }

    func apply(_ next: BreakRingView) {
        let previous = current
        current = next
        CATransaction.begin(); CATransaction.setDisableActions(true)
        track.lineWidth = next.lineWidth
        ring.lineWidth = next.lineWidth
        glow.lineWidth = next.lineWidth * 2.6
        CATransaction.commit()
        if previous?.lineWidth != next.lineWidth { appliedSize = .zero; needsLayout = true }
        if let previous, !previous.color.isEqual(next.color) {
            for shape in [ring, glow] {
                let change = CABasicAnimation(keyPath: "strokeColor")
                change.fromValue = shape.presentation()?.strokeColor ?? shape.strokeColor
                change.toValue = next.color.cgColor
                change.duration = next.reduceMotion ? 0.2 : 1
                change.timingFunction = Motion.standard
                shape.add(change, forKey: "color")
            }
        }
        CATransaction.begin(); CATransaction.setDisableActions(true)
        ring.strokeColor = next.color.cgColor
        glow.strokeColor = next.color.cgColor
        CATransaction.commit()
        if appliedGeneration != next.generation, appliedSize != .zero {
            appliedGeneration = next.generation
            restart(next, settle: next.reduceMotion ? 0.2 : 0.6)
        }
        if appliedBreathing != next.breathing {
            appliedBreathing = next.breathing
            if next.breathing {
                let breathe = CABasicAnimation(keyPath: "transform.scale")
                breathe.fromValue = 1
                breathe.toValue = 1.018
                breathe.duration = 1.1
                breathe.autoreverses = true
                breathe.repeatCount = .infinity
                breathe.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                container.add(breathe, forKey: "breathe")
            } else {
                container.removeAnimation(forKey: "breathe")
            }
        }
    }

    private func restart(_ config: BreakRingView, settle: TimeInterval) {
        appliedGeneration = config.generation
        let start = ring.presentation()?.strokeEnd ?? ring.strokeEnd
        let target = CGFloat(min(max(config.fraction, 0), 1))
        ring.removeAnimation(forKey: "drain"); glow.removeAnimation(forKey: "drain")
        let animation: CAAnimation
        let final: CGFloat
        if let seconds = config.secondsToEmpty, seconds.isFinite, seconds > settle, target > 0 {
            let drain = CAKeyframeAnimation(keyPath: "strokeEnd")
            let settled = target * CGFloat((seconds - settle) / seconds)
            drain.values = [start, settled, 0]
            drain.keyTimes = [0, NSNumber(value: settle / seconds), 1]
            drain.timingFunctions = [Motion.enter, CAMediaTimingFunction(name: .linear)]
            drain.duration = seconds
            // The model keeps the current fill so captures and snapshots show it; the animation holds
            // the empty end until the next change replaces it.
            drain.fillMode = .forwards
            drain.isRemovedOnCompletion = false
            animation = drain
            final = target
        } else {
            let move = CABasicAnimation(keyPath: "strokeEnd")
            move.fromValue = start
            move.toValue = target
            move.duration = max(settle, 0.001)
            move.timingFunction = Motion.enter
            animation = move
            final = target
        }
        CATransaction.begin(); CATransaction.setDisableActions(true)
        ring.strokeEnd = final
        glow.strokeEnd = final
        CATransaction.commit()
        ring.removeAnimation(forKey: "settle"); glow.removeAnimation(forKey: "settle")
        let key = animation is CAKeyframeAnimation ? "drain" : "settle"
        if settle > 0 || key == "drain" {
            ring.add(animation, forKey: key)
            glow.add(animation, forKey: key)
        }
    }
}

// MARK: Background (Core Animation)

struct BreakBackdrop: NSViewRepresentable {
    var background: BreakTimerSettings.Background
    var palette: BreakTimerSettings.Palette
    var moves: Bool
    var blending: NSVisualEffectView.BlendingMode

    func makeNSView(context: Context) -> BreakBackdropView { BreakBackdropView() }
    func updateNSView(_ view: BreakBackdropView, context: Context) {
        view.configure(background: background, palette: palette, moves: moves, blending: blending)
    }
}

final class BreakBackdropView: NSView {
    private var effect: NSVisualEffectView?
    /// Holds every color layer above the optional system blur, so their order never depends on AppKit.
    private let canvas = NSView()
    private var configuration: (BreakTimerSettings.Background, BreakTimerSettings.Palette, Bool, NSVisualEffectView.BlendingMode)?
    private var laidOutSize = CGSize.zero
    private(set) var drifting = false

    init() {
        super.init(frame: .zero)
        wantsLayer = true
        layer?.masksToBounds = true
        canvas.wantsLayer = true
        canvas.autoresizingMask = [.width, .height]
        addSubview(canvas)
    }

    @available(*, unavailable) required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    var usesSystemBlur: Bool { effect != nil }

    func configure(background: BreakTimerSettings.Background, palette: BreakTimerSettings.Palette,
                   moves: Bool, blending: NSVisualEffectView.BlendingMode) {
        if let configuration, configuration == (background, palette, moves, blending) { return }
        configuration = (background, palette, moves, blending)
        rebuild()
    }

    override func layout() {
        super.layout()
        guard bounds.size != laidOutSize else { return }
        laidOutSize = bounds.size
        rebuild()
    }

    private func rebuild() {
        guard let (background, palette, moves, blending) = configuration else { return }
        canvas.frame = bounds
        guard let root = canvas.layer else { return }
        let colors = palette.colors
        CATransaction.begin(); CATransaction.setDisableActions(true)
        root.sublayers?.forEach { $0.removeFromSuperlayer() }
        drifting = false
        if background == .blur {
            if effect == nil {
                let view = NSVisualEffectView(frame: bounds)
                view.autoresizingMask = [.width, .height]
                view.material = .fullScreenUI
                view.state = .active
                view.appearance = NSAppearance(named: .darkAqua)
                addSubview(view, positioned: .below, relativeTo: canvas)
                effect = view
            }
            effect?.blendingMode = blending
            let tint = CALayer()
            tint.frame = bounds
            tint.backgroundColor = colors.bottom.withAlphaComponent(0.55).cgColor
            root.addSublayer(tint)
        } else {
            effect?.removeFromSuperview()
            effect = nil
            let base = CAGradientLayer()
            base.frame = bounds
            base.colors = [colors.top.cgColor, colors.bottom.cgColor]
            base.startPoint = CGPoint(x: 0.5, y: 1)
            base.endPoint = CGPoint(x: 0.5, y: 0)
            root.addSublayer(base)
        }
        if background == .moving {
            let side = max(bounds.width, bounds.height) * 0.9
            let anchors = [CGPoint(x: 0.18, y: 0.78), CGPoint(x: 0.82, y: 0.28), CGPoint(x: 0.55, y: 0.5)]
            let travel = [CGSize(width: 0.2, height: -0.14), CGSize(width: -0.18, height: 0.16), CGSize(width: -0.12, height: -0.1)]
            let periods: [CFTimeInterval] = [22, 29, 37]
            for (index, color) in colors.fields.enumerated() {
                let field = CAGradientLayer()
                field.type = .radial
                field.colors = [color.withAlphaComponent(0.62).cgColor, color.withAlphaComponent(0).cgColor]
                field.startPoint = CGPoint(x: 0.5, y: 0.5)
                field.endPoint = CGPoint(x: 1, y: 1)
                field.bounds = CGRect(x: 0, y: 0, width: side, height: side)
                let anchor = anchors[index % anchors.count]
                field.position = CGPoint(x: bounds.width * anchor.x, y: bounds.height * anchor.y)
                root.addSublayer(field)
                guard moves, bounds.width > 0 else { continue }
                let offset = travel[index % travel.count]
                let drift = CABasicAnimation(keyPath: "position")
                drift.fromValue = field.position
                drift.toValue = CGPoint(x: field.position.x + bounds.width * offset.width,
                                        y: field.position.y + bounds.height * offset.height)
                drift.duration = periods[index % periods.count]
                drift.autoreverses = true
                drift.repeatCount = .infinity
                drift.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                field.add(drift, forKey: "drift")
                let swell = CABasicAnimation(keyPath: "transform.scale")
                swell.fromValue = 1
                swell.toValue = 1.16
                swell.duration = periods[(index + 1) % periods.count] * 0.8
                swell.autoreverses = true
                swell.repeatCount = .infinity
                swell.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                field.add(swell, forKey: "swell")
                drifting = true
            }
        }
        let vignette = CAGradientLayer()
        vignette.frame = bounds
        vignette.type = .radial
        vignette.colors = [NSColor.black.withAlphaComponent(0).cgColor, NSColor.black.withAlphaComponent(0.42).cgColor]
        vignette.locations = [0.45, 1]
        vignette.startPoint = CGPoint(x: 0.5, y: 0.5)
        vignette.endPoint = CGPoint(x: 1.15, y: 1.15)
        root.addSublayer(vignette)
        CATransaction.commit()
    }
}
