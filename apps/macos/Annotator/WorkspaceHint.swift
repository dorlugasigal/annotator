import AppKit
import AnnotationCore
import MotionCore

@MainActor
enum WorkspaceHint {
    static let closeLabel = "Turn off tips"
    static let closeHelp = "Turn them back on in Settings > General."
    static let lineSize: CGFloat = 12
    static let fill = NSColor.black.withAlphaComponent(0.82)
    static let inset = CGSize(width: 9, height: 7)
    /// Room after the text for the ✕ that turns tips off.
    static let closeReserve: CGFloat = 18
    static let closeHalfSize: CGFloat = 3.5
    static let closeHitSize: CGFloat = 24

    static func text(model: WorkspaceModel) -> String {
        let draw = (model.preferences.resumeShortcut ?? .resumeDrawing).label
        let zoom = (model.preferences.zoomShortcut ?? .liveZoom).label
        var first = model.mode == .draw ? "Drawing" : "Using apps  ·  \(draw): draw"
        if model.active?.canvas.hasTextEditor == true { first += "  ·  Escape finishes text" }
        else if model.active?.canvas.hasActiveEdit == true { first += "  ·  Escape cancels the edit" }
        else if model.mode == .draw && !model.screenZoomEnabled { first += "  ·  Escape: use apps" }
        var lines = [first]
        if model.mode == .draw && model.tool == .highlighter {
            lines.append("Hold Shift for a straight highlight.")
        }
        if model.screenZoomEnabled {
            lines.append("Zoom \(String(format: "%.1f", model.magnification))×  ·  \(zoom): stop")
            lines.append(model.zoomScrollStatus)
        }
        return lines.joined(separator: "\n")
    }

    static func attributed(_ text: String) -> NSAttributedString {
        NSAttributedString(string: text, attributes: [
            .font: NSFont.systemFont(ofSize: lineSize, weight: .medium),
            .foregroundColor: NSColor.white
        ])
    }

    /// The drawn box for `text` with its top-left corner at `origin`; `closable` leaves room for the ✕.
    static func box(_ text: String, in bounds: CGRect, origin: CGPoint, closable: Bool) -> CGRect {
        let string = attributed(text)
        let reserve = closable ? closeReserve : 0
        let width = min(540, max(80, bounds.width - origin.x - 16))
        let size = string.boundingRect(with: CGSize(width: width - inset.width * 2 - reserve, height: 140),
            options: [.usesLineFragmentOrigin, .usesFontLeading]).size
        return CGRect(x: origin.x, y: origin.y, width: min(width, ceil(size.width) + inset.width * 2 + reserve),
                      height: ceil(size.height) + inset.height * 2)
    }

    /// The ✕'s center: at the right of the first line.
    static func closeCenter(in box: CGRect) -> CGPoint {
        let font = NSFont.systemFont(ofSize: lineSize, weight: .medium)
        let line = font.ascender.rounded() + (-font.descender).rounded()
        return CGPoint(x: box.maxX - inset.width - closeHalfSize, y: box.minY + inset.height + line / 2)
    }

    /// Where a click on the ✕ lands, and its accessibility frame.
    static func closeFrame(in box: CGRect) -> CGRect {
        let center = closeCenter(in: box)
        return CGRect(x: center.x - closeHitSize / 2, y: center.y - closeHitSize / 2, width: closeHitSize, height: closeHitSize)
    }

