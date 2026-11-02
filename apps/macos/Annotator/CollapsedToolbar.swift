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

/// The toolbar's background and outline, drawn by AppKit so they follow the window while it resizes. With Glass toolbar on,
/// the system's glass replaces the solid color (WIN-UI-09), unless Reduce transparency is on.
final class ToolbarChromeView: NSView {
    var cornerRadius: CGFloat = 15 { didSet { if cornerRadius != oldValue { needsDisplay = true; shapeBackdrop() } } }
    var glass = false { didSet { if glass != oldValue { refreshBackdrop() } } }
    /// Reduce transparency in System Settings > Accessibility > Display; tests replace it.
    var reducesTransparency: @MainActor () -> Bool = { ToolbarGlass.reducesTransparency } {
        didSet { refreshBackdrop() }
    }
    /// Runs when the glass appears or goes, for example after Reduce transparency changes.
    var onGlassChanged: (() -> Void)?
    private var backdrop: NSView?
    /// How the background draws while a picture of the toolbar is taken (`ToolbarGlass.picture`).
    private var picture: ToolbarGlass.Picture?
    override var isOpaque: Bool { false }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    // Decoration only: screen readers see the same toolbar with or without glass.
    override func isAccessibilityElement() -> Bool { false }
    override func accessibilityChildren() -> [Any]? { [] }

