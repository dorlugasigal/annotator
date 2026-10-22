import SwiftUI
import AnnotationCore

/// A small native scene for one tour page, drawn for a single moment of its story.
struct IntroductionIllustration: View {
    let page: IntroductionPage
    let progress: Double
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let dark = colorScheme == .dark
        Canvas { context, size in
            let scene = IntroductionScene(size: size, progress: progress, dark: dark)
            switch page {
            case .draw: scene.drawShortcuts(in: &context)
            case .color: scene.drawColor(in: &context)
            case .focus: scene.drawFocus(in: &context)
            case .setup: break
            }
        }
    }
}

private typealias Ease = IntroductionEase

private struct IntroductionScene {
    let size: CGSize
    let progress: Double
    let dark: Bool

    static let accent = Color(red: 0.17, green: 0.45, blue: 0.88)
    static let ink = Color(red: 0.94, green: 0.27, blue: 0.22)
    static let halo = Color(red: 1, green: 0.82, blue: 0)
    static let sunset = Gradient(colors: GradientPreset.sunset.gradient.colors.map {
        Color(red: $0.red, green: $0.green, blue: $0.blue)
    })

    private var bounds: CGRect { CGRect(origin: .zero, size: size) }
    private var desktopShape: Path { Path(roundedRect: bounds, cornerRadius: 12, style: .continuous) }
    private var windowFill: Color { dark ? Color(red: 0.17, green: 0.19, blue: 0.24) : .white }
    private var edge: Color { dark ? .white.opacity(0.09) : .black.opacity(0.07) }
    private var chrome: Color { dark ? .white.opacity(0.2) : .black.opacity(0.13) }
    private var line: Color { dark ? .white.opacity(0.13) : .black.opacity(0.08) }
    private var strongLine: Color { dark ? .white.opacity(0.24) : .black.opacity(0.15) }
    private var bar: Color { dark ? Color(red: 0.29, green: 0.35, blue: 0.47) : Color(red: 0.8, green: 0.85, blue: 0.93) }
    private var printColor: Color { dark ? .white.opacity(0.78) : Color(red: 0.2, green: 0.23, blue: 0.28) }

    // MARK: Shared pieces

    private func drawDesktop(_ context: inout GraphicsContext) {
        context.clip(to: desktopShape)
        let wallpaper = dark
            ? Gradient(colors: [Color(red: 0.11, green: 0.15, blue: 0.23), Color(red: 0.2, green: 0.18, blue: 0.3)])
            : Gradient(colors: [Color(red: 0.87, green: 0.91, blue: 0.98), Color(red: 0.97, green: 0.91, blue: 0.95)])
        context.fill(desktopShape, with: .linearGradient(wallpaper, startPoint: .zero,
                                                         endPoint: CGPoint(x: size.width, y: size.height)))
        context.fill(desktopShape, with: .radialGradient(
            Gradient(colors: [.white.opacity(dark ? 0.07 : 0.5), .white.opacity(0)]),
            center: CGPoint(x: size.width * 0.18, y: 0), startRadius: 0, endRadius: size.width * 0.55))
    }

