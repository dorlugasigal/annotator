import AppKit
import SwiftUI
import AnnotationCore
import MotionCore

/// Where the toolbar's content sits while its window is bigger or smaller than it, during a resize or a morph.
/// It is the point both the old and the new frame share, so the content stays put on screen.
enum ToolbarPin: Equatable {
    case topLeading, top, bottom, leading, trailing

    /// The content's rect in a non-flipped view's `bounds`.
    func rect(size: CGSize, in bounds: CGRect) -> CGRect {
        let x: CGFloat
        let y: CGFloat
        switch self {
        case .topLeading: x = bounds.minX; y = bounds.maxY - size.height
        case .top: x = bounds.midX - size.width / 2; y = bounds.maxY - size.height
        case .bottom: x = bounds.midX - size.width / 2; y = bounds.minY
        case .leading: x = bounds.minX; y = bounds.midY - size.height / 2
        case .trailing: x = bounds.maxX - size.width; y = bounds.midY - size.height / 2
        }
        return CGRect(x: x.rounded(), y: y.rounded(), width: size.width, height: size.height)
    }

    /// A frame of `size` that keeps this pin's point of `frame` in place, in screen coordinates.
    func frame(size: CGSize, keeping frame: CGRect) -> CGRect {
        let x: CGFloat
        let y: CGFloat
        switch self {
        case .topLeading: x = frame.minX; y = frame.maxY - size.height
        case .top: x = frame.midX - size.width / 2; y = frame.maxY - size.height
        case .bottom: x = frame.midX - size.width / 2; y = frame.minY
        case .leading: x = frame.minX; y = frame.midY - size.height / 2
        case .trailing: x = frame.maxX - size.width; y = frame.midY - size.height / 2
        }
        return CGRect(x: floor(x), y: floor(y), width: size.width, height: size.height)
    }

    /// The pin for a toolbar docked at `edge`, or for a floating one that keeps its top-left corner.
    static func docked(orientation: ToolbarOrientation, edge: ToolbarDockEdge) -> Self {
        if orientation == .vertical { return edge == .left ? .leading : .trailing }
        return edge == .bottom ? .bottom : .top
    }
}

/// The toolbar's background and outline, drawn by AppKit so they follow the window while it resizes.
final class ToolbarChromeView: NSView {
    var cornerRadius: CGFloat = 15 { didSet { if cornerRadius != oldValue { needsDisplay = true } } }
    override var isOpaque: Bool { false }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        needsDisplay = true
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        let radius = min(cornerRadius, bounds.width / 2, bounds.height / 2)
        let shape = NSBezierPath(roundedRect: bounds, xRadius: radius, yRadius: radius)
        NSColor.windowBackgroundColor.setFill()
        shape.fill()
        let outline = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5),
                                   xRadius: max(0, radius - 0.5), yRadius: max(0, radius - 0.5))
        NSColor.labelColor.withAlphaComponent(0.07).setStroke()
        outline.lineWidth = 1
        outline.stroke()
    }
}

/// Moves and resizes the toolbar window frame by frame with `MotionCurves.standard`, so the window, its chrome and the
/// cross-fade stay in step. A new animation starts from wherever the window is.
@MainActor
final class ToolbarFrameAnimator {
    private let ticker = MotionTicker()
    private(set) var target: CGRect?
    private var finish: (() -> Void)?
    var isRunning: Bool { target != nil }

    func animate(_ window: NSWindow, to target: CGRect, duration: Double,
                 step: ((Double) -> Void)? = nil, completion: (() -> Void)? = nil) {
        complete(jump: false)
        let start = window.frame
        self.target = target
        finish = completion
        let began = CACurrentMediaTime()
        ticker.start(view: window.contentView) { [weak self, weak window] now in
            guard let self, let window else { return false }
            let time = duration > 0 ? min(1, max(0, (now - began) / duration)) : 1
            let progress = ToolbarMorph.shape(time)
            window.setFrame(Self.interpolate(start, target, progress), display: true)
            step?(time)
            if time >= 1 {
                self.complete(jump: false)
                return false
            }
            return true
        }
    }

    /// Ends the animation at its final frame and runs its completion.
    func finishNow(_ window: NSWindow?) {
        guard let target else { return }
        ticker.stop()
        window?.setFrame(target, display: true)
        complete(jump: true)
    }

    private func complete(jump: Bool) {
        ticker.stop()
        target = nil
        let done = finish
        finish = nil
        done?()
    }

    static func interpolate(_ start: CGRect, _ end: CGRect, _ progress: Double) -> CGRect {
        func mix(_ a: CGFloat, _ b: CGFloat) -> CGFloat { a + (b - a) * CGFloat(progress) }
        return CGRect(x: mix(start.minX, end.minX), y: mix(start.minY, end.minY),
                      width: mix(start.width, end.width), height: mix(start.height, end.height)).integral
    }
}

/// The toolbar shrunk to one button: the Annotator mark and whether you draw or use your apps, in the Draw button's
/// colors. Pointing at it or clicking it brings every tool back; dragging it moves the toolbar.
struct CollapsedToolbarButton: View {
    @ObservedObject var model: WorkspaceModel
    let vertical: Bool
    var drawsChrome = true
    @State private var hovering = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    private var metrics: ToolbarMetrics { ToolbarMetrics(model.toolbarSize) }
    private var drawing: Bool { model.mode == .draw }

