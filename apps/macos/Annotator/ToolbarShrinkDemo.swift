import AppKit
import SwiftUI
import AnnotationCore
import MotionCore

/// Shows what Shrink does, on a loop: the pointer leaves the toolbar, the toolbar turns into one button, and as soon as
/// the pointer is back the tools open again, with the toolbar's own morph. Each loop starts with a still second. With
/// Reduce Motion it shows the toolbar and its one button side by side instead.
struct ToolbarShrinkDemo: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var start = Date()
    @State private var playing = false

    var body: some View {
        Group {
            if reduceMotion {
                HStack(spacing: 10) {
                    ShrinkDemoStage(pose: .open, width: ShrinkDemoStage.toolbar.width + 36)
                    Image(systemName: "arrow.right").font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
                    ShrinkDemoStage(pose: .shrunk, width: ShrinkDemoStage.button.width + 36)
                }
            } else {
                TimelineView(.animation(paused: !playing)) { context in
                    ShrinkDemoStage(pose: ShrinkDemoPose(at: max(0, context.date.timeIntervalSince(start))))
                }
            }
        }
        .onAppear {
            start = Date()
            playing = true
        }
        .onDisappear { playing = false }
        .accessibilityElement(children: .ignore)
        .accessibilityAddTraits(.isImage)
        .accessibilityLabel("Shrink preview")
        .accessibilityHint("The toolbar turns into one button after the pointer leaves it, and opens as soon as the pointer is back.")
        .accessibilityIdentifier("toolbar-shrink-demo")
    }
}

/// One moment of the demo's loop.
struct ShrinkDemoPose: Equatable {
    /// How far the background has gone from the toolbar's shape (0) to the one button's (1).
    var shrink: Double
    var toolbarOpacity: Double
    var buttonOpacity: Double
    /// The pointer's tip, as a share of the stage's width and height.
    var pointer: CGPoint

    static let onToolbar = CGPoint(x: 0.53, y: 0.4)
    static let away = CGPoint(x: 0.84, y: 0.86)
    static let open = ShrinkDemoPose(shrink: 0, toolbarOpacity: 1, buttonOpacity: 0, pointer: away)
    static let shrunk = ShrinkDemoPose(shrink: 1, toolbarOpacity: 0, buttonOpacity: 1, pointer: away)

    /// The still start, the pointer leaving, the wait, the shrink, the pointer coming back and the toolbar opening.
    static let leave = 1.0, wait = 1.6, collapse = 2.4, rest = 2.4 + MotionTiming.toolbarCollapseDuration
    static let back = 3.6, expand = 4.2, opened = 4.2 + MotionTiming.toolbarExpandDuration, period = 6.0

    init(shrink: Double, toolbarOpacity: Double, buttonOpacity: Double, pointer: CGPoint) {
        self.shrink = shrink
        self.toolbarOpacity = toolbarOpacity
        self.buttonOpacity = buttonOpacity
        self.pointer = pointer
    }

    init(at elapsed: Double) {
        let time = elapsed.truncatingRemainder(dividingBy: Self.period)
        func share(_ from: Double, _ to: Double) -> Double { min(1, max(0, (time - from) / (to - from))) }
        func move(_ from: CGPoint, _ to: CGPoint, _ t: Double) -> CGPoint {
            let p = MotionCurves.standard.progress(at: t)
            return CGPoint(x: from.x + (to.x - from.x) * p, y: from.y + (to.y - from.y) * p)
        }
        switch time {
        case ..<Self.leave: self = Self.open.with(pointer: Self.onToolbar)
        case ..<Self.wait: self = Self.open.with(pointer: move(Self.onToolbar, Self.away, share(Self.leave, Self.wait)))
        case ..<Self.collapse: self = Self.open
        case ..<Self.rest:
            let t = share(Self.collapse, Self.rest)
            self = ShrinkDemoPose(shrink: ToolbarMorph.shape(t), toolbarOpacity: ToolbarMorph.outgoingOpacity(t),
                                  buttonOpacity: ToolbarMorph.incomingOpacity(t), pointer: Self.away)
        case ..<Self.back: self = Self.shrunk
        case ..<Self.expand: self = Self.shrunk.with(pointer: move(Self.away, Self.onToolbar, share(Self.back, Self.expand)))
        case ..<Self.opened:
            let t = share(Self.expand, Self.opened)
            self = ShrinkDemoPose(shrink: 1 - ToolbarMorph.shape(t), toolbarOpacity: ToolbarMorph.incomingOpacity(t),
                                  buttonOpacity: ToolbarMorph.outgoingOpacity(t), pointer: Self.onToolbar)
        default: self = Self.open.with(pointer: Self.onToolbar)
        }
    }