    private func drawFrame(_ context: inout GraphicsContext) {
        context.stroke(Path(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), cornerRadius: 11.5, style: .continuous),
                       with: .color(edge), lineWidth: 1)
    }

    private func drawWindow(_ context: inout GraphicsContext, _ rect: CGRect, titleBar: Bool = true) {
        let shape = Path(roundedRect: rect, cornerRadius: 7, style: .continuous)
        var shadowed = context
        shadowed.addFilter(.shadow(color: .black.opacity(dark ? 0.35 : 0.1), radius: 7, x: 0, y: 2))
        shadowed.fill(shape, with: .color(windowFill))
        context.stroke(shape, with: .color(edge), lineWidth: 0.75)
        guard titleBar else { return }
        for index in 0..<3 {
            let center = CGPoint(x: rect.minX + 9 + CGFloat(index) * 6.5, y: rect.minY + 7.5)
            context.fill(Path(ellipseIn: CGRect(x: center.x - 2, y: center.y - 2, width: 4, height: 4)),
                         with: .color(chrome))
        }
    }

    private func drawLines(_ context: inout GraphicsContext, x: CGFloat, y: CGFloat, width: CGFloat,
                           fractions: [CGFloat], step: CGFloat = 9, strong: Int? = nil) {
        for (index, fraction) in fractions.enumerated() {
            let rect = CGRect(x: x, y: y + CGFloat(index) * step, width: width * fraction, height: 4)
            context.fill(Path(roundedRect: rect, cornerRadius: 2), with: .color(index == strong ? strongLine : line))
        }
    }

    private func drawPointer(_ context: inout GraphicsContext, tip: CGPoint, scale: CGFloat = 1, opacity: Double = 1) {
        guard opacity > 0 else { return }
        var path = Path()
        path.addLines([CGPoint(x: 0, y: 0), CGPoint(x: 0, y: 15.5), CGPoint(x: 3.7, y: 12),
                       CGPoint(x: 6.3, y: 17.6), CGPoint(x: 8.8, y: 16.5), CGPoint(x: 6.3, y: 11),
                       CGPoint(x: 11.2, y: 11)])
        path.closeSubpath()
        var pointer = context
        pointer.opacity = opacity
        pointer.translateBy(x: tip.x, y: tip.y)
        pointer.scaleBy(x: scale, y: scale)
        var shadowed = pointer
        shadowed.addFilter(.shadow(color: .black.opacity(0.3), radius: 1.5, x: 0, y: 1))
        shadowed.stroke(path, with: .color(.white), style: StrokeStyle(lineWidth: 2.4, lineJoin: .round))
        pointer.fill(path, with: .color(.black))
    }

    private func symbol(_ name: String, size: CGFloat, color: Color) -> Text {
        Text(Image(systemName: name)).font(.system(size: size, weight: .semibold)).foregroundColor(color)
    }

    // MARK: Make a point

    /// Draw circles a chart, Use apps clicks through while the ink stays, Show / hide fades the ink out and back.
    func drawShortcuts(in context: inout GraphicsContext) {
        drawDesktop(&context)
        let width = size.width, height = size.height
        let back = CGRect(x: width * 0.57, y: 30, width: width * 0.36, height: height - 42)
        drawWindow(&context, back)
        drawLines(&context, x: back.minX + 12, y: back.minY + 22, width: back.width - 24,
                  fractions: [0.9, 0.62, 0.78, 0.5, 0.7], strong: 0)

        let front = CGRect(x: width * 0.07, y: 16, width: width * 0.56, height: height - 28)
        drawWindow(&context, front)
        let base = front.maxY - 12
        let chartHeight = front.height - 34
        let barWidth: CGFloat = 11, gap: CGFloat = 7
        var target = CGRect.zero
        for (index, fraction) in [0.36, 0.58, 0.44, 0.9, 0.52].enumerated() {
            let barHeight = chartHeight * CGFloat(fraction)
            let rect = CGRect(x: front.minX + 18 + CGFloat(index) * (barWidth + gap), y: base - barHeight,
                              width: barWidth, height: barHeight)
            context.fill(Path(roundedRect: rect, cornerRadius: 2.5), with: .color(index == 3 ? Self.accent : bar))
            if index == 3 { target = rect }
        }
        let textX = front.minX + 18 + 5 * (barWidth + gap) + 14
        drawLines(&context, x: textX, y: front.minY + 24, width: front.maxX - 14 - textX,
                  fractions: [0.95, 0.7, 0.84], step: 10, strong: 0)

        let button = CGRect(x: front.maxX - 52, y: front.maxY - 24, width: 40, height: 14)
        let click = Ease.pulse(progress, at: 0.635, width: 0.05)
        context.fill(Path(roundedRect: button, cornerRadius: 7),
                     with: .color(Self.accent.opacity(1 - 0.3 * click)))
        context.draw(Text("Send").font(.system(size: 7.5, weight: .semibold)).foregroundColor(.white),
                     at: CGPoint(x: button.midX, y: button.midY))

        // Show / hide fades every drawing out, then back in.
        let hidden = Ease.smooth(Ease.segment(progress, 0.745, 0.79)) *
            (1 - Ease.smooth(Ease.segment(progress, 0.84, 0.9)))
        let center = CGPoint(x: target.midX, y: target.midY)
        let loop = Self.loop(center: center, radiusX: 17, radiusY: target.height / 2 + 9)
        let drawn = Ease.easeInOut(Ease.segment(progress, 0.13, 0.38))
        var ink = context
        ink.opacity = 1 - hidden
        ink.translateBy(x: center.x, y: center.y)
        ink.scaleBy(x: 1 - 0.05 * hidden, y: 1 - 0.05 * hidden)
        ink.translateBy(x: -center.x, y: -center.y)
        if drawn > 0 {
            let stroke = loop.trimmedPath(from: 0, to: drawn)
            ink.stroke(stroke, with: .color(Self.ink),
                       style: StrokeStyle(lineWidth: 2.6, lineCap: .round, lineJoin: .round))
            let head = Ease.smooth(Ease.segment(progress, 0.12, 0.15)) *
                (1 - Ease.smooth(Ease.segment(progress, 0.38, 0.42)))
            if head > 0, let point = stroke.currentPoint {
                var tip = ink
                tip.opacity = head
                tip.fill(Path(ellipseIn: CGRect(x: point.x - 4, y: point.y - 4, width: 8, height: 8)), with: .color(.white))
                tip.fill(Path(ellipseIn: CGRect(x: point.x - 2.8, y: point.y - 2.8, width: 5.6, height: 5.6)),
                         with: .color(Self.ink))
            }
        }

        // The toolbar drops in for drawing and leaves when you go back to your apps.
        let arrive = Ease.settle(Ease.segment(progress, 0.02, 0.13))
        let leave = Ease.smooth(Ease.segment(progress, 0.42, 0.47))
        let toolbarOpacity = Ease.clamp(arrive * 1.4) * (1 - leave)
        if toolbarOpacity > 0 {
            let pill = CGRect(x: width * 0.74 - 52, y: 8 + CGFloat(1 - arrive) * -9 - CGFloat(leave) * 6,
                              width: 104, height: 22)
            var toolbar = context
            toolbar.opacity = toolbarOpacity
            var shadowed = toolbar
            shadowed.addFilter(.shadow(color: .black.opacity(0.25), radius: 5, x: 0, y: 2))
            shadowed.fill(Path(roundedRect: pill, cornerRadius: 11), with: .color(Color(white: 0.13, opacity: 0.95)))
            toolbar.stroke(Path(roundedRect: pill, cornerRadius: 11), with: .color(.white.opacity(0.1)), lineWidth: 0.75)
            for (index, name) in ["pencil.tip", "highlighter", "rectangle", "arrow.up.right", "textformat"].enumerated() {
                let point = CGPoint(x: pill.minX + 16 + CGFloat(index) * 18, y: pill.midY)
                if index == 0 {
                    toolbar.fill(Path(roundedRect: CGRect(x: point.x - 8, y: point.y - 8, width: 16, height: 16),
                                      cornerRadius: 5), with: .color(Self.accent))
                }
                toolbar.draw(symbol(name, size: 8.5, color: .white.opacity(index == 0 ? 1 : 0.75)), at: point)
            }
        }

        // Use apps: the pointer glides to the button and clicks it.
        let travel = Ease.easeInOut(Ease.segment(progress, 0.45, 0.62))
        let start = CGPoint(x: width * 0.99, y: height * 1.05)
        let end = CGPoint(x: button.maxX - 7, y: button.midY + 2)
        let control = CGPoint(x: width * 0.86, y: height * 0.36)
        let pointer = Self.bezier(start, control, end, travel)
        let ripple = Ease.segment(progress, 0.64, 0.74)
        if ripple > 0 && ripple < 1 {
            let radius = 5 + 13 * Ease.easeOut(ripple)
            context.stroke(Path(ellipseIn: CGRect(x: end.x - radius, y: end.y - radius, width: radius * 2, height: radius * 2)),
                           with: .color((dark ? Color.white : Self.accent).opacity(0.6 * (1 - ripple))), lineWidth: 1.5)
        }
        drawPointer(&context, tip: pointer, scale: 1 - 0.12 * click,
                    opacity: Ease.smooth(Ease.segment(progress, 0.44, 0.48)))
        drawFrame(&context)
    }

    /// A loose hand-drawn loop that overshoots its start, like a quick circle made with a pen.
    static func loop(center: CGPoint, radiusX: CGFloat, radiusY: CGFloat) -> Path {
        var path = Path()
        let steps = 96, start = -1.95, sweep = 2 * Double.pi + 0.6, tilt = -0.16
        for step in 0...steps {
            let t = Double(step) / Double(steps)
            let angle = start + sweep * t
            let grow = 1 + 0.12 * t
            let wobble = 1 + 0.035 * sin(3 * angle + 0.8)
            let x = Double(radiusX) * grow * wobble * cos(angle)
            let y = Double(radiusY) * grow * wobble * sin(angle)
            let point = CGPoint(x: center.x + CGFloat(x * cos(tilt) - y * sin(tilt)),
                                y: center.y + CGFloat(x * sin(tilt) + y * cos(tilt)))
            if step == 0 { path.move(to: point) } else { path.addLine(to: point) }
        }
        return path
    }

    static func bezier(_ start: CGPoint, _ control: CGPoint, _ end: CGPoint, _ t: Double) -> CGPoint {
        let u = CGFloat(1 - t), v = CGFloat(t)
        return CGPoint(x: u * u * start.x + 2 * u * v * control.x + v * v * end.x,
                       y: u * u * start.y + 2 * u * v * control.y + v * v * end.y)
    }

    // MARK: Make it yours

    /// Pick the gradient, draw the outline, soften the fill, then type the label.
    func drawColor(in context: inout GraphicsContext) {
        drawDesktop(&context)
        let width = size.width, height = size.height
        let card = CGRect(x: 14, y: 14, width: 122, height: height - 28)
        drawWindow(&context, card, titleBar: false)

        let swatches: [Color] = [
            dark ? .white : Color(red: 0.12, green: 0.15, blue: 0.16), Self.accent,
            Color(red: 0, green: 0.59, blue: 0.62), Color(red: 0.96, green: 0.89, blue: 0.22), Self.ink
        ]
        var swatchFrames: [CGRect] = []
        for (index, color) in swatches.enumerated() {
            let frame = CGRect(x: card.minX + 12 + CGFloat(index) * 21.5, y: card.minY + 11, width: 14, height: 14)
            swatchFrames.append(frame)
            context.fill(Path(ellipseIn: frame), with: .color(color))
            context.stroke(Path(ellipseIn: frame), with: .color(edge), lineWidth: 0.5)
        }
        let chip = CGRect(x: card.minX + 12, y: card.minY + 33, width: 80, height: 14)
        context.fill(Path(roundedRect: chip, cornerRadius: 7),
                     with: .linearGradient(Self.sunset, startPoint: CGPoint(x: chip.minX, y: chip.minY),
                                           endPoint: CGPoint(x: chip.maxX, y: chip.maxY)))
        let wheel = CGRect(x: card.maxX - 26, y: chip.minY, width: 14, height: 14)
        context.fill(Path(ellipseIn: wheel), with: .conicGradient(
            Gradient(colors: [.red, .yellow, .green, .cyan, .blue, .purple, .red]),
            center: CGPoint(x: wheel.midX, y: wheel.midY)))

        // The selection ring moves from the default color to the gradient.
        let pick = Ease.easeInOut(Ease.segment(progress, 0.08, 0.22))
        let from = swatchFrames[4].insetBy(dx: -3, dy: -3), to = chip.insetBy(dx: -3, dy: -3)
        let ring = CGRect(x: Ease.mix(from.minX, to.minX, pick), y: Ease.mix(from.minY, to.minY, pick),
                          width: Ease.mix(from.width, to.width, pick), height: Ease.mix(from.height, to.height, pick))
        context.stroke(Path(roundedRect: ring, cornerRadius: ring.height / 2),
                       with: .color(dark ? .white.opacity(0.85) : .black.opacity(0.7)), lineWidth: 1.5)

        // Separate opacity controls: the outline stays full, the fill slides down.
        let fillAmount = 1 - 0.72 * Ease.easeInOut(Ease.segment(progress, 0.5, 0.74))
        for (index, value) in [1.0, fillAmount].enumerated() {
            let y = card.minY + 61 + CGFloat(index) * 13
            let icon = CGRect(x: card.minX + 12, y: y - 4, width: 8, height: 8)
            if index == 0 {
                context.stroke(Path(ellipseIn: icon.insetBy(dx: 0.75, dy: 0.75)), with: .color(strongLine), lineWidth: 1.5)
            } else {
                context.fill(Path(ellipseIn: icon), with: .color(strongLine))
            }
            let track = CGRect(x: card.minX + 27, y: y - 1.5, width: card.width - 39, height: 3)
            context.fill(Path(roundedRect: track, cornerRadius: 1.5), with: .color(line))
            let filled = CGRect(x: track.minX, y: track.minY, width: track.width * CGFloat(value), height: 3)
            context.fill(Path(roundedRect: filled, cornerRadius: 1.5),
                         with: .linearGradient(Self.sunset, startPoint: CGPoint(x: track.minX, y: 0),
                                               endPoint: CGPoint(x: track.maxX, y: 0)))
            let knob = CGRect(x: filled.maxX - 4.5, y: y - 4.5, width: 9, height: 9)
            var shadowed = context
            shadowed.addFilter(.shadow(color: .black.opacity(0.25), radius: 1.5, x: 0, y: 0.5))
            shadowed.fill(Path(ellipseIn: knob), with: .color(.white))
        }

        let shape = CGRect(x: card.maxX + 24, y: 22, width: width - card.maxX - 46, height: height - 44)
        let start = CGPoint(x: shape.minX, y: shape.minY)
        let end = CGPoint(x: shape.maxX, y: shape.maxY)
        let fill = Ease.smooth(Ease.segment(progress, 0.5, 0.76))
        if fill > 0 {
            var soft = context
            soft.opacity = 0.22 * fill
            soft.fill(Path(roundedRect: shape, cornerRadius: 12, style: .continuous),
                      with: .linearGradient(Self.sunset, startPoint: start, endPoint: end))
        }
        let drawn = Ease.easeInOut(Ease.segment(progress, 0.22, 0.62))
        if drawn > 0 {
            let outline = Self.outline(shape, radius: 12).trimmedPath(from: 0, to: drawn)
            context.stroke(outline, with: .linearGradient(Self.sunset, startPoint: start, endPoint: end),
                           style: StrokeStyle(lineWidth: 4.5, lineCap: .round, lineJoin: .round))
            if drawn < 1, let point = outline.currentPoint {
                var spark = context
                spark.opacity = 0.9 * sin(.pi * drawn)
                spark.addFilter(.blur(radius: 1.5))
                spark.fill(Path(ellipseIn: CGRect(x: point.x - 3.5, y: point.y - 3.5, width: 7, height: 7)),
                           with: .color(.white))
            }
        }

        let label = "Make your point"
        let typed = Ease.segment(progress, 0.64, 0.92)
        let count = Int((Double(label.count) * typed).rounded())
        let font = Font.system(size: 16, weight: .bold, design: .rounded)
        let full = context.resolve(Text(label).font(font)).measure(in: size)
        let left = shape.midX - full.width / 2
        if count > 0 {
            let resolved = context.resolve(Text(String(label.prefix(count))).font(font).foregroundColor(.black))
            let textRect = CGRect(x: left, y: shape.midY - full.height / 2, width: full.width, height: full.height)
            context.drawLayer { layer in
                layer.clipToLayer { mask in mask.draw(resolved, at: CGPoint(x: left, y: shape.midY), anchor: .leading) }
                layer.fill(Path(textRect.insetBy(dx: -2, dy: -2)),
                           with: .linearGradient(Self.sunset, startPoint: CGPoint(x: textRect.minX, y: 0),
                                                 endPoint: CGPoint(x: textRect.maxX, y: 0)))
            }
        }
        let caret = Ease.smooth(Ease.segment(progress, 0.6, 0.64)) * (1 - Ease.smooth(Ease.segment(progress, 0.93, 0.98)))
        if caret > 0 {
            let typedWidth = count > 0
                ? context.resolve(Text(String(label.prefix(count))).font(font)).measure(in: size).width : 0
            let bar = CGRect(x: left + typedWidth + 1.5, y: shape.midY - 8.5, width: 1.6, height: 17)
            context.fill(Path(roundedRect: bar, cornerRadius: 0.8), with: .color(Self.ink.opacity(caret)))
        }
        drawFrame(&context)
    }

    /// A rounded outline that starts at the top center, so the stroke draws on in one clockwise pass.
    static func outline(_ rect: CGRect, radius: CGFloat) -> Path {
        var path = Path()
        path.move(to: CGPoint(x: rect.midX, y: rect.minY))
        path.addArc(tangent1End: CGPoint(x: rect.maxX, y: rect.minY), tangent2End: CGPoint(x: rect.maxX, y: rect.maxY),
                    radius: radius)
        path.addArc(tangent1End: CGPoint(x: rect.maxX, y: rect.maxY), tangent2End: CGPoint(x: rect.minX, y: rect.maxY),
                    radius: radius)
        path.addArc(tangent1End: CGPoint(x: rect.minX, y: rect.maxY), tangent2End: CGPoint(x: rect.minX, y: rect.minY),
                    radius: radius)
        path.addArc(tangent1End: CGPoint(x: rect.minX, y: rect.minY), tangent2End: CGPoint(x: rect.maxX, y: rect.minY),
                    radius: radius)
        path.closeSubpath()
        return path
    }

    // MARK: Lead the way

    /// The pointer arrives, its ring pulses once, Spotlight dims the rest and the ring opens into a zoom lens.
    func drawFocus(in context: inout GraphicsContext) {
        drawDesktop(&context)
        let width = size.width, height = size.height
        let window = CGRect(x: width * 0.1, y: 12, width: width * 0.8, height: height - 22)
        let tip = CGPoint(x: window.minX + window.width * 0.7, y: window.minY + 50)
        let amount = CGPoint(x: tip.x - 3, y: tip.y - 6)
        drawFocusContent(&context, window: window, amount: amount)

        let travel = Ease.easeInOut(Ease.segment(progress, 0.02, 0.26))
        let pointer = Self.bezier(CGPoint(x: width * 0.2, y: height * 1.15),
                                  CGPoint(x: width * 0.3, y: height * 0.3), tip, travel)
        let ringIn = Ease.settle(Ease.segment(progress, 0.24, 0.34))
        let spotlight = Ease.smooth(Ease.segment(progress, 0.44, 0.62))
        let lens = Ease.settle(Ease.segment(progress, 0.62, 0.86))
        let lensRadius = Ease.mix(13, 40, lens)

        if spotlight > 0 {
            let hole = 28 + 14 * CGFloat(min(1, lens)), feather: CGFloat = 10
            let dim = (dark ? 0.55 : 0.32) * spotlight
            context.fill(Path(bounds), with: .radialGradient(
                Gradient(stops: [.init(color: .black.opacity(0), location: 0),
                                 .init(color: .black.opacity(0), location: hole / (hole + feather)),
                                 .init(color: .black.opacity(dim), location: 1)]),
                center: tip, startRadius: 0, endRadius: hole + feather))
        }

        if lens > 0 {
            let circle = Path(ellipseIn: CGRect(x: tip.x - lensRadius, y: tip.y - lensRadius,
                                                width: lensRadius * 2, height: lensRadius * 2))
            var shadowed = context
            shadowed.addFilter(.shadow(color: .black.opacity(dark ? 0.45 : 0.25), radius: 7, x: 0, y: 3))
            shadowed.fill(circle, with: .color(windowFill))
            var magnified = context
            magnified.clip(to: circle)
            magnified.translateBy(x: tip.x, y: tip.y)
            magnified.scaleBy(x: 1.8, y: 1.8)
            magnified.translateBy(x: -tip.x, y: -tip.y)
            drawFocusContent(&magnified, window: window, amount: amount)
        }

        if ringIn > 0 {
            let ring = Path(ellipseIn: CGRect(x: tip.x - lensRadius, y: tip.y - lensRadius,
                                              width: lensRadius * 2, height: lensRadius * 2))
            var halo = context
            halo.opacity = Ease.clamp(ringIn * 1.3)
            halo.translateBy(x: tip.x, y: tip.y)
            let grow = 0.6 + 0.4 * CGFloat(ringIn)
            halo.scaleBy(x: grow, y: grow)
            halo.translateBy(x: -tip.x, y: -tip.y)
            halo.stroke(ring, with: .color(Self.halo), lineWidth: 3 - 0.75 * CGFloat(min(1, lens)))
        }
        let pulse = Ease.segment(progress, 0.32, 0.46)
        if pulse > 0 && pulse < 1 {
            let radius = 13 + 12 * Ease.easeOut(pulse)
            context.stroke(Path(ellipseIn: CGRect(x: tip.x - radius, y: tip.y - radius, width: radius * 2, height: radius * 2)),
                           with: .color(Self.halo.opacity(0.7 * (1 - pulse))), lineWidth: 2)
        }
        drawPointer(&context, tip: pointer, opacity: Ease.smooth(Ease.segment(progress, 0, 0.06)))
        drawFrame(&context)
    }

    /// A window of small print. `amount` is the center of the total the pointer points at.
    private func drawFocusContent(_ context: inout GraphicsContext, window: CGRect, amount: CGPoint) {
        drawWindow(&context, window)
        drawLines(&context, x: window.minX + 14, y: window.minY + 22, width: window.width * 0.42,
                  fractions: [0.7, 1, 0.86, 0.94, 0.6, 0.8], step: 10, strong: 0)
        let small = Font.system(size: 6.5, weight: .medium)
        let bold = Font.system(size: 6.5, weight: .bold)
        let label = amount.x - 62, right = amount.x + 17
        let quiet = printColor.opacity(0.6)
        context.draw(Text("Subtotal").font(small).foregroundColor(quiet), at: CGPoint(x: label, y: amount.y - 22), anchor: .leading)
        context.draw(Text("$1,176.00").font(small).foregroundColor(quiet), at: CGPoint(x: right, y: amount.y - 22), anchor: .trailing)
        context.draw(Text("Tax").font(small).foregroundColor(quiet), at: CGPoint(x: label, y: amount.y - 12), anchor: .leading)
        context.draw(Text("$108.00").font(small).foregroundColor(quiet), at: CGPoint(x: right, y: amount.y - 12), anchor: .trailing)
        context.fill(Path(CGRect(x: label, y: amount.y - 6.5, width: right - label, height: 0.75)), with: .color(line))
        context.draw(Text("Total").font(bold).foregroundColor(printColor), at: CGPoint(x: label, y: amount.y), anchor: .leading)
        context.draw(Text("$1,284.00").font(bold).foregroundColor(printColor), at: CGPoint(x: right, y: amount.y), anchor: .trailing)
    }
}