    @discardableResult
    static func draw(_ text: String, in bounds: CGRect, origin: CGPoint, closable: Bool = false) -> CGRect {
        let box = box(text, in: bounds, origin: origin, closable: closable)
        fill.setFill()
        NSBezierPath(roundedRect: box, xRadius: 7, yRadius: 7).fill()
        let textBox = CGRect(x: box.minX + inset.width, y: box.minY + inset.height,
                             width: box.width - inset.width * 2 - (closable ? closeReserve : 0), height: box.height - inset.height * 2)
        attributed(text).draw(with: textBox, options: [.usesLineFragmentOrigin, .usesFontLeading])
        if closable {
            let center = closeCenter(in: box)
            let cross = NSBezierPath()
            cross.move(to: CGPoint(x: center.x - closeHalfSize, y: center.y - closeHalfSize))
            cross.line(to: CGPoint(x: center.x + closeHalfSize, y: center.y + closeHalfSize))
            cross.move(to: CGPoint(x: center.x + closeHalfSize, y: center.y - closeHalfSize))
            cross.line(to: CGPoint(x: center.x - closeHalfSize, y: center.y + closeHalfSize))
            cross.lineWidth = 1.5
            cross.lineCapStyle = .round
            NSColor.white.withAlphaComponent(0.7).setStroke()
            cross.stroke()
        }
        return box
    }
}

/// Decides when the on-screen tip shows: it wakes when what it says matters changes (it appears, the mode, the
/// highlighter, zoom or a text editor), then fades out after `MotionTiming.hintDwellDuration`. Strokes and zoom
/// values update the text without waking it.
struct HintWake: Equatable {
    var available: Bool
    var drawing: Bool
    var highlighter: Bool
    var zooming: Bool
    var editingText: Bool
}

struct HintSchedule {
    static let dwell: CFTimeInterval = MotionTiming.hintDwellDuration
    private var last: HintWake?
    private(set) var wokeAt: CFTimeInterval = -.infinity

    /// Whether the tip shows at `now` for `wake`.
    mutating func shows(_ wake: HintWake, at now: CFTimeInterval) -> Bool {
        if wake != last {
            last = wake
            if wake.available { wokeAt = now }
        }
        return wake.available && now - wokeAt < Self.dwell
    }

    /// When the tip that shows now should fade out.
    var hidesAt: CFTimeInterval { wokeAt + Self.dwell }
}

@MainActor
final class WorkspaceHintView: NSView {
    static let textCrossfade: CFTimeInterval = MotionTiming.hintTextCrossfadeDuration
    private var lastTextChange: CFTimeInterval = 0
    weak var fader: HUDFader?
    var onClose: (() -> Void)?
    private lazy var closeButton: NSButton = {
        let button = HintCloseButton(title: WorkspaceHint.closeLabel, target: self, action: #selector(close))
        button.isBordered = false
        button.isTransparent = true
        button.setAccessibilityLabel(WorkspaceHint.closeLabel)
        button.setAccessibilityHelp(WorkspaceHint.closeHelp)
        button.toolTip = WorkspaceHint.closeHelp
        button.owner = self
        return button
    }()
    /// Whether the ✕ shows; only while drawing, when the canvas takes clicks.
    var closable = false {
        didSet {
            if closable != oldValue { needsDisplay = true; layoutClose() }
        }
    }
    var text = "" {
        didSet {
            if text != oldValue {
                crossfadeText(replacing: oldValue)
                needsDisplay = true
                setAccessibilityElement(true)
                setAccessibilityRole(.staticText)
                setAccessibilityValue(text)
                layoutClose()
            }
        }
    }
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
    }
    required init?(coder: NSCoder) {
        super.init(coder: coder)
        wantsLayer = true
    }
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? {
        guard closable, fader?.acceptsInput != false, let hit = super.hitTest(point), hit === closeButton else { return nil }
        return hit
    }
    override func draw(_ dirtyRect: NSRect) { WorkspaceHint.draw(text, in: bounds, origin: .zero, closable: closable) }
    override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); layoutClose() }

    var closeFrame: CGRect? { closable ? closeButton.frame : nil }

    private func layoutClose() {
        guard closable else { closeButton.removeFromSuperview(); return }
        if closeButton.superview == nil { addSubview(closeButton) }
        closeButton.frame = WorkspaceHint.closeFrame(in: WorkspaceHint.box(text, in: bounds, origin: .zero, closable: true))
    }

    @objc private func close() { onClose?() }

    fileprivate var acceptsClicks: Bool { closable && fader?.acceptsInput != false }

    /// Values that change every frame, such as a zoom ramp, update in place instead of restarting the crossfade.
    private func crossfadeText(replacing oldValue: String) {
        let now = CACurrentMediaTime()
        defer { lastTextChange = now }
        guard !oldValue.isEmpty, !isHidden, window?.isVisible == true, let layer,
              now - lastTextChange >= Self.textCrossfade else { return }
        let transition = CATransition()
        transition.type = .fade
        transition.duration = Self.textCrossfade
        transition.timingFunction = Motion.fade
        layer.add(transition, forKey: kCATransition)
    }
}