    private func with(pointer: CGPoint) -> Self {
        var pose = self
        pose.pointer = pointer
        return pose
    }
}

/// A small desktop with the toolbar, or its one button, docked at the top, and the pointer.
struct ShrinkDemoStage: View {
    let pose: ShrinkDemoPose
    var width: CGFloat = 300
    static let toolbar = CGSize(width: 214, height: 36)
    static let button = CGSize(width: 82, height: 28)
    private static let height: CGFloat = 92

    var body: some View {
        let shrink = CGFloat(pose.shrink)
        let size = CGSize(width: Self.toolbar.width + (Self.button.width - Self.toolbar.width) * shrink,
                          height: Self.toolbar.height + (Self.button.height - Self.toolbar.height) * shrink)
        let shape = RoundedRectangle(cornerRadius: 9 + (Self.button.height / 2 - 9) * shrink, style: .continuous)
        ZStack {
            ShrinkDemoToolbarFace().frame(width: Self.toolbar.width, height: Self.toolbar.height).opacity(pose.toolbarOpacity)
            ShrinkDemoButtonFace().frame(width: Self.button.width, height: Self.button.height).opacity(pose.buttonOpacity)
        }
        // Like the real toolbar docked at the top, both faces keep the top middle while the background changes.
        .frame(width: size.width, height: size.height, alignment: .top)
        .clipShape(shape)
        .background(shape.fill(Color(nsColor: .windowBackgroundColor)).shadow(color: .black.opacity(0.14), radius: 4, y: 1))
        .overlay(shape.strokeBorder(Color.primary.opacity(0.08)))
        .padding(.top, 14)
        .frame(width: width, height: Self.height, alignment: .top)
        .overlay(alignment: .topLeading) { ShrinkDemoPointer(tip: CGPoint(x: pose.pointer.x * width, y: pose.pointer.y * Self.height)) }
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.accentColor.opacity(0.07)))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color.primary.opacity(0.07)))
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }
}

/// The toolbar's face, small: a few tools with Pen chosen, then Draw and three buttons.
private struct ShrinkDemoToolbarFace: View {
    private let tools: [Tool] = [.select, .rectangle, .arrow, .pen, .text]

    var body: some View {
        HStack(spacing: 4) {
            ForEach(Array(tools.enumerated()), id: \.offset) { index, tool in
                ToolGlyph(tool: tool, size: 11)
                    .frame(width: 18, height: 24)
                    .background(RoundedRectangle(cornerRadius: 5).fill(index == 3 ? Color.accentColor.opacity(0.2) : .clear))
            }
            Rectangle().fill(Color.primary.opacity(0.15)).frame(width: 1, height: 18)
            ShrinkDemoDraw(cornerRadius: 5).frame(width: 40, height: 24)
            ForEach(0..<3, id: \.self) { _ in
                RoundedRectangle(cornerRadius: 2).fill(Color.primary.opacity(0.25)).frame(width: 8, height: 8)
                    .frame(width: 12)
            }
        }
    }
}

/// The one button's face: the Annotator mark and Draw, as `CollapsedToolbarButton` draws them.
private struct ShrinkDemoButtonFace: View {
    var body: some View {
        HStack(spacing: 6) {
            Image(nsImage: MenuBarIcon.makeImage(size: 14)).renderingMode(.template).foregroundStyle(.primary)
            ShrinkDemoDraw(cornerRadius: 10).frame(width: 44, height: 20)
        }
    }
}

/// Draw with its green dot, in the Draw button's colors.
private struct ShrinkDemoDraw: View {
    let cornerRadius: CGFloat

    var body: some View {
        HStack(spacing: 3) {
            Circle().fill(Color.green).frame(width: 4, height: 4)
            Text("Draw").font(.system(size: 8, weight: .semibold))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.green.opacity(0.15), in: RoundedRectangle(cornerRadius: cornerRadius))
    }
}

/// The system arrow pointer, drawn small with its tip at `tip`.
private struct ShrinkDemoPointer: View {
    let tip: CGPoint
    private static let height: CGFloat = 17

    var body: some View {
        let image = NSCursor.arrow.image
        let scale = Self.height / max(1, image.size.height)
        let hotSpot = NSCursor.arrow.hotSpot
        Image(nsImage: image).resizable()
            .frame(width: image.size.width * scale, height: Self.height)
            .offset(x: tip.x - hotSpot.x * scale, y: tip.y - hotSpot.y * scale)
    }
}