    static func size(vertical: Bool, toolbarSize: ToolbarSize) -> CGSize {
        let minimal = toolbarSize == .minimal
        if vertical { return CGSize(width: minimal ? 50 : 68, height: minimal ? 74 : 88) }
        return CGSize(width: minimal ? 98 : 112, height: minimal ? 38 : 44)
    }

    var body: some View {
        let size = Self.size(vertical: vertical, toolbarSize: model.toolbarSize)
        let layout = vertical ? AnyLayout(VStackLayout(spacing: metrics.minimal ? 5 : 7)) :
            AnyLayout(HStackLayout(spacing: metrics.minimal ? 6 : 8))
        layout {
            Image(nsImage: MenuBarIcon.makeImage(size: metrics.minimal ? 18 : 22))
                .renderingMode(.template)
                .foregroundStyle(.primary)
                .accessibilityHidden(true)
            status
        }
        .frame(width: size.width, height: size.height)
        .background {
            if drawsChrome {
                Capsule().fill(Color(nsColor: .windowBackgroundColor))
                    .overlay { Capsule().strokeBorder(Color.primary.opacity(0.07)) }
            }
        }
        .scaleEffect(hovering && !reduceMotion ? 1.03 : 1)
        .animation(Motion.resolve(Motion.snappy, reduceMotion: reduceMotion), value: hovering)
        .contentShape(Capsule())
        .onHover { hovering = $0 }
        .onTapGesture { model.requestToolbarExpansion() }
        .gesture(DragGesture(minimumDistance: 3, coordinateSpace: .global)
            .onChanged { value in model.onToolbarBackgroundDrag?(value.translation, false) }
            .onEnded { value in model.onToolbarBackgroundDrag?(value.translation, true) })
        .contextMenu {
            Button("Show Toolbar") { model.requestToolbarExpansion() }
            Button(drawing ? "Use Apps" : "Draw") { toggleMode() }
        }
        .help(drawing ? "Drawing is on. Click to show your tools." : "Your apps get the clicks. Click to show your tools.")
        .accessibilityElement(children: .ignore)
        .accessibilityAddTraits(.isButton)
        .accessibilityLabel("Annotator toolbar")
        .accessibilityValue(drawing ? "Drawing is active" : "Apps receive input")
        .accessibilityHint("Shows all tools")
        .accessibilityIdentifier("collapsed-toolbar")
        .accessibilityAction { model.requestToolbarExpansion() }
        .accessibilityAction(named: drawing ? "Use apps" : "Draw") { toggleMode() }
    }

    /// The Draw button's dot, word and tint.
    private var status: some View {
        let dot = Circle().fill(drawing ? Color.green : Color.secondary).frame(width: 6, height: 6)
        let word = Text(drawing ? "Draw" : "Apps").font(.system(size: metrics.minimal ? 10 : 11, weight: .semibold))
            .contentTransition(reduceMotion ? .identity : .opacity)
        return Group {
            if vertical {
                VStack(spacing: 3) { dot; word }
                    .frame(width: metrics.minimal ? 36 : 46, height: metrics.minimal ? 30 : 36)
            } else {
                HStack(spacing: 5) { dot; word }
                    .frame(width: metrics.minimal ? 52 : 58, height: metrics.minimal ? 24 : 28)
            }
        }
        .background(drawing ? Color.green.opacity(0.15) : Color.secondary.opacity(0.1),
                    in: RoundedRectangle(cornerRadius: vertical ? 7 : 14))
        .animation(Motion.resolve(Motion.snappy, reduceMotion: reduceMotion), value: drawing)
    }

    private func toggleMode() {
        if drawing { model.interact() } else { model.resumeDrawing() }
    }
}

/// Decides when the toolbar shrinks to one button and when it opens again. It shrinks once the pointer has been off it
/// for the chosen delay, unless something holds it open (`isHeldOpen`), and opens as soon as the pointer is on it.
@MainActor
final class ToolbarCollapseController {
    private let model: WorkspaceModel
    private var collapseTimer: Task<Void, Never>?
    private(set) var pointerInside = false
    /// Things that keep the toolbar open, such as an open menu, a drag, the tutorial or the pointer on the toolbar.
    var isHeldOpen: @MainActor () -> Bool = { false }
    var collapse: @MainActor () -> Void = {}
    var expand: @MainActor () -> Void = {}
    /// Tests shorten the waits.
    var wait: @MainActor (Double) async throws -> Void = { try await Task.sleep(for: .seconds($0)) }
    var isWaitingToCollapse: Bool { collapseTimer != nil }

    init(model: WorkspaceModel) { self.model = model }

    func pointerChanged(inside: Bool) {
        pointerInside = inside
        if inside {
            collapseTimer?.cancel()
            collapseTimer = nil
            if model.toolbarCollapsed { expand() }
        } else {
            restart()
        }
    }

    /// Starts the wait again, for example when the toolbar appears or opens, or the setting changes.
    func restart() {
        collapseTimer?.cancel()
        collapseTimer = nil
        guard let delay = model.toolbarCollapseDelay, !model.toolbarCollapsed else { return }
        collapseTimer = Task { @MainActor [weak self] in
            try? await self?.wait(delay)
            guard !Task.isCancelled, let self else { return }
            self.collapseTimer = nil
            if self.pointerInside || self.isHeldOpen() { self.restart(); return }
            self.collapse()
        }
    }

    func cancel() {
        collapseTimer?.cancel()
        collapseTimer = nil
    }
}
