import SwiftUI
import AnnotationCore

struct WheelColor: Equatable {
    var hue: Double
    var saturation: Double
    var brightness: Double

    init(_ color: RGBA) {
        let maximum = max(color.red, color.green, color.blue)
        let minimum = min(color.red, color.green, color.blue)
        let delta = maximum - minimum
        brightness = maximum
        saturation = maximum == 0 ? 0 : delta / maximum
        if delta == 0 { hue = 0 }
        else if maximum == color.red { hue = ((color.green - color.blue) / delta).truncatingRemainder(dividingBy: 6) / 6 }
        else if maximum == color.green { hue = ((color.blue - color.red) / delta + 2) / 6 }
        else { hue = ((color.red - color.green) / delta + 4) / 6 }
        if hue < 0 { hue += 1 }
    }

    var color: RGBA {
        let sector = (hue == 1 ? 0 : hue) * 6
        let chroma = brightness * saturation
        let second = chroma * (1 - abs(sector.truncatingRemainder(dividingBy: 2) - 1))
        let offset = brightness - chroma
        let components: (Double, Double, Double)
        switch sector {
        case 0..<1: components = (chroma, second, 0)
        case 1..<2: components = (second, chroma, 0)
        case 2..<3: components = (0, chroma, second)
        case 3..<4: components = (0, second, chroma)
        case 4..<5: components = (second, 0, chroma)
        default: components = (chroma, 0, second)
        }
        return RGBA(components.0 + offset, components.1 + offset, components.2 + offset)
    }

    mutating func move(to point: CGPoint, size: CGSize) {
        let dx = point.x - size.width / 2, dy = point.y - size.height / 2
        let distance = hypot(dx, dy)
        if distance > 0 {
            hue = atan2(dy, dx) / (2 * .pi)
            if hue < 0 { hue += 1 }
        }
        saturation = min(1, distance / max(1, min(size.width, size.height) / 2))
    }
}

struct InlineColorWheel: View {
    let color: RGBA
    let choose: (RGBA) -> Void
    @State private var value = WheelColor(.coral)
    private let hues = [
        Color(red: 1, green: 0, blue: 0), Color(red: 1, green: 1, blue: 0),
        Color(red: 0, green: 1, blue: 0), Color(red: 0, green: 1, blue: 1),
        Color(red: 0, green: 0, blue: 1), Color(red: 1, green: 0, blue: 1),
        Color(red: 1, green: 0, blue: 0)
    ]

    var body: some View {
        VStack(spacing: 10) {
            GeometryReader { geometry in
                let radius = geometry.size.width / 2
                ZStack {
                    Circle().fill(AngularGradient(
                        colors: hues,
                        center: .center, startAngle: .zero, endAngle: .degrees(360)))
                    Circle().fill(RadialGradient(colors: [.white, .white.opacity(0)],
                        center: .center, startRadius: 0, endRadius: radius))
                    Circle().fill(.black.opacity(1 - value.brightness))
                    Circle().strokeBorder(.white, lineWidth: 2)
                        .background(Circle().stroke(.black.opacity(0.75), lineWidth: 4))
                        .frame(width: 12, height: 12)
                        .position(x: radius + cos(value.hue * 2 * .pi) * value.saturation * radius,
                                  y: radius + sin(value.hue * 2 * .pi) * value.saturation * radius)
                }
                .drawingGroup(opaque: false, colorMode: .nonLinear)
                .contentShape(Circle())
                .gesture(DragGesture(minimumDistance: 0).onChanged { event in
                    value.move(to: event.location, size: geometry.size)
                    choose(value.color)
                })
            }
            .frame(width: 176, height: 176).padding(6)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Hue and saturation wheel")
            .accessibilityHint("Use the hue, saturation and brightness sliders to choose a color with the keyboard.")
            channel("Hue", key: \.hue, scale: 360, suffix: "°")
            channel("Saturation", key: \.saturation, scale: 100, suffix: "%")
            channel("Brightness", key: \.brightness, scale: 100, suffix: "%")
            Text("Use the wheel for a solid color.").font(.caption).foregroundStyle(.secondary)
        }
        .onAppear { value = WheelColor(color) }
        .onChange(of: color) { old, new in
            guard old.red != new.red || old.green != new.green || old.blue != new.blue else { return }
            let updated = WheelColor(new)
            // Gray and black have no hue; retain the wheel's chosen hue until color returns.
            value.brightness = updated.brightness
            if updated.brightness > 0 { value.saturation = updated.saturation }
            if updated.saturation > 0, !(value.hue == 1 && updated.hue == 0) { value.hue = updated.hue }
        }
    }

    private func channel(_ name: String, key: WritableKeyPath<WheelColor, Double>,
                         scale: Double, suffix: String) -> some View {
        HStack(spacing: 8) {
            Text(name).font(.caption).frame(width: 66, alignment: .leading)
            Slider(value: Binding(get: { value[keyPath: key] * scale }, set: {
                value[keyPath: key] = $0 / scale
                choose(value.color)
            }), in: 0...scale, step: 1).accessibilityLabel("Color \(name.lowercased())")
            Text("\(Int((value[keyPath: key] * scale).rounded()))\(suffix)")
                .font(.caption.monospacedDigit()).frame(width: 36, alignment: .trailing)
        }
    }
}