/// A green check that draws itself once when a permission is already allowed.
struct IntroductionAllowedBadge: View {
    let accessibilityText: String
    let delay: Double
    let reduceMotion: Bool
    /// Freezes the check for review frames instead of animating it.
    let fixedDraw: Double?
    @State private var animatedDraw: Double

    init(accessibilityText: String, delay: Double, reduceMotion: Bool, fixedDraw: Double? = nil) {
        self.accessibilityText = accessibilityText
        self.delay = delay
        self.reduceMotion = reduceMotion
        self.fixedDraw = fixedDraw
        _animatedDraw = State(initialValue: reduceMotion ? 1 : 0)
    }

    private var drawn: Double { fixedDraw ?? animatedDraw }

    var body: some View {
        HStack(spacing: 6) {
            ZStack {
                Circle().fill(Color.green.opacity(0.16))
                    .scaleEffect(0.6 + 0.4 * min(1, drawn * 1.6))
                IntroductionCheckShape().trim(from: 0, to: drawn)
                    .stroke(Color.green, style: StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round))
                    .padding(6)
            }
            .frame(width: 22, height: 22)
            Text("Allowed").font(.callout.weight(.medium)).foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityText)
        .onAppear {
            guard fixedDraw == nil, !reduceMotion, animatedDraw < 1 else { return }
            withAnimation(.easeOut(duration: 0.38).delay(delay)) { animatedDraw = 1 }
        }
    }
}

struct IntroductionCheckShape: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.midY + rect.height * 0.04))
        path.addLine(to: CGPoint(x: rect.minX + rect.width * 0.38, y: rect.maxY - rect.height * 0.08))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY + rect.height * 0.12))
        return path
    }
}
