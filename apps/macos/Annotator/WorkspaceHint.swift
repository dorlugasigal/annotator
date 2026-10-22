import AppKit
import AnnotationCore
import MotionCore

@MainActor
enum WorkspaceHint {
    static func text(model: WorkspaceModel) -> String {
        let mode = model.mode == .draw ? "Drawing" : "Using apps"
        let draw = (model.preferences.resumeShortcut ?? .resumeDrawing).label
        let zoom = (model.preferences.zoomShortcut ?? .liveZoom).label
        var lines = ["\(mode)  ·  \(draw): draw"]
        if model.mode == .draw && model.tool == .highlighter {
            lines.append("Hold Shift for a straight highlight.")
        }
        if model.screenZoomEnabled {
            lines.append("Zoom \(String(format: "%.1f", model.magnification))×  ·  \(zoom): stop")
            lines.append(model.zoomScrollStatus)
        } else {
            let scroll = model.zoomScrollPermissionGranted
                ? "Option-scroll starts zoom"
                : "Option-scroll needs Accessibility"
            lines.append("\(zoom): screen zoom  ·  \(scroll)")
        }
        if model.active?.canvas.hasTextEditor == true { lines.append("Escape finishes text.") }
        else if model.active?.canvas.hasActiveEdit == true { lines.append("Escape cancels the current edit.") }
        else if model.mode == .draw && !model.screenZoomEnabled { lines.append("Escape: use apps") }
        lines.append(footer)
        return lines.joined(separator: "\n")
    }

    static let footer = "Turn off these tips in Settings > General."

    static func attributed(_ text: String) -> NSAttributedString {
        let result = NSMutableAttributedString(string: text, attributes: [
            .font: NSFont.systemFont(ofSize: 11, weight: .medium),
            .foregroundColor: NSColor.white
        ])
        let range = (text as NSString).range(of: footer, options: .backwards)
        if range.location != NSNotFound {
            result.addAttributes([
                .font: NSFont.systemFont(ofSize: 10, weight: .regular),
                .foregroundColor: NSColor.white.withAlphaComponent(0.62)
            ], range: range)
        }
        return result
    }

    static func draw(_ text: String, in bounds: CGRect, origin: CGPoint) {
        let string = attributed(text)
        let width = min(540, max(80, bounds.width - origin.x - 16))
        let size = string.boundingRect(with: CGSize(width: width - 18, height: 140),
            options: [.usesLineFragmentOrigin, .usesFontLeading]).size
        let box = CGRect(x:origin.x,y:origin.y,width:min(width,ceil(size.width)+18),height:ceil(size.height)+14)
        NSColor.black.withAlphaComponent(0.78).setFill()
        NSBezierPath(roundedRect:box,xRadius:7,yRadius:7).fill()
        string.draw(with: box.insetBy(dx:9,dy:7), options: [.usesLineFragmentOrigin, .usesFontLeading])
    }
}

@MainActor
final class WorkspaceHintView: NSView {
    static let textCrossfade: CFTimeInterval = MotionTiming.hintTextCrossfadeDuration
    private var lastTextChange: CFTimeInterval = 0
    var text = "" {
        didSet {
            if text != oldValue {
                crossfadeText(replacing: oldValue)
                needsDisplay = true
                setAccessibilityElement(true)
                setAccessibilityRole(.staticText)
                setAccessibilityValue(text)
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
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func draw(_ dirtyRect: NSRect) { WorkspaceHint.draw(text, in: bounds, origin: .zero) }

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