/// The ✕ is drawn by the tip; this invisible button takes the click and speaks for it.
@MainActor
private final class HintCloseButton: NSButton {
    weak var owner: WorkspaceHintView?
    override func hitTest(_ point: NSPoint) -> NSView? {
        owner?.acceptsClicks == false ? nil : super.hitTest(point)
    }
    override func draw(_ dirtyRect: NSRect) {}
}

/// Fades a heads-up view in and out while `isHidden` keeps the settled state.
/// A view that is fading out reports `acceptsInput == false` so it never takes a click.
@MainActor
final class HUDFader {
    static let fadeIn: CFTimeInterval = MotionTiming.hintFadeInDuration
    static let fadeOut: CFTimeInterval = MotionTiming.hintFadeOutDuration
    static let rise: CGFloat = 3
    private weak var view: NSView?
    private var generation: UInt64 = 0
    private(set) var targetVisible: Bool
    var reducesMotion: () -> Bool = { Motion.systemReducesMotion }
    var acceptsInput: Bool { targetVisible }
    var isFading: Bool { view?.layer?.animation(forKey: "hud.opacity") != nil }

    init(_ view: NSView) {
        self.view = view
        targetVisible = !view.isHidden
        view.wantsLayer = true
    }

    func setVisible(_ visible: Bool) {
        guard let view else { return }
        guard visible != targetVisible || (visible && view.isHidden) else { return }
        targetVisible = visible
        generation &+= 1
        let token = generation
        let layer = view.layer
        let current = Double(layer?.presentation()?.opacity ?? (view.isHidden ? 0 : 1))
        let starting = view.isHidden ? 0 : current
        layer?.removeAnimation(forKey: "hud.opacity")
        layer?.removeAnimation(forKey: "hud.rise")
        guard let layer, view.window?.isVisible == true else {
            view.isHidden = !visible
            return
        }
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = starting
        fade.toValue = visible ? 1 : 0
        fade.duration = (visible ? Self.fadeIn : Self.fadeOut) * abs((visible ? 1 : 0) - starting)
        fade.timingFunction = Motion.fade
        guard fade.duration > 0.001 else {
            view.isHidden = !visible
            return
        }
        if visible {
            view.isHidden = false
            layer.add(fade, forKey: "hud.opacity")
            if !reducesMotion() && starting == 0 {
                let rise = CABasicAnimation(keyPath: "position.y")
                let flipped = view.superview?.layer?.contentsAreFlipped() == true
                rise.fromValue = flipped ? Self.rise : -Self.rise
                rise.toValue = 0
                rise.isAdditive = true
                rise.duration = Self.fadeIn
                rise.timingFunction = Motion.enter
                layer.add(rise, forKey: "hud.rise")
            }
        } else {
            fade.fillMode = .forwards
            fade.isRemovedOnCompletion = false
            CATransaction.begin()
            CATransaction.setCompletionBlock { [weak self, weak view] in
                MainActor.assumeIsolated {
                    guard let self, let view, self.generation == token, !self.targetVisible else { return }
                    view.isHidden = true
                    view.layer?.removeAnimation(forKey: "hud.opacity")
                }
            }
            layer.add(fade, forKey: "hud.opacity")
            CATransaction.commit()
        }
    }
}