    override init(frame: NSRect) {
        super.init(frame: frame)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(displayOptionsChanged),
            name: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification, object: nil)
    }

    convenience init(cornerRadius: CGFloat, glass: Bool) {
        self.init(frame: .zero)
        self.cornerRadius = cornerRadius
        self.glass = glass
        refreshBackdrop()
    }

    required init?(coder: NSCoder) { nil }

    /// True while the glass shows, false for the solid background.
    var showsGlass: Bool { backdrop != nil }

    /// The corner radius the background draws with: never more than half its width or height.
    var effectiveCornerRadius: CGFloat { max(0, min(cornerRadius, bounds.width / 2, bounds.height / 2)) }

    /// Leaves the glass out of pictures of the toolbar (`clear`) or draws the solid background in them (`solid`), until
    /// `endPicture`. The glass on screen is back before the next frame.
    func beginPicture(_ style: ToolbarGlass.Picture) {
        guard let backdrop, picture == nil else { return }
        picture = style
        backdrop.isHidden = true
    }

    func endPicture() {
        guard picture != nil else { return }
        picture = nil
        backdrop?.isHidden = false
        needsDisplay = true
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        needsDisplay = true
        if let backdrop { ToolbarGlass.restyle(backdrop, for: effectiveAppearance) }
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        needsDisplay = true
        shapeBackdrop()
    }

    @objc private func displayOptionsChanged() { refreshBackdrop() }

    private func refreshBackdrop() {
        let showed = showsGlass
        defer { if showsGlass != showed { onGlassChanged?() } }
        if glass && !reducesTransparency() {
            if backdrop == nil {
                let view = ToolbarGlass.makeBackdrop()
                view.frame = bounds
                view.autoresizingMask = [.width, .height]
                addSubview(view)
                backdrop = view
                ToolbarGlass.restyle(view, for: effectiveAppearance)
            }
            shapeBackdrop()
        } else {
            backdrop?.removeFromSuperview()
            backdrop = nil
        }
        needsDisplay = true
    }

    private func shapeBackdrop() {
        guard let backdrop else { return }
        ToolbarGlass.shape(backdrop, radius: effectiveCornerRadius)
    }

    override func draw(_ dirtyRect: NSRect) {
        guard backdrop == nil || picture == .solid else { return }
        let radius = effectiveCornerRadius
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

/// The system glass behind the toolbar: Liquid Glass on macOS 26 and later, a behind-window blur on macOS 14 and 15.
/// The system draws both, so they ask for no permission and add no work of Annotator's own (DV-14).
@MainActor
enum ToolbarGlass {
    /// What a picture of the toolbar (`cacheDisplay`) shows where the glass is. A picture can't hold live glass: it comes
    /// out as a dark plate. Animations leave the glass out (`clear`) and draw live glass under the picture; the shared
    /// region's output, which only shows pixels, gets the solid background (`solid`).
    enum Picture { case clear, solid }

    /// On macOS 26 and later, live glass moves with pictures of the toolbar (`VisibilityImageView.addGlass`, the morph's
    /// leaving face). Earlier, the pictures draw the solid background instead.
    static var movesWithPictures: Bool {
        if #available(macOS 26, *) { true } else { false }
    }

    /// How pictures for the toolbar's own animations draw its glass.
    static var animationPicture: Picture { movesWithPictures ? .clear : .solid }

    /// A glass surface of the toolbar: its frame, measured with y up from the bottom-left of a view, and its corner radius.
    struct Surface: Equatable {
        var frame: CGRect
        var cornerRadius: CGFloat
    }

    /// The glass surfaces showing in `root`: the toolbar's background, or a side toolbar's rail and properties.
    static func surfaces(in root: NSView) -> [Surface] {
        chromeViews(in: root).filter { $0.showsGlass && !$0.isHiddenOrHasHiddenAncestor && $0.alphaValue > 0.01 }.map { chrome in
            var frame = chrome.convert(chrome.bounds, to: root)
            if root.isFlipped { frame.origin.y = root.bounds.height - frame.maxY }
            return Surface(frame: frame, cornerRadius: chrome.effectiveCornerRadius)
        }
    }

    /// Runs `body`, which takes a picture of `root`, with the glass in `root` drawn as `style`.
    static func picture<T>(of root: NSView, glass style: Picture, _ body: () throws -> T) rethrows -> T {
        let chromes = chromeViews(in: root)
        chromes.forEach { $0.beginPicture(style) }
        defer { chromes.forEach { $0.endPicture() } }
        return try body()
    }

    private static func chromeViews(in view: NSView) -> [ToolbarChromeView] {
        (view as? ToolbarChromeView).map { [$0] } ?? view.subviews.flatMap { chromeViews(in: $0) }
    }

    /// AppKit draws Liquid Glass in full (clear, with refraction and a bright edge) only in windows that look active, and
    /// the toolbar's windows rarely have the keyboard: the canvas or your other apps do. Windows that show glass answer
    /// AppKit's private `_hasActiveAppearanceIgnoringKeyFocus` with this: active while they show glass, otherwise what
    /// `base` would say. They never take the keyboard for it. If a later macOS stops asking, the glass falls back to its
    /// flatter inactive look and nothing else changes.
    static func activeAppearance(of window: NSWindow, showsGlass: Bool, inheritedFrom base: AnyClass) -> Bool {
        if #available(macOS 26, *), showsGlass { return true }
        let selector = NSSelectorFromString("_hasActiveAppearanceIgnoringKeyFocus")
        guard let inherited = class_getInstanceMethod(base, selector) else { return window.isKeyWindow }
        typealias Getter = @convention(c) (AnyObject, Selector) -> Bool
        return unsafeBitCast(method_getImplementation(inherited), to: Getter.self)(window, selector)
    }

    /// How much of the window color stays in the glass, so labels stay readable over white, black or busy screens.
    static let bodyOpacity: CGFloat = 0.25
    static var reducesTransparency: Bool { NSWorkspace.shared.accessibilityDisplayShouldReduceTransparency }
    private static var masks: [CGFloat: NSImage] = [:]

    /// On macOS 26 and later: Liquid Glass with a light body of the window color, clipped to the toolbar's shape with the
    /// solid toolbar's hairline edge. The clip keeps the glass's edge light inside the toolbar, so the window never cuts it
    /// off, and the toolbar keeps its size. The toolbar window keeps the glass in its active look (`InspectorPanel`).
    static func makeBackdrop() -> NSView {
        if #available(macOS 26, *) {
            let shape = NSView()
            shape.wantsLayer = true
            shape.layer?.cornerCurve = .continuous
            shape.layer?.masksToBounds = true
            shape.layer?.borderWidth = 1
            let glass = NSGlassEffectView()
            glass.style = .regular
            glass.autoresizingMask = [.width, .height]
            let body = NSView()
            body.wantsLayer = true
            glass.contentView = body
            shape.addSubview(glass)
            return shape
        }
        let blur = NSVisualEffectView()
        blur.material = .popover
        blur.blendingMode = .behindWindow
        blur.state = .active
        return blur
    }

    static func shape(_ backdrop: NSView, radius: CGFloat) {
        if #available(macOS 26, *), let glass = backdrop.subviews.first as? NSGlassEffectView {
            backdrop.layer?.cornerRadius = radius
            glass.frame = backdrop.bounds
            glass.cornerRadius = radius
        } else if let blur = backdrop as? NSVisualEffectView {
            blur.maskImage = mask(radius: radius.rounded())
        }
    }

    /// The body and edge colors for Light or Dark.
    static func restyle(_ backdrop: NSView, for appearance: NSAppearance) {
        guard #available(macOS 26, *), let glass = backdrop.subviews.first as? NSGlassEffectView else { return }
        appearance.performAsCurrentDrawingAppearance {
            backdrop.layer?.borderColor = NSColor.labelColor.withAlphaComponent(0.07).cgColor
            glass.contentView?.layer?.backgroundColor = NSColor.windowBackgroundColor.withAlphaComponent(bodyOpacity).cgColor
        }
    }

    /// A stretchable rounded rectangle, the supported way to round a visual effect view's corners.
    private static func mask(radius: CGFloat) -> NSImage {
        if let mask = masks[radius] { return mask }
        let edge = radius * 2 + 1
        let mask = NSImage(size: NSSize(width: edge, height: edge), flipped: false) { rect in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
            return true
        }
        mask.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
        mask.resizingMode = .stretch
        masks[radius] = mask
        return mask
    }
}

/// The toolbar's background in SwiftUI, for the vertical toolbar's separate surfaces.
struct ToolbarChromeBackground: NSViewRepresentable {
    var cornerRadius: CGFloat
    var glass: Bool

    func makeNSView(context: Context) -> ToolbarChromeView { ToolbarChromeView(cornerRadius: cornerRadius, glass: glass) }
    func updateNSView(_ view: ToolbarChromeView, context: Context) {
        view.cornerRadius = cornerRadius
        view.glass = glass
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
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    private var metrics: ToolbarMetrics { ToolbarMetrics(model.toolbarSize) }
    private var drawing: Bool { model.mode == .draw }
    /// On glass, the Draw or Apps mark sits on a lens like the Draw button (`ToolbarLens`).
    private var lens: Bool { !drawsChrome && model.preferences.glass == true && !reduceTransparency }

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
        .background(lens ? Color.clear : drawing ? Color.green.opacity(0.15) : Color.secondary.opacity(0.1),
                    in: RoundedRectangle(cornerRadius: vertical ? 7 : 14))
        .background { if lens { ToolbarLens(role: drawing ? .state(.green) : .neutral, radius: vertical ? 7 : 14) } }
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
